namespace SubtitlesFree.Core;

/// <summary>一份热词组：组名 + 专有名词（逗号/换行分隔）。按领域各建一份，生成时选其一。</summary>
public sealed class HotwordSet
{
    public string Name { get; set; } = "";

    public string Words { get; set; } = "";
}

/// <summary>用户设置（%AppData%\SubtitlesFree\settings.json）。</summary>
public sealed class AppSettings
{
    /// <summary>Python 解释器路径；空 = 自动探测（设置优先 → App 旁 venv → PATH）。</summary>
    public string PythonPath { get; set; } = "";

    public string Model { get; set; } = "large-v3-turbo";

    /// <summary>zh | bilingual</summary>
    public string LanguageMode { get; set; } = "zh";

    /// <summary>float16 | int8_float16</summary>
    public string ComputeType { get; set; } = "float16";

    /// <summary>专有名词热词，逗号/换行分隔。</summary>
    public string Hotwords { get; set; } = "";

    /// <summary>热词组列表（按领域一份一份）。旧 Hotwords 单串由 EnsureMigrated 迁成「默认」组。</summary>
    public List<HotwordSet> HotwordSets { get; set; } = [];

    /// <summary>当前使用的组名；空 = 不使用热词。</summary>
    public string ActiveHotwordSet { get; set; } = "";

    /// <summary>模型下载走 hf-mirror.com（国内加速）。</summary>
    public bool UseMirror { get; set; } = true;

    /// <summary>字幕单条字数上限（不含空格）。超出时在标点/停顿处断开。默认 18（B站单行安全宽度）。</summary>
    public int MaxChars { get; set; } = 18;

    /// <summary>断句后剩余不超过此字数时并入前一条，避免孤字尾。默认 4；0 = 关闭。</summary>
    public int AbsorbChars { get; set; } = 4;

    public string LastInputDir { get; set; } = "";
}
