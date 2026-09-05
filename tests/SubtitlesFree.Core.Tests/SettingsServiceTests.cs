using SubtitlesFree.Core;
using Xunit;

namespace SubtitlesFree.Core.Tests;

public class SettingsServiceTests
{
    private string TempPath()
        => Path.Combine(Path.GetTempPath(), "sf-tests", Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void Defaults_AreSane()
    {
        var s = new AppSettings();
        Assert.Equal("large-v3-turbo", s.Model);
        Assert.Equal("zh", s.LanguageMode);
        Assert.Equal("float16", s.ComputeType);
        Assert.True(s.UseMirror);
        Assert.Equal("", s.PythonPath);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        string path = TempPath();
        var s = new AppSettings
        {
            PythonPath = @"E:\python311\python.exe",
            Model = "large-v3",
            LanguageMode = "bilingual",
            ComputeType = "int8_float16",
            Hotwords = "ComfyUI,SDXL",
            UseMirror = false,
            LastInputDir = @"D:\videos",
        };
        SettingsService.Save(s, path);
        var loaded = SettingsService.Load(path);
        Assert.Equivalent(s, loaded);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
        => Assert.Equivalent(new AppSettings(), SettingsService.Load(TempPath()));

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Assert.Equivalent(new AppSettings(), SettingsService.Load(path));
    }
}
