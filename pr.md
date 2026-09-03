# 视频自动化高精度字幕生成方案设计书

## 一、 方案概述与核心诉求响应

本方案旨在针对 MP4 视频文件，高效率、高精度地生成符合标准的 SRT 字幕文件。

针对“识别精准度要求极高”**与**“特定词汇干预”的核心诉求，方案的技术逻辑如下：

1. **解决语音识别错漏：** 选用开源界识别率天花板的 **Whisper `large-v3` / `large-v3-turbo**` 大模型做基础文本提取。
2. **解决专有名词/行业黑话错字：** 引入 **`initial_prompt` (前置上下文干预机制)**，在推理前注入用户预定义的“专业词汇表”，纠正同音字错别字。
3. **解决字幕时间轴漂移/断句卡点不准：** 放弃原生 Whisper 的粗粒度时间戳，采用 **WhisperX 架构**，引入 **VAD（语音端点检测）** 预切分 + **Wav2Vec2（音素级强制对齐）**，将时间轴精度提升至毫秒级。

---

## 二、 技术选型与对比

### 1. 主流开源语音识别技术对比

| 评估维度 | 原生 OpenAI Whisper | Faster-Whisper | SenseVoice Small | **WhisperX（本方案选定）** |
| --- | --- | --- | --- | --- |
| **识别准确率** | 极高（Large-v3） | 极高（与 Whisper 一致） | 中文高 / 多语言一般 | **极高（结合 VAD 过滤幻觉）** |
| **时间轴对齐精度** | 中等（词级/句级） | 中等 | 一般 | **极高（音素级卡点对齐）** |
| **专有名词/热词干预** | 支持 (`initial_prompt`) | 支持 (`initial_prompt`) | 支持 (Hotwords 权重) | **支持 (`initial_prompt`)** |
| **处理速度 (GPU)** | 基准 1x | 约 4x | 约 15x (极快) | **约 3x - 5x (含对齐耗时)** |
| **抗噪音/幻觉能力** | 一般（易重复输出） | 一般 | 较好 | **极强（VAD 切除无声段）** |

### 2. 最终选型与理由

**选型：WhisperX + `large-v3-turbo` / `large-v3**`

* **选择理由：**
1. **最高精度保障：** WhisperX 在 Faster-Whisper 的基础上扩展了 Wav2Vec2 强制对齐模型。它能先通过 VAD（如 PyAnnote）去除背景音乐和无声噪音，防止模型产生无中生有的“幻觉”文本；识别完成后，再对文本和声音波形进行逐音素比对，生成的 SRT 时间轴极为精准。
2. **兼顾速度与资源：** 默认推荐模型选用 `large-v3-turbo`，在保持与 `large-v3` 98% 以上相同精度的同时，推理速度提升近 4 倍，显存占用显著降低。
3. **完美的词汇干预接口：** 支持在推理命令中动态传入词库列表，满足特定领域（如 AI 技术、医疗、法律、游戏）的精准识别。



### 3. 系统配置需求

#### 硬件配置建议

* **最低配置（CPU 可跑/速度慢）：** Intel i5 10代以上 / AMD R5，16GB 内存，无独显（仅能跑 `small` 模型）。
* **推荐配置（主流 GPU 加速）：**
* **GPU：** NVIDIA 显卡，**显存 $\ge$ 8GB**（如 RTX 3060 / 4060 / 2060 Super）。
* **内存：** 16GB RAM。
* **存储：** 20GB 以上 SSD 空余空间（用于存放模型与临时音频数据）。


* **极速/高并发配置：** NVIDIA RTX 3090 / 4090 (24GB 显存)，全精度并发处理。

#### 软件依赖环境

* **操作系统：** Windows 10/11 64位 或 Linux (Ubuntu 22.04)
* **核心环境：** Python 3.10+、CUDA 11.8 / 12.1、PyTorch (GPU 版)、FFmpeg（系统环境变量中需包含）

---

## 三、 三种实现方案落地指南

### 方案 1：Python 自动化脚本实现 (后台/批处理首选)

使用 Python 直接调用 `whisperx` 库，处理从视频输入、词库注入到生成 SRT 的完整管道。

#### (1) 环境准备

```bash
# 安装 FFmpeg (需确保配置到了系统 PATH 环境变量)
# 安装核心依赖
pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu118
pip install whisperx

```

#### (2) 核心代码实现 (`video_to_srt.py`)

```python
import os
import whisperx

def generate_srt(video_path: str, output_srt_path: str, custom_words: list[str], device="cuda", batch_size=16):
    # 1. 准备专业词汇干预 Prompt
    initial_prompt = "以下是可能出现的专有名词：" + "，".join(custom_words) + "。"
    
    # 2. 加载 WhisperX 模型 (使用 large-v3-turbo)
    print("正在加载 Whisper 模型...")
    model = whisperx.load_model(
        "large-v3-turbo", 
        device=device, 
        compute_type="float16", # 若显存受限可改为 "int8"
        language="zh", 
        asr_options={"initial_prompt": initial_prompt}
    )
    
    # 3. 加载音频并转写
    print("正在提取音频并进行 ASR 识别...")
    audio = whisperx.load_audio(video_path)
    result = model.transcribe(audio, batch_size=batch_size)
    
    # 4. 加载音素对齐模型 (Align Model) 实现毫秒级卡点
    print("正在进行 Wav2Vec2 音素级强制对齐...")
    model_a, metadata = whisperx.load_align_model(
        language_code=result["language"], 
        device=device
    )
    result = whisperx.align(
        result["segments"], 
        model_a, 
        metadata, 
        audio, 
        device, 
        return_char_alignments=False
    )
    
    # 5. 格式化并输出为 SRT 文件
    print(f"正在保存 SRT 文件至: {output_srt_path}")
    writer = whisperx.utils.get_writer("srt", os.path.dirname(output_srt_path))
    writer(
        result, 
        os.path.basename(video_path), 
        {"max_line_width": None, "max_line_count": None, "highlight_words": False}
    )
    
    # 重命名生成的 srt 文件到指定路径
    default_srt = os.path.splitext(video_path)[0] + ".srt"
    if os.path.exists(default_srt) and default_srt != output_srt_path:
        os.rename(default_srt, output_srt_path)
    print("字幕生成完成！")

if __name__ == "__main__":
    # 配置你的常用词汇干预表
    MY_HOTWORDS = ["ComfyUI", "SDXL", "ControlNet", "LoRA", "WhisperX", "大语言模型"]
    
    generate_srt(
        video_path="input_video.mp4", 
        output_srt_path="output_subtitles.srt", 
        custom_words=MY_HOTWORDS
    )

```

---

### 方案 2：.NET (C#) Windows 桌面应用实现

在 Windows 环境下构建 C# Client（WPF 或 WinForms），实现 GUI 界面操作。推荐采用“架构分离”设计模式：C# 负责界面、任务队列和 SRT 文本微调；后台通过 Process 调用已打包好的 Python/WhisperX 执行引擎，或建立 HTTP/gRPC 本地微服务通信。

#### 体系架构图

`[C# GUI 界面]` $\rightarrow$ `(参数/视频路径/词表)` $\rightarrow$ `[后台 Process / CLI 执行]` $\rightarrow$ `[读取/编辑 SRT]`

#### C# 核心调用代码 (`SrtGeneratorService.cs`)

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

public class SrtGeneratorService
{
    private readonly string _pythonExecutablePath; // python.exe 路径
    private readonly string _scriptPath;           // 上述 python 脚本路径

    public SrtGeneratorService(string pythonPath, string scriptPath)
    {
        _pythonExecutablePath = pythonPath;
        _scriptPath = scriptPath;
    }

    public async Task<bool> GenerateSrtAsync(string videoPath, string outputSrtPath, string[] hotwords, Action<string> logHandler)
    {
        if (!File.Exists(videoPath)) throw new FileNotFoundException("未找到视频文件", videoPath);

        // 拼接热词参数
        string hotwordsArg = string.Join(",", hotwords);
        
        // 构建 Process 启动参数
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = _pythonExecutablePath,
            Arguments = $"\"{_scriptPath}\" --input \"{videoPath}\" --output \"{outputSrtPath}\" --words \"{hotwordsArg}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using (Process process = new Process { StartInfo = startInfo })
        {
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) logHandler?.Invoke(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) logHandler?.Invoke($"[ERROR] {e.Data}"); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
    }
}

```

> **注：** 如果坚持追求纯 C# 原生运行（无 Python 依赖），可以使用开源库 `Whisper.net`（封装了 `whisper.cpp` 的 C# 绑定），但该方式目前难以接入 Wav2Vec2 音素强制对齐模型，时间轴准确度会略低于 Python/WhisperX 方案。

---

### 方案 3：ComfyUI 工作流实现

对于已经在生产环境使用 ComfyUI 的用户，可以通过安装社区节点插件，将“视频输入 $\rightarrow$ 语音提取 $\rightarrow$ 字幕生成 $\rightarrow$ 渲染压制”整合为可视化图表。

#### (1) 前置插件安装

在 ComfyUI 的 `custom_nodes` 目录下安装以下节点插件：

1. **`ComfyUI-WhisperX` (或 AIFSH 版本的 ComfyUI-WhisperX)**：负责调用 WhisperX 模型。
2. **`ComfyUI-VideoHelperSuite` (VHS)**：负责 MP4 视频的读取、音频分离与最终编码保存。

#### (2) 工作流搭建节点连线步骤

```
[VHS Load Video] 节点
  │
  ├─► (IMAGE 端口) ────────────────────────────────────────────────┐
  │                                                                │
  └─► (AUDIO 端口) ────────┐                                       ▼
                           ▼                            [Add Subtitle To Video] 节点
                   [WhisperX Node]                        (可选：压制字幕到画面上)
                     ├─ Model: large-v3-turbo                    │
                     ├─ Language: zh                             │
                     ├─ Prompt: "ComfyUI,SDXL,节点,工作流"         │
                     │                                           ▼
                     ├─► (SRT_FILE_PATH 端口) ─────────────► [VHS Video Combine]
                     │                                     (输出带字幕的 MP4 视频)
                     └─► [Save SRT Text] 节点
                           (输出单独的 .srt 文件至 /output 目录)

```

#### (3) 核心节点参数设置

* **`VHS Load Video`**：传入目标 MP4 视频，确保 `Force Rate` 或音频提取开启。
* **`WhisperX Node`**：
* `model_name`: 设置为 `large-v3-turbo`。
* `language`: 强制指定为 `zh`。
* `initial_prompt`: 填入专有名词文本（如 `"ComfyUI, SDXL, ControlNet, LoRA"`）。
* `align_output`: 勾选 `True`（开启音素级强对齐）。


* **`Save SRT` 节点**：设置保存文件名格式（如 `%date%_subtitles.srt`），节点会自动将格式化好的时间戳与文本写入指定的 Output 目录。