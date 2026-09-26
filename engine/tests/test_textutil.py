"""textutil：whisper 解码伪影清理。"""
from textutil import clean_text


def test_clean_text_removes_replacement_char():
    # 真实样本：2026-09-18 访谈转写行 3「更有意义的一个选择�然后呢」
    assert clean_text("更有意义的一个选择�然后呢") == "更有意义的一个选择然后呢"


def test_clean_text_multiple_occurrences():
    # 真实样本：行 47「呃�他可能需要我们再跳一下」+ 行 71「铁丝网�所以它」
    assert clean_text("呃�他可能") == "呃他可能"
    assert clean_text("铁丝网�所以它�本身") == "铁丝网所以它本身"


def test_clean_text_no_replacement_untouched():
    assert clean_text("TTK 和 killstreak，正常文本。") == "TTK 和 killstreak，正常文本。"
    assert clean_text("") == ""
