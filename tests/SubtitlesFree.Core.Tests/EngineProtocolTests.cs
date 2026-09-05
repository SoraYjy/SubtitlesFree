using SubtitlesFree.Core;
using Xunit;

namespace SubtitlesFree.Core.Tests;

public class EngineProtocolTests
{
    [Theory]
    [InlineData("""{"type":"stage","value":"vad"}""", "vad")]
    [InlineData("""{"type":"stage","value":"translate"}""", "translate")]
    public void Parses_StageEvent(string line, string expected)
        => Assert.Equal(expected, Assert.IsType<StageEvent>(EngineProtocol.ParseLine(line)!).Stage);

    [Fact]
    public void Parses_ProgressEvent()
        => Assert.Equal(0.42, Assert.IsType<ProgressEvent>(
            EngineProtocol.ParseLine("""{"type":"progress","value":0.42}""")!).Value);

    [Fact]
    public void Parses_LogEvent_WithChinese()
    {
        var ev = Assert.IsType<LogEvent>(EngineProtocol.ParseLine(
            """{"type":"log","level":"warn","message":"显存不足"}""")!);
        Assert.Equal("warn", ev.Level);
        Assert.Equal("显存不足", ev.Message);
    }

    [Fact]
    public void Parses_DoneEvent()
    {
        var ev = Assert.IsType<DoneEvent>(EngineProtocol.ParseLine(
            """{"type":"done","srt_path":"D:\\a.srt","segments":12,"video_sec":91.5,"elapsed_sec":33.3}""")!);
        Assert.Equal("D:\\a.srt", ev.SrtPath);
        Assert.Equal(12, ev.Segments);
        Assert.Equal(91.5, ev.VideoSec);
        Assert.Equal(33.3, ev.ElapsedSec);
    }

    [Fact]
    public void Parses_DoneEvent_MissingOptionals_DefaultToZero()
    {
        var ev = Assert.IsType<DoneEvent>(EngineProtocol.ParseLine(
            """{"type":"done","srt_path":"a.srt","segments":3}""")!);
        Assert.Equal(0, ev.VideoSec);
        Assert.Equal(0, ev.ElapsedSec);
    }

    [Fact]
    public void Parses_ErrorEvent_OptionalFields()
    {
        var ev = Assert.IsType<ErrorEvent>(EngineProtocol.ParseLine(
            """{"type":"error","code":"oom"}""")!);
        Assert.Equal("oom", ev.Code);
        Assert.Null(ev.Message);
        Assert.Null(ev.Hint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"type":"unknown"}""")]
    [InlineData("""{"no_type":1}""")]
    [InlineData("""{"type":"stage"}""")]
    [InlineData("""{"type":"log"}""")]
    [InlineData("""{"type":"progress","value":"abc"}""")]
    public void Returns_Null_For_BadLines(string line)
        => Assert.Null(EngineProtocol.ParseLine(line));
}
