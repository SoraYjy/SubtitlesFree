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

## 环境要求

- Windows 10/11 x64
- **NVIDIA 显卡（显存 ≥ 8GB 推荐）** + 最新驱动（游戏玩家一般都已就绪）
- **Python 3.10~3.12**（推荐 3.11，安装方法见下）
- FFmpeg：**已内置**，无需安装
- ~10GB 磁盘（依赖 + 模型）

## 快速开始

以下三步均为一次性操作。命令都在**同一个黑窗口**里粘贴执行，每条粘贴后回车，等它跑完再贴下一条。

### ① 安装 Python（约 5 分钟）

1. 打开 [python.org/downloads](https://www.python.org/downloads/)，下载 **Windows installer (64-bit)**（3.10~3.12 都行，页面默认版本即可）
2. 运行安装程序，**务必勾选最底部的 `Add python.exe to PATH`**，再点 Install Now——这个勾漏掉是绝大多数环境问题的根源
3. 验证：按 `Win` 键 → 输入 `cmd` 回车打开命令提示符 → 输入 `python --version`，显示 `Python 3.x.x` 即成功

### ② 安装引擎依赖（约 10 分钟，视网速）

**怎么让黑窗口「就在正确目录」**：文件资源管理器进入发布包解压目录（源码用户则进仓库根目录），在上方**地址栏**输入 `cmd` 回车——弹出的黑窗口就已经在这个目录里，下面所有命令原样粘贴（右键粘贴）即可：

```bash
pip install -U pip
pip install torch --index-url https://download.pytorch.org/whl/cu124
pip install -r engine\requirements.txt
```

> ⚠️ 第二条**必须原样复制**（含 `--index-url` 部分）：不加它装到的是 CPU 版，能跑但慢约 50 倍，且界面没有任何提示。
>
> `requirements.txt` 已 pin 全部关键版本（含 cuDNN 组件 `nvidia-cudnn-cu12`，缺它 CUDA 初始化会直接崩溃），**不要**手动挑选着装。版本细节与踩坑记录见 [engine/SETUP.md](engine/SETUP.md)。

### ③ 模型（约 2.9GB）

- **能访问 [huggingface.co](https://huggingface.co)（或勾选设置里的镜像后能访问 hf-mirror.com）：跳过本步**，首次生成字幕时自动下载，设置里默认已勾「镜像国内加速」
- **都不通（国内常见）**：继续在黑窗口粘贴（仍在解压目录/仓库根目录，路径不用改一个字；实测约 117MB/s）：

```bash
pip install modelscope
modelscope download --model mobiuslabsgmbh/faster-whisper-large-v3-turbo --local_dir engine\models\large-v3-turbo
modelscope download --model jonatasgrosman/wav2vec2-large-xlsr-53-chinese-zh-cn --local_dir engine\models\align
```

将来在应用里切 large-v3 / small 模型时，同理换 repo 名再下载到 `engine\models\<模型名>`：

```bash
modelscope download --model Systran/faster-whisper-large-v3 --local_dir engine\models\large-v3
modelscope download --model Systran/faster-whisper-small --local_dir engine\models\small
```

### ④ 首次运行

1. 发布包：双击 `SubtitlesFree.exe`（源码：`dotnet run --project src/SubtitlesFree.App`，或 [打包](#从源码构建)）
2. 点「检测环境」——五项全绿即就绪（Python / torch+CUDA / whisperx / 模型缓存 / ffmpeg）
3. 拖入 MP4 → 「生成字幕」

任何一项红叉时，状态条会直接给出该项的修复提示，照做后重新检测即可；详情同时打在日志面板里。

<details>
<summary><b>进阶：venv（虚拟环境）——大多数用户不需要</b></summary>

上面的步骤把依赖装进了**全局 Python**，对只用这台电脑跑本工具的人最简单。venv 的作用是让多个 Python 项目的依赖互不干扰——**如果你这台机器还有其它依赖特定 torch/numpy 版本的 Python 项目**（如 ComfyUI、GPT-SoVITS 等用 pip 装过东西的项目），为避免版本被覆盖，建议改用隔离安装：

```bash
cd engine
python -m venv .venv
.venv\Scripts\pip install -U pip
.venv\Scripts\pip install torch --index-url https://download.pytorch.org/whl/cu124
.venv\Scripts\pip install -r requirements.txt
```

装完后在应用「设置 → Python」里填 `engine\.venv\Scripts\python.exe`（不填的话应用也会自动优先探测它）。开发测试还需 `requirements-dev.txt`（pytest、edge-tts）。

</details>

## 使用说明

- **热词**：专有名词表，注入识别提示纠正同音错字，如 `修脚弹 ST弹 腰射 开镜 后坐力`。**空格、逗号、换行分隔均可**。可按领域建多份热词组（如「三角洲行动」「电脑装机」），生成时用下拉选用其一，编辑自动保存
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

> ⚠️ 仓库内 `engine/ffmpeg.exe` 为 FFmpeg 项目的第三方二进制，版权归 FFmpeg 开发者，按其自身（L）GPL 许可分发，不随本项目 MIT 授权。
