"""loopfix：循环幻觉检测/窗口规划/重解码采纳拼接（decode 全 DI，不碰模型）。"""
from loopfix import find_looped, is_loop_text, loop_run

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
