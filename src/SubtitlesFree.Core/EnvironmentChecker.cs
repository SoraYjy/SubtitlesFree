using System.Diagnostics;
using System.Text;

namespace SubtitlesFree.Core;

public sealed record EnvReport(
    bool PythonOk, string PythonDetail,
    bool TorchOk, bool CudaOk,
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
        string? ver = await RunCaptureAsync(python,
            "-c \"import sys;print(sys.version.split()[0])\"", 15, log);
        bool pyOk = ver is not null;
        string pyDetail = ver is null ? "未找到可用的 python" : $"Python {ver.Trim()}";
        log?.Invoke(pyOk ? $"{pyDetail} @ {python}" : pyDetail);

        // 2) torch + CUDA
        bool torchOk = false, cudaOk = false;
        string torchDetail = "torch 未安装";
        if (pyOk)
        {
            string? out2 = await RunCaptureAsync(python,
                "-c \"import torch;print(torch.__version__);print(torch.cuda.is_available())\"", 60, log);
            string[] lines = (out2 ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            torchOk = lines.Length >= 1;
            cudaOk = lines.Length >= 2 && lines[^1].Equals("True", StringComparison.OrdinalIgnoreCase);
            if (torchOk) torchDetail = $"torch {lines[0]}";
            log?.Invoke(torchDetail + (cudaOk ? "，CUDA 可用" : "，CUDA 不可用"));
        }

        // 3) whisperx（import 成功时无 stdout 输出，须主动打印标记，否则空输出会被当作失败）
        bool whisperxOk = pyOk
            && await RunCaptureAsync(python, "-c \"import whisperx;print('ok')\"", 60, log) is not null;
        log?.Invoke(whisperxOk ? "whisperx 已安装" : "whisperx 未安装");

        // 4) 模型缓存：本地 engine/models/<model>（turbo 另认遗留 engine/models/asr）或 HF hub 缓存，
        //    候选目录与 engine/pipeline.py 的 _local_asr_dir 同语义（同名目录优先，不跨模型顶替）
        string baseDir = AppContext.BaseDirectory;
        string[] roots =
        [
            baseDir,                                                   // 发布布局（dist/SubtitlesFree/）
            Path.Combine(baseDir, "..", "..", "..", "..", ".."),       // 开发仓库布局（上溯 5 级到仓库根）
        ];
        string hubDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "huggingface", "hub", ModelRepoDir(selectedModel));
        bool modelCached = roots.SelectMany(r => LocalAsrCandidates(selectedModel, r)).Any(Directory.Exists)
            || Directory.Exists(hubDir);
        string modelDetail = modelCached ? $"{selectedModel} 已缓存" : $"{selectedModel} 未缓存（首跑会下载）";
        log?.Invoke(modelDetail);

        // 5) ffmpeg（whisperx.load_audio 硬依赖）
        bool ffmpegOk = await RunCaptureAsync("ffmpeg", "-version", 10, log) is not null;
        log?.Invoke(ffmpegOk ? "ffmpeg 已安装" : "ffmpeg 未安装（whisperx.load_audio 需要）");

        // 6) GPU（nvidia-smi，无则跳过）
        string? gpuOut = await RunCaptureAsync("nvidia-smi",
            "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits", 10, log);
        (string? gpuName, int? total, int? used) = (null, null, null);
        if (gpuOut is not null)
        {
            string firstLine = gpuOut.Split('\n')[0];
            (gpuName, total, used) = ParseGpuLine(firstLine);
        }

        return new EnvReport(pyOk, pyDetail, torchOk, cudaOk, whisperxOk, modelCached, modelDetail,
            gpuName, total, used, ffmpegOk);
    }

    /// <summary>EnvReport → 状态条逐项展示（纯函数）。缺项按项给修复提示；正常项 Hint 为空。</summary>
    public static IReadOnlyList<EnvItem> BuildItems(EnvReport r)
    {
        string torchHint = !r.TorchOk
            ? "pip install torch --index-url https://download.pytorch.org/whl/cu124"
            : !r.CudaOk
                ? "CUDA 不可用：更新 NVIDIA 显卡驱动（否则回退 CPU，速度很慢）"
                : "";
        return
        [
            new("Python", r.PythonOk,
                r.PythonOk ? "" : "未找到可用的 python：检查设置中的 Python 路径（留空=自动探测 engine/.venv）"),
            new("torch+CUDA", r.TorchOk && r.CudaOk, torchHint),
            new("whisperx", r.WhisperXOk,
                r.WhisperXOk ? "" : "pip install whisperx"),
            new("模型缓存", r.ModelCached,
                r.ModelCached ? "" : "首跑自动下载；国内可开镜像加速，或按 README 用 ModelScope 预下载"),
            new("ffmpeg", r.FfmpegOk,
                r.FfmpegOk ? "" : "安装 ffmpeg 并加入 PATH（winget install Gyan.FFmpeg）"),
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

    /// <summary>跑命令取全部 stdout；失败/超时返回 null。超时杀进程。</summary>
    private static async Task<string?> RunCaptureAsync(
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
            if (process is null) return null;
            string output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                log?.Invoke($"探测超时（{timeoutSec}s）：{exe}");
                return null;
            }
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception ex)
        {
            log?.Invoke($"探测失败 {exe}: {ex.Message}");
            return null;
        }
    }
}
