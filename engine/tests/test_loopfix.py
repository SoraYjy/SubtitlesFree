"""loopfix：循环幻觉检测/窗口规划/重解码采纳拼接（decode 全 DI，不碰模型）。"""
from loopfix import (
    find_looped,
    is_loop_text,
    loop_run,
    repair_looped_segments,
    repair_windows,
    splice,
)

# 真实样本：1009AK12 生产循环段（前缀正常文本 + 「智耀」连排）
LOOP_AK12 = ("如果你玩的是机密鼠工,不想花太多钱改枪。那就尤其我们今天的主角啊, "
             "AK-12 " + "智耀 " * 24 + "智")


def test_is_loop_text_ak12_real_case():
    assert is_loop_text(LOOP_AK12) is True


def test_is_loop_text_unspaced_loop():
    # 无空格连写的循环（whisper 偶发形态）
    assert is_loop_text("前面一句话。" + "智耀" * 12) is True


def test_normal_segments_not_flagged():
    # 1009AK12 正确转写开头（探针实测原文）
    assert is_loop_text("如果你玩的是机密鼠工,不想花太多钱改枪,而又怕对面护甲等级他搞打不动。") is False
    assert is_loop_text("想要达成5枪致死的TDK上限是有一定条件的,可以看到在这个命中率预设下的期望TDK。") is False


def test_short_real_repetition_not_flagged():
    # 口语/歌词级真实重复：保守优先，不得误伤
    assert is_loop_text("对 对 对 对 对 对") is False          # token=1 字，token 判据不参与
    assert is_loop_text("加油加油加油加油") is False            # 5 连 < REPEAT_MIN
    assert is_loop_text("来了来了") is False


def test_loop_run_counts_max_consecutive():
    assert loop_run(LOOP_AK12) == 24
    # 无合格 token（全部超长/无重复）→ 0
    assert loop_run("先来看一图流,甲伤非常高42点") == 0


def test_find_looped_returns_only_flagged():
    segs = [
        {"start": 0.0, "end": 6.0, "text": "如果你玩的是机密鼠工不想花太多钱改枪"},
        {"start": 6.0, "end": 18.0, "text": LOOP_AK12},
        {"start": 24.0, "end": 30.0, "text": "先来看一图流甲伤非常高42点"},
    ]
    flagged = find_looped(segs)
    assert [s["start"] for s in flagged] == [6.0]


def test_find_looped_empty_fast_path():
    assert find_looped([{"start": 0.0, "end": 2.0, "text": "正常内容"}]) == []


def test_repair_windows_pad_and_merge():
    flagged = [{"start": 6.0, "end": 18.0, "text": LOOP_AK12}]
    assert repair_windows(flagged, audio_dur=116.0) == [(5.0, 19.0)]
    # 相邻循环段窗口重叠 → 合并；起点截 0
    two = [{"start": 6.0, "end": 18.0, "text": LOOP_AK12},
           {"start": 18.5, "end": 25.0, "text": LOOP_AK12}]
    assert repair_windows(two, audio_dur=None) == [(5.0, 26.0)]
    assert repair_windows([{"start": 0.2, "end": 3.0, "text": LOOP_AK12}], audio_dur=None) == [(0.0, 4.0)]


def _base_segments():
    return [
        {"start": 0.0, "end": 6.0, "text": "如果你玩的是机密鼠工不想花太多钱改枪"},
        {"start": 6.0, "end": 18.0, "text": LOOP_AK12},
        {"start": 24.0, "end": 30.0, "text": "先来看一图流甲伤非常高42点"},
    ]


def test_splice_adopts_clean_repair():
    out, report = splice(_base_segments(), [
        ((5.0, 19.0), [
            {"start": 5.4, "end": 8.6, "text": "AK-12只要12-17万就能在机密硬钢高阶甲"},
            {"start": 8.6, "end": 9.6, "text": "爽爽引操"},
            {"start": 9.6, "end": 12.3, "text": "现在三角洲这些四级枪都太贵了"},
        ]),
    ])
    assert [s["text"] for s in out] == [
        "如果你玩的是机密鼠工不想花太多钱改枪",
        "AK-12只要12-17万就能在机密硬钢高阶甲",
        "爽爽引操",
        "现在三角洲这些四级枪都太贵了",
        "先来看一图流甲伤非常高42点",
    ]
    assert report == [{"start": 6.0, "end": 18.0, "action": "repaired"}]


def test_splice_keeps_original_when_repair_still_loops():
    segs = [{"start": 6.0, "end": 18.0, "text": LOOP_AK12}]
    out, report = splice(segs, [((5.0, 19.0), [{"start": 6.0, "end": 17.0, "text": LOOP_AK12}])])
    assert out == segs
    assert report == [{"start": 6.0, "end": 18.0, "action": "kept"}]


def test_splice_drops_repair_overlapping_healthy_neighbor():
    # 窗口 padding 压到邻近正常段 → 与正常段重叠过半的修复段丢弃（防复制邻段内容）
    segs = [
        {"start": 0.0, "end": 6.0, "text": "如果你玩的是机密鼠工不想花太多钱改枪"},
        {"start": 6.0, "end": 18.0, "text": LOOP_AK12},
    ]
    repairs = [
        {"start": 2.0, "end": 7.0, "text": "重复邻段内容的东西"},   # 与邻段重叠 1s/其长 1s → 过半 → 丢
        {"start": 7.0, "end": 12.0, "text": "修复内容"},            # 不与正常段重叠 → 采纳
    ]
    out, report = splice(segs, [((5.0, 19.0), repairs)])
    assert [s["text"] for s in out] == ["如果你玩的是机密鼠工不想花太多钱改枪", "修复内容"]
    assert report == [{"start": 6.0, "end": 18.0, "action": "repaired"}]


def test_splice_empty_repairs_keeps_original():
    segs = [{"start": 6.0, "end": 18.0, "text": LOOP_AK12}]
    out, report = splice(segs, [((5.0, 19.0), [])])
    assert out == segs and report[0]["action"] == "kept"


def test_repair_looped_segments_orchestration():
    # decode 收到切片（相对时间），返回段由编排层平移回绝对时间
    calls = []

    def decode(window):
        a, b = window
        calls.append((round(a, 1), round(b, 1)))
        return [{"start": 0.4, "end": 13.0, "text": "AK-12只要12-17万就能在机密硬钢高阶甲爽爽引操"}]

    segs = [
        {"start": 0.0, "end": 6.0, "text": "如果你玩的是机密鼠工不想花太多钱改枪"},
        {"start": 6.0, "end": 18.0, "text": LOOP_AK12},
    ]
    out, report = repair_looped_segments(segs, decode=decode, audio_dur=116.0, log=lambda m: None)
    assert calls == [(5.0, 19.0)]
    assert report == [{"start": 6.0, "end": 18.0, "action": "repaired"}]
    fixed = [s for s in out if "硬钢高阶甲" in s["text"]]
    assert fixed and fixed[0]["start"] == 5.4   # 相对 0.4 + 窗口起点 5.0


def test_repair_looped_segments_no_loop_no_decode():
    calls = []

    def decode(window):
        calls.append(window)
        return []

    out, report = repair_looped_segments(
        [{"start": 0.0, "end": 2.0, "text": "正常内容"}],
        decode=decode, audio_dur=10.0, log=lambda m: None)
    assert calls == [] and report == []
    assert out[0]["text"] == "正常内容"
