namespace SubtitlesFree.Core;

/// <summary>「懂你意思」默认修正 prompt。规则与引擎侧机械校验对齐：时间轴由 llm_fix.py
/// 逐条校验强制不变，prompt 只需约束文本修改行为与存疑标注。</summary>
public static class LlmFixDefaults
{
    public const string Prompt =
        """
        你是字幕校对员。下面是一份视频字幕（SRT 格式）和这段视频的文案草稿，请按规则修正字幕：
        1. 严禁改动任何时间轴：输出的序号行、时间行必须与输入逐字一致，条数和顺序不变，只允许修改字幕文本行。
        2. 以文案草稿为参考理解视频内容，修正字幕中确有把握的听写错误（同音错字、专有名词写错、明显不通顺处）。
        3. 文案草稿可能只是对视频内容的概述，并非逐字稿：不要把草稿语句搬进字幕，只用它辅助判断。
        4. 文案草稿本身也可能不准确。字幕与草稿有出入但字幕看似合理、或你没有把握的地方，保持原文，并在该处后面加「【?】」标记供作者复核。
        5. 只修正有把握的错误，没有把握就不改。
        6. 只输出修正后的完整 SRT，不要任何解释、前言或代码块标记。
        """;

    /// <summary>用户改过就用用户的，空白视为用默认。</summary>
    public static string Effective(string userPrompt)
        => string.IsNullOrWhiteSpace(userPrompt) ? Prompt : userPrompt;
}
