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
| **纯文本转写** | 视频直出 TXT：逐段自动检测语言，中文英文混排原样转出 |
| **懂你意思（可选）** | 生成 SRT 后按视频文案用 DeepSeek 自动纠错，产出 `.ai.srt` 不改原文件；存疑处标「【?】」 |
| **热词干预** | 专有名词表注入 initial_prompt，纠正同音错字 |
| **中英双语** | 可选上行中文、下行英文 |
| **绿色单文件** | self-contained exe + 内嵌 Python 运行时，无需安装 .NET 与 Python |
| **进程隔离** | CUDA 崩溃不影响界面，取消即杀进程树 |

## 环境要求

- Windows 10/11 x64
- **NVIDIA 显卡（显存 ≥ 8GB 推荐）** + 最新驱动（游戏玩家一般都已就绪）；**RTX 50 系暂不支持**（当前 CUDA 12.4 轮子不含 Blackwell 内核，GTX 900 系 ~ RTX 40 系均可）
- **无需安装 Python**（发布包自带运行时；仅源码运行需要，见下）
- FFmpeg：**已内置**，无需安装
- ~10GB 磁盘（依赖 + 模型）

## 快速开始

从 [GitHub Releases](https://github.com/SoraYjy/SubtitlesFree/releases) 下载 `SubtitlesFree-win64-*.zip`，**解压到一个普通目录**（不要放 `C:\Program Files` 等需要管理员权限的位置，桌面/`D:\tools` 都行），然后按下面两步走——均为一次性操作，全部完成即可永久使用。

### ① 安装依赖（约 3.5GB，视网速 10~40 分钟）

进入解压目录，**双击 `安装依赖.bat`**，等窗口显示「INSTALL OK」即可。依赖全部装进应用自带的 `runtime\` 文件夹，**不碰系统 Python 一个字节**（不需要安装 Python，也不配置任何环境变量）；中途断网/关窗，重新双击一次即可续装（已装好的自动跳过）。

> 杀毒软件若拦截 `runtime\python.exe` 写入，将其加入信任/临时放行后再双击一次。

### ② 模型（约 2.9GB）

- **能访问 [huggingface.co](https://huggingface.co)（或勾选设置里的镜像后能访问 hf-mirror.com）：跳过本步**，首次生成字幕时自动下载，设置里默认已勾「镜像国内加速」
- **都不通（国内常见）**：用命令行下载（实测约 117MB/s）。**怎么让黑窗口「就在正确目录」**：文件资源管理器进入解压目录，在上方**地址栏**输入 `cmd` 回车——弹出的黑窗口就已经在这个目录里，命令原样粘贴（右键粘贴）：

```bash
runtime\python.exe -m pip install modelscope -i https://mirrors.aliyun.com/pypi/simple/
runtime\python.exe -m modelscope download --model mobiuslabsgmbh/faster-whisper-large-v3-turbo --local_dir engine\models\large-v3-turbo
runtime\python.exe -m modelscope download --model jonatasgrosman/wav2vec2-large-xlsr-53-chinese-zh-cn --local_dir engine\models\align
```

将来在应用里切 large-v3 / small 模型时，同理换 repo 名再下载到 `engine\models\<模型名>`：

```bash
runtime\python.exe -m modelscope download --model Systran/faster-whisper-large-v3 --local_dir engine\models\large-v3
runtime\python.exe -m modelscope download --model Systran/faster-whisper-small --local_dir engine\models\small
```

### ③ 首次运行与基本使用

1. 双击 `SubtitlesFree.exe`（免安装；绿色单文件）
2. 点「检测环境」——五项全绿即就绪（runtime / torch+CUDA / whisperx / 模型缓存 / ffmpeg）；任何一项红叉时，状态条会直接给出该项的修复提示，照做后重新检测即可
3. 把 MP4 拖进窗口（或点「选择视频」）→ 点「**生成字幕**」出 SRT 字幕；点「**转写文本**」则输出纯文本 TXT（逐段自动检测语言，不做时间轴，更快）
4. 生成的文件在视频同目录，文件名同视频（`.srt` / `.txt`）；底部「打开输出所在文件夹」可直达

> 有多 Python 项目隔离需求的高级用户：自建 venv 后把 python.exe 路径填进「设置 → Python」即可（应用留空时会自动按 runtime → engine/.venv → 系统 Python 的顺序探测）。源码开发见下「从源码构建」。

## 使用说明

- **转写文本**：点「转写文本」输出纯文本 TXT（视频同名 `.txt`）——**逐段自动检测语言**，中文叙述里的英文词和成段英文都原样转出，语言块之间空行分段；不做时间轴、不加载对齐模型，比字幕更快。热词/模型/精度设置同样生效；双语设置在此模式下忽略
- **懂你意思（AI 修正字幕）**：展开「懂你意思」面板勾选启用，填入 [DeepSeek](https://platform.deepseek.com) API Key 和这个视频的**文案**（模型下拉自动从官方接口拉取在售模型，拉取失败用缓存/内置清单）（提纲/概述/逐字稿都行），之后每次「生成字幕」会自动用大模型对照文案纠错一遍，产出 `视频名.ai.srt`（**原 SRT 不动**）和**改动清单** `视频名.ai.改动清单.txt`（每条 原文/新文 对照 + 【?】存疑索引）。听错写错、同音错字、明显不通顺处会被修正；没把握或与文案对不上的地方不改原文、只加「【?】」标记供你复核。时间轴**永远不会被 AI 改动**（引擎逐条机械校验）。修正一次的 API 费用大约几分钱（模型默认 deepseek-flash）；断网或 Key 无效会告警并正常交付原字幕
- **热词**：专有名词表，注入识别提示纠正同音错字，如 `修脚弹 ST弹 腰射 开镜 后坐力`。**空格、逗号、换行分隔均可**。可按领域建多份热词组（如「三角洲行动」「电脑装机」），生成时用下拉选用其一，编辑自动保存
- **双语**：语言选「中英双语」→ SRT 每条上行中文、下行英文。英文行由第二遍 translate 转写，按重叠时长匹配回中文 cue，**复用中文时间轴**（不单独对齐）
- **双语模型限制**：**large-v3-turbo 不支持 zh→en 翻译**（实测输出多为中文回写），双语请切 `large-v3`；双语+turbo 时应用会输出 WARN 日志
- **断句**：设置里「单条最多字数」（默认 18，B站单行安全宽度）与「尾部并入字数」（默认 4，防孤字尾）可调；断句本身全自动，优先在标点和语气停顿处切分，数字/英文词内部不会切断
- **精度**：显存吃紧（<6GB 可用）选 int8_float16
- **循环幻觉自动修复**：片头音乐/回声偶尔会让识别器陷入重复词循环（整段只有同一个词来回念）。应用会自动检出并用无热词模式局部重转写修复（日志可搜「循环幻觉」）；修复段内的专有名词纠错交给懂你意思收补即可
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

源码运行不使用 `runtime/`，自动走 `engine/.venv` 或系统 Python，开发流与以往一致（测试还需 `requirements-dev.txt`：pytest、edge-tts）。

## 致谢

- [WhisperX](https://github.com/m-bain/whisperX)、[faster-whisper](https://github.com/SYSTRAN/faster-whisper)、[Whisper](https://github.com/openai/whisper)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)、Microsoft .NET / WPF

## 协议

[MIT](LICENSE)

> ⚠️ 仓库内 `engine/ffmpeg.exe` 为 FFmpeg 项目的第三方二进制，版权归 FFmpeg 开发者，按其自身（L）GPL 许可分发，不随本项目 MIT 授权。
