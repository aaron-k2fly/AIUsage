using System.Text.Json;

namespace AIUsage.Platform;

/// <summary>Subscription facts read from Claude Code's own config (`~/.claude.json`).</summary>
public sealed record ClaudeAccountInfo(string? Plan, DateTime? UsageResetsAt);

/// <summary>
/// Reads a few non-secret account fields from Claude Code's `~/.claude.json` to show the
/// subscription package and when usage limits reset. Deliberately reads ONLY the plan/tier
/// enums and the reset date — never the org name, email, or OAuth tokens in that file.
/// Everything is best-effort: any problem returns nulls (the panel shows "—").
/// </summary>
public static class ClaudeAccount
{
    public static ClaudeAccountInfo Read()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return new ClaudeAccountInfo(null, null);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var orgType = FindString(root, "organizationType");     // e.g. claude_team / claude_enterprise
            var userTier = FindString(root, "userRateLimitTier");   // e.g. default_claude_max_5x
            var resetRaw = FindString(root, "planLimitsEndDate");   // ISO date

            DateTime? resets = DateTime.TryParse(
                resetRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

            return new ClaudeAccountInfo(BuildPlan(orgType, userTier), resets);
        }
        catch
        {
            return new ClaudeAccountInfo(null, null);
        }
    }

    /// <summary>Combine org type (Team/Enterprise/…) and rate tier (Max 5x/Pro/…) into a label.</summary>
    private static string? BuildPlan(string? orgType, string? userTier)
    {
        var org = MapOrgType(orgType);
        var tier = MapTier(userTier);
        if (org is not null && tier is not null) return $"{org} · {tier}";
        return org ?? tier;
    }

    // "claude_team" -> "Team", "claude_enterprise" -> "Enterprise". "claude_individual"/null -> null
    // (individual accounts are named by their rate tier instead, e.g. "Max 5x").
    private static string? MapOrgType(string? orgType)
    {
        if (string.IsNullOrEmpty(orgType) || !orgType.StartsWith("claude_", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = orgType["claude_".Length..];
        if (name is "individual" or "") return null;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    // "default_claude_max_5x" -> "Max 5x", "default_claude_pro" -> "Pro", "default_claude_free" -> "Free".
    private static string? MapTier(string? tier)
    {
        if (string.IsNullOrEmpty(tier)) return null;
        var t = tier;
        foreach (var prefix in new[] { "default_claude_", "default_", "claude_" })
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { t = t[prefix.Length..]; break; }
        if (t.Length == 0) return null;
        // "max_5x" -> ["max","5x"] -> "Max 5x"
        var parts = t.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : p);
        return string.Join(" ", parts);
    }

    /// <summary>First string value for <paramref name="name"/> anywhere in the JSON tree.</summary>
    private static string? FindString(JsonElement el, string name)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals(name) && prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                    var nested = FindString(prop.Value, name);
                    if (nested is not null) return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var nested = FindString(item, name);
                    if (nested is not null) return nested;
                }
                break;
        }
        return null;
    }
}
