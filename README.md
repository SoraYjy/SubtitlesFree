# SubtitlesFree

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6)

> **作者**：**Sora** · B站 ID：**SoraYjy**

MP4 视频高精度 SRT 字幕生成器（Windows）。WhisperX 架构：VAD 切除无声 → Whisper large-v3-turbo 识别 → Wav2Vec2 音素级强制对齐（毫秒级卡点）→ 可选中英双语。热词表纠正专有名词同音错字。

## 特性

| 特性 | 说明 |
|---|---|
| **高精度识别** | Whisper large-v3-turbo（可切 large-v3 / small），VAD 过滤幻觉 |
| **毫秒级时间轴** | Wav2Vec2 音素级强制对齐 |
| **热词干预** | 专有名词表注入 initial_prompt，纠正同音错字 |
| **中英双语** | 可选上行中文、下行英文 |
| **绿色单文件** | self-contained exe，无需安装 .NET |
| **进程隔离** | CUDA 崩溃不影响界面，取消即杀进程树 |

## 环境要求（自备，开发者向）

- Windows 10/11 x64
- **NVIDIA 显卡（显存 ≥ 8GB 推荐）** + 最新驱动
- **Python 3.10+**（推荐 3.11）
- **FFmpeg（硬依赖，须在 PATH 上）**：`winget install Gyan.FFmpeg`，或下载后把所在目录加入 PATH。提取音频直接调用 ffmpeg，未装时环境检测会标红
- ~10GB 磁盘（torch + 模型）

## 快速开始

### ① 装引擎环境（一次性）

```bash
cd engine
python -m venv .venv
.venv/Scripts/python -m pip install -U pip
.venv/Scripts/pip install torch --index-url https://download.pytorch.org/whl/cu124
.venv/Scripts/pip install -r requirements.txt
```

> ⚠️ torch 必须用 cu124 索引装 CUDA 版；Windows 上默认 PyPI 是 CPU 版（慢 50 倍）。
>
> requirements.txt 已 pin `nvidia-cudnn-cu12==8.9.7.29`：ctranslate2 4.4 的 Windows wheel 缺 cuDNN 8 子组件 DLL，引擎运行时会把 venv 内的 cuDNN 目录自动补进 PATH，缺它 CUDA 初始化直接进程崩溃（[engine/SETUP.md](engine/SETUP.md) 已知问题 2）。
>
> `requirements-dev.txt`（pytest、edge-tts 合成测试音频）只有开发测试才需要，日常使用不必安装。版本细节与踩坑记录见 [engine/SETUP.md](engine/SETUP.md)。

### ② 预下载模型（可选，HF 直连被墙时）

模型默认首跑自动下载（ASR ~1.6GB + 对齐模型 ~1.3GB），设置里可勾「镜像：hf-mirror.com 国内加速」。HF 与镜像都不通时，用 ModelScope 手动预下载到本地目录（本机实测 ~117MB/s）：

```bash
pip install modelscope
cd engine
modelscope download --model mobiuslabsgmbh/faster-whisper-large-v3-turbo --local_dir models/large-v3-turbo
modelscope download --model jonatasgrosman/wav2vec2-large-xlsr-53-chinese-zh-cn --local_dir models/align
```

选 large-v3 / small 时同理换 repo 名与目录名：

```bash
modelscope download --model Systran/faster-whisper-large-v3 --local_dir models/large-v3
modelscope download --model Systran/faster-whisper-small --local_dir models/small
```

引擎按所选模型解析本地目录：优先 `engine/models/<模型名>` 同名目录（turbo 兼容遗留 `engine/models/asr`），不存在则回退在线下载。

### ③ 首次运行

1. `dotnet run --project src/SubtitlesFree.App`（在仓库根目录执行；或先 [打包](#从源码构建) 再双击 `SubtitlesFree.exe`）
2. 点「检测环境」——五项全绿即就绪（Python / torch+CUDA / whisperx / 模型缓存 / ffmpeg）
3. 拖入 MP4 → 「生成字幕」

## 使用说明

- **热词**：设置里填专有名词（逗号/换行分隔），如 `ComfyUI, SDXL, ControlNet, LoRA`
- **双语**：语言选「中英双语」→ SRT 每条上行中文、下行英文。英文行由第二遍 translate 转写，按重叠时长匹配回中文 cue，**复用中文时间轴**（不单独对齐）
- **双语模型限制**：**large-v3-turbo 不支持 zh→en 翻译**（实测输出多为中文回写），双语请切 `large-v3`；双语+turbo 时应用会输出 WARN 日志
- **精度**：显存吃紧（<6GB 可用）选 int8_float16
- **取消**：随时点取消，立即杀掉引擎进程
- **配置/日志**：`%AppData%\SubtitlesFree\`

## 性能参考（fixture 实测）

RTX 3080 Ti 12GB · turbo · float16 · 单语，edge-tts 合成的 19.8s 中文音频：总处理 **7.1–7.9s**（含 ~5.5s 模型加载），**RTF ≈ 0.36–0.40**；GPU 峰值显存 ~1.6GB。详见 [engine/SETUP.md](engine/SETUP.md)。

## 从源码构建

需要 .NET 8 SDK。

```bash
dotnet run --project src/SubtitlesFree.App      # debug 运行
./pack.sh                                        # 打绿色包 → dist/SubtitlesFree/
```

## 致谢

- [WhisperX](https://github.com/m-bain/whisperX)、[faster-whisper](https://github.com/SYSTRAN/faster-whisper)、[Whisper](https://github.com/openai/whisper)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)、Microsoft .NET / WPF

## 协议

[MIT](LICENSE)
