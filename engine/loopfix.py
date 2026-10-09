"""循环幻觉修复：whisper 批量解码无 temperature fallback/压缩比守卫，热词 prompt
+ 音乐/回声段会把解码器推进短 token 重复循环（1009AK12「智耀」型，同 prompt 必现）。
本模块在转写后检出循环 segment，对相应区间去掉热词（只留引导句）局部重解码并拼接。
纯函数 + decode 回调 DI；torch/whisperx 只允许出现在 pipeline 注入的回调里。
"""
import re

# 阈值（宁松勿紧：误伤=多一次局部重解码且采纳自校验兜底；漏检=循环进 SRT）
REPEAT_MIN = 6        # 同一 token 连续出现 ≥ 此次数判循环
TOKEN_MAX_CHARS = 4   # 循环 token 字长上限（「智耀」2 字；1 字 token 不参与防误伤口语）
COVER_MIN = 0.6       # 无空格循环：重复块对清洗后文本的覆盖率
PAD = 1.0             # 重解码窗口前后余量（秒）；切片实测不留会吃掉首词

_PUNCT = r"[\s　，。？！、：；“”‘’…,.!?;:~\-—（）()【】\[\]「」]+"


def _tokens(text: str) -> list[str]:
    return [t for t in re.split(r"[\s　]+", text) if t]


def loop_run(text: str) -> int:
    """最长「连续相同 token」次数（仅统计 2~TOKEN_MAX_CHARS 字的 token；
    1 字 token 不参与——「对/啊」类口语重复保守放行）。"""
    best = run = 0
    prev: str | None = None
    for t in _tokens(text):
        run = run + 1 if t == prev else 1
        prev = t
        if 2 <= len(t) <= TOKEN_MAX_CHARS:
            best = max(best, run)
    return best


def _unspaced_loop(text: str) -> bool:
    """无空格连写循环：清洗标点空格后，某 2~4 字块重复 ≥ REPEAT_MIN 且覆盖 ≥ COVER_MIN。"""
    clean = re.sub(_PUNCT, "", text)
    if len(clean) < REPEAT_MIN * 2:
        return False
    for p in range(2, TOKEN_MAX_CHARS + 1):
        counts: dict[str, int] = {}
        for i in range(len(clean) - p + 1):
            counts[clean[i:i + p]] = counts.get(clean[i:i + p], 0) + 1
        block, n = max(counts.items(), key=lambda kv: kv[1])
        if n >= REPEAT_MIN and n * p / len(clean) >= COVER_MIN:
            return True
    return False


def is_loop_text(text: str) -> bool:
    """检出循环文本：空格 token 连续重复（≥REPEAT_MIN、字长 ≤TOKEN_MAX_CHARS），
    或无空格块状重复。1 字 token 不参与 token 判据（「对 对 对」类口语保守放行）。"""
    if loop_run(text) >= REPEAT_MIN:
        return True
    return _unspaced_loop(text)


def find_looped(segments: list[dict]) -> list[dict]:
    """标记为循环的原始 segment 子集（保持原顺序）。"""
    return [s for s in segments if is_loop_text(s.get("text", ""))]


def repair_windows(flagged: list[dict], audio_dur: float | None) -> list[tuple[float, float]]:
    """循环 segment → 重解码窗口（前后各 PAD，相邻窗口重叠则合并）。"""
    wins: list[list[float]] = []
    for s in flagged:
        a = max(0.0, s["start"] - PAD)
        b = s["end"] + PAD if audio_dur is None else min(s["end"] + PAD, audio_dur)
        if wins and a <= wins[-1][1]:
            wins[-1][1] = max(wins[-1][1], b)
        else:
            wins.append([a, b])
    return [(a, b) for a, b in wins]


def _overlap_ratio(a: dict, b: dict) -> float:
    """重叠时长 / 较短者时长。"""
    inter = min(a["end"], b["end"]) - max(a["start"], b["start"])
    if inter <= 0:
        return 0.0
    return inter / max(1e-9, min(a["end"] - a["start"], b["end"] - b["start"]))


def splice(segments: list[dict],
           repairs_by_window: list[tuple[tuple[float, float], list[dict]]]) -> tuple[list[dict], list[dict]]:
    """按窗口采纳修复段：重解码仍循环或与正常邻段重叠过半（padding 压到邻段）的
    修复段丢弃——什么都采纳不了就整窗保留原文（自校验，永不劣化）。
    返回 (新 segments 按 start 排序, 修复报告 action=repaired|kept)。"""
    flagged = find_looped(segments)
    healthy = [s for s in segments if not is_loop_text(s.get("text", ""))]
    out: list[dict] = []
    report: list[dict] = []
    replaced_ids: set[int] = set()
    for win, repairs in repairs_by_window:
        mine = [s for s in flagged if win[0] - 1e-6 <= s["start"] < win[1]]
        span = (mine[0]["start"], mine[0]["end"]) if mine else (win[0], win[1])
        joined = "".join(r["text"] for r in repairs)
        adopted = [
            dict(r) for r in repairs
            if repairs and not is_loop_text(joined)
            and not any(_overlap_ratio(r, h) > 0.5 for h in healthy)
        ]
        if adopted:
            replaced_ids.update(id(s) for s in mine)
            out.extend(adopted)
        report.append({"start": span[0], "end": span[1],
                       "action": "repaired" if adopted else "kept"})
    result = [s for s in segments if id(s) not in replaced_ids] + out
    result.sort(key=lambda s: s["start"])
    return result, report
