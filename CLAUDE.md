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
- `engine/`：Python 引擎。stdout JSON Lines 事件（stage/progress/log/done/error）是 C#/Python 唯一契约；改动协议两端同步改。**新增 stage 须同步 `emitter.STAGES` 白名单**（漏加会让引擎跑到该阶段才 ValueError 崩掉，`test_pipeline_never_emits_stage_outside_whitelist` 守护；C# 侧不校验 stage 值）。
- **词级断句**（`engine/segmentation.py`，纯函数）：align 已产出单字级时间戳但 segment 是 20~30s 长段，写 SRT 前重切为可读短条。边界优先级：segment **文本标点映射回原子**（对齐会剥掉标点；句号必切、逗号切分、`1,25` 数字内逗号忽略）> 声学 gap ≥0.35s > 18 字/6s 上限回退切。数字/字母串（125、MK4、5GST）内部不切；尾部 ≤4 字吸收避免孤字尾；条内无标点、短语间空格（B站惯例）。阈值按 0909mk4 人工字幕对照校准，改前先跑该对照。**单条最多字数/尾部并入字数已开放 GUI 设置**（`--max-chars`/`--absorb-chars` 透传，两端钳 6..40 / 0..10，引擎侧另限吸收 ≤ 上限 1/3），其余阈值是内部常量。
- 模型解析（**两端同语义，改动须同步**）：候选根**逐级上溯**（`EnvironmentChecker.EngineRoots` ↔ `pipeline._models_roots`：自运行目录向上找所有含 `engine/` 的应用根，近者优先——dist-in-repo 也能找到仓库 `engine/models`）。根内 `<model>` 同名目录优先，仅 turbo 兼容遗留 `engine/models/asr`，否则 HF 在线下载（`EnvironmentChecker.LocalAsrCandidates` ↔ `pipeline._local_asr_dir`）。HF hub 缓存在 `%USERPROFILE%\.cache\huggingface\hub`；镜像 `HF_ENDPOINT=hf-mirror.com`；全被墙时按 README 用 ModelScope 预下载到 `engine/models/<model>` 与 `engine/models/align`。
- **热词组**（`Core/HotwordLibrary.cs` 纯函数 + VM 层）：按领域多份（`AppSettings.HotwordSets` + `ActiveHotwordSet`），下拉选组（末位「（不使用热词）」哨兵）、编辑框绑定当前组改动即存。不变量：**引擎协议不变**——`ActiveWords()` 把当前组拍平成逗号串经 `--hotwords` 透传，引擎侧无需感知分组；旧 `Hotwords` 单串字段**只读保留**，`SettingsService.Load` 里 `EnsureMigrated` 一次性迁成「默认」组（勿在 UI 再绑定旧字段）；分隔符空格/Tab/全角/逗号/换行混用。
- **txt 转写**（`engine/transcribe_text.py`，`--format srt|txt` 两端协议）：VAD 语音区间逐个检测语言（置信度 <0.6 兜底 zh）→ **相邻同语言区间合并** → 按语言逐段转写 → 块间空行拼接 TXT；跳过对齐/断句/翻译（双语在 txt 下忽略）。关键：分块用 `Binarize` 原始区间、**不走 `merge_chunks`**——后者按 30s 批处理窗合并会把语言切换处停顿抹掉（实测成段英文被 zh 吞掉）。用到 whisperx 内部（`vad_model`/`_vad_params`/`detect_language` 镜像拿置信度），版本 pin 3.4.5，**升版须复查两个 `_default_*` 工厂**。initial_prompt 两模式统一为「以下是普通话的句子。」+ 热词（`build_initial_prompt`，2026-09-30 起用户批准改默认；此前 srt 只注热词，无热词时 zh 无标点 → 断句退化 53 碎条，加引导后 73 条，带热词回归 83 条持平）。initial_prompt 是软偏置非约束：同词同文件有对有错（0926m249 实测 TTK 在表内仍错 TDK ×2），热词只能缓解同音错。whisper 解码伪影 U+FFFD（字节级 BPE 残缺多字节序列，88 分钟实录 4 处）由 `engine/textutil.py` 的 `clean_text` 在两路 intake（`segmentation._sanitize` 清 text+words、`transcribe_blocks` 清 segment text）统一过滤。
- **懂你意思**（`engine/llm_fix.py`，`--llm-fix --llm-key --llm-model --llm-prompt-file --draft-file` 两端协议）：SRT 写盘后调 DeepSeek（OpenAI 兼容 `/chat/completions`，venv 自带 requests 零新依赖）按视频文案修正，产出 `视频名.ai.srt` 不动原文件；done 事件路径随之指向修正版。改动清单 `视频名.ai.改动清单.txt`（`llm_fix.change_report_path`，勿再手拼 suffix——曾产出双 `.ai` 名）。**时间轴安全靠机械校验不靠 prompt**：`merge_corrected` 要求条数一致 + 每条时间戳 format_ts 逐字符相同，任一不符该条回退原文、条数不符整体回退；LLM 失败（网络/429/5xx 重试 2 次退避，401/402 不重试）永不毁掉转写结果——warn 后交付原 SRT。prompt/文案走 %TEMP% 临时文件传路径（`EngineLauncher.PrepareLlmFiles/CleanupLlmFiles`，几千字不进命令行）。修正后 `build_change_report` 本地 diff 两份 SRT 产出 `视频名.ai.改动清单.txt`（原文/新文对照 + 【?】复核索引；0 改动不产出——清单不用模型返回，免费且与实际产物一致）。默认 prompt 在 `Core/LlmFixDefaults.cs`（空设置回退默认；**prompt 内只说明规则、不放举例**；规则 5：名词/数字**整体替换**即使与草稿一致也必须标【?】——草稿可能过时、录制时已口头纠正，仅同音纠字免标；用户原样存着的历版默认 prompt 由 `EnsureMigrated` 清空以跟随新默认，改 Prompt 前旧文本要追加进 `LegacyPrompts`），Key 明文本机 settings.json；「【?】」存疑标注是文案约定，校验只管时间轴、标注原样放行。max_tokens 显式 65536（服务端默认会截断长视频修正）；DeepSeek 模型名会随版本改名——模型下拉由 `Core/DeepSeekClient.ListModels` GET `/models` 实时拉取（勾选启用或点刷新时），缓存进 settings（`LlmModels`/`LlmModelsFetchedAt`），失败回退缓存→`FallbackModels` 兜底（2026-10-01 与接口实测一致）；手选过的模型不在新清单也不丢。当前模型 deepseek-flash / deepseek-v4-pro，1M 上下文整份 SRT 一次装下不分块。txt 模式忽略懂你意思（无时间轴可修，两端都不下发）。步骤条由 `_stages` 列表驱动（BuildSteps/MarkStep 同源，勿再写死下标）。
- **循环幻觉修复**（`engine/loopfix.py`，转写后自动，零协议改动）：热词 prompt+音乐/回声段会把批量解码（whisperx 无 temperature fallback）推进短 token 重复循环（1009AK12「智耀」型：循环词是真实语音「只要」的同音诱饵，同 prompt 必现）。转写后扫描 segment，检出循环（2~4 字 token 连续 ≥6 次、或无空格块重复 ≥6 且覆盖 ≥0.6；1 字 token 放行防误伤口语）→ 该区间前后各加 1s 局部重解码：**CPU int8 副模型、只留引导句不带热词**（热词正是诱因，不回注；失去的专有名词纠错由懂你意思下游收补）。自校验：重解码仍循环则保留原文永不劣化；修复段与正常邻段重叠过半丢弃（padding 防复制邻段内容）。干净视频零副作用（A/B 逐字节一致实测）。txt 路径未接（检测函数已通用）。**测试勿用空格串当热词**——pipeline 只按逗号切热词，空格串会变成单个伪热词、prompt 形状意外改变（1009AK12 诊断的关键教训）。
- 测试：Core 层 xUnit（假引擎 fake_engine.py 走真子进程）；引擎纯函数 pytest。
- Python 测试用 `python -m pytest`（cwd 进 sys.path，tests/ 才能导入 engine/ 模块）。

## 约定

- UI 文案中文；提交 `feat:`/`fix:`/`chore:` + 中文摘要
- 引擎 stdout 只出 JSON Lines；C# 端对坏行容错（归日志不崩）
- torch 装 CUDA 轮子（`--index-url .../cu124`），不要 PyPI 默认
- 产品事实：turbo 不支持 zh→en 翻译，双语须选 large-v3（双语+turbo 引擎会 WARN）
- `docs/`、`engine/.venv/`、`engine/samples/`、`engine/models/`、`dist/`、`probe/`、`.superpowers/` 不入库
