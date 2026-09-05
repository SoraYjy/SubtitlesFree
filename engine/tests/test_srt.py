from bilingual import Cue
from srt import cue_text, format_ts, render_srt, write_srt


def test_format_ts():
    assert format_ts(1.2) == "00:00:01,200"
    assert format_ts(3661.5) == "01:01:01,500"
    assert format_ts(-0.5) == "00:00:00,000"


def test_cue_text_bilingual():
    assert cue_text(Cue(0, 1, "中文", "English")) == "中文\nEnglish"
    assert cue_text(Cue(0, 1, "中文", None)) == "中文"


def test_render_srt_blocks():
    cues = [Cue(1.2, 3.8, "中文", "English"), Cue(4, 6, "第二句")]
    text = render_srt(cues)
    assert text == (
        "1\n00:00:01,200 --> 00:00:03,800\n中文\nEnglish\n"
        "\n"
        "2\n00:00:04,000 --> 00:00:06,000\n第二句\n"
    )


def test_write_srt_utf8_sig(tmp_path):
    p = tmp_path / "out.srt"
    write_srt([Cue(0, 1, "中文", "En")], str(p))
    raw = p.read_bytes()
    assert raw.startswith(b"\xef\xbb\xbf")  # BOM
    assert "中文".encode("utf-8") in raw[3:]
