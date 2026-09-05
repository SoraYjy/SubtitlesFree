"""中英双 cue 匹配（spec §5.3 双语匹配策略·无重复分配）。

每个英文段唯一分配给「重叠时长/英文段时长」比例最高的中文 cue；
无重叠的英文段分给中心距离最近的 cue；分不到英文段的 cue 只保留中文行。
"""
from dataclasses import dataclass


@dataclass
class Cue:
    start: float
    end: float
    text_zh: str
    text_en: str | None = None


def _overlap(a_start: float, a_end: float, b_start: float, b_end: float) -> float:
    return max(0.0, min(a_end, b_end) - max(a_start, b_start))


def match_en_to_cues(zh_segments: list[dict], en_segments: list[dict]) -> list[Cue]:
    cues = [Cue(s["start"], s["end"], s["text"].strip()) for s in zh_segments]
    if not en_segments:
        return cues

    assignment: dict[int, list[str]] = {}
    for en in en_segments:
        text = en["text"].strip()
        if not text:
            continue
        e_start, e_end = en["start"], en["end"]
        best_i, best_ratio = -1, 0.0
        for i, cue in enumerate(cues):
            ov = _overlap(cue.start, cue.end, e_start, e_end)
            if ov <= 0:
                continue
            ratio = ov / max(1e-9, e_end - e_start)
            if ratio > best_ratio:  # 严格大于：比例打平归先出现的 cue
                best_i, best_ratio = i, ratio
        if best_i < 0:  # 无重叠 → 中心最近的 cue
            center = (e_start + e_end) / 2
            best_i = min(range(len(cues)),
                         key=lambda i: abs((cues[i].start + cues[i].end) / 2 - center))
        assignment.setdefault(best_i, []).append(text)

    for i, texts in assignment.items():
        cues[i].text_en = " ".join(texts)
    return cues
