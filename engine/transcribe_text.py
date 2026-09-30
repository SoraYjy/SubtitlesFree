"""txt 转写模式：VAD 语音区间 → 逐区间检测语言 → 相邻同语言合并 → 按语言逐段转写 → 拼接成纯文本。

与 SRT 模式的差别：整条音频钉死 language="zh" 会把成段英文翻成中文；whisperx 的
language=None 也只看前 30s 检测一次（asr.py detect_language）。故按 VAD 区间（边界
落在静音处，语言切换处几乎必有停顿）逐个检测——成段英文可原样转出；相邻同语言区间
再合并成段送转写，同语言连续句不丢上下文。

纯函数（decide_language/join_segments/format_transcript）与编排（transcribe_blocks）
分离；whisperx 内部 API（vad_model/_vad_params/detect_language 镜像）全部收在
_default_* 工厂里懒加载，编排通过 DI 参数注入，测试无需 whisperx。whisperx 已
pin 3.4.5，升版须复查两个 _default_*。
"""
from contextlib import nullcontext

from textutil import clean_text

# 检测置信度低于此值兜底 zh（短块检测噪声大，asr.py:288 官方 warning）
CONFIDENCE_THRESHOLD = 0.6

# 词间无空格的语言（拼接时直接相连）；其余（en/de/fr…）按空格拼接
_CJK_LANGS = {"zh", "ja", "ko", "yue"}


def build_initial_prompt(hotwords: list[str]) -> str | None:
    """构造 initial_prompt：普通话句子引导 + 热词纠错（SRT/TXT 两模式统一）。

    「以下是普通话的句子。」引导 whisper 输出标点——txt 短区间孤立转写会丢标点
    （实测短句全无逗号句号）；srt 的断句靠 segment 文本标点映射，0926m249 实测
    无标点长串只能按字数上限硬切出词中切 ×5，标点密度直接决定切点质量。
    v1.0.0~0927 的 srt 模式只注热词（无热词则无 prompt，断句退化为 53 碎条 vs
    83 正常），2026-09-30 起经用户批准两模式统一加引导。
    """
    parts = ["以下是普通话的句子。"]
    if hotwords:
        parts.append("以下是可能出现的专有名词：" + "，".join(hotwords) + "。")
    return "".join(parts)


def decide_language(code: str, prob: float, default: str = "zh") -> str:
    """检测结果 → 实际转写语言：空码或置信度不足用默认（zh）。"""
    if not code or prob < CONFIDENCE_THRESHOLD:
        return default
    return code


def join_segments(texts: list[str], lang: str) -> str:
    """块内 whisper segments 文本拼接：CJK 直接相连，拉丁语系空格相连。"""
    stripped = [t.strip() for t in texts if t and t.strip()]
    if not stripped:
        return ""
    sep = "" if lang in _CJK_LANGS else " "
    return sep.join(stripped)


def format_transcript(blocks: list[str]) -> str:
    """语言块 → txt 全文：块间空行分段（自然段落感），末尾单个换行。"""
    texts = [b.strip() for b in blocks if b and b.strip()]
    if not texts:
        return ""
    return "\n\n".join(texts) + "\n"


def merge_language_runs(tagged: list[dict]) -> list[dict]:
    """相邻同语言区间合并成一个转写段：语言纯度不破，同语言连续句仍整段转写（保上下文）。"""
    runs: list[dict] = []
    for t in tagged:
        if runs and runs[-1]["lang"] == t["lang"]:
            runs[-1]["end"] = t["end"]
        else:
            runs.append(dict(t))
    return runs


def transcribe_blocks(model, audio, *, chunk_size=30, sample_rate=16000,
                      vad_chunker=None, detect=None, on_progress=None,
                      stdout_guard=None) -> list[str]:
    """分块转写编排（两阶段）：

    1) VAD 区间逐个检测语言（每区间一次编码前向，便宜）；
    2) 相邻同语言区间合并成段，逐段按语言转写（贵），段内拼接。

    model: whisperx.load_model 的 pipeline；vad_chunker/detect 可注入替身（测试），
    缺省用 whisperx 内部实现的 _default_*。stdout_guard 生产端传 emitter 的
    _stdout_to_stderr（第三方库 print 不得污染 JSON Lines stdout）。静音段（无
    segments）产出空文本，跳过。on_progress 按转写段推进 (i+1)/n。
    """
    vad_chunker = vad_chunker or _default_vad_chunker(model, chunk_size)
    detect = detect or _default_detect(model)
    guard = stdout_guard or nullcontext

    tagged: list[dict] = []
    for region in vad_chunker(audio):
        a = audio[int(region["start"] * sample_rate): int(region["end"] * sample_rate)]
        with guard():
            code, prob = detect(a)
        tagged.append({"start": region["start"], "end": region["end"],
                       "lang": decide_language(code, prob)})

    runs = merge_language_runs(tagged)
    blocks: list[str] = []
    for i, run in enumerate(runs):
        a = audio[int(run["start"] * sample_rate): int(run["end"] * sample_rate)]
        with guard():
            result = model.transcribe(a, batch_size=16, language=run["lang"])
        text = join_segments(
            [clean_text(s.get("text", "")) for s in result.get("segments", [])], run["lang"])
        if text:
            blocks.append(text)
        if on_progress is not None:
            on_progress((i + 1) / len(runs))
    return blocks


def _default_vad_chunker(model, chunk_size: int):
    """VAD 语音区间（语言块）：Binarize 直接输出，不走 merge_chunks 的批处理窗合并。

    asr.py 的 merge_chunks 会把相邻区间合并进 ≤chunk_size 的批处理窗——语言切换
    处的停顿被抹掉，成段英文就被 zh 检测吞了（实测）。Binarize(max_duration) 只在
    最弱得分点切超长区间，输出以自然停顿为界的区间，正是语言块的粒度。
    silero 等 Vad 子类（非默认）无 Binarize 输入可复用，退回其 merge_chunks（粗粒度）。
    """
    from whisperx.asr import SAMPLE_RATE
    from whisperx.vads import Vad
    from whisperx.vads.pyannote import Binarize, Pyannote

    def chunk(audio):
        waveform = model.vad_model.preprocess_audio(audio)
        scores = model.vad_model({"waveform": waveform, "sample_rate": SAMPLE_RATE})
        if isinstance(model.vad_model, Pyannote):
            binarize = Binarize(max_duration=chunk_size,
                                onset=model._vad_params["vad_onset"],
                                offset=model._vad_params["vad_offset"])
            regions = binarize(scores)
            return [{"start": s.start, "end": s.end} for s in regions.get_timeline()]
        return model.vad_model.merge_chunks(
            scores, chunk_size,
            onset=model._vad_params["vad_onset"], offset=model._vad_params["vad_offset"])

    return chunk


def _default_detect(model):
    """带置信度的语言检测：镜像 whisperx asr.py:286-298 detect_language（其只返码不返概率）。"""
    from whisperx.audio import N_SAMPLES, log_mel_spectrogram

    def detect(audio):
        m = model.model  # faster-whisper WhisperModel
        n_mels = m.feat_kwargs.get("feature_size")
        segment = log_mel_spectrogram(
            audio[: N_SAMPLES],
            n_mels=n_mels if n_mels is not None else 80,
            padding=0 if audio.shape[0] >= N_SAMPLES else N_SAMPLES - audio.shape[0])
        encoder_output = m.encode(segment)
        results = m.model.detect_language(encoder_output)
        language_token, language_probability = results[0][0]
        return language_token[2:-2], float(language_probability)

    return detect
