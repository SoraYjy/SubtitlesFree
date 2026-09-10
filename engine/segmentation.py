"""词级断句：把 align 后的段落级 segment（20~30 秒长段）重切成可读短 cue。

输入 whisperx.align 结果的 segments（每段含 words: [{word,start,end}, ...]；
中文为单字级原子、标点不进 words——见 probe 实测），输出同形状的
{"start","end","text"} 列表，供 match_en_to_cues / write_srt 直接消费，
双语英文段按时间重叠照常匹配到切细后的中文条。

切分边界（按优先级）：
1. segment 文本里的标点（对齐时被剥离，此处逐字符映射回原子序列）：
   句末标点（。？！）必切；逗号切分（切出的短语不足 MIN_CHARS 字会自动
   并回相邻条并带空格）；数字间的千分位逗号（如 1,25）不算边界
2. 字间声学 gap ≥ HARD_GAP 视为语句停顿 → 切分；segment 边界必切
3. 超限兜底：单条 > MAX_CHARS 字或 > MAX_DUR 秒时，优先回退到条内
   最大的软间隙（≥ SOFT_GAP）处切；再不行贪心切

其他规则：
- 数字/字母串（125、MK4、5GST）内部任何位置都不切（宁可本条多 1~3 字）
- 剩余 ≤ ABSORB_CHARS 字的尾巴并进当前条，避免孤字尾
- 短语间（逗号/软间隙/拉丁词首空格）加空格；条内不出标点（B站惯例）
- 条尾轻微延长不瞬灭、不越过下一条起点
"""
import re
from dataclasses import dataclass, replace

HARD_GAP = 0.35     # ≥ 此字间隔视为语句停顿（实测常规字间隔 0~0.25s）
SOFT_GAP = 0.28     # ≥ 此字间隔视为短语边界：加空格、可作回退切点
MAX_CHARS = 18      # 单条字数上限（不含空格；人工字幕样本上限 20，留吸收余量）
MAX_DUR = 6.0       # 单条时长上限（秒；人工样本最长 6.1s 密集条）
MIN_CHARS = 4       # 少于此字数视为碎片，尝试并入相邻条
ABSORB_CHARS = 4    # 超限切分时剩余 ≤ 此字数的尾巴并进当前条（孤字尾/词组尾规避，上限 18+4=22 与人工样本极值一致）
DUR_SLACK = 2.0     # 吸收/合并允许的时长松量（秒）
END_PAD = 0.15      # 条尾轻微延长，字幕不瞬灭；不越过下一条起点

_SENTENCE_END = set("。！？!?…")
_COMMA = set("，、,;；")

# 条内不保留的标点（句读符全去，B站字幕惯例；数字/技术符号不动）
_PUNCT_RE = re.compile(r"[，。！？；：、·…—,.!?;:\"'“”‘’()（）\[\]【】《》]+")
# 兜底路径（无词级时间戳）里逗号降级为空格而非直接删，保留短语结构
_FALLBACK_COMMA_RE = re.compile(r"[，、,;；]")


@dataclass
class _Atom:
    """一个不可再分的显示单元：单字（中文）或词片（拉丁/数字）。"""
    text: str
    start: float
    end: float
    space_before: bool = False       # 与前一原子之间应有空格
    break_after: str | None = None   # 文本标点断句："sentence" / "comma"


def segment_cues(segments: list[dict], max_chars: int = MAX_CHARS,
                absorb_chars: int = ABSORB_CHARS) -> list[dict]:
    """segments（align 后，含 words）→ 短句 cue 列表 [{"start","end","text"}]。

    max_chars：单条字数上限（不含空格），GUI 可配（--max-chars 透传），钳到 [6, 40]；
    absorb_chars：断句后剩余 ≤ 此字数并入前一条避免孤字尾（0=关闭），钳到 [0, 10]。
    其余阈值（停顿 gap、时长上限等）为算法内部常量，按人工字幕对照校准，不对外暴露。
    """
    max_chars = min(max(6, max_chars), 40)  # 与 C# 侧 Math.Clamp(6, 40) 同区间
    # 吸收量另受上限 1/3 约束：吸收阈值 ≥ 字数上限会级联吸收整段，上限形同虚设
    absorb_chars = min(max(0, absorb_chars), 10, max_chars // 3)
    groups: list[list[_Atom]] = []
    for phrase in _phrases(segments):
        groups.extend(_phrase_to_cues(phrase, max_chars, absorb_chars))
    groups = _merge_fragments(groups, max_chars, absorb_chars)
    return _format(groups)


# ---------- 原子构建与文本标点映射 ----------

def _clean(raw: str) -> str:
    return _PUNCT_RE.sub("", re.sub(r"\s+", "", raw))


def _clean_fallback(text: str) -> str:
    """无词级时间戳的整段文本：句末标点删除，句中标点降级为空格。"""
    t = _FALLBACK_COMMA_RE.sub(" ", text)
    t = _PUNCT_RE.sub("", t)
    return re.sub(r"\s+", " ", t).strip()


def _segment_atoms(seg: dict) -> list[_Atom]:
    """单个 segment → 原子列表。无 words（对齐失败）时整段兜底为单原子。"""
    atoms: list[_Atom] = []
    pending_space = False
    for w in seg.get("words") or []:
        s, e = w.get("start"), w.get("end")
        raw = w.get("word") or ""
        if s is None or e is None:  # 对齐失败的词：跳过，不破坏时间轴
            continue
        leading_space = raw[:1].isspace()
        text = _clean(raw)
        if not text:  # 纯标点原子：丢弃，但给下一原子留空格标记
            if raw.strip():
                pending_space = True
            continue
        space = leading_space or pending_space
        if atoms and space and not _splittable(atoms[-1], _Atom(text, float(s), float(e))):
            space = False  # 数字/字母串内部不留空格（1,25 → 125，不是 1 25）
        atoms.append(_Atom(text, float(s), float(e), space_before=space))
        pending_space = False
    if not atoms:  # 兜底：无可用词级时间戳，整段按段级时间成一条
        text = _clean_fallback(seg.get("text") or "")
        if text:
            atoms = [_Atom(text, float(seg["start"]), float(seg["end"]))]
    return atoms


def _is_junk(ch: str) -> bool:
    return ch.isspace() or _PUNCT_RE.fullmatch(ch) is not None


def _is_digit_comma(text: str, i: int) -> bool:
    """千分位/数字内逗号（如 1,25 / 2,000）：前后都是数字 → 不是短语边界。"""
    prev = text[i - 1] if i > 0 else ""
    nxt = text[i + 1] if i + 1 < len(text) else ""
    return text[i] == "," and prev.isdigit() and nxt.isdigit()


def _map_breaks(seg: dict, atoms: list[_Atom]) -> None:
    """把 segment 文本标点映射到原子序列（align 会剥掉标点，words 里没有）。

    原子与文本逐字符对齐（中文单字级）；对不齐就放弃整段映射，
    退回纯声学 gap 判定（宁可不切，不可错切）。
    """
    text = seg.get("text") or ""
    ti = 0
    for idx, a in enumerate(atoms):
        seen_sentence = seen_comma = False
        while ti < len(text) and _is_junk(text[ti]):
            ch = text[ti]
            if ch in _SENTENCE_END:
                seen_sentence = True
            elif ch in _COMMA and not _is_digit_comma(text, ti):
                seen_comma = True
            ti += 1
        if not text.startswith(a.text, ti):
            return  # 对不齐：放弃映射
        ti += len(a.text)
        if idx == 0:
            continue  # 段首标点不属于任何原子
        if seen_sentence:
            atoms[idx - 1].break_after = "sentence"
        elif seen_comma:
            atoms[idx - 1].break_after = "comma"
            a.space_before = True  # 切开后若并回，短语间保留空格


# ---------- 短语分组与切分 ----------

def _splittable(a: _Atom, b: _Atom) -> bool:
    """数字/字母串（ASCII 字母数字连串）内部不可切。"""
    return not (a.text and b.text
                and a.text[-1].isascii() and a.text[-1].isalnum()
                and b.text[0].isascii() and b.text[0].isalnum())


def _phrases(segments: list[dict]) -> list[list[_Atom]]:
    """全部 segment 摊平成原子，按文本断句/硬停顿/段边界分组为短语。"""
    phrases: list[list[_Atom]] = []
    prev: _Atom | None = None
    for seg in segments:
        atoms = _segment_atoms(seg)
        _map_breaks(seg, atoms)
        for i, a in enumerate(atoms):
            gap = a.start - prev.end if prev is not None else float("inf")
            hard = (prev is not None and gap >= HARD_GAP and _splittable(prev, a))
            if i == 0 or prev is None or hard or prev.break_after:  # 段边界必切
                phrases.append([a])
            else:
                a.space_before = a.space_before or (gap >= SOFT_GAP and _splittable(prev, a))
                phrases[-1].append(a)
            prev = a
    return phrases


def _chars(atoms: list[_Atom]) -> int:
    return sum(len(a.text) for a in atoms)


def _best_cut(run: list[_Atom]) -> int | None:
    """在 run 内找回退切点：软间隙里 gap 最大的位置，返回切点下标（该原子后切）。
    要求左侧至少 MIN_CHARS 字、且切点不在数字/字母串内部。找不到返回 None。"""
    best_i, best_gap = None, 0.0
    for i in range(len(run) - 1):
        if not run[i + 1].space_before or not _splittable(run[i], run[i + 1]):
            continue
        gap = run[i + 1].start - run[i].end
        if gap > best_gap and _chars(run[: i + 1]) >= MIN_CHARS:
            best_i, best_gap = i, gap
    return best_i


def _phrase_to_cues(atoms: list[_Atom], max_chars: int,
                    absorb_chars: int) -> list[list[_Atom]]:
    """单个短语 → 若干 cue 原子组。超限时优先在最大软间隙处回退切。"""
    cues: list[list[_Atom]] = []
    run = [atoms[0]]
    k = 1
    while k < len(atoms):
        nxt = atoms[k]
        rest = atoms[k:]
        over_chars = _chars(run) + len(nxt.text) > max_chars
        over_dur = nxt.end - run[0].start > MAX_DUR
        if not (over_chars or over_dur):
            run.append(nxt)
        elif not _splittable(run[-1], nxt):
            run.append(nxt)  # 数字/字母串不拆开，宁可本条超限 1~3 字
        elif (_chars(rest) <= absorb_chars
              and rest[-1].end - run[0].start <= MAX_DUR + DUR_SLACK):
            run.append(nxt)  # 尾部少量字且时长可控 → 吸收，避免孤字尾
        else:
            cut = _best_cut(run)
            if cut is not None:
                cues.append(run[: cut + 1])
                run = run[cut + 1:]
            else:
                cues.append(run)
                run = []
            run.append(nxt if run else replace(nxt, space_before=False))
        k += 1
    cues.append(run)
    return cues


def _merge_fragments(groups: list[list[_Atom]], max_chars: int,
                     absorb_chars: int) -> list[list[_Atom]]:
    """碎片合并：相邻两条任一不足 MIN_CHARS 字，且无硬停顿、装得下 → 并为一条。"""
    out: list[list[_Atom]] = []
    for g in groups:
        if out and (_is_fragment(out[-1]) or _is_fragment(g)) \
                and _mergeable(out[-1], g, max_chars, absorb_chars):
            _join(out[-1], g)
        else:
            out.append(g)
    return out


def _is_fragment(g: list[_Atom]) -> bool:
    return _chars(g) < MIN_CHARS


def _mergeable(a: list[_Atom], b: list[_Atom], max_chars: int, absorb_chars: int) -> bool:
    gap = b[0].start - a[-1].end
    return (gap < HARD_GAP
            and _chars(a) + _chars(b) <= max_chars + absorb_chars
            and b[-1].end - a[0].start <= MAX_DUR + DUR_SLACK)


def _join(a: list[_Atom], b: list[_Atom]) -> None:
    gap_space = b[0].start - a[-1].end >= SOFT_GAP
    b0 = replace(b[0], space_before=b[0].space_before or gap_space)  # 逗号空格保留
    a.extend([b0] + b[1:])


def _format(groups: list[list[_Atom]]) -> list[dict]:
    cues = []
    for g in groups:
        parts = [g[0].text]
        for a in g[1:]:
            parts.append((" " if a.space_before else "") + a.text)
        cues.append({"start": g[0].start, "end": g[-1].end, "text": "".join(parts)})
    for i, c in enumerate(cues):  # 条尾延长不越过下一条起点
        padded = c["end"] + END_PAD
        if i + 1 < len(cues):
            padded = min(padded, max(c["end"], cues[i + 1]["start"] - 0.001))
        c["end"] = padded
    return cues
