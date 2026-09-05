using SubtitlesFree.Core;
using Xunit;

namespace SubtitlesFree.Core.Tests;

public class EngineLauncherTests
{
    private static readonly string FakeEngine = Path.Combine(
        AppContext.BaseDirectory, "fake_engine.py");

    /// <summary>子进程测试用 Python：SF_TEST_PYTHON 或缺省 venv；换机器不存在则跳过（机器解耦）。
    /// BuildCommandLine 纯函数测试不经此路径，保持无条件运行。</summary>
    private static string FindPython() => TestPython.Resolve();

    /// <summary>生成带参数的假引擎包装脚本。
    /// brief 原写法 FakeEngine + " --error" 会被 BuildCommandLine 的引号整体包成一个文件名，
    /// python 无法打开（实测 exit 2 "can't open file"）；改用包装脚本把参数放进 sys.argv 后 runpy 执行，
    /// fake_engine.py 本体与 EngineLauncher 实现均保持不变。</summary>
    private static string FakeEngineWithArgs(params string[] scriptArgs)
    {
        if (scriptArgs.Length == 0) return FakeEngine;
        string wrapper = Path.Combine(AppContext.BaseDirectory,
            "fake_engine_wrapper_" + string.Join("_", scriptArgs.Select(a => a.TrimStart('-'))) + ".py");
        File.WriteAllText(wrapper,
            "import sys, runpy\n" +
            "sys.argv = ['fake_engine.py'" + string.Concat(scriptArgs.Select(a => $", '{a}'")) + "]\n" +
            $"runpy.run_path(r'{FakeEngine}', run_name='__main__')\n");
        return wrapper;
    }

    [Fact]
    public void BuildCommandLine_IncludesAllArgs()
    {
        var req = new EngineRequest(
            @"D:\vid\我的 视频.mp4", @"D:\vid\我的 视频.srt", "large-v3-turbo",
            "int8_float16", "bilingual", "ComfyUI,SDXL", true);
        var (exe, args) = EngineLauncher.BuildCommandLine(
            @"E:\py\python.exe", @"D:\repo\engine\engine.py", req);
        Assert.Equal(@"E:\py\python.exe", exe);
        Assert.Contains("\"D:\\repo\\engine\\engine.py\"", args);
        Assert.Contains("--video \"D:\\vid\\我的 视频.mp4\"", args);
        Assert.Contains("--output \"D:\\vid\\我的 视频.srt\"", args);
        Assert.Contains("--model large-v3-turbo", args);
        Assert.Contains("--compute-type int8_float16", args);
        Assert.Contains("--bilingual", args);
        Assert.Contains("--hotwords \"ComfyUI,SDXL\"", args);
        Assert.DoesNotContain("--language", args); // 默认 zh 不用传
    }

    [Fact]
    public void BuildCommandLine_ZhOnly_HasNoBilingualFlag()
    {
        var req = new EngineRequest("a.mp4", "a.srt", "small", "float16", "zh", "", true);
        string args = EngineLauncher.BuildCommandLine("p", "s", req).args;
        Assert.DoesNotContain("--bilingual", args);
        Assert.DoesNotContain("--hotwords", args);
    }

    [SkippableFact]
    public async Task RunAsync_WithFakeEngine_ReceivesAllEvents()
    {
        var launcher = new EngineLauncher(FindPython(), FakeEngine);
        var events = new List<EngineEvent>();
        var raws = new List<string>();
        await launcher.RunAsync(new EngineRequest("v.mp4", "v.srt", "small", "float16", "zh", "", true),
            CancellationToken.None, events.Add, raws.Add);
        Assert.Contains(events, e => e is StageEvent { Stage: "vad" } or StageEvent { Stage: "load_model" });
        Assert.Contains(events, e => e is ProgressEvent { Value: 0.5 });
        Assert.Contains(events, e => e is DoneEvent { Segments: 2 });
        Assert.Contains(raws, r => r.Contains("not-json-garbage")); // 非 JSON 行进 rawLog
        Assert.DoesNotContain(raws, r => r.TrimStart().StartsWith("{\"type\"")); // JSON 行不重复进 rawLog
    }

    [SkippableFact]
    public async Task RunAsync_FakeEngineError_CompletesWithoutThrowing()
    {
        var launcher = new EngineLauncher(FindPython(), FakeEngineWithArgs("--error"));
        var events = new List<EngineEvent>();
        await launcher.RunAsync(new EngineRequest("v.mp4", "v.srt", "small", "float16", "zh", "", true),
            CancellationToken.None, events.Add);
        Assert.Contains(events, e => e is ErrorEvent { Code: "oom" });
    }

    [SkippableFact]
    public async Task RunAsync_Cancel_ThrowsAndKillsProcess()
    {
        var launcher = new EngineLauncher(FindPython(), FakeEngineWithArgs("--sleep"));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            launcher.RunAsync(new EngineRequest("v.mp4", "v.srt", "small", "float16", "zh", "", true),
                cts.Token));
        // 进程被杀后 RunAsync 已返回（无残留等待）
    }
}
