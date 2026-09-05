"""SRT 序列化：Cue 列表 → .srt 文本/文件（utf-8-sig，Windows 播放器兼容）。"""
from pathlib import Path

from bilingual import Cue


def format_ts(sec: float) -> str:
    if sec < 0:
        sec = 0
    ms = int(round(sec * 1000))
    h, rem = divmod(ms, 3_600_000)
    m, rem = divmod(rem, 60_000)
    s, ms2 = divmod(rem, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms2:03d}"


def cue_text(cue: Cue) -> str:
    return cue.text_zh + (f"\n{cue.text_en}" if cue.text_en else "")


def render_srt(cues: list[Cue]) -> str:
    blocks = [f"{i}\n{format_ts(cue.start)} --> {format_ts(cue.end)}\n{cue_text(cue)}"
              for i, cue in enumerate(cues, start=1)]
    return "\n\n".join(blocks) + "\n"


def write_srt(cues: list[Cue], path: str) -> None:
    Path(path).write_text(render_srt(cues), encoding="utf-8-sig")
