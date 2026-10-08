"""llm_fix：懂你意思——SRT 解析/消息构造/机械校验合并/LLM 调用重试（transport 全 DI，不碰网络）。"""
from pathlib import Path

import llm_fix
from llm_fix import (
    build_change_report,
    build_messages,
    call_llm,
    fix_srt,
    merge_corrected,
    parse_ts,
    read_srt_cues,
    strip_code_fence,
    to_cues,
)
from srt import format_ts

# 真实样本取自 0926m249（用户实际产出）
ORIG_SRT = (
    "1\n00:00:00,331 --> 00:00:02,742\n机密大坝长工AZ-3的入场费是11万\n"
    "\n"
    "2\n00:00:03,132 --> 00:00:04,111\n今天给大家推荐者M249\n"
)


def _fake_transport(status=200, content="", calls=None):
    """status==200 返回带 usage 的正常体，否则返回 DeepSeek 风格错误体。"""
    def transport(url, payload, key, timeout):
        if calls is not None:
            calls.append({"url": url, "payload": payload, "key": key, "timeout": timeout})
        body = ({"choices": [{"message": {"content": content}}],
                 "usage": {"prompt_tokens": 10, "completion_tokens": 5}}
                if status == 200 else {"error": {"message": "boom"}})
        return status, body
    return transport


def _srt_with_times(cues):
    return "\n\n".join(
        f"{i}\n{format_ts(c['start'])} --> {format_ts(c['end'])}\n{c['text']}"
        for i, c in enumerate(cues, start=1)) + "\n"


# ---- parse_ts / read_srt_cues：宽松 SRT 解析（兼容缺序号行、CRLF、多行文本） ----

def test_parse_ts_handles_comma_ms():
    assert parse_ts("00:00:01,234") == 1.234
    assert parse_ts("01:02:03,005") == 3723.005


def test_read_srt_cues_parses_blocks_and_multiline_text():
    srt = "1\n00:00:00,000 --> 00:00:01,000\n第一行\n第二行\n\n2\n00:00:02,000 --> 00:00:03,000\n单独\n"
    cues = read_srt_cues(srt)
    assert [c["text"] for c in cues] == ["第一行\n第二行", "单独"]
    assert cues[1]["start"] == 2.0 and cues[1]["end"] == 3.0


def test_read_srt_cues_tolerates_crlf_and_missing_index():
    srt = "00:00:00,500 --> 00:00:01,500\r\n没有序号行\r\n\r\n00:00:02,000 --> 00:00:03,000\r\n第二条\r\n"
    cues = read_srt_cues(srt)
    assert [c["text"] for c in cues] == ["没有序号行", "第二条"]
    assert cues[0]["start"] == 0.5


def test_read_srt_cues_empty_returns_empty():
    assert read_srt_cues("") == []
    assert read_srt_cues("不是 srt 的文本") == []


# ---- build_messages：system=prompt，user=草稿+SRT ----

def test_build_messages_system_prompt_user_has_draft_and_srt():
    msgs = build_messages("P", "这是文案", "1\n00:00:00,000 --> 00:00:01,000\n字\n")
    assert msgs[0] == {"role": "system", "content": "P"}
    assert "这是文案" in msgs[1]["content"]
    assert "机密" not in msgs[1]["content"] and "00:00:01,000" in msgs[1]["content"]


def test_build_messages_empty_draft_gets_placeholder():
    msgs = build_messages("P", "  ", "SRT")
    assert "（未提供）" in msgs[1]["content"]


# ---- strip_code_fence：剥掉 LLM 爱包的 ``` 围栏 ----

def test_strip_code_fence_removes_fences_and_lang_tag():
    assert strip_code_fence("```srt\n1\n00:00:00,000 --> 00:00:01,000\n字\n```") == \
        "1\n00:00:00,000 --> 00:00:01,000\n字"
    assert strip_code_fence("```\n内容\n```") == "内容"


def test_strip_code_fence_plain_untouched():
    assert strip_code_fence("1\n00:00:00,000 --> 00:00:01,000\n字\n") == \
        "1\n00:00:00,000 --> 00:00:01,000\n字"


# ---- merge_corrected：机械校验——条数与每条时间戳必须一致，文本才可替换 ----

def test_merge_corrected_replaces_when_times_match():
    orig = read_srt_cues(ORIG_SRT)
    reply = read_srt_cues(ORIG_SRT.replace("推荐者", "推荐"))
    merged, stats = merge_corrected(orig, reply)
    assert stats == {"status": "ok", "changed": 1, "kept": 0}  # 仅条 2 文本实际有改动
    assert merged[1]["text"] == "今天给大家推荐M249"


def test_merge_corrected_count_mismatch_returns_original():
    orig = read_srt_cues(ORIG_SRT)
    reply = read_srt_cues(ORIG_SRT.split("\n\n")[0] + "\n")  # LLM 擅自并条
    merged, stats = merge_corrected(orig, reply)
    assert stats["status"] == "count_mismatch"
    assert merged == [dict(c) for c in orig]


def test_merge_corrected_time_mismatch_keeps_that_cue_only():
    orig = read_srt_cues(ORIG_SRT)
    tampered = ORIG_SRT.replace("推荐者", "推荐").replace("00:00:04,111", "00:00:04,999")
    merged, stats = merge_corrected(orig, read_srt_cues(tampered))
    assert stats == {"status": "ok", "changed": 0, "kept": 1}  # 条 1 文本没改 → changed 0
    assert merged[0]["text"].startswith("机密大坝")   # 条 1 正常替换
    assert merged[1]["text"] == "今天给大家推荐者M249"  # 条 2 时间轴被动过 → 回退原文


def test_merge_corrected_empty_reply_text_keeps_orig():
    orig = read_srt_cues(ORIG_SRT)
    reply = read_srt_cues(ORIG_SRT)
    reply[0]["text"] = ""  # SRT 里无法表达空文本块，直接构造
    merged, stats = merge_corrected(orig, reply)
    assert stats["kept"] == 1
    assert merged[0]["text"] == "机密大坝长工AZ-3的入场费是11万"


# ---- to_cues：映射到 bilingual.Cue 复用 render_srt ----

def test_to_cues_maps_to_cue_dataclass():
    blocks = read_srt_cues(ORIG_SRT)
    cues = to_cues(blocks)
    assert cues[0].start == 0.331 and cues[0].text_zh.startswith("机密大坝")
    assert cues[0].text_en is None


# ---- call_llm：重试/退避/错误分类（sleep、transport 注入） ----

def test_call_llm_success_first_try():
    calls = []
    result, err = call_llm([{"role": "user", "content": "x"}], key="sk-1", model="deepseek-flash",
                           transport=_fake_transport(200, "内容", calls), sleep=lambda s: None)
    assert err is None and result["content"] == "内容"
    assert result["usage"] == {"prompt_tokens": 10, "completion_tokens": 5}
    assert calls[0]["url"] == llm_fix.DEEPSEEK_URL
    assert calls[0]["key"] == "sk-1"
    assert calls[0]["payload"]["model"] == "deepseek-flash"
    assert calls[0]["payload"]["messages"][0]["content"] == "x"


def test_call_llm_retries_on_500_then_succeeds():
    states = iter([500, 200])

    def transport(url, payload, key, timeout):
        s = next(states)
        return s, ({"choices": [{"message": {"content": "ok"}}]} if s == 200
                   else {"error": {"message": "boom"}})

    result, err = call_llm([{"role": "user", "content": "x"}], key="k", model="m",
                           transport=transport, sleep=lambda s: None)
    assert err is None and result["content"] == "ok"


def test_call_llm_exhausts_retries():
    calls = []
    result, err = call_llm([{"role": "user", "content": "x"}], key="k", model="m",
                           transport=_fake_transport(500, "", calls), retries=2,
                           sleep=lambda s: None)
    assert result is None and "500" in err
    assert len(calls) == 3  # 首次 + 2 次重试


def test_call_llm_auth_error_no_retry():
    calls = []
    result, err = call_llm([{"role": "user", "content": "x"}], key="bad", model="m",
                           transport=_fake_transport(401, "", calls), sleep=lambda s: None)
    assert result is None and "Key" in err
    assert len(calls) == 1  # 认证失败重试无益


def test_call_llm_network_error_retries_then_succeeds():
    attempts = iter([Exception("conn reset"), None])

    def transport(url, payload, key, timeout):
        e = next(attempts)
        if e:
            raise e
        return 200, {"choices": [{"message": {"content": "ok"}}]}

    result, err = call_llm([{"role": "user", "content": "x"}], key="k", model="m",
                           transport=transport, sleep=lambda s: None)
    assert err is None and result["content"] == "ok"


def test_call_llm_backoff_grows_and_notifies():
    sleeps = []
    marks = []

    def transport(url, payload, key, timeout):
        return 503, {}

    call_llm([{"role": "user", "content": "x"}], key="k", model="m", transport=transport,
             retries=2, backoff=2.0, sleep=sleeps.append,
             on_retry=lambda n, e: marks.append((n, e)))
    assert sleeps == [2.0, 4.0]
    assert [n for n, _ in marks] == [1, 2]


# ---- fix_srt：端到端编排（假 transport） ----

def test_fix_srt_end_to_end_replaces_text_keeps_timeline():
    reply = ORIG_SRT.replace("推荐者", "推荐")
    calls = []
    fixed, info = fix_srt(ORIG_SRT, key="sk", model="deepseek-flash", prompt="P", draft="文案",
                          transport=_fake_transport(200, reply, calls))
    assert fixed is not None and "今天给大家推荐M249" in fixed
    assert "00:00:00,331 --> 00:00:02,742" in fixed      # 时间轴原样
    assert info["status"] == "ok" and info["changed"] == 1
    assert info["usage"]["completion_tokens"] == 5
    assert calls[0]["payload"]["messages"][0]["content"] == "P"


def test_fix_srt_count_mismatch_falls_back_to_original():
    one_cue = ORIG_SRT.split("\n\n")[0] + "\n"
    fixed, info = fix_srt(ORIG_SRT, key="k", model="m", prompt="P", draft="文案",
                          transport=_fake_transport(200, one_cue))
    assert fixed is not None and "机密大坝" in fixed and "推荐者" in fixed  # 全回原文
    assert info["status"] == "count_mismatch"


def test_fix_srt_strips_fence_from_reply():
    reply = "```srt\n" + ORIG_SRT.replace("推荐者", "推荐") + "```"
    fixed, info = fix_srt(ORIG_SRT, key="k", model="m", prompt="P", draft="文案",
                          transport=_fake_transport(200, reply))
    assert info["status"] == "ok" and "推荐M249" in fixed


def test_fix_srt_llm_error_returns_none():
    fixed, info = fix_srt(ORIG_SRT, key="bad", model="m", prompt="P", draft="文案",
                          transport=_fake_transport(401))
    assert fixed is None
    assert info["status"] == "llm_error" and "Key" in info["error"]


def test_fix_srt_garbage_reply_is_parse_error():
    fixed, info = fix_srt(ORIG_SRT, key="k", model="m", prompt="P", draft="文案",
                          transport=_fake_transport(200, "抱歉，我无法处理这个请求。"))
    assert fixed is None
    assert info["status"] == "parse_error"


# ---- build_change_report：本地 diff 产出改动清单（修正 43 条的 1001g3 实测形态） ----

def _orig_and_fixed():
    orig = read_srt_cues(ORIG_SRT)
    fixed = read_srt_cues(ORIG_SRT.replace("推荐者", "推荐").replace("入场费是11万", "入场费是11万【?】"))
    return orig, fixed


def test_change_report_lists_changed_cues_with_old_new():
    orig, fixed = _orig_and_fixed()
    report = build_change_report(orig, fixed, model="deepseek-flash",
                                 stats={"status": "ok", "changed": 2, "kept": 0})
    assert "[#1] 00:00:00,331" in report
    assert "- 机密大坝长工AZ-3的入场费是11万" in report
    assert "+ 机密大坝长工AZ-3的入场费是11万【?】" in report
    assert "[#2] 00:00:03,132" in report
    assert "- 今天给大家推荐者M249" in report and "+ 今天给大家推荐M249" in report


def test_change_report_has_header_and_uncertain_section():
    orig, fixed = _orig_and_fixed()
    report = build_change_report(orig, fixed, model="deepseek-flash",
                                 stats={"status": "ok", "changed": 2, "kept": 0})
    assert "deepseek-flash" in report
    assert "共 2 条" in report and "修改 2" in report
    assert "存疑待复核" in report
    assert "[#1]" in report.split("存疑待复核")[1]  # 【?】条目进复核索引


def test_change_report_no_changes_returns_empty():
    orig = read_srt_cues(ORIG_SRT)
    assert build_change_report(orig, orig, model="m",
                               stats={"status": "ok", "changed": 0, "kept": 0}) == ""


def test_change_report_uncertain_only_marker_addition_counts():
    # 纯加【?】也是改动（对照 1001g3 #4/#20）
    orig, fixed = _orig_and_fixed()
    fixed = read_srt_cues(ORIG_SRT)  # 只给原文
    fixed[0] = {**fixed[0], "text": fixed[0]["text"] + "【?】"}
    report = build_change_report(orig, fixed, model="m",
                                 stats={"status": "ok", "changed": 1, "kept": 0})
    assert "+ " in report and "【?】" in report


def test_change_report_path_ai_srt_no_double_ai():
    # 回归：1009AK12 实测产出「x.ai.ai.改动清单.txt」——suffix 参数里误带 .ai.
    assert llm_fix.change_report_path("D:/out/1009AK12.ai.srt") == Path("D:/out/1009AK12.ai.改动清单.txt")


def test_change_report_path_plain_srt():
    assert llm_fix.change_report_path("D:/out/video.srt") == Path("D:/out/video.改动清单.txt")
