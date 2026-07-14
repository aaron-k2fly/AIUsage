namespace AIUsage.Terminal;

/// <summary>Locates the Claude Code CLI so the Live Code page can warn when it isn't installed.</summary>
public static class ClaudeCli
{
    // Native install is claude.exe; npm global shim is claude.cmd; POSIX is bare "claude".
    private static readonly string[] Names = { "claude.exe", "claude.cmd", "claude.bat", "claude" };

    /// <summary>Full path to the claude CLI, or null if it can't be found.</summary>
    public static string? Resolve()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in Names)
            {
                try
                {
                    var full = Path.Combine(dir, name);
                    if (File.Exists(full)) return full;
                }
                catch { /* malformed PATH entry */ }
            }
        }

        // Native installer's default location, even if it isn't on this process's PATH.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in Names)
        {
            var full = Path.Combine(home, ".local", "bin", name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public static bool IsInstalled() => Resolve() is not null;
}
