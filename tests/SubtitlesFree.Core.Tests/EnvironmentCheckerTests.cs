using SubtitlesFree.Core;
using Xunit;

namespace SubtitlesFree.Core.Tests;

public class EnvironmentCheckerTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080 Ti, 12288, 6270", "NVIDIA GeForce RTX 3080 Ti", 12288, 6270)]
    [InlineData("NVIDIA GeForce RTX 4060, 8188, 0", "NVIDIA GeForce RTX 4060", 8188, 0)]
    public void ParseGpuLine_ParsesNameAndVram(string line, string name, int total, int used)
    {
        var (n, t, u) = EnvironmentChecker.ParseGpuLine(line);
        Assert.Equal(name, n);
        Assert.Equal(total, t);
        Assert.Equal(used, u);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("NVIDIA GeForce RTX 3080 Ti, abc, 6270")]
    public void ParseGpuLine_BadLines_ReturnNulls(string line)
    {
        var (n, t, u) = EnvironmentChecker.ParseGpuLine(line);
        Assert.Null(n);
        Assert.Null(t);
        Assert.Null(u);
    }

    [Theory]
    [InlineData("large-v3-turbo", "models--mobiuslabsgmbh--faster-whisper-large-v3-turbo")] // ruling 2：faster-whisper 1.2.1 映射 mobiuslabsgmbh
    [InlineData("large-v3", "models--Systran--faster-whisper-large-v3")]
    [InlineData("small", "models--Systran--faster-whisper-small")]
    public void ModelRepoDir_MapsModelToHfCacheDir(string model, string dir)
        => Assert.Equal(dir, EnvironmentChecker.ModelRepoDir(model));

    [Fact]
    public void LocalAsrCandidates_NonTurbo_OnlySelfNamedDir()
    {
        // 与 engine/pipeline.py 同语义：非 turbo 只认 engine/models/<model>，不认遗留 asr
        string[] dirs = EnvironmentChecker.LocalAsrCandidates("small", @"C:\app").ToArray();
        Assert.Equal([Path.Combine(@"C:\app", "engine", "models", "small")], dirs);
        Assert.DoesNotContain(dirs, d => d.EndsWith("asr", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocalAsrCandidates_Turbo_SelfNamedDirFirstThenLegacyAsr()
    {
        string[] dirs = EnvironmentChecker.LocalAsrCandidates("large-v3-turbo", @"C:\app").ToArray();
        Assert.Equal(
        [
            Path.Combine(@"C:\app", "engine", "models", "large-v3-turbo"), // 同名目录优先
            Path.Combine(@"C:\app", "engine", "models", "asr"),            // turbo 兼容 T5 遗留布局
        ], dirs);
    }

    [Fact]
    public void LocalAsrCandidates_OnlySelfNamedDirPreloaded_SatisfiesCacheCheck()
    {
        // 新机器只预下载 models/large-v3：large-v3 应判定已缓存（旧实现只探 asr 会误报未缓存）
        string root = Path.Combine(Path.GetTempPath(), "sftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "engine", "models", "large-v3"));
            Assert.Contains(EnvironmentChecker.LocalAsrCandidates("large-v3", root), Directory.Exists);
            Assert.DoesNotContain(EnvironmentChecker.LocalAsrCandidates("small", root), Directory.Exists); // 不跨模型顶替
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EnvReport MakeReport(
        bool py = true, bool torch = true, bool cuda = true, bool wx = true,
        bool model = true, bool ffmpeg = true) =>
        new(py, "Python 3.11", torch, cuda, wx, model, "已缓存", "GPU", 12288, 100, ffmpeg);

    [Fact]
    public void BuildItems_AllOk_FiveGreenItemsNoHints()
    {
        var items = EnvironmentChecker.BuildItems(MakeReport());
        Assert.Equal(["Python", "torch+CUDA", "whisperx", "模型缓存", "ffmpeg"],
            items.Select(i => i.Label));
        Assert.All(items, i => Assert.True(i.Ok));
        Assert.All(items, i => Assert.Equal("", i.Hint));
    }

    [Fact]
    public void BuildItems_AllMissing_EachHasSpecificHint()
    {
        var items = EnvironmentChecker.BuildItems(
            MakeReport(py: false, torch: false, cuda: false, wx: false, model: false, ffmpeg: false));
        Assert.All(items, i => Assert.False(i.Ok));
        Assert.Contains("Python 路径", items[0].Hint);
        Assert.Contains("cu124", items[1].Hint);           // torch 缺 → pip cu124 命令
        Assert.Equal("pip install whisperx", items[2].Hint);
        Assert.Contains("ModelScope", items[3].Hint);      // 模型缺 → 预下载提示
        Assert.Equal("安装 ffmpeg 并加入 PATH（winget install Gyan.FFmpeg）", items[4].Hint);
    }

    [Fact]
    public void BuildItems_TorchOkCudaMissing_HintsDriverNotPip()
    {
        var torch = EnvironmentChecker.BuildItems(MakeReport(cuda: false))
            .Single(i => i.Label == "torch+CUDA");
        Assert.False(torch.Ok);
        Assert.Contains("驱动", torch.Hint);
        Assert.DoesNotContain("pip", torch.Hint);
    }

    [Fact]
    public async Task CheckAsync_OnThisMachine_TorchAndCudaOk()
    {
        // 本机集成测试：依赖 engine/.venv 已装好（Task 1 前置）
        string python = Environment.GetEnvironmentVariable("SF_TEST_PYTHON")
            ?? @"D:\sora\SubtitlesFree\engine\.venv\Scripts\python.exe";
        var report = await EnvironmentChecker.CheckAsync(python, "large-v3-turbo");
        Assert.True(report.PythonOk);
        Assert.True(report.TorchOk);
        Assert.True(report.CudaOk);
        Assert.True(report.WhisperXOk);
        Assert.True(report.ModelCached);  // ruling 3：T5 已把模型移到 engine/models/asr（本地路径存在）
        Assert.Equal("NVIDIA GeForce RTX 3080 Ti", report.GpuName);
        // ruling 1：本机 ffmpeg 不在 PATH 上 → FfmpegOk=false，AllOk 因此为 false
        Assert.False(report.FfmpegOk);
        Assert.False(report.AllOk);
    }
}
