using System.Diagnostics;
using System.Text;

namespace SubtitlesFree.Core;

public sealed record EngineRequest(
    string VideoPath, string OutputPath, string Model, string ComputeType,
    string LanguageMode, string Hotwords, bool UseMirror);

/// <summary>引擎非零退出且未发 error 事件（取消除外）。</summary>
public sealed class EngineFailedException(string details) : Exception(details);

public interface IEngineLauncher
{
    Task RunAsync(EngineRequest req, CancellationToken ct,
                  Action<EngineEvent>? onEvent = null, Action<string>? rawLog = null);
}

public sealed class EngineLauncher(string pythonPath, string engineScriptPath) : IEngineLauncher
{
    public string PythonPath { get; } = pythonPath;
    public string EngineScriptPath { get; } = engineScriptPath;

    public async Task RunAsync(EngineRequest req, CancellationToken ct,
                               Action<EngineEvent>? onEvent = null, Action<string>? rawLog = null)
    {
        (string exe, string args) = BuildCommandLine(PythonPath, EngineScriptPath, req);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(EngineScriptPath)!,
        };
        if (req.UseMirror)
            psi.EnvironmentVariables["HF_ENDPOINT"] = "https://hf-mirror.com";
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8"; // 双保险，emitter 侧已 reconfigure
        // 内置 ffmpeg（engine/ffmpeg.exe 或 exe 旁）前置到子进程 PATH——引擎代码只认裸名 ffmpeg，零改动
        string? ffmpegPath = EnvironmentChecker.FindBundledFfmpeg();
        if (ffmpegPath is not null)
        {
            string dir = Path.GetDirectoryName(ffmpegPath)!;
            psi.EnvironmentVariables["PATH"] = dir + Path.PathSeparator + psi.EnvironmentVariables["PATH"];
        }

        using var process = new Process { StartInfo = psi };
        bool sawErrorEvent = false;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            EngineEvent? evt = EngineProtocol.ParseLine(e.Data);
            if (evt is ErrorEvent) sawErrorEvent = true;
            if (evt is not null) onEvent?.Invoke(evt);
            else rawLog?.Invoke(e.Data); // 只收非 JSON 行，事件行已走 onEvent，避免 GUI 日志重复
        };
        var stderrTail = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrTail)
            {
                stderrTail.Enqueue(e.Data);
                while (stderrTail.Count > 20) stderrTail.Dequeue();
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct);
            // 排干异步输出回调（.NET 文档：同步 WaitForExit() 才等 async 读处理器收尾），
            // 防止最后的 done/error 事件与 RunAsync 返回竞态丢失。
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw;
        }

        if (process.ExitCode != 0 && !sawErrorEvent)
            throw new EngineFailedException(
                "引擎进程异常退出。stderr 尾部：\n" + string.Join("\n", stderrTail));
    }

    /// <summary>组装命令行（纯函数）。热词里的引号剥掉防注入。</summary>
    public static (string exe, string args) BuildCommandLine(
        string pythonExe, string scriptPath, EngineRequest req)
    {
        var sb = new StringBuilder();
        sb.Append($"\"{scriptPath}\"");
        sb.Append($" --video \"{req.VideoPath}\"");
        sb.Append($" --output \"{req.OutputPath}\"");
        sb.Append($" --model {req.Model}");
        sb.Append($" --compute-type {req.ComputeType}");
        if (req.LanguageMode == "bilingual")
            sb.Append(" --bilingual");
        string hotwords = req.Hotwords.Replace("\"", "").Trim();
        if (hotwords.Length > 0)
            sb.Append($" --hotwords \"{hotwords}\"");
        return (pythonExe, sb.ToString());
    }
}
