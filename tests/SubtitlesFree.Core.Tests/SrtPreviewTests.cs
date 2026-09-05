using System.Text;
using SubtitlesFree.Core;
using Xunit;

namespace SubtitlesFree.Core.Tests;

public class SrtPreviewTests
{
    private static string WriteTemp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "sf-tests", Guid.NewGuid().ToString("N") + ".srt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(true));
        return path;
    }

    [Fact]
    public void ParseFirst_ReadsBilingualCues()
    {
        string path = WriteTemp(
            "1\r\n00:00:01,200 --> 00:00:03,800\r\n中文\r\nEnglish\r\n\r\n" +
            "2\r\n00:00:04,000 --> 00:00:06,000\r\n第二句\r\n");
        var cues = SrtPreview.ParseFirst(path, 5);
        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.2), cues[0].Start);
        Assert.Equal("中文\r\nEnglish", cues[0].Text);
    }

    [Fact]
    public void ParseFirst_LimitsCount()
    {
        string path = WriteTemp(
            "1\r\n00:00:01,000 --> 00:00:02,000\r\n一\r\n\r\n" +
            "2\r\n00:00:02,000 --> 00:00:03,000\r\n二\r\n\r\n" +
            "3\r\n00:00:03,000 --> 00:00:04,000\r\n三\r\n");
        Assert.Equal(2, SrtPreview.ParseFirst(path, 2).Count);
    }

    [Fact]
    public void ParseFirst_SkipsMalformedBlocks()
    {
        string path = WriteTemp(
            "garbage\r\n\r\n" +
            "1\r\n00:00:01,000 --> 00:00:02,000\r\n好块\r\n");
        var cues = SrtPreview.ParseFirst(path, 5);
        Assert.Single(cues);
    }

    [Fact]
    public void ParseFirst_EmptyFile_ReturnsEmpty()
        => Assert.Empty(SrtPreview.ParseFirst(WriteTemp(""), 5));
}
