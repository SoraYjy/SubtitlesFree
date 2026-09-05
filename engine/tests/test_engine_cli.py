"""engine.py --selftest 子进程冒烟：JSON Lines 可解析、退出码 0。"""
import json
import subprocess
import sys
from pathlib import Path

ENGINE = Path(__file__).resolve().parent.parent / "engine.py"


def test_selftest_emits_valid_json_lines():
    proc = subprocess.run([sys.executable, str(ENGINE), "--selftest"],
                          capture_output=True, text=True, encoding="utf-8", timeout=300)
    assert proc.returncode == 0
    events = [json.loads(l) for l in proc.stdout.splitlines() if l.strip()]
    types = [e["type"] for e in events]
    assert "stage" in types and "log" in types
    assert any(e.get("message") == "selftest ok" for e in events if e["type"] == "log")
