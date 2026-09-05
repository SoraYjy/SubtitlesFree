"""C# 测试用假引擎：发一串 JSON Lines 事件后退出。

用法： python fake_engine.py [--error|--sleep]
  默认    正常流：stage/progress/log/done + 一行垃圾（测 rawLog 容错）
  --error 发 error(oom) 事件后退出码 1（测 error 事件不抛异常）
  --sleep 睡 30s（测取消杀树）
"""
import json
import sys
import time

args = sys.argv[1:]

if "--error" in args:
    print(json.dumps({"type": "error", "code": "oom", "message": "fake oom",
                      "hint": "fake hint"}), flush=True)
    sys.exit(1)

if "--sleep" in args:
    time.sleep(30)

print(json.dumps({"type": "stage", "value": "load_model"}), flush=True)
print(json.dumps({"type": "progress", "value": 0.5}), flush=True)
print(json.dumps({"type": "log", "level": "info", "message": "fake log"}), flush=True)
print("not-json-garbage", flush=True)
print(json.dumps({"type": "done", "srt_path": "out.srt", "segments": 2,
                  "video_sec": 10.0, "elapsed_sec": 1.0}), flush=True)
