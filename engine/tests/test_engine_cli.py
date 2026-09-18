"""engine.py CLI：--selftest 子进程冒烟 + parse_args 参数解析。"""
import json
import subprocess
import sys
from pathlib import Path

from engine import parse_args

ENGINE = Path(__file__).resolve().parent.parent / "engine.py"


def test_parse_args_format_defaults_srt():
    assert parse_args(["--video", "x.mp4"]).format == "srt"


def test_parse_args_format_txt():
    assert parse_args(["--video", "x.mp4", "--format", "txt"]).format == "txt"


def test_selftest_emits_valid_json_lines():
    proc = subprocess.run([sys.executable, str(ENGINE), "--selftest"],
                          capture_output=True, text=True, encoding="utf-8", timeout=300)
    assert proc.returncode == 0
    events = [json.loads(l) for l in proc.stdout.splitlines() if l.strip()]
    types = [e["type"] for e in events]
    assert "stage" in types and "log" in types
    assert any(e.get("message") == "selftest ok" for e in events if e["type"] == "log")
