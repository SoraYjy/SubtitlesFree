using System.Text.Json;

namespace SubtitlesFree.Core;

/// <summary>DeepSeek 官方接口只读客户端。模型会随版本升级改名，下拉清单用 GET /models
/// 实时获取，写死清单仅作拉取失败时的兜底。</summary>
public static class DeepSeekClient
{
    public const string ModelsUrl = "https://api.deepseek.com/models";

    /// <summary>拉取失败且无缓存时兜底的模型清单（2026-10-01 与 /models 实测一致）。</summary>
    public static readonly string[] FallbackModels = ["deepseek-flash", "deepseek-v4-pro"];

    /// <summary>GET /models → 在售模型 id（去重排序）；任何失败（网络/鉴权/非 200/坏 JSON）
    /// 返回 null，由调用方回退缓存或内置清单。handler 可注入（测试）。</summary>
    public static async Task<string[]?> ListModels(
        string apiKey, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        try
        {
            using HttpClient http = handler is null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) }
                : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using HttpResponseMessage resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(e => e.GetProperty("id").GetString() ?? "")
                .Where(id => id.Length > 0)
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
