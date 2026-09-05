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
- `src/SubtitlesFree.App`（WPF）：Views + ViewModels + AppServices 组合根。
- `engine/`：Python 引擎。stdout JSON Lines 事件（stage/progress/log/done/error）是 C#/Python 唯一契约；改动协议两端同步改。
- 模型解析（**两端同语义，改动须同步**）：本地 `engine/models/<model>` 同名目录优先，仅 turbo 兼容遗留 `engine/models/asr`，否则 HF 在线下载（`EnvironmentChecker.LocalAsrCandidates` ↔ `pipeline._local_asr_dir`）。HF hub 缓存在 `%USERPROFILE%\.cache\huggingface\hub`；镜像 `HF_ENDPOINT=hf-mirror.com`；全被墙时按 README 用 ModelScope 预下载。
- 测试：Core 层 xUnit（假引擎 fake_engine.py 走真子进程）；引擎纯函数 pytest。
- Python 测试用 `python -m pytest`（cwd 进 sys.path，tests/ 才能导入 engine/ 模块）。

## 约定

- UI 文案中文；提交 `feat:`/`fix:`/`chore:` + 中文摘要
- 引擎 stdout 只出 JSON Lines；C# 端对坏行容错（归日志不崩）
- torch 装 CUDA 轮子（`--index-url .../cu124`），不要 PyPI 默认
- 产品事实：turbo 不支持 zh→en 翻译，双语须选 large-v3（双语+turbo 引擎会 WARN）
- `docs/`、`engine/.venv/`、`engine/samples/`、`engine/models/`、`dist/`、`probe/`、`.superpowers/` 不入库
