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
        HotwordLibrary.EnsureMigrated(s); // 预迁移，否则 Load 侧会补默认组导致不等价
        s.ActiveHotwordSet = "默认";
        SettingsService.Save(s, path);
        var loaded = SettingsService.Load(path);
        Assert.Equivalent(s, loaded);
    }

    [Fact]
    public void SaveLoad_RoundTripsHotwordSets()
    {
        string path = TempPath();
        var s = new AppSettings();
        s.HotwordSets.Add(new HotwordSet { Name = "三角洲", Words = "修脚弹，ST弹" });
        s.HotwordSets.Add(new HotwordSet { Name = "装机", Words = "4090,7800X3D" });
        s.ActiveHotwordSet = "装机";
        SettingsService.Save(s, path);
        var loaded = SettingsService.Load(path);
        Assert.Equivalent(s, loaded);
    }

    [Fact]
    public void EnsureMigrated_OldHotwordsBecomeDefaultSet()
    {
        var s = new AppSettings { Hotwords = "ComfyUI，SDXL" };
        HotwordLibrary.EnsureMigrated(s);
        HotwordSet set = Assert.Single(s.HotwordSets);
        Assert.Equal("默认", set.Name);
        Assert.Equal("ComfyUI，SDXL", set.Words);
        Assert.Equal("默认", s.ActiveHotwordSet);

        HotwordLibrary.EnsureMigrated(s); // 幂等：只迁一次
        Assert.Single(s.HotwordSets);
    }

    [Fact]
    public void EnsureMigrated_ExistingSetsOrEmptyString_Untouched()
    {
        var withSets = new AppSettings { Hotwords = "ComfyUI" };
        withSets.HotwordSets.Add(new HotwordSet { Name = "A", Words = "x" });
        HotwordLibrary.EnsureMigrated(withSets);
        Assert.Single(withSets.HotwordSets); // 已有组 → 不迁

        var empty = new AppSettings { Hotwords = "  " };
        HotwordLibrary.EnsureMigrated(empty);
        Assert.Empty(empty.HotwordSets); // 旧串为空 → 不迁
    }

    [Theory]
    [InlineData("", "")]                                    // 未选组
    [InlineData("不存在", "")]                              // 组名失效 → 空串
    public void ActiveWords_NoActiveOrUnknownSet_Empty(string active, string expected)
    {
        var s = new AppSettings { ActiveHotwordSet = active };
        s.HotwordSets.Add(new HotwordSet { Name = "三角洲", Words = "修脚弹" });
        Assert.Equal(expected, HotwordLibrary.ActiveWords(s));
    }

    [Fact]
    public void ActiveWords_FlattensSeparatorsAndWhitespace()
    {
        var s = new AppSettings { ActiveHotwordSet = "三角洲" };
        s.HotwordSets.Add(new HotwordSet { Name = "三角洲", Words = "修脚弹，ST弹\n 腰射 , ,开镜 " });
        Assert.Equal("修脚弹,ST弹,腰射,开镜", HotwordLibrary.ActiveWords(s));
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
