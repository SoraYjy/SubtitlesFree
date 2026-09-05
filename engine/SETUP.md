# 引擎环境实测记录（M0 spike，2026-09-06）

一次性 spike `probe/spike.py` 在本机（RTX 3080 Ti 12GB / Win10）跑通 whisperx 全管道
（pyannote VAD → faster-whisper large-v3-turbo 转写 → wav2vec2 中文音素对齐）后的结论与环境记录。
README（T11）可直接复用本文档。

## 技术路径结论：PATH_A（whisperx 原生管道可用）

**结论：PATH_A。** 三项风险实测结果：

1. **pyannote VAD gating —— 不存在。** whisperx 3.4.5 的 wheel 自带 VAD 权重
   `site-packages/whisperx/assets/pytorch_model.bin`（17.7MB，即 pyannote/segmentation-0.5.3
   的权重），加载全程未访问 HuggingFace、未要求 HF_TOKEN、无 GatedRepoError/401。
   无需向用户索要 HF 授权，PATH_B 备选不需要。
2. **CUDA/torch 运行时兼容 —— 可用，但有两个必做的坑**（见下「已知问题」）：
   torch 2.6 `weights_only` 默认值变化需要 safe-globals shim；ctranslate2 4.4.0 需要
   cuDNN 8 DLL。transformers 5.16.1 与 whisperx 3.4.5 的跨代组合**未**出现 API 报错，
   已文档化的 `transformers<5` 降级**不需要**。
3. **中文音素对齐 —— 可用，有已知特性。** 中文逐字分数普遍 0.9–1.0；拉丁热词
   （ComfyUI/SDXL 等）被逐字符对齐、部分字符分数为 0（时长退化到 <50ms）。
   做字幕断句时需把相邻的拉丁单字 token 重新合并为词（见「对齐质量」）。

成功判据（brief 定义）：转写文本中 ComfyUI/SDXL/ControlNet/LoRA 拼写全部正确（各出现
2 次共 8 处全对）；对齐段与词级时间戳均有 start/end；无异常。全部满足。

## 实测版本（venv: engine/.venv）

| 组件 | 版本 | 备注 |
|---|---|---|
| Python | 3.11.0 | E:\python311 |
| torch | 2.6.0+cu124 | CUDA 可用，RTX 3080 Ti 12GB |
| whisperx | 3.4.5 | pin `ctranslate2<4.5.0` |
| ctranslate2 | 4.4.0 | Win wheel 自带 cudnn64_8.dll 但缺子组件 DLL |
| faster-whisper | 1.2.1 | `large-v3-turbo` → repo `mobiuslabsgmbh/faster-whisper-large-v3-turbo` |
| transformers | 5.16.1 | 未降级，加载 wav2vec2 对齐模型正常 |
| pyannote.audio | 3.4.0 | VAD 权重随 whisperx wheel 分发 |
| nvidia-cudnn-cu12 | **8.9.7.29（本次新增 pin）** | 给 ctranslate2 提供 cuDNN 8 DLL；见已知问题 2 |
| edge-tts | 7.2.8 | fixture 合成 |
| ffmpeg | 4.3.2 | **硬依赖**，whisperx.load_audio 直接调 ffmpeg；本机用 E:\Applications\GPT-SoVITS\ffmpeg.exe |

## 模型与对齐模型

- ASR：`mobiuslabsgmbh/faster-whisper-large-v3-turbo`（faster-whisper 1.2.1 对
  `large-v3-turbo` 的映射），float16，本地目录加载。
- VAD：whisperx wheel 内置（pyannote segmentation），零下载零授权。
- 对齐：`jonatasgrosman/wav2vec2-large-xlsr-53-chinese-zh-cn`（whisperx 对 zh 的默认），
  经 `load_align_model(language_code="zh", model_name=<本地目录>)` 加载。

## 本机网络现实与模型获取方式（重要，T5 需沿用）

本机 huggingface.co 直连被墙（DNS 污染 + SNI 重置），hf-mirror.com 主站 TCP 不通，
本机代理（127.0.0.1:7897）未运行。实测可行路径：**ModelScope 镜像**（modelscope.cn
直连 0.3s），两个所需 repo 在 ModelScope 均有同名同文件镜像，且 API 提供 sha256 可校验：

- `https://modelscope.cn/api/v1/models/mobiuslabsgmbh/faster-whisper-large-v3-turbo`
- `https://modelscope.cn/api/v1/models/jonatasgrosman/wav2vec2-large-xlsr-53-chinese-zh-cn`

文件下载（2.9GB 实测约 117MB/s，全部 sha256 校验通过）后放本地目录，spike 用
`WHISPERX_MODEL` / `ALIGN_MODEL` 环境变量传入路径（faster-whisper 的
`WhisperModel` 与 `load_align_model(model_name=...)` 都接受本地目录，零下载）。
T5 实现时应把「HF 优先、ModelScope 兜底」做成引擎的模型获取逻辑。

## 已知问题（T5 必须处理）

1. **torch 2.6 + pyannote checkpoint（weights_only）**：torch 2.6 起 `torch.load` 默认
   `weights_only=True`，whisperx 内置 VAD checkpoint 含 omegaconf/pyannote 对象会
   `UnpicklingError`。解决：加载前调用
   `torch.serialization.add_safe_globals([...14 个类...])`（omegaconf 的
   ListConfig/AnyNode/ContainerMetadata/Metadata、pyannote 的
   Introspection/Specifications/Problem/Resolution、TorchVersion、typing.Any、
   collections.defaultdict、builtins.list/dict/int）。完整清单见 `probe/spike.py`。
2. **ctranslate2 4.4.0 缺 cuDNN 8 子组件 DLL**：其 Win wheel 只带 `cudnn64_8.dll`，
   缺 `cudnn_ops_infer64_8.dll` 等，CUDA 加载直接进程崩溃（0xC0000135）。解决：
   `pip install nvidia-cudnn-cu12==8.9.7.29`（PyPI 直连可用），运行时把
   `engine/.venv/Lib/site-packages/nvidia/cudnn/bin` 加入 PATH。whisperx pin 了
   `ctranslate2<4.5`，不能靠升级 CT2 解决（4.5+ 才用 cuDNN 9）。
3. **ffmpeg 硬依赖**：未装 ffmpeg 时 `load_audio` 报 WinError 2。产品需自带或检测
   ffmpeg；本机暂用 E:\Applications\GPT-SoVITS\ffmpeg.exe（4.3.2，解码 mp3/mp4 正常）。
4. **对齐质量特性**：中文逐字 score 0.9–1.0；拉丁词逐字符切分，个别字符 score=0.00、
   时长塌缩（如 ControlNet 的 t/r/o/l 挤在 20ms 内）。T5 断句逻辑需（a）把相邻拉丁
   单字 token 合并成词、（b）对 score 极低的字符时间戳做钳制/平滑。
5. 转写会把 fixture 里的连接词（和/还有/的用法）吞成逗号——ASR 层噪声，不影响热词
   拼写，后续用真实视频再评估（T10）。

## 实测耗时（fixture：edge-tts 合成，19.8s 中文，含 4 个热词各出现 2 次）

| 阶段 | 首次（模型已在本地） | 二次（热） |
|---|---|---|
| 模型加载（ASR+VAD+对齐） | 5.5s | 5.3s |
| VAD+转写 | ~1.1s | ~0.6s |
| 音素对齐 | ~1.3s | ~1.1s |
| **总耗时** | **7.9s** | **7.1s** |

- RTF ≈ 0.36–0.40（19.8s 音频 / 7.1–7.9s 处理），GPU 峰值显存 1602 MiB（float16，
  12GB 卡余量充足；即使本机其它进程占用 3GB 也不会 OOM）。
- 词级时间戳 106 个（中文按字）。
- 模型一次性下载：2.9GB（ASR 1.62GB + 对齐 1.28GB），ModelScope 实测 ~117MB/s。

## 复现命令（本机）

```bash
cd engine
PATH="/e/Applications/GPT-SoVITS:/d/sora/SubtitlesFree/engine/.venv/Lib/site-packages/nvidia/cudnn/bin:$PATH" \
HF_HUB_OFFLINE=1 PYTHONIOENCODING=utf-8 \
WHISPERX_MODEL="D:\sora\SubtitlesFree\probe\models\asr" \
ALIGN_MODEL="D:\sora\SubtitlesFree\probe\models\align" \
.venv/Scripts/python ../probe/spike.py samples/fixture.mp3
```

（`probe/` 为 gitignored 一次性探针；模型缓存在 `probe/models/`，T5 可迁移复用。）
