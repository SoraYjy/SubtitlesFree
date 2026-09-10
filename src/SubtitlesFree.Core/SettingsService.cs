using System.Text.Json;

namespace SubtitlesFree.Core;

public static class SettingsService
{
    public static string DefaultDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SubtitlesFree");

    public static string DefaultPath { get; } = Path.Combine(DefaultDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            HotwordLibrary.EnsureMigrated(settings); // 旧单串热词 → 「默认」组（一次性）
            return settings;
        }
        catch
        {
            return new AppSettings(); // 损坏则重置（调用方可记日志）
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
