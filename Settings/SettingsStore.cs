using System.Security.Cryptography;
using System.Text;
using AIUsage.Data;

namespace AIUsage.Settings;

public static class SettingsStore
{
    /// <summary>
    /// Store a secret. DPAPI-protected (current user) on Windows; on other platforms
    /// falls back to plaintext with a marker prefix — documented v1 limitation.
    /// </summary>
    public static void SetProtected(string key, string value)
    {
        if (OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            Set(key, "dpapi:" + Convert.ToBase64String(protectedBytes));
        }
        else
        {
            Set(key, "plain:" + value);
        }
    }

    public static string? GetProtected(string key)
    {
        var raw = Get(key);
        if (raw is null) return null;
        if (raw.StartsWith("dpapi:") && OperatingSystem.IsWindows())
        {
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(raw["dpapi:".Length..]), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException)
            {
                return null; // protected on another machine/user — treat as unset
            }
        }
        if (raw.StartsWith("plain:")) return raw["plain:".Length..];
        return null;
    }

    public static string? Get(string key)
    {
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM Settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public static void Set(string key, string? value)
    {
        using var conn = Db.Open();
        using var cmd = conn.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM Settings WHERE key = $k";
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO Settings(key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$v", value);
        }
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Transcript scan roots; semicolon-separated setting, default ~/.claude/projects.</summary>
    public static string[] ScanRoots()
    {
        var raw = Get("scan_paths");
        var roots = string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (roots.Length > 0) return roots;

        // fall back to the default when unset OR when the value parses to nothing
        // (e.g. a lone ";") — settings.get indexes [0] and must never see an empty array
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [Path.Combine(home, ".claude", "projects")];
    }

    /// <summary>JIRA project keys allowed for ticket inference (comma-separated). Empty = allow all.</summary>
    public static HashSet<string> ProjectKeyAllowlist()
    {
        var raw = Get("project_key_allowlist") ?? "";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToUpperInvariant())
            .ToHashSet();
    }

    /// <summary>Only scan transcript files modified on/after this date. Null = scan everything.</summary>
    public static DateTime? BackfillFrom()
    {
        var raw = Get("backfill_from");
        return DateTime.TryParse(raw, out var d) ? d.ToUniversalTime() : null;
    }
}
