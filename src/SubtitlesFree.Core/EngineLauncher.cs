using System.Diagnostics;
using System.Text;

namespace SubtitlesFree.Core;

public sealed record EngineRequest(
    string VideoPath, string OutputPath, string Model, string ComputeType,
    string LanguageMode, string Hotwords, bool UseMirror,
    int MaxChars = 18, int AbsorbChars = 4, string Format = "srt",
    bool LlmFix = false, string LlmModel = "", string LlmApiKey = "",
    string LlmPrompt = "", string LlmDraft = "",
    string LlmPromptFile = "", string LlmDraftFile = "");

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
        req = PrepareLlmFiles(req);
        try
        {
            await RunCoreAsync(req, ct, onEvent, rawLog);
        }
        finally
        {
            CleanupLlmFiles(req);
        }
    }

    private async Task RunCoreAsync(EngineRequest req, CancellationToken ct,
                                    Action<EngineEvent>? onEvent, Action<string>? rawLog)
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
        // txt 转写按块检测语言（引擎忽略双语），不下发 --bilingual
        if (req.LanguageMode == "bilingual" && req.Format != "txt")
            sb.Append(" --bilingual");
        string hotwords = req.Hotwords.Replace("\"", "").Trim();
        if (hotwords.Length > 0)
            sb.Append($" --hotwords \"{hotwords}\"");
        sb.Append($" --max-chars {Math.Clamp(req.MaxChars, 6, 40)}");
        sb.Append($" --absorb-chars {Math.Clamp(req.AbsorbChars, 0, 10)}");
        sb.Append($" --format {req.Format}");
        // 懂你意思：勾选且 SRT 模式才下发；prompt/文案走临时文件路径（几千字塞不进命令行）
        if (req.LlmFix && req.Format != "txt")
        {
            sb.Append(" --llm-fix");
            if (req.LlmModel.Length > 0) sb.Append($" --llm-model {req.LlmModel}");
            string key = req.LlmApiKey.Replace("\"", "").Trim();
            if (key.Length > 0) sb.Append($" --llm-key \"{key}\"");
            if (req.LlmPromptFile.Length > 0) sb.Append($" --llm-prompt-file \"{req.LlmPromptFile}\"");
            if (req.LlmDraftFile.Length > 0) sb.Append($" --draft-file \"{req.LlmDraftFile}\"");
        }
        return (pythonExe, sb.ToString());
    }

    /// <summary>懂你意思：prompt/文案可能几千字，写 %TEMP% 临时文件传路径（跑完 CleanupLlmFiles 删）。</summary>
    public static EngineRequest PrepareLlmFiles(EngineRequest req)
    {
        if (!req.LlmFix || req.Format == "txt") return req;
        string dir = Path.GetTempPath();
        var promptFile = Path.Combine(dir, $"sf-llm-prompt-{Guid.NewGuid():N}.txt");
        var draftFile = Path.Combine(dir, $"sf-llm-draft-{Guid.NewGuid():N}.txt");
        File.WriteAllText(promptFile, req.LlmPrompt, new UTF8Encoding(false));
        File.WriteAllText(draftFile, req.LlmDraft, new UTF8Encoding(false));
        return req with { LlmPromptFile = promptFile, LlmDraftFile = draftFile };
    }

    /// <summary>删 PrepareLlmFiles 建的临时文件（尽力而为，失败留给系统清 %TEMP%）。</summary>
    public static void CleanupLlmFiles(EngineRequest req)
    {
        foreach (string f in new[] { req.LlmPromptFile, req.LlmDraftFile })
            if (f.Length > 0)
                try { File.Delete(f); }
                catch { /* 尽力而为 */ }
    }
}
