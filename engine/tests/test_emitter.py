"""emitter：JSON Lines 契约 + stdout 编码安全。"""
import json

import pytest

from emitter import STAGES, emit, emit_done, emit_error, emit_log, emit_progress, emit_stage


def test_emit_produces_one_json_line(capsys):
    emit({"type": "log", "message": "你好"})
    out = capsys.readouterr().out
    assert out.endswith("\n")
    assert json.loads(out.strip()) == {"type": "log", "message": "你好"}


def test_emit_keeps_chinese_untouched(capsys):
    emit_log("中文不转义")
    assert json.loads(capsys.readouterr().out.strip())["message"] == "中文不转义"


def test_emit_stage_all_stages_roundtrip(capsys):
    for s in STAGES:
        emit_stage(s)
    lines = [json.loads(l) for l in capsys.readouterr().out.splitlines()]
    assert [l["value"] for l in lines] == list(STAGES)


def test_emit_stage_rejects_unknown(capsys):
    with pytest.raises(ValueError):
        emit_stage("transcribing")


def test_emit_progress_clamps(capsys):
    emit_progress(1.5)
    emit_progress(-0.2)
    lines = [json.loads(l) for l in capsys.readouterr().out.splitlines()]
    assert [l["value"] for l in lines] == [1.0, 0.0]


def test_emit_done_fields(capsys):
    emit_done("a.srt", 12, 91.5, 33.3)
    obj = json.loads(capsys.readouterr().out.strip())
    assert obj == {"type": "done", "srt_path": "a.srt", "segments": 12,
                   "video_sec": 91.5, "elapsed_sec": 33.3}


def test_emit_error_fields(capsys):
    emit_error("oom", "显存不足", "切 int8_float16")
    obj = json.loads(capsys.readouterr().out.strip())
    assert obj == {"type": "error", "code": "oom", "message": "显存不足", "hint": "切 int8_float16"}
