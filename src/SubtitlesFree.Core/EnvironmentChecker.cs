using System.Diagnostics;
using System.Text;

namespace SubtitlesFree.Core;

public sealed record EnvReport(
    bool PythonOk, string PythonDetail,
    bool TorchOk, bool CudaOk, bool TorchInstalledSpec, bool CpuBuild,
    bool WhisperXOk,
    bool ModelCached, string ModelDetail,
    string? GpuName, int? VramTotalMb, int? VramUsedMb,
    bool FfmpegOk)
{
    public bool AllOk => PythonOk && TorchOk && WhisperXOk && ModelCached && FfmpegOk;
}

/// <summary>环境状态条单项（spec §6）：Label + 通过与否 + 缺项修复提示。</summary>
public sealed record EnvItem(string Label, bool Ok, string Hint);

public static class EnvironmentChecker
{
    /// <summary>探测 python/torch/whisperx/模型缓存/ffmpeg/显卡。每项独立，失败不中断。</summary>
    public static async Task<EnvReport> CheckAsync(
        string? pythonPath, string selectedModel, Action<string>? log = null)
    {
        string python = string.IsNullOrWhiteSpace(pythonPath) ? "python" : pythonPath;

        // 1) Python
        string? ver = (await RunCaptureAsync(python,
            "-c \"import sys;print(sys.version.split()[0])\"", 15, log)).Out;
        bool pyOk = ver is not null;
        string pyDetail = ver is null ? "未找到可用的 python" : $"Python {ver.Trim()}";
        log?.Invoke(pyOk ? $"{pyDetail} @ {python}" : pyDetail);

        // 2) torch + CUDA。import 失败 ≠ 未安装：瞬时驱动重置/杀软锁 DLL 同样让 import 挂掉
        //    （2026-10-01 实测：游戏启动瞬间 import 失败，稍后自愈，却被显示成「未安装」误导重装）。
        //    故 find_spec 区分两种失败；stderr 尾部进日志定责；torch.version.cuda 区分 CPU 轮子。
        bool torchOk = false, cudaOk = false, torchSpec = false, cpuBuild = false;
        string torchDetail = "torch 未安装";
        if (pyOk)
        {
            Probe probe = await RunCaptureAsync(python,
                "-c \"import torch;print(torch.__version__);print(torch.version.cuda);print(torch.cuda.is_available())\"",
                60, log);
            string[] lines = (probe.Out ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            torchOk = lines.Length >= 1;
            cpuBuild = lines.Length >= 2 && lines[1] == "None";
            cudaOk = lines.Length >= 3 && lines[^1].Equals("True", StringComparison.OrdinalIgnoreCase);
            if (torchOk) torchDetail = $"torch {lines[0]}";
            else
            {
                Probe spec = await RunCaptureAsync(python,
                    "-c \"import importlib.util;print(importlib.util.find_spec('torch') is not None)\"", 15, log);
                torchSpec = (spec.Out ?? "").Trim() == "True";
                if (torchSpec)
                {
                    torchDetail = "torch 已安装但加载失败";
                    if (probe.ErrTail is not null) log?.Invoke($"torch 导入报错：{probe.ErrTail}");
                }
            }
            log?.Invoke(torchDetail + (cudaOk ? "，CUDA 可用" : "，CUDA 不可用"));
        }

        // 3) whisperx（import 成功时无 stdout 输出，须主动打印标记，否则空输出会被当作失败）
        bool whisperxOk = pyOk
            && (await RunCaptureAsync(python, "-c \"import whisperx;print('ok')\"", 60, log)).Out is not null;
        log?.Invoke(whisperxOk ? "whisperx 已安装" : "whisperx 未安装");

        // 4) 模型缓存：本地 engine/models/<model>（turbo 另认遗留 engine/models/asr）或 HF hub 缓存，
        //    候选根逐级上溯（EngineRoots，兼容 dist-in-repo），与 engine/pipeline.py 的 _models_roots 同语义
        string[] roots = EngineRoots(AppContext.BaseDirectory).ToArray();
        string hubDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "huggingface", "hub", ModelRepoDir(selectedModel));
        bool modelCached = roots.SelectMany(r => LocalAsrCandidates(selectedModel, r)).Any(Directory.Exists)
            || Directory.Exists(hubDir);
        string expectedDir = roots.Length > 0
            ? Path.Combine(roots[0], "engine", "models", selectedModel)
            : Path.Combine(AppContext.BaseDirectory, "engine", "models", selectedModel);
        string modelDetail = modelCached
            ? $"{selectedModel} 已缓存"
            : $"未缓存：应放到 {expectedDir}（首跑自动下载；国内可开镜像或按 README 用 ModelScope 预下载）";
        log?.Invoke(modelDetail);

        // 5) ffmpeg（whisperx.load_audio 硬依赖）：内置优先（随仓库/发布包分发），否则探 PATH
        string? bundledFfmpeg = FindBundledFfmpeg();
        bool ffmpegOk = bundledFfmpeg is not null
            || (await RunCaptureAsync("ffmpeg", "-version", 10, log)).Out is not null;
        log?.Invoke(ffmpegOk
            ? bundledFfmpeg is not null ? $"ffmpeg 内置：{bundledFfmpeg}" : "ffmpeg 已安装（系统 PATH）"
            : "ffmpeg 未找到（内置与系统 PATH 均无）");

        // 6) GPU（nvidia-smi，无则跳过）
        string? gpuOut = (await RunCaptureAsync("nvidia-smi",
            "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits", 10, log)).Out;
        (string? gpuName, int? total, int? used) = (null, null, null);
        if (gpuOut is not null)
        {
            string firstLine = gpuOut.Split('\n')[0];
            (gpuName, total, used) = ParseGpuLine(firstLine);
        }

        return new EnvReport(pyOk, pyDetail, torchOk, cudaOk, torchSpec, cpuBuild,
            whisperxOk, modelCached, modelDetail, gpuName, total, used, ffmpegOk);
    }

    /// <summary>EnvReport → 状态条逐项展示（纯函数）。缺项按项给修复提示；正常项 Hint 为空。
    /// torch 失败按成因分三类：真未装（pip 命令）/ 装了加载失败（瞬时，重测）/ CPU 轮子（重装 cu124）。</summary>
    public static IReadOnlyList<EnvItem> BuildItems(EnvReport r)
    {
        string torchHint;
        if (!r.TorchOk)
            torchHint = r.TorchInstalledSpec
                ? "torch 已安装但加载失败（多为驱动重置/杀软瞬时占用，与显卡游戏等同时刻易发）：稍后点「检测环境」重测；仍失败看日志「torch 导入报错」一行定位"
                : "pip install torch --index-url https://download.pytorch.org/whl/cu124";
        else if (!r.CudaOk)
            torchHint = r.CpuBuild
                ? "torch 是 CPU 版轮子（无 CUDA，更新驱动也没用）：pip uninstall torch 后重装 pip install torch --index-url https://download.pytorch.org/whl/cu124"
                : "CUDA 不可用：更新 NVIDIA 显卡驱动（否则回退 CPU，速度很慢）";
        else
            torchHint = "";
        return
        [
            new("Python", r.PythonOk,
                r.PythonOk ? "" : "未找到可用的 python：检查设置中的 Python 路径（留空=自动探测 engine/.venv）"),
            new("torch+CUDA", r.TorchOk && r.CudaOk, torchHint),
            new("whisperx", r.WhisperXOk,
                r.WhisperXOk ? "" : "pip install whisperx"),
            new("模型缓存", r.ModelCached,
                r.ModelCached ? "" : r.ModelDetail),
            new("ffmpeg", r.FfmpegOk,
                r.FfmpegOk ? "" : "ffmpeg.exe 缺失：从仓库 engine/ffmpeg.exe 恢复，或安装到 PATH（winget install Gyan.FFmpeg）"),
        ];
    }

    /// <summary>解析 nvidia-smi 一行输出："name, total_mb, used_mb"。</summary>
    public static (string? name, int? total, int? used) ParseGpuLine(string line)
    {
        string[] parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return (null, null, null);
        if (!int.TryParse(parts[1], out int total) || !int.TryParse(parts[2], out int used))
            return (null, null, null);
        return (parts[0], total, used);
    }

    /// <summary>模型名 → HF hub 缓存目录名。turbo 用 mobiuslabsgmbh（faster-whisper 1.2.1 映射）。</summary>
    public static string ModelRepoDir(string model) => model switch
    {
        "large-v3" => "models--Systran--faster-whisper-large-v3",
        "small" => "models--Systran--faster-whisper-small",
        _ => "models--mobiuslabsgmbh--faster-whisper-large-v3-turbo",
    };

    /// <summary>模型名 → 单根下的本地 ASR 候选目录（镜像 engine/pipeline.py 的 _local_asr_dir 语义）：
    /// engine/models/<model> 同名目录优先；仅 turbo 额外兼容遗留 engine/models/asr，
    /// 其余模型不认 asr（否则会把「缓存了 turbo」误报成「所选模型已就绪」）。</summary>
    public static IEnumerable<string> LocalAsrCandidates(string model, string root)
    {
        string modelsDir = Path.Combine(root, "engine", "models");
        yield return Path.Combine(modelsDir, model);
        if (model == "large-v3-turbo")
            yield return Path.Combine(modelsDir, "asr");
    }

    /// <summary>从 baseDir 逐级上溯，返回所有含 engine\ 子目录的「应用根」（含 baseDir 自身，近者优先）。
    /// 与 engine/pipeline.py 的 _models_roots 同语义（改动须两端同步）：兼容 dist-in-repo
    /// （dist/SubtitlesFree 里跑、模型在仓库 engine/models），真分发包仍只认自己旁边。</summary>
    public static IEnumerable<string> EngineRoots(string baseDir)
    {
        string dir = Path.GetFullPath(baseDir);
        for (int i = 0; i < 9; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "engine")))
                yield return dir;
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) yield break;
            dir = parent;
        }
    }

    /// <summary>内置 ffmpeg.exe 路径：发布布局 exe 旁 → 各应用根 engine/ 下；都没有返回 null（回退系统 PATH）。
    /// ffmpeg 随仓库与发布包分发（engine/ffmpeg.exe），whisperx.load_audio 只认 PATH 上的裸名，
    /// 故 EngineLauncher 启动引擎时把本目录前置到子进程 PATH。</summary>
    public static string? FindBundledFfmpeg()
    {
        string exeAdjacent = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(exeAdjacent)) return Path.GetFullPath(exeAdjacent);
        return EngineRoots(AppContext.BaseDirectory)
            .Select(r => Path.Combine(r, "engine", "ffmpeg.exe"))
            .FirstOrDefault(File.Exists) is { } found ? Path.GetFullPath(found) : null;
    }

    /// <summary>探测结果：Out 是 stdout（空输出 = null）；ErrTail 是 stderr 最后一行（诊断
    /// import 失败用——「torch 未安装」与「装了但加载失败」症状相同，报错文本才定得了责）。</summary>
    private sealed record Probe(string? Out, string? ErrTail)
    {
        public static readonly Probe Fail = new(null, null);
    }

    /// <summary>跑命令取全部 stdout；失败/超时 Out=null。超时杀进程。stderr 同步读尾不阻塞。</summary>
    private static async Task<Probe> RunCaptureAsync(
        string exe, string args, int timeoutSec, Action<string>? log)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(psi);
            if (process is null) return Probe.Fail;
            Task<string?> outTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> errTask = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                log?.Invoke($"探测超时（{timeoutSec}s）：{exe}");
                return Probe.Fail;
            }
            string? output = await outTask;   // 进程已退出，读尾不会久等
            string err = await errTask;
            return string.IsNullOrWhiteSpace(output) ? new Probe(null, Tail(err)) : new Probe(output, null);
        }
        catch (Exception ex)
        {
            log?.Invoke($"探测失败 {exe}: {ex.Message}");
            return Probe.Fail;
        }
    }

    /// <summary>stderr 末行（traceback 的异常名+消息最值钱）；空/超长截断。</summary>
    private static string? Tail(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return null;
        string[] lines = err.Trim().Split('\n');
        string last = lines[^1].Trim();
        if (last.Length == 0) return null;
        return last.Length > 300 ? last[..300] : last;
    }
}
