"""转写管道：load_model → vad/transcribe → align → translate(可选) → write。

事件契约 spec §5.2；进度为阶段粒度尽力而为。
技术路径为 SETUP.md 结论 PATH_A（whisperx 原生 VAD 管道）。

模型解析：ASR 优先 models/<model> 同名目录（turbo 兼容遗留 models/asr），
对齐用 models/align（离线可用，权重从 ModelScope 镜像预先下载，见 SETUP.md）；
候选根逐级上溯（本文件同级 models → 各级应用根的 engine/models，兼容
dist-in-repo），全不存在则回退 --model 参数走 HuggingFace 在线下载（供有网
用户）。路径以 pipeline.py 自身位置解析，与 cwd 无关。
"""
import os
import sys
import sysconfig
import time
from contextlib import contextmanager
from pathlib import Path

import emitter
from bilingual import match_en_to_cues
from emitter import emit_log, emit_progress, emit_stage
from segmentation import segment_cues
from srt import write_srt

PROGRESS = {"load_model": 0.05, "vad": 0.10, "transcribe": 0.60,
            "align": 0.75, "translate": 0.90, "write": 0.95}

_MODELS_DIR = Path(__file__).resolve().parent / "models"


def _models_roots() -> list[Path]:
    """候选 models 根（近者优先）：本文件同级 models，再逐级上溯各「应用根」的 engine/models。

    兼容 dist-in-repo：从 dist/SubtitlesFree/engine 里运行、模型在仓库 engine/models
    时，固定单目录会漏掉仓库模型而误走被墙的 HF 在线下载；逐级上溯即可命中。
    真正分发出去的包（拷到任意位置）仍只认自己旁边的目录，行为不变。
    与 C# EnvironmentChecker.EngineRoots 同语义（改动须两端同步）。
    """
    roots = [_MODELS_DIR]
    roots += [p / "engine" / "models" for p in list(_MODELS_DIR.parents)[:8]]
    return roots


def _local_model_dir(name: str) -> str | None:
    """本地模型目录存在则返回其绝对路径，否则 None（走 HF 在线下载）。"""
    for root in _models_roots():
        d = root / name
        if d.is_dir():
            return str(d)
    return None


def _local_asr_dir(model: str) -> str | None:
    """按所选模型解析本地 ASR 目录（不静默替换成别的模型）。

    turbo 兼容 T5 遗留约定 models/asr；其余模型只认 models/{model} 同名目录，
    不存在则返回 None 走 HF 在线下载——否则「选 large-v3 实际跑 turbo」会让
    双语翻译（turbo 无 zh→en 翻译能力，T10 实测）静默失效。
    所选模型先扫全部候选根，再扫 asr（不跨模型顶替的语义跨根保持）。
    """
    names = [model] + (["asr"] if model == "large-v3-turbo" else [])
    for name in names:
        d = _local_model_dir(name)
        if d:
            return d
    return None


@contextmanager
def _stdout_to_stderr():
    """把进程级 stdout（fd 1）暂时接到 stderr，吞掉第三方库的 stdout 噪声。

    whisperx/pyannote 会往 stdout print 调试信息（"No language specified..."、
    ">>Performing voice activity detection..."、"Model was trained with..."），
    而 stdout 是与 C# 宿主的 JSON Lines 唯一契约（spec §5.2），一行都不能污染。
    用 fd 级 dup2（而非 redirect_stdout）才能同时拦截 Python print 的缓冲写、
    logging handler 直接持有的 stdout 对象、以及 C 层写。
    """
    sys.stdout.flush()
    saved_fd = os.dup(1)
    try:
        os.dup2(2, 1)
        yield
    finally:
        sys.stdout.flush()  # 正常/异常路径都把窗口内缓冲的第三方 print 冲进 stderr（此时 fd1 仍指向 stderr）
        os.dup2(saved_fd, 1)
        os.close(saved_fd)


def _ensure_cudnn_on_path() -> None:
    """把 venv 内的 cuDNN 8 DLL 目录加入 PATH（SETUP.md 已知问题 2，必须）。

    ctranslate2 4.4 的 Windows wheel 只带 cudnn64_8.dll，缺
    cudnn_ops_infer64_8.dll 等子组件；nvidia-cudnn-cu12 装在
    site-packages/nvidia/cudnn/bin，torch 仅在 Linux 上自动加入库搜索
    路径，Windows 下须自行追加，否则 CUDA 初始化直接进程崩溃(0xC0000135)。
    目录按 sysconfig 解析（跟随本 venv），非硬编码机器路径。
    """
    cudnn_bin = Path(sysconfig.get_paths()["purelib"]) / "nvidia" / "cudnn" / "bin"
    if cudnn_bin.is_dir():
        os.environ["PATH"] = str(cudnn_bin) + os.pathsep + os.environ.get("PATH", "")


def _install_weights_only_shim() -> None:
    """torch 2.6 + pyannote VAD checkpoint 兼容 shim（SETUP.md 已知问题 1，必须）。

    torch 2.6 起 torch.load 默认 weights_only=True，whisperx 自带的 pyannote VAD
    checkpoint 内含 omegaconf/pyannote 对象，UnpicklingError；须在加载前把这
    14 个类型加入 safe-globals 白名单（与 probe/spike.py 实测清单一致）。
    """
    import builtins

    import torch.serialization
    from collections import defaultdict
    from typing import Any

    from omegaconf.base import ContainerMetadata, Metadata
    from omegaconf.listconfig import ListConfig
    from omegaconf.nodes import AnyNode
    from pyannote.audio.core.model import Introspection
    from pyannote.audio.core.task import Problem, Resolution, Specifications
    from torch.torch_version import TorchVersion

    torch.serialization.add_safe_globals([
        ListConfig, AnyNode, ContainerMetadata, Metadata, Introspection,
        Specifications, Problem, Resolution, TorchVersion, Any, defaultdict,
        builtins.list, builtins.dict, builtins.int,
    ])


def run_pipeline(args) -> None:
    t0 = time.time()
    hotwords = [w.strip() for w in args.hotwords.replace("，", ",").split(",") if w.strip()]
    initial_prompt = ("以下是可能出现的专有名词：" + "，".join(hotwords) + "。") if hotwords else None
    if args.bilingual and args.model == "large-v3-turbo":
        emit_log("large-v3-turbo 的内置翻译实测多为中文回写，双语建议改选 large-v3 模型", "warn")

    emit_stage("load_model")
    _ensure_cudnn_on_path()  # 须在 ctranslate2 触碰 CUDA DLL 之前
    import torch
    import whisperx
    _install_weights_only_shim()  # 须在 whisperx.load_model 加载 VAD checkpoint 之前
    device = "cuda" if torch.cuda.is_available() else "cpu"
    if device == "cpu":
        emit_log("CUDA 不可用，回退 CPU（速度会慢很多）", "warn")
    asr_dir = _local_asr_dir(args.model)
    if asr_dir:
        emit_log(f"使用本地 ASR 模型（{args.model}）：{asr_dir}")
    with _stdout_to_stderr():
        model = whisperx.load_model(
            asr_dir or args.model, device,
            compute_type=args.compute_type if device == "cuda" else "int8",
            asr_options={"initial_prompt": initial_prompt} if initial_prompt else None,
        )
    emit_progress(PROGRESS["load_model"])

    emit_log(f"提取音频：{args.video}")
    audio = whisperx.load_audio(args.video)  # 硬依赖 PATH 上的 ffmpeg（spec §修订）
    video_sec = len(audio) / 16000.0
    emit_log(f"音频时长 {video_sec:.1f}s")
    emit_stage("vad")  # whisperx.transcribe 内部先跑 pyannote VAD 再批量识别
    emit_stage("transcribe")
    with _stdout_to_stderr():
        result = model.transcribe(audio, batch_size=16, language="zh")
    emit_progress(PROGRESS["transcribe"])

    emit_stage("align")
    align_dir = _local_model_dir("align")
    if align_dir:
        emit_log(f"使用本地对齐模型：{align_dir}")
        with _stdout_to_stderr():
            model_a, meta = whisperx.load_align_model(language_code="zh", device=device,
                                                      model_name=align_dir)
    else:
        with _stdout_to_stderr():
            model_a, meta = whisperx.load_align_model(language_code="zh", device=device)
    with _stdout_to_stderr():
        result = whisperx.align(result["segments"], model_a, meta, audio, device,
                                return_char_alignments=False)
    emit_progress(PROGRESS["align"])

    en_segments: list[dict] = []
    if args.bilingual:
        emit_stage("translate")
        with _stdout_to_stderr():
            result_en = model.transcribe(audio, batch_size=16, language="zh",
                                         task="translate")
        en_segments = result_en["segments"]
        emit_progress(PROGRESS["translate"])

    emit_stage("write")
    out_path = args.output or str(Path(args.video).with_suffix(".srt"))
    cue_src = segment_cues(result["segments"],
                           max_chars=args.max_chars, absorb_chars=args.absorb_chars)
    emit_log(f"断句：{len(result['segments'])} 段 → {len(cue_src)} 条字幕")
    cues = match_en_to_cues(cue_src, en_segments)
    write_srt(cues, out_path)
    emitter.emit_done(out_path, len(cues), video_sec, time.time() - t0)
