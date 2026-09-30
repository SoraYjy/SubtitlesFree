"""transcribe_text：txt 转写模式——块语言决策/文本拼接纯函数 + 分块转写编排（DI 假模型）。"""
from transcribe_text import (
    CONFIDENCE_THRESHOLD,
    build_initial_prompt,
    decide_language,
    format_transcript,
    join_segments,
    merge_language_runs,
    transcribe_blocks,
)


# ---- build_initial_prompt：普通话句子引导（两模式统一）+ 热词注入 ----

def test_build_initial_prompt_no_hotwords_still_biases_punctuation():
    # 0926m249 实测：无标点长串导致断句词中切 ×5；普通话引导提高标点密度（用户已批准改默认）
    assert build_initial_prompt([]) == "以下是普通话的句子。"


def test_build_initial_prompt_hotwords_combined():
    assert build_initial_prompt(["修脚弹", "ST弹"]) == \
        "以下是普通话的句子。以下是可能出现的专有名词：修脚弹，ST弹。"


# ---- decide_language：置信度阈值 + 兜底默认语言 ----

def test_decide_language_high_prob_keeps_code():
    assert decide_language("en", 0.93) == "en"
    assert decide_language("ja", 0.7) == "ja"


def test_decide_language_below_threshold_falls_back():
    assert decide_language("en", 0.59) == "zh"
    assert decide_language("en", CONFIDENCE_THRESHOLD) == "en"  # 边界：恰好等于阈值保留


def test_decide_language_empty_code_falls_back():
    assert decide_language("", 0.99) == "zh"


# ---- join_segments：块内句拼接（CJK 无空格，拉丁空格） ----

def test_join_segments_cjk_no_spaces():
    assert join_segments([" 今 ", "天赢了。"], "zh") == "今天赢了。"


def test_join_segments_latin_joins_with_space():
    assert join_segments(["Hello", " world."], "en") == "Hello world."


def test_join_segments_empty():
    assert join_segments([], "zh") == ""
    assert join_segments(["", "  "], "en") == ""


# ---- format_transcript：语言块间空行分段，末尾一个换行 ----

def test_format_transcript_blank_line_between_blocks():
    assert format_transcript(["第一段。", "Second paragraph."]) == "第一段。\n\nSecond paragraph.\n"


def test_format_transcript_skips_empty_blocks():
    assert format_transcript(["A", "", "  ", "B"]) == "A\n\nB\n"


def test_format_transcript_all_empty():
    assert format_transcript([]) == ""
    assert format_transcript(["", " "]) == ""


# ---- transcribe_blocks：分块 → 逐块检测 → 逐块转写（全 DI，不碰 whisperx） ----

class FakeModel:
    """按语言返回固定 segments 的假 whisperx pipeline。"""

    def __init__(self, texts_by_lang):
        self.texts_by_lang = texts_by_lang
        self.requested_langs = []

    def transcribe(self, audio, batch_size=None, language=None):
        self.requested_langs.append(language)
        return {"segments": [{"text": t} for t in self.texts_by_lang.get(language, [])]}


def _fake_chunker(chunks):
    return lambda audio, **kw: chunks


def _scripted_detect(results):
    it = iter(results)
    return lambda audio: next(it)


def test_transcribe_blocks_per_block_language():
    model = FakeModel({"zh": [" 今天", "讲了。"], "en": ["The GPU", " is fast."]})
    blocks = transcribe_blocks(
        model, [0.0] * 100,
        vad_chunker=_fake_chunker([{"start": 0, "end": 5}, {"start": 5, "end": 10}]),
        detect=_scripted_detect([("zh", 0.9), ("en", 0.95)]))
    assert model.requested_langs == ["zh", "en"]
    assert blocks == ["今天讲了。", "The GPU is fast."]


def test_transcribe_blocks_low_confidence_defaults_zh():
    model = FakeModel({"zh": ["中文"], "en": ["english noise"]})
    blocks = transcribe_blocks(
        model, [0.0] * 100,
        vad_chunker=_fake_chunker([{"start": 0, "end": 5}]),
        detect=_scripted_detect([("en", 0.4)]))
    assert model.requested_langs == ["zh"]
    assert blocks == ["中文"]


def test_transcribe_blocks_skips_silent_block():
    model = FakeModel({"zh": ["你好"], "en": []})
    blocks = transcribe_blocks(
        model, [0.0] * 100,
        vad_chunker=_fake_chunker([{"start": 0, "end": 5}, {"start": 5, "end": 9}]),
        detect=_scripted_detect([("zh", 0.9), ("en", 0.99)]))
    assert blocks == ["你好"]


# ---- merge_language_runs：相邻同语言区间合并成一个转写段（保上下文） ----

def test_merge_language_runs_merges_adjacent_same_language():
    tagged = [{"start": 0, "end": 3, "lang": "zh"},
              {"start": 3.2, "end": 6, "lang": "zh"},
              {"start": 6.1, "end": 9, "lang": "en"},
              {"start": 9.5, "end": 12, "lang": "en"},
              {"start": 12.2, "end": 15, "lang": "zh"}]
    assert merge_language_runs(tagged) == [
        {"start": 0, "end": 6, "lang": "zh"},
        {"start": 6.1, "end": 12, "lang": "en"},
        {"start": 12.2, "end": 15, "lang": "zh"},
    ]


def test_merge_language_runs_empty():
    assert merge_language_runs([]) == []


def test_transcribe_blocks_merges_adjacent_same_language_regions():
    """相邻同语言区间只送一次转写（zh 两区间合一），段间静音含在切片里无害。"""
    model = FakeModel({"zh": ["句一", "句二"], "en": ["English."]})
    blocks = transcribe_blocks(
        model, [0.0] * 100,
        vad_chunker=_fake_chunker([{"start": 0, "end": 3}, {"start": 3.2, "end": 6},
                                  {"start": 6.1, "end": 9}]),
        detect=_scripted_detect([("zh", 0.9), ("zh", 0.9), ("en", 0.9)]))
    assert model.requested_langs == ["zh", "en"]  # 3 区间 → 2 次转写
    assert blocks == ["句一句二", "English."]


def test_transcribe_blocks_strips_replacement_chars():
    """whisper 解码伪影 U+FFFD 不进 txt 输出（真实样本：访谈转写行 3/47/71 各 1 处）。"""
    model = FakeModel({"zh": ["更有意义的一个选择�然后呢", "呃�他可能"]})
    blocks = transcribe_blocks(
        model, [0.0] * 100,
        vad_chunker=_fake_chunker([{"start": 0, "end": 5}]),
        detect=_scripted_detect([("zh", 0.9)]))
    assert blocks == ["更有意义的一个选择然后呢呃他可能"]
    assert "�" not in blocks[0]


def test_transcribe_blocks_progress_increments():
    model = FakeModel({"zh": ["一"], "en": ["two"]})
    seen = []
    transcribe_blocks(
        model, [0.0] * 100,
        vad_chunker=_fake_chunker([{"start": 0, "end": 4}, {"start": 4, "end": 8}]),
        detect=_scripted_detect([("zh", 0.9), ("en", 0.9)]),
        on_progress=seen.append)
    assert seen == [0.5, 1.0]


def test_transcribe_blocks_slices_audio_by_time():
    model = FakeModel({"zh": ["词"]})
    audio = list(range(16000 * 3))  # 3s 样本
    got = []

    def chunker(a, **kw):
        got.append(len(a))
        return [{"start": 1.0, "end": 2.5}]

    transcribe_blocks(model, audio, vad_chunker=chunker,
                      detect=_scripted_detect([("zh", 0.9)]))
    assert got == [48000]          # 完整音频交给 chunker
    assert model.requested_langs == ["zh"]
