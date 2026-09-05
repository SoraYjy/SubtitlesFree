using System.Text.Json;

namespace SubtitlesFree.Core;

/// <summary>引擎 stdout JSON Lines 事件（契约：spec §5.2）。</summary>
public abstract record EngineEvent;

public sealed record StageEvent(string Stage) : EngineEvent;

public sealed record ProgressEvent(double Value) : EngineEvent;

public sealed record LogEvent(string? Level, string Message) : EngineEvent;

public sealed record DoneEvent(string SrtPath, int Segments, double VideoSec, double ElapsedSec)
    : EngineEvent;

public sealed record ErrorEvent(string Code, string? Message, string? Hint) : EngineEvent;

public static class EngineProtocol
{
    /// <summary>解析一行 stdout。非 JSON / 缺关键字段返回 null，调用方归入原始日志。</summary>
    public static EngineEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        string t = line.Trim();
        if (!t.StartsWith('{')) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(t);
            var r = doc.RootElement;
            if (!r.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String) return null;
            switch (typeEl.GetString())
            {
                case "stage":
                    return r.TryGetProperty("value", out var v)
                        && v.ValueKind == JsonValueKind.String
                        ? new StageEvent(v.GetString()!) : null;
                case "progress":
                    return r.TryGetProperty("value", out var p)
                        && p.ValueKind == JsonValueKind.Number
                        ? new ProgressEvent(p.GetDouble()) : null;
                case "log":
                    string? lvl = r.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String
                        ? l.GetString() : null;
                    return r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? new LogEvent(lvl, m.GetString()!) : null;
                case "done":
                    if (!r.TryGetProperty("srt_path", out var sp)
                        || sp.ValueKind != JsonValueKind.String) return null;
                    if (!r.TryGetProperty("segments", out var sg)
                        || sg.ValueKind != JsonValueKind.Number) return null;
                    return new DoneEvent(
                        sp.GetString()!,
                        sg.GetInt32(),
                        r.TryGetProperty("video_sec", out var vs) && vs.ValueKind == JsonValueKind.Number
                            ? vs.GetDouble() : 0.0,
                        r.TryGetProperty("elapsed_sec", out var es) && es.ValueKind == JsonValueKind.Number
                            ? es.GetDouble() : 0.0);
                case "error":
                    if (!r.TryGetProperty("code", out var c)
                        || c.ValueKind != JsonValueKind.String) return null;
                    return new ErrorEvent(
                        c.GetString()!,
                        r.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String
                            ? em.GetString() : null,
                        r.TryGetProperty("hint", out var eh) && eh.ValueKind == JsonValueKind.String
                            ? eh.GetString() : null);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
