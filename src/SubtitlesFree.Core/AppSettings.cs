namespace SubtitlesFree.Core;

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

    /// <summary>模型下载走 hf-mirror.com（国内加速）。</summary>
    public bool UseMirror { get; set; } = true;

    public string LastInputDir { get; set; } = "";
}
