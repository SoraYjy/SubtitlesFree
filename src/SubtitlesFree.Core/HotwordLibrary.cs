namespace SubtitlesFree.Core;

/// <summary>热词组纯逻辑：旧单串迁移、当前组词表解析（App 层不做逻辑，全在这层）。</summary>
public static class HotwordLibrary
{
    /// <summary>旧版单串热词 → 「默认」组并选中。只迁一次：已有组或旧串为空则不动。</summary>
    public static void EnsureMigrated(AppSettings s)
    {
        if (s.HotwordSets.Count > 0 || string.IsNullOrWhiteSpace(s.Hotwords))
            return;
        s.HotwordSets.Add(new HotwordSet { Name = "默认", Words = s.Hotwords });
        s.ActiveHotwordSet = "默认";
    }

    /// <summary>当前组词表拍平成逗号串（供引擎 --hotwords）；未选/组不存在 → 空串。
    /// 全角逗号/顿号先归一（旧链路靠引擎侧归一，本函数现在是唯一出口）。</summary>
    public static string ActiveWords(AppSettings s)
    {
        HotwordSet? set = s.HotwordSets.FirstOrDefault(h => h.Name == s.ActiveHotwordSet);
        if (set is null)
            return "";
        return string.Join(",", set.Words
            .Replace("，", ",").Replace("、", ",")
            .Split([",", "\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length > 0));
    }
}
