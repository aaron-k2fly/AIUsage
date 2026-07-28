using System.Diagnostics;
using AIUsage.Data;
using AIUsage.Data.Repositories;
using AIUsage.Export;

namespace AIUsage.Bridge.Handlers;

/// <summary>Excel (.xlsx) export for the Sessions, Manual-entry and Tickets tables.</summary>
public static class ExportHandlers
{
    public static void Register(MessageRouter router)
    {
        router.Register("export.sessions", _ => ExportAsync("sessions"));
        router.Register("export.manual", _ => ExportAsync("manual"));
        router.Register("export.tickets", _ => ExportAsync("tickets"));
    }

    /// <summary>Build the .xlsx bytes for a table. Public so it can be exercised headlessly.</summary>
    public static byte[] BuildWorkbook(string what)
    {
        var (sheet, _, headers, rows) = Dataset(what);
        return XlsxWriter.Build(sheet, headers, rows);
    }

    private static (string Sheet, string BaseName, List<string> Headers, List<IReadOnlyList<object?>> Rows)
        Dataset(string what)
    {
        using var conn = Db.Open();
        switch (what)
        {
            case "sessions":
            {
                var data = SessionRepo.List(conn, "all");
                var headers = new List<string>
                {
                    "Session", "Started", "Last activity", "Model", "Input tokens", "Output tokens",
                    "Edits", "Reads", "Shell", "Other tools", "User messages",
                    "Review state", "Project", "Tickets"
                };
                var rows = data.Select(d => (IReadOnlyList<object?>)new object?[]
                {
                    Get(d, "title"), Get(d, "startedAt"), Get(d, "endedAt"), Model(d),
                    Get(d, "inputTokens"), Get(d, "outputTokens"),
                    Lng(d, "editCount") + Lng(d, "writeCount"), Get(d, "readCount"), Get(d, "bashCount"), Get(d, "otherToolCount"),
                    Get(d, "userMessageCount"), Get(d, "reviewState"), Get(d, "projectDir"), Links(d)
                }).ToList();
                return ("Sessions", "sessions", headers, rows);
            }
            case "manual":
            {
                var data = ManualEntryRepo.List(conn, int.MaxValue);
                var headers = new List<string> { "Ticket", "Ticket summary", "Date", "Activity", "Description", "Tool" };
                var rows = data.Select(d => (IReadOnlyList<object?>)new object?[]
                {
                    Get(d, "ticketKey"), Get(d, "ticketSummary"), Get(d, "entryDate"),
                    Get(d, "category"), Get(d, "description"), Get(d, "toolUsed")
                }).ToList();
                return ("Manual entries", "manual-entries", headers, rows);
            }
            case "tickets":
            {
                var data = TicketRepo.List(conn);
                var headers = new List<string>
                {
                    "Key", "Summary", "Project", "Type", "Priority", "Status", "Sprint",
                    "Sessions", "Manual entries", "AI-touched", "Updated", "Last synced"
                };
                var rows = data.Select(d => (IReadOnlyList<object?>)new object?[]
                {
                    Get(d, "key"), Get(d, "summary"), Get(d, "project"), Get(d, "issueType"),
                    Get(d, "priority"), Get(d, "status"), Get(d, "sprint"),
                    Get(d, "sessionCount"), Get(d, "manualCount"),
                    Lng(d, "sessionCount") + Lng(d, "manualCount") > 0 ? "Yes" : "No",
                    Get(d, "updated"), Get(d, "lastSynced")
                }).ToList();
                return ("Tickets", "tickets", headers, rows);
            }
            default:
                throw new ArgumentException($"Unknown export '{what}'");
        }
    }

    private static async Task<object?> ExportAsync(string what)
    {
        var (_, baseName, _, rows) = Dataset(what);
        var bytes = BuildWorkbook(what);

        var dir = ExportFolder();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx");

        await File.WriteAllBytesAsync(path, bytes);
        RevealInFileManager(path);
        return new { saved = true, path, rows = rows.Count };
    }

    /// <summary>Prefer the user's Downloads folder, falling back to Documents.</summary>
    private static string ExportFolder()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>Open the OS file manager with the exported file selected (best-effort).</summary>
    private static void RevealInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", $"-R \"{path}\"");
            else
                Process.Start("xdg-open", $"\"{Path.GetDirectoryName(path)}\"");
        }
        catch
        {
            // revealing is a convenience; the file is already saved
        }
    }

    private static object? Get(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v : null;

    private static long Lng(Dictionary<string, object?> d, string key) =>
        Get(d, key) is long l ? l : 0L;

    private static object? Model(Dictionary<string, object?> d) =>
        (Get(d, "model") as string)?.Replace("claude-", "");

    private static object? Links(Dictionary<string, object?> d)
    {
        if (Get(d, "links") is not string s || s.Length == 0) return null;
        return string.Join(", ", s.Split(';').Select(pair =>
        {
            var parts = pair.Split('|');
            return parts.Length == 2 ? $"{parts[0]} ({parts[1]})" : parts[0];
        }));
    }
}
