using System.IO;
using SubtitlesFree.Core;

namespace SubtitlesFree.App.Services;

/// <summary>组合根：进程内单例。</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public AppSettings Settings { get; private set; } = SettingsService.Load();
    public FileLogger Logger { get; } = new(SettingsService.DefaultDir);
    public string EngineScriptPath { get; } = LocateEngineScript();

    private AppServices() { }

    private static string LocateEngineScript()
    {
        // 测试钩子：T10 错误路径演练用
        string? fake = Environment.GetEnvironmentVariable("SF_FAKE_ENGINE");
        if (!string.IsNullOrEmpty(fake) && File.Exists(fake)) return fake;

        // 1) exe 旁（发布形态）2) 开发仓路径（App 在 src/App/bin/Debug/net8.0-windows/，
        //    回到仓库根需 5 级 ..：net8.0-windows→Debug→bin→App→src→仓库根）
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "engine", "engine.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "engine", "engine.py"),
        ];
        foreach (string c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        throw new FileNotFoundException("找不到 engine/engine.py（应在应用目录或仓库内）");
    }

    /// <summary>Python 解析：设置值 → exe 旁 runtime（bat 已装完）→ engine 旁 .venv → PATH。
    /// 逻辑在 Core 纯函数（可单测），这里只喂路径。</summary>
    public string ResolvePython() =>
        EnvironmentChecker.ResolvePython(
            Settings.PythonPath, AppContext.BaseDirectory, Path.GetDirectoryName(EngineScriptPath)!);

    public void SaveSettings()
    {
        SettingsService.Save(Settings);
        Logger.Info("设置已保存");
    }
}
