namespace AIUsage.Scanner;

/// <summary>A resumable Claude Code session found in a specific working folder's transcript dir.</summary>
public sealed record FolderSession(string SessionId, string Label, string? UpdatedIso);

/// <summary>
/// Lists the existing Claude Code sessions whose transcript lives in a given working folder's
/// encoded project dir (~/.claude/projects/&lt;encoded-cwd&gt;), newest-first. Powers the Live Code
/// "Resume Sessions" picker. The cwd is encoded by replacing ':', '\\', '/' with '-' — the same
/// encoding used by LiveCodeHandlers.TranscriptPath.
/// </summary>
public static class FolderSessions
{
    public static List<FolderSession> List(string? folder, int max = 25)
    {
        var results = new List<FolderSession>();
        if (string.IsNullOrWhiteSpace(folder)) return results;

        var encoded = folder.Replace(':', '-').Replace('\\', '-').Replace('/', '-');
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects", encoded);
        if (!Directory.Exists(dir)) return results;

        try
        {
            var files = new DirectoryInfo(dir)
                .EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max);
            foreach (var f in files)
                results.Add(new FolderSession(
                    Path.GetFileNameWithoutExtension(f.Name),
                    SessionAggregator.FirstUserPrompt(f.FullName) ?? "(no prompt recorded)",
                    f.LastWriteTimeUtc.ToString("o")));
        }
        catch (IOException) { /* dir vanished mid-enumerate — return what we have */ }
        return results;
    }
}
