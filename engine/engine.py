"""SubtitlesFree 转写引擎：MP4 → SRT 字幕 / TXT 纯文本转写（可选中英双语）。

stdout 输出 JSON Lines 事件（契约 spec §5.2）；stderr 留给原生 traceback。
用法示例：
  python engine.py --video in.mp4 --bilingual --hotwords "ComfyUI,SDXL"
"""
import argparse
import sys

import emitter
from emitter import emit_error, emit_log, emit_stage


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="SubtitlesFree WhisperX 引擎")
    p.add_argument("--video", help="输入视频路径（MP4）")
    p.add_argument("--output", help="输出路径，默认视频同名 .srt / .txt（按 --format）")
    p.add_argument("--format", default="srt", choices=["srt", "txt"],
                   help="输出格式：srt 字幕（默认，含对齐与断句）/ txt 纯文本转写（逐块检测语言，中英混出）")
    p.add_argument("--language", default="zh", choices=["zh"])
    p.add_argument("--bilingual", action="store_true", help="输出中英双语字幕")
    p.add_argument("--model", default="large-v3-turbo",
                   choices=["large-v3-turbo", "large-v3", "small"])
    p.add_argument("--compute-type", default="float16",
                   choices=["float16", "int8_float16", "int8"])
    p.add_argument("--hotwords", default="", help="专有名词表，逗号分隔")
    p.add_argument("--max-chars", type=int, default=18,
                   help="字幕单条字数上限（不含空格），超出时在标点/停顿处断开")
    p.add_argument("--absorb-chars", type=int, default=4,
                   help="断句后剩余不超过此字数时并入前一条，避免孤字尾；0=关闭")
    p.add_argument("--selftest", action="store_true", help="环境自检后退出")
    return p.parse_args(argv)


def classify_exception(e: Exception) -> tuple[str, str]:
    """把异常映射为 (error.code, 中文 hint)。"""
    msg = str(e)
    if "out of memory" in msg.lower() or "OutOfMemoryError" in type(e).__name__:
        return "oom", "显存不足：切 int8_float16 或换 large-v3-turbo/small 模型后重试"
    if "gated" in msg.lower() or "401" in msg or "agreement" in msg.lower():
        return "pyannote_auth", "VAD 模型需 HF 授权：到 hf.co/pyannote/segmentation-3.0 接受条款并设置 HF_TOKEN"
    if "Connection" in type(e).__name__ or "timed out" in msg.lower() or "getaddrinfo" in msg:
        return "hf_download", "模型下载失败：可开启「镜像加速」(hf-mirror.com) 后重试"
    return "generic", ""


def selftest() -> int:
    emit_stage("load_model")
    import torch
    emit_log(f"torch {torch.__version__} 导入成功")
    if torch.cuda.is_available():
        emit_log(f"CUDA 可用：{torch.cuda.get_device_name(0)}")
    else:
        emit_log("CUDA 不可用（将回退 CPU，速度慢）", "warn")
    import whisperx
    emit_log(f"whisperx {getattr(whisperx, '__version__', '?')} 导入成功")
    emit_log("selftest ok")
    return 0


def main(argv=None) -> int:
    args = parse_args(argv)
    if args.selftest:
        return selftest()
    if not args.video:
        emit_error("generic", "缺少 --video 参数")
        return 1
    try:
        from pipeline import run_pipeline  # T5 实现
        run_pipeline(args)
        return 0
    except Exception as e:  # 顶层兜底
        code, hint = classify_exception(e)
        emit_error(code, str(e), hint)
        return 1


if __name__ == "__main__":
    sys.exit(main())
