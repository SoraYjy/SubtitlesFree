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
