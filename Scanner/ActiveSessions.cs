namespace AIUsage.Scanner;

public sealed record ActiveSession(string Folder, long ContextTokens, long ContextSize, int Percent, string SessionId);

/// <summary>
/// Lists the most recently active Claude Code sessions across all project dirs — a session is
/// "active" if its transcript was written to within the given window. Used by the Live Code
/// metrics panel to show the top-N running sessions (including ones outside this app).
/// </summary>
public static class ActiveSessions
{
    public static List<ActiveSession> Top(int count, TimeSpan window)
    {
        var results = new List<ActiveSession>();
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return results;

        var cutoff = DateTime.UtcNow - window;
        var recent = new List<FileInfo>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTimeUtc >= cutoff) recent.Add(fi);
                }
        }
        catch (IOException) { return results; }

        foreach (var fi in recent.OrderByDescending(f => f.LastWriteTimeUtc).Take(count))
        {
            var info = SessionAggregator.ReadLive(fi.FullName);
            var size = SessionAggregator.ContextWindow(info.Model);
            var pct = size > 0 ? (int)Math.Round(100.0 * info.ContextTokens / size) : 0;
            results.Add(new ActiveSession(
                FolderLabel(info.Cwd, fi.Directory?.Name),
                info.ContextTokens, size, pct,
                Path.GetFileNameWithoutExtension(fi.Name)));
        }
        return results;
    }

    /// <summary>Short label for a session: the last segment of its cwd, else the encoded project dir.</summary>
    private static string FolderLabel(string? cwd, string? dirName)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var trimmed = cwd.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return dirName ?? "(unknown)";
    }
}
