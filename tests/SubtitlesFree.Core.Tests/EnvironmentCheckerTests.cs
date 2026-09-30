using SubtitlesFree.Core;
using Xunit;

namespace SubtitlesFree.Core.Tests;

public static class TestPython
{
    /// <summary>解析测试用 Python：SF_TEST_PYTHON 优先，缺省回退本仓库 venv。
    /// 换机器跑测试时路径不存在 → Skip，而非失败（机器解耦）。</summary>
    public static string Resolve()
    {
        string? python = Environment.GetEnvironmentVariable("SF_TEST_PYTHON");
        if (string.IsNullOrWhiteSpace(python))
            python = @"D:\sora\SubtitlesFree\engine\.venv\Scripts\python.exe";
        Skip.If(!File.Exists(python), $"测试 Python 不存在，跳过（可设 SF_TEST_PYTHON 指向 engine venv）：{python}");
        return python;
    }
}

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
        bool model = true, bool ffmpeg = true, bool torchSpec = false, bool cpuBuild = false) =>
        new(py, "Python 3.11", torch, cuda, torchSpec, cpuBuild, wx, model,
            model ? "已缓存" : "未缓存：应放到 <engine/models 目录>（首跑自动下载；国内可开镜像或按 README 用 ModelScope 预下载）",
            "GPU", 12288, 100, ffmpeg);

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
        Assert.Equal("ffmpeg.exe 缺失：从仓库 engine/ffmpeg.exe 恢复，或安装到 PATH（winget install Gyan.FFmpeg）", items[4].Hint);
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

    // ---- torch import 失败的分类提示（03:51 实测：瞬时 DLL 失败被显示成「未安装」误导重装） ----

    [Fact]
    public void BuildItems_TorchImportFailButInstalled_HintsRetestNotPip()
    {
        // find_spec 能找到 torch = 装了但 import 挂（瞬时驱动/杀软占用），不该叫人 pip install
        var torch = EnvironmentChecker.BuildItems(MakeReport(torch: false, cuda: false, torchSpec: true))
            .Single(i => i.Label == "torch+CUDA");
        Assert.False(torch.Ok);
        Assert.Contains("已安装但加载失败", torch.Hint);
        Assert.DoesNotContain("pip install", torch.Hint);
    }

    [Fact]
    public void BuildItems_TorchTrulyMissing_KeepsPipInstallHint()
    {
        var torch = EnvironmentChecker.BuildItems(MakeReport(torch: false, cuda: false, torchSpec: false))
            .Single(i => i.Label == "torch+CUDA");
        Assert.Contains("pip install torch", torch.Hint);
        Assert.Contains("cu124", torch.Hint);
    }

    [Fact]
    public void BuildItems_CudaMissingCpuBuild_HintsWheelReinstallNotDriver()
    {
        // hint 三态盲区修复：CPU 版轮子（torch.version.cuda 为 None）装最新驱动也没用
        var torch = EnvironmentChecker.BuildItems(MakeReport(cuda: false, cpuBuild: true))
            .Single(i => i.Label == "torch+CUDA");
        Assert.False(torch.Ok);
        Assert.Contains("CPU 版", torch.Hint);
        Assert.Contains("cu124", torch.Hint);
        Assert.DoesNotContain("更新 NVIDIA 显卡驱动", torch.Hint);  // 换驱动没用，别误导
    }

    [SkippableFact]
    public async Task CheckAsync_WithVenvPython_TorchAndCudaOk()
    {
        // 集成测试：依赖 engine/.venv 已装好（SF_TEST_PYTHON 或缺省 venv，无则跳过）
        var report = await EnvironmentChecker.CheckAsync(TestPython.Resolve(), "large-v3-turbo");
        Assert.True(report.PythonOk);
        Assert.True(report.TorchOk);
        Assert.True(report.CudaOk);
        Assert.True(report.WhisperXOk);
        // GPU 型号 / ModelCached / ffmpeg 是否在 PATH 随机器与预下载状态而变，不做断言
    }

    [Fact]
    public void FindBundledFfmpeg_DevLayout_FindsCommittedBinary()
    {
        // ffmpeg.exe 随仓库分发（engine/ffmpeg.exe），开发布局从测试 bin 上溯必命中
        string? found = EnvironmentChecker.FindBundledFfmpeg();
        Assert.NotNull(found);
        Assert.Equal("ffmpeg.exe", Path.GetFileName(found));
        Assert.Equal("engine", Path.GetFileName(Path.GetDirectoryName(found!)));
    }

    [Fact]
    public void EngineRoots_DistInsideRepo_FindsRepoEngineRoot()
    {
        // dist-in-repo：dist/SubtitlesFree 自带 engine/（无模型），仓库根也有 engine/（有模型）
        string tmp = Path.Combine(Path.GetTempPath(), "sf-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string distApp = Path.Combine(tmp, "dist", "SubtitlesFree");
            Directory.CreateDirectory(Path.Combine(distApp, "engine"));
            Directory.CreateDirectory(Path.Combine(tmp, "engine", "models", "large-v3-turbo"));
            var roots = EnvironmentChecker.EngineRoots(distApp).ToList();
            Assert.Contains(distApp, roots);   // dist 自己的 engine/
            Assert.Contains(tmp, roots);       // 仓库根的 engine/
            bool cached = roots
                .SelectMany(r => EnvironmentChecker.LocalAsrCandidates("large-v3-turbo", r))
                .Any(Directory.Exists);
            Assert.True(cached);               // dist-in-repo 也能发现仓库模型 → 不再误报红叉
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
