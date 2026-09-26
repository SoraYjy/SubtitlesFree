"""whisper 输出文本清理：进管道前的统一清洗（SRT/TXT 两路共用）。"""


def clean_text(text: str) -> str:
    """去掉 U+FFFD 替换字符。

    whisper 分词器是字节级 BPE，模型偶发采样出残缺的多字节序列，HF tokenizer 按
    errors=replace 解码成 �（whisper 生态已知伪影；88 分钟实测 4 处，用户称此前
    也常见）。上游不可修，在两条 intake（segmentation 的 text/words、
    transcribe_text 的 segment text）边界统一过滤。
    """
    return text.replace("�", "")
