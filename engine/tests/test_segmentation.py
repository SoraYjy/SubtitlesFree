from segmentation import segment_cues


def w(word, start, end):
    return {"word": word, "start": start, "end": end}


def seg(text, start, end, words):
    return {"start": start, "end": end, "text": text, "words": words}


# ---------- 声学 gap 驱动 ----------

def test_hard_pause_splits_two_cues_with_padded_end():
    s = seg("今天。世界。", 0, 0.9,
            [w("今", 0.0, 0.1), w("天", 0.1, 0.2), w("世", 0.7, 0.8), w("界", 0.8, 0.9)])
    cues = segment_cues([s])
    assert [c["text"] for c in cues] == ["今天", "世界"]
    assert cues[0]["start"] == 0.0 and cues[1]["start"] == 0.7
    assert cues[0]["end"] == 0.35          # 0.2+END_PAD(0.15)，未到下一条起点 0.699
    assert cues[1]["end"] == 1.05          # 末条照常延长


def test_soft_gap_stays_one_cue_with_space():
    s = seg("今天 世界", 0, 0.7,
            [w("今", 0.0, 0.1), w("天", 0.1, 0.2), w("世", 0.5, 0.6), w("界", 0.6, 0.7)])
    cues = segment_cues([s])  # gap 0.30：< HARD_GAP(0.35) 但 ≥ SOFT_GAP(0.28)
    assert len(cues) == 1
    assert cues[0]["text"] == "今天 世界"


def test_char_cap_cuts_back_at_largest_soft_gap():
    atoms = [w(c, i * 0.10, i * 0.10 + 0.10) for i, c in enumerate("一二三四五六七八九十")]
    atoms += [w(c, 1.30 + i * 0.10, 1.40 + i * 0.10) for i, c in enumerate("甲乙丙丁戊己庚辛")]
    atoms += [w(c, 2.39 + i * 0.10, 2.49 + i * 0.10) for i, c in enumerate("壬癸子丑寅卯巳午未")]
    cues = segment_cues([seg("", 0, 3.19, atoms)])  # 26 字 > MAX_CHARS(18)
    # 超限时回退到最大软间隙（十→甲 gap 0.30）处切；回退切不丢内部空格（辛→壬 gap 0.29）
    assert [c["text"] for c in cues] == ["一二三四五六七八九十", "甲乙丙丁戊己庚辛 壬癸子丑寅卯巳午未"]
    assert cues[1]["start"] == 1.30


def test_char_cap_without_soft_gap_cuts_greedy():
    text = "甲乙丙丁戊己庚辛壬癸子丑寅卯辰巳午未申酉戌亥丑"  # 23 字，全部 0 间隙
    atoms = [w(c, i * 0.1, i * 0.1 + 0.1) for i, c in enumerate(text)]
    cues = segment_cues([seg(text, 0, 2.3, atoms)])
    assert len(cues) == 2
    assert len(cues[0]["text"]) == 18 and len(cues[1]["text"]) == 5
    # 尾部 5 字 > ABSORB_CHARS(4)：不吸收；也无软间隙可回退 → 贪心 18/5


def test_char_cap_absorbs_tiny_tail_instead_of_orphan():
    text = "甲乙丙丁戊己庚辛壬癸子丑寅卯辰巳午未申"  # 19 字，0 间隙
    atoms = [w(c, i * 0.1, i * 0.1 + 0.1) for i, c in enumerate(text)]
    cues = segment_cues([seg(text, 0, 1.9, atoms)])
    assert len(cues) == 1                  # 剩 1 字 ≤ ABSORB_CHARS → 吸收，不留孤字尾
    assert len(cues[0]["text"]) == 19


def test_duration_cap_splits_and_absorbs_final_atom():
    atoms = [w(c, i * 1.5, i * 1.5 + 1.5) for i, c in enumerate("一二三四五六七八九")]  # 13.5s
    cues = segment_cues([seg("", 0, 13.5, atoms)])
    # 6s 上限切成 4+5；最后的「九」是孤字尾（时长松量内）→ 吸收进第二条
    assert [c["text"] for c in cues] == ["一二三四", "五六七八九"]


# ---------- 文本标点映射 ----------

def test_comma_in_text_splits_cues():
    s = seg("早安北京，世界真好吗。", 0, 0.9,
            [w(c, i * 0.1, i * 0.1 + 0.1) for i, c in enumerate("早安北京世界真好吗")])
    cues = segment_cues([s])
    assert [c["text"] for c in cues] == ["早安北京", "世界真好吗"]  # 逗号处切


def test_comma_fragment_merges_back_with_space():
    s = seg("早安，世界真好吗。", 0, 0.9,
            [w(c, i * 0.1, i * 0.1 + 0.1) for i, c in enumerate("早安世界真好吗")])
    cues = segment_cues([s])  # 「早安」只有 2 字 < MIN_CHARS → 并回，逗号空格保留
    assert len(cues) == 1
    assert cues[0]["text"] == "早安 世界真好吗"


def test_sentence_end_in_text_splits_cues():
    s = seg("早安北京。世界真好吗。", 0, 0.9,
            [w(c, i * 0.1, i * 0.1 + 0.1) for i, c in enumerate("早安北京世界真好吗")])
    cues = segment_cues([s])
    assert [c["text"] for c in cues] == ["早安北京", "世界真好吗"]


def test_digit_comma_is_not_a_boundary():
    s = seg("打1,25弹。", 0, 0.5,
            [w("打", 0, 0.1), w("1", 0.1, 0.2), w("2", 0.2, 0.3),
             w("5", 0.3, 0.4), w("弹", 0.4, 0.5)])
    cues = segment_cues([s])  # 1,25 的逗号是数字内分隔，不是短语边界
    assert len(cues) == 1
    assert cues[0]["text"] == "打125弹"


def test_text_atom_mismatch_bails_out_of_mapping():
    s = seg("完全不匹配的内容", 0, 0.2, [w("甲", 0, 0.1), w("乙", 0.1, 0.2)])
    cues = segment_cues([s])  # 对不齐 → 放弃映射，退回 gap 判定，不崩溃
    assert [c["text"] for c in cues] == ["甲乙"]


# ---------- 数字/字母串粘合 ----------

def test_hard_gap_between_digits_does_not_split_run():
    s = seg("", 0, 0.8, [w("甲", 0, 0.1), w("1", 0.6, 0.7), w("2", 0.7, 0.8)])
    cues = segment_cues([s])  # 甲|1 之间 0.5s 停顿可切；1|2 之间数字串不切
    assert [c["text"] for c in cues] == ["甲", "12"]


def test_soft_gap_between_digits_inserts_no_space():
    s = seg("", 0, 0.8, [w("甲", 0, 0.1), w("1", 0.4, 0.5), w("2", 0.8, 0.9)])
    cues = segment_cues([s])  # 甲|1 之间 0.3s 软间隙（可切但此处同短语）；1|2 之间 0.3s
    # 数字串内部即使有声学间隙也不加空格（否则「125」会被写成「1 25」）
    assert [c["text"] for c in cues] == ["甲 12"]


# ---------- 原子清洗与兜底 ----------

def test_punct_atoms_dropped_but_leave_space():
    s = seg("枪，很强。", 0, 0.45,
            [w("枪", 0, 0.1), w("，", 0.1, 0.12), w("很", 0.25, 0.35), w("强", 0.35, 0.45)])
    cues = segment_cues([s])
    assert cues[0]["text"] == "枪 很强"  # 纯逗号原子丢弃，但短语边界留空格


def test_punct_atom_between_digits_leaves_no_space():
    s = seg("打1,25弹", 0, 0.5,
            [w("打", 0, 0.1), w("1", 0.1, 0.2), w(",", 0.2, 0.22),
             w("2", 0.22, 0.32), w("5", 0.32, 0.42), w("弹", 0.42, 0.5)])
    cues = segment_cues([s])  # whisperx 会把数字内逗号当独立原子；丢弃后不产生空格
    assert cues[0]["text"] == "打125弹"


def test_punct_stripped_from_attached_text_and_no_output_punct():
    s = seg("MK4真好？", 0, 0.4,
            [w("MK4，", 0, 0.2), w("真", 0.2, 0.3), w("好", 0.3, 0.4)])
    cues = segment_cues([s])  # 原子自带的附着标点剥离；文本里的句尾？不进输出
    assert cues[0]["text"] == "MK4真好"


def test_latin_leading_space_kept_as_separator():
    s = seg("属于 MK4", 0, 0.4,
            [w("属", 0, 0.1), w("于", 0.1, 0.2), w(" MK4", 0.2, 0.4)])
    cues = segment_cues([s])
    assert cues[0]["text"] == "属于 MK4"


def test_segment_without_words_falls_back_whole_text():
    s = seg("你好，世界。", 0, 2, [])
    cues = segment_cues([s])
    assert len(cues) == 1
    assert cues[0]["text"] == "你好 世界"  # 兜底：逗号降级空格，句号删除
    assert cues[0]["start"] == 0 and cues[0]["end"] == 2.15


def test_empty_and_punct_only_segments_yield_nothing():
    assert segment_cues([]) == []
    assert segment_cues([seg("", 0, 1, [])]) == []
    assert segment_cues([seg("。。", 0, 1, [w("。", 0, 0.1), w("。", 0.1, 0.2)])]) == []


def test_end_pad_clamped_by_next_cue_and_padded_tail():
    # 跨段小间隙（< END_PAD）强制切分且互不合并时，条尾延长只能顶到下一条前 1ms
    s1 = seg("早安世界", 0, 0.4,
             [w("早", 0, 0.1), w("安", 0.1, 0.2), w("世", 0.2, 0.3), w("界", 0.3, 0.4)])
    s2 = seg("你好吗呀", 0.5, 0.9,
             [w("你", 0.5, 0.6), w("好", 0.6, 0.7), w("吗", 0.7, 0.8), w("呀", 0.8, 0.9)])
    cues = segment_cues([s1, s2])
    assert len(cues) == 2              # 段边界必切；各自 4 字非碎片不合并
    assert cues[0]["end"] == 0.499     # 0.4+0.15=0.55 会被截到下一条起点前 1ms
    assert cues[1]["end"] == 1.05      # 末条无下一条，照常 +0.15
