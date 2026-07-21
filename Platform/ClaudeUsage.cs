using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIUsage.Platform;

/// <summary>Rolling usage snapshot from Anthropic's OAuth usage endpoint — the same one Claude
/// Code's <c>/usage</c> command reads: a 5-hour "session" window and a 7-day "week" window, each a
/// server-computed <c>utilization</c> percent (0–100) plus a reset time. Percent + limits are
/// computed server-side (no local quota table); we just surface them.</summary>
public sealed record ClaudeUsageInfo(
    double? SessionPct, DateTime? SessionResetsAt,
    double? WeekPct, DateTime? WeekResetsAt)
{
    public bool HasAny => SessionPct is not null || WeekPct is not null;
}

/// <summary>
/// Reads the rolling session/week usage bars shown on the Live Code page. Authenticates the
/// <c>oauth/usage</c> GET with the access token from <c>~/.claude/.credentials.json</c>
/// (<c>claudeAiOauth.accessToken</c>) — the token is used only to sign this request and is never
/// stored, logged, or returned. Best-effort throughout: any problem (no token, expired, offline,
/// non-2xx) returns the last good value (or null) so the panel silently degrades rather than erroring.
/// The endpoint is hit at most once per <see cref="CacheTtl"/>; callers may poll freely.
/// </summary>
public static class ClaudeUsage
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly SemaphoreSlim FetchLock = new(1, 1);
    private static ClaudeUsageInfo? _cached;
    private static DateTime _attemptedAtUtc = DateTime.MinValue;

    public static async Task<ClaudeUsageInfo?> ReadAsync()
    {
        // Serialize + rate-limit: concurrent pollers share one in-flight fetch and the 5-min cache.
        await FetchLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _attemptedAtUtc < CacheTtl) return _cached;
            _attemptedAtUtc = DateTime.UtcNow;

            var token = ReadToken();
            if (token is null) return _cached; // not signed in / expired — keep last good

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return _cached;

            _cached = Parse(await resp.Content.ReadAsStringAsync());
            return _cached;
        }
        catch
        {
            return _cached;
        }
        finally
        {
            FetchLock.Release();
        }
    }

    /// <summary>Map the endpoint's <c>five_hour</c>/<c>seven_day</c> windows to the session/week bars.</summary>
    internal static ClaudeUsageInfo Parse(string json)
    {
        double? sPct = null, wPct = null;
        DateTime? sResets = null, wResets = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                ReadWindow(root, "five_hour", ref sPct, ref sResets);
                ReadWindow(root, "seven_day", ref wPct, ref wResets);
            }
        }
        catch (JsonException) { /* malformed — surface whatever parsed */ }
        return new ClaudeUsageInfo(sPct, sResets, wPct, wResets);
    }

    private static void ReadWindow(JsonElement root, string name, ref double? pct, ref DateTime? resets)
    {
        if (!root.TryGetProperty(name, out var win) || win.ValueKind != JsonValueKind.Object) return;
        if (win.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number)
            pct = u.GetDouble();
        if (win.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTime.TryParse(r.GetString(), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            resets = d;
    }

    /// <summary>OAuth access token from <c>~/.claude/.credentials.json</c>, or null if the file is
    /// missing, malformed, or the token has expired. Read only to authenticate the usage request.</summary>
    private static string? ReadToken()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object)
                return null;

            if (o.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
                && e.TryGetInt64(out var ms)
                && DateTimeOffset.FromUnixTimeMilliseconds(ms) <= DateTimeOffset.UtcNow)
                return null; // expired

            return o.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
