# SubtitlesFree — 开发文档

MP4 → SRT 字幕生成。WPF (.NET 8) GUI + Python/WhisperX 引擎（子进程 + stdout JSON Lines）。

## 常用命令

- 运行：`dotnet run --project src/SubtitlesFree.App`
- C# 测试：`dotnet test tests/SubtitlesFree.Core.Tests`
- Python 测试：`cd engine && .venv/Scripts/python -m pytest tests -q`（必须 -m pytest，见下）
- 引擎冒烟：`cd engine && .venv/Scripts/python engine.py --selftest`
- 打包：`./pack.sh`

## 架构

- `src/SubtitlesFree.Core`：协议解析（EngineProtocol）、设置（SettingsService）、子进程服务（EngineLauncher）、环境探测（EnvironmentChecker）。**纯逻辑全在这层，App 层不做逻辑**。
- **ffmpeg 内置**：`engine/ffmpeg.exe`（50MB 二进制，**有意入库**，别加 gitignore）。引擎只认 PATH 上的裸名 ffmpeg，由 `EngineLauncher` 启动子进程时把内置所在目录前置到 PATH（`EnvironmentChecker.FindBundledFfmpeg`：发布布局 exe 旁 → EngineRoots 各根的 `engine/ffmpeg.exe`）。
- `src/SubtitlesFree.App`（WPF）：Views + ViewModels + AppServices 组合根。
- `engine/`：Python 引擎。stdout JSON Lines 事件（stage/progress/log/done/error）是 C#/Python 唯一契约；改动协议两端同步改。
- **词级断句**（`engine/segmentation.py`，纯函数）：align 已产出单字级时间戳但 segment 是 20~30s 长段，写 SRT 前重切为可读短条。边界优先级：segment **文本标点映射回原子**（对齐会剥掉标点；句号必切、逗号切分、`1,25` 数字内逗号忽略）> 声学 gap ≥0.35s > 18 字/6s 上限回退切。数字/字母串（125、MK4、5GST）内部不切；尾部 ≤4 字吸收避免孤字尾；条内无标点、短语间空格（B站惯例）。阈值按 0909mk4 人工字幕对照校准，改前先跑该对照。**单条最多字数/尾部并入字数已开放 GUI 设置**（`--max-chars`/`--absorb-chars` 透传，两端钳 6..40 / 0..10，引擎侧另限吸收 ≤ 上限 1/3），其余阈值是内部常量。
- 模型解析（**两端同语义，改动须同步**）：候选根**逐级上溯**（`EnvironmentChecker.EngineRoots` ↔ `pipeline._models_roots`：自运行目录向上找所有含 `engine/` 的应用根，近者优先——dist-in-repo 也能找到仓库 `engine/models`）。根内 `<model>` 同名目录优先，仅 turbo 兼容遗留 `engine/models/asr`，否则 HF 在线下载（`EnvironmentChecker.LocalAsrCandidates` ↔ `pipeline._local_asr_dir`）。HF hub 缓存在 `%USERPROFILE%\.cache\huggingface\hub`；镜像 `HF_ENDPOINT=hf-mirror.com`；全被墙时按 README 用 ModelScope 预下载到 `engine/models/<model>` 与 `engine/models/align`。
- 测试：Core 层 xUnit（假引擎 fake_engine.py 走真子进程）；引擎纯函数 pytest。
- Python 测试用 `python -m pytest`（cwd 进 sys.path，tests/ 才能导入 engine/ 模块）。

## 约定

- UI 文案中文；提交 `feat:`/`fix:`/`chore:` + 中文摘要
- 引擎 stdout 只出 JSON Lines；C# 端对坏行容错（归日志不崩）
- torch 装 CUDA 轮子（`--index-url .../cu124`），不要 PyPI 默认
- 产品事实：turbo 不支持 zh→en 翻译，双语须选 large-v3（双语+turbo 引擎会 WARN）
- `docs/`、`engine/.venv/`、`engine/samples/`、`engine/models/`、`dist/`、`probe/`、`.superpowers/` 不入库
