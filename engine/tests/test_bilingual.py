from bilingual import match_en_to_cues


def seg(start, end, text):
    return {"start": start, "end": end, "text": text}


def test_no_en_segments_returns_zh_only():
    cues = match_en_to_cues([seg(0, 2, "你好"), seg(2, 4, "世界")], [])
    assert [(c.text_zh, c.text_en) for c in cues] == [("你好", None), ("世界", None)]


def test_one_to_one_overlap():
    zh = [seg(0, 2, "你好"), seg(2, 4, "世界")]
    en = [seg(0.1, 1.9, "Hello"), seg(2.1, 3.9, "World")]
    cues = match_en_to_cues(zh, en)
    assert cues[0].text_en == "Hello"
    assert cues[1].text_en == "World"


def test_en_spanning_two_cues_goes_to_larger_overlap():
    zh = [seg(0, 2, "甲"), seg(2, 4, "乙")]
    en = [seg(1.5, 3.5, "long en")]  # 重叠比例 0.25 vs 0.75 → 更大比例胜出
    cues = match_en_to_cues(zh, en)
    assert cues[0].text_en is None
    assert cues[1].text_en == "long en"


def test_en_tied_ratio_goes_to_first_cue():
    zh = [seg(0, 2, "甲"), seg(2, 4, "乙")]
    en = [seg(1.5, 2.5, "long en")]  # 比例打平归先出现的 cue
    cues = match_en_to_cues(zh, en)
    assert cues[0].text_en == "long en"
    assert cues[1].text_en is None


def test_no_overlap_assigns_nearest_center():
    zh = [seg(0, 2, "甲"), seg(10, 12, "乙")]
    en = [seg(4, 5, "en")]  # 与谁都无重叠 → 中心 4.5 距 cue0 中心 1.0 最近
    cues = match_en_to_cues(zh, en)
    assert cues[0].text_en == "en"


def test_multiple_en_to_one_cue_join_in_order():
    zh = [seg(0, 10, "长句")]
    en = [seg(1, 3, "first"), seg(4, 6, "second")]
    cues = match_en_to_cues(zh, en)
    assert cues[0].text_en == "first second"


def test_empty_en_text_skipped():
    zh = [seg(0, 2, "甲")]
    en = [seg(0, 2, "   ")]
    cues = match_en_to_cues(zh, en)
    assert cues[0].text_en is None
