"""stdout JSON Lines 事件发射器 —— 与 C# EngineProtocol 的唯一契约（spec §5.2）。

stdout 重定向下 Windows 默认编码是 cp936，必须重配为 UTF-8，
否则中文事件文本会在 C# 端乱码。
"""
import json
import sys

if sys.stdout and sys.stdout.encoding and sys.stdout.encoding.lower() not in ("utf-8", "utf8"):
    sys.stdout.reconfigure(encoding="utf-8")
if sys.stderr and sys.stderr.encoding and sys.stderr.encoding.lower() not in ("utf-8", "utf8"):
    sys.stderr.reconfigure(encoding="utf-8")

STAGES = ["load_model", "vad", "transcribe", "align", "translate", "write", "llm_fix"]
ERROR_CODES = ["oom", "no_cuda", "hf_download", "pyannote_auth", "generic"]


def emit(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def emit_stage(stage: str) -> None:
    if stage not in STAGES:
        raise ValueError(f"未知 stage: {stage}")
    emit({"type": "stage", "value": stage})


def emit_progress(value: float) -> None:
    emit({"type": "progress", "value": round(min(1.0, max(0.0, value)), 4)})


def emit_log(message: str, level: str = "info") -> None:
    emit({"type": "log", "level": level, "message": message})


def emit_done(srt_path: str, segments: int, video_sec: float, elapsed_sec: float) -> None:
    emit({"type": "done", "srt_path": srt_path, "segments": segments,
          "video_sec": round(video_sec, 2), "elapsed_sec": round(elapsed_sec, 2)})


def emit_error(code: str, message: str, hint: str = "") -> None:
    emit({"type": "error", "code": code, "message": message, "hint": hint})
