"""懂你意思：LLM（DeepSeek，OpenAI 兼容接口）基于视频文案草稿修正 SRT 字幕。

安全模型：时间轴不被 LLM 破坏**不靠 prompt 自觉**——修好后的 SRT 逐条机械校验
（条数一致 + 每条时间戳逐字符相同），任何一条对不上就回退该条原文；整体不合法
（非 SRT、条数不符）则放弃修正、保留原字幕。LLM 调用失败（网络/Key/限流）永不
毁掉转写结果：fix_srt 返回 None + 错误信息，由调用方决定告警后交付原 SRT。

纯函数（read_srt_cues/build_messages/strip_code_fence/merge_corrected）与编排
（call_llm/fix_srt）分离；HTTP transport 与 sleep 注入，测试不碰网络。
whisper 解码伪影 U+FFFD 已在 segmentation._sanitize 清过，此处无需重复处理。
"""
import re
import time

from bilingual import Cue
from srt import format_ts, render_srt

DEEPSEEK_URL = "https://api.deepseek.com/chat/completions"

# 输出上限：1M 上下文下整份 SRT 一次装下；max_tokens 须显式给足（服务端默认远小于此，
# 长视频修正会被静默截断）。计费按实际用量。
MAX_TOKENS = 65536
TIMEOUT = (10, 600)  # 连接, 读取（秒）——长输出单次响应可达数分钟

RETRYABLE_HTTP = {429, 500, 502, 503, 504}
_AUTH_HTTP = {401: "Key 无效", 402: "余额不足", 403: "无权限"}

_TIME_LINE = re.compile(r"(\d+:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(\d+:\d{2}:\d{2}[,.]\d{1,3})")


def parse_ts(s: str) -> float:
    """SRT 时间串（逗号毫秒）→ 秒。"""
    h, m, rest = s.replace(",", ".").split(":")
    return int(h) * 3600 + int(m) * 60 + float(rest)


def read_srt_cues(text: str) -> list[dict]:
    """宽松解析 SRT → [{start, end, text}]。

    兼容缺序号行（LLM 常丢）、CRLF、多行文本（合并为一行带 \\n）。块内定位到含
    "-->" 的时间行后，其余行全是文本；无时间行或文本为空的块丢弃。
    """
    text = text.replace("\r\n", "\n")
    cues: list[dict] = []
    for block in re.split(r"\n[ \t]*\n", text.strip()):
        lines = [l for l in block.splitlines() if l.strip()]
        for i, line in enumerate(lines):
            m = _TIME_LINE.search(line)
            if m:
                body = "\n".join(lines[i + 1:]).strip()
                if body:
                    cues.append({"start": parse_ts(m.group(1)),
                                 "end": parse_ts(m.group(2)), "text": body})
                break
    return cues


def build_messages(prompt: str, draft: str, srt_text: str) -> list[dict]:
    """system=修正规则（prompt），user=草稿（明示可能只是概述）+ 原始 SRT。"""
    user = (f"【视频文案草稿（可能只是内容概述，未必是逐字稿，仅供参考）】\n"
            f"{draft.strip() or '（未提供）'}\n\n【视频字幕 SRT】\n{srt_text}")
    return [{"role": "system", "content": prompt},
            {"role": "user", "content": user}]


def strip_code_fence(text: str) -> str:
    """剥掉 LLM 无视「不要代码块标记」时包的 ``` 围栏（含 ```srt 语言标注）。"""
    t = text.strip()
    if t.startswith("```"):
        t = re.sub(r"^```[a-zA-Z]*[ \t]*\n?", "", t)
        t = re.sub(r"\n?[ \t]*```$", "", t)
    return t.strip()


def merge_corrected(orig: list[dict], reply: list[dict]) -> tuple[list[dict], dict]:
    """机械校验合并：条数一致且每条时间戳与原文相同（format_ts 规范化后逐字符比）
    才采纳该条新文本；时间轴被动过或新文本为空 → 该条回退原文；条数不符 → 整体
    回退。返回 (合并结果, 统计)：status ok{changed,kept} / count_mismatch{orig,got}。
    changed 只计文本实际有改动的条数；kept 是回退条数。
    """
    if len(reply) != len(orig):
        return [dict(c) for c in orig], {"status": "count_mismatch",
                                         "orig": len(orig), "got": len(reply)}
    merged: list[dict] = []
    changed = kept = 0
    for o, r in zip(orig, reply):
        same_time = (format_ts(o["start"]), format_ts(o["end"])) == \
                    (format_ts(r["start"]), format_ts(r["end"]))
        new_text = (r.get("text") or "").strip()
        if same_time and new_text:
            if new_text != o["text"]:
                changed += 1
            merged.append({**o, "text": new_text})
        else:
            merged.append(dict(o))
            kept += 1
    return merged, {"status": "ok", "changed": changed, "kept": kept}


def to_cues(blocks: list[dict]) -> list[Cue]:
    """解析块 → bilingual.Cue（文本整体走 text_zh，双语 SRT 的英文行随 \\n 原样保留）。"""
    return [Cue(start=b["start"], end=b["end"], text_zh=b["text"]) for b in blocks]


def _default_transport(url: str, payload: dict, key: str, timeout):
    """requests POST（OpenAI 兼容 /chat/completions）→ (status_code, json_body)。"""
    import requests
    resp = requests.post(url, json=payload, timeout=timeout,
                         headers={"Authorization": f"Bearer {key}"})
    try:
        body = resp.json()
    except ValueError:
        body = {}
    return resp.status_code, body


def call_llm(messages: list[dict], *, key: str, model: str, url: str = DEEPSEEK_URL,
             transport=None, retries: int = 2, backoff: float = 2.0,
             sleep=time.sleep, timeout=TIMEOUT, on_retry=None) -> tuple[dict | None, str | None]:
    """调用 LLM，可重试退避。返回 ({"content", "usage"} | None, 错误描述 | None)。

    429/5xx/网络异常重试（backoff 线性增长），401/402/403（Key/余额/权限）不重试。
    on_retry(第几次重试, 错误) 供上层记日志。
    """
    transport = transport or _default_transport
    payload = {"model": model, "messages": messages,
               "temperature": 0.1, "max_tokens": MAX_TOKENS, "stream": False}
    last_err = ""
    for attempt in range(retries + 1):
        try:
            status, body = transport(url, payload, key, timeout)
        except Exception as e:  # requests.ConnectionError/Timeout 等
            last_err = f"网络错误：{e}"
        else:
            if status == 200:
                try:
                    content = body["choices"][0]["message"]["content"]
                except (KeyError, IndexError, TypeError):
                    return None, "HTTP 200 但响应缺少 choices[0].message.content"
                return {"content": content, "usage": body.get("usage")}, None
            if status in _AUTH_HTTP:
                return None, f"HTTP {status}：DeepSeek {_AUTH_HTTP[status]}"
            last_err = f"HTTP {status}：{str(body.get('error', {}).get('message', ''))[:200]}"
        if attempt < retries:
            if on_retry is not None:
                on_retry(attempt + 1, last_err)
            sleep(backoff * (attempt + 1))
    return None, f"{last_err}（已重试 {retries} 次）"


UNCERTAIN_MARK = "【?】"


def build_change_report(orig: list[dict], new: list[dict], *, model: str,
                        stats: dict) -> str:
    """本地 diff 产出改动清单 txt（给用户复核用，替代模型自述清单——免费且绝对准确）。

    结构：统计头 + 修改区（每条 - 原文 / + 新文，带序号与起始时间码）+ 存疑待复核区
    （新文本含【?】的条目索引）。无任何文本改动返回 ""（调用方不产出文件）。
    """
    changed = [(o, n) for o, n in zip(orig, new) if o["text"] != n["text"]]
    uncertain = [(i, n) for i, n in enumerate(new, start=1) if UNCERTAIN_MARK in n["text"]]
    if not changed and not uncertain:
        return ""
    head = (f"懂你意思 · 改动清单\n"
            f"模型 {model} ｜ 共 {len(orig)} 条 ｜ 修改 {stats.get('changed', len(changed))}"
            f" ｜ 回退 {stats.get('kept', 0)} ｜ 存疑【?】{len(uncertain)}\n")
    parts = [head]
    if changed:
        blocks = []
        for idx, (o, n) in ((i + 1, pair) for i, pair in enumerate(zip(orig, new))
                            if pair[0]["text"] != pair[1]["text"]):
            blocks.append(f"[#{idx}] {format_ts(o['start'])}\n- {o['text']}\n+ {n['text']}")
        parts.append(f"== 修改（{len(changed)}）==\n" + "\n\n".join(blocks) + "\n")
    if uncertain:
        lines = [f"[#{i}] {n['text']}" for i, n in uncertain]
        parts.append(f"\n== 存疑待复核（{len(uncertain)}）==\n" + "\n".join(lines) + "\n")
    return "\n".join(parts)


def fix_srt(srt_text: str, *, key: str, model: str, prompt: str, draft: str,
            url: str = DEEPSEEK_URL, transport=None, retries: int = 2,
            backoff: float = 2.0, sleep=time.sleep, on_retry=None) -> tuple[str | None, dict]:
    """修正编排：解析原文 → 调 LLM → 校验合并 → 渲染。返回 (修正后 SRT | None, 信息)。

    info.status：ok / count_mismatch（已整体回退，内容即原文）/ llm_error / parse_error。
    修正失败永不抛异常——上层据此告警并交付原 SRT。
    """
    orig = read_srt_cues(srt_text)
    if not orig:
        return None, {"status": "parse_error", "error": "原 SRT 解析结果为空"}
    result, err = call_llm(build_messages(prompt, draft, srt_text), key=key, model=model,
                           url=url, transport=transport, retries=retries,
                           backoff=backoff, sleep=sleep, on_retry=on_retry)
    if err:
        return None, {"status": "llm_error", "error": err}
    reply = read_srt_cues(strip_code_fence(result["content"] or ""))
    if not reply:
        return None, {"status": "parse_error", "error": "修正结果不是有效 SRT"}
    merged, stats = merge_corrected(orig, reply)
    return render_srt(to_cues(merged)), {**stats, "usage": result.get("usage")}
