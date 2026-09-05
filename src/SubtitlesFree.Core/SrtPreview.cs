using System.Globalization;

namespace SubtitlesFree.Core;

public sealed record SrtCue(int Index, TimeSpan Start, TimeSpan End, string Text);

public static class SrtPreview
{
    /// <summary>宽容解析 SRT 前若干条（坏块跳过）。用于完成后预览。</summary>
    public static IReadOnlyList<SrtCue> ParseFirst(string path, int count = 5)
    {
        var result = new List<SrtCue>();
        if (!File.Exists(path)) return result;
        string content = File.ReadAllText(path);
        // 保留文件原换行风格（CRLF/LF），多行文本按原样返回（测试契约：中文\r\nEnglish）
        string nl = content.Contains("\r\n") ? "\r\n" : "\n";
        string[] blocks = content.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (string block in blocks)
        {
            if (result.Count >= count) break;
            string[] lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) continue;
            // 行0：序号（可缺）；行1：时间轴；行2+：文本
            int tsLine = lines[0].Contains("-->") ? 0 : 1;
            if (tsLine >= lines.Length || !lines[tsLine].Contains("-->")) continue;
            string[] parts = lines[tsLine].Split("-->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (!TimeSpan.TryParseExact(parts[0].Trim(), "hh\\:mm\\:ss\\,fff",
                    CultureInfo.InvariantCulture, out var start)
                || !TimeSpan.TryParseExact(parts[1].Trim(), "hh\\:mm\\:ss\\,fff",
                    CultureInfo.InvariantCulture, out var end))
                continue;
            int idx = int.TryParse(lines[0].Trim(), out int n) && tsLine == 1 ? n : result.Count + 1;
            string text = string.Join(nl, lines[(tsLine + 1)..]);
            result.Add(new SrtCue(idx, start, end, text));
        }
        return result;
    }
}
