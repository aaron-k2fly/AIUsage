using Microsoft.Data.Sqlite;

namespace AIUsage.Data.Repositories;

public static class TicketRepo
{
    public static void UpsertFetched(SqliteConnection conn, string key, string? summary, string? status,
        string? issueType, string? project, string? sprint, string? priority, string? updated,
        string? description = null)
    {
        using var cmd = conn.CreateCommand();
        // description is COALESCEd: bulk JQL search doesn't fetch it (passes null), so a search-based
        // upsert must not wipe a description populated by a full single-issue fetch.
        cmd.CommandText = """
            INSERT INTO Tickets(key, summary, status, issue_type, project, sprint, priority, updated, description, last_synced, fetch_failed)
            VALUES ($key, $summary, $status, $type, $project, $sprint, $priority, $updated, $description, $now, 0)
            ON CONFLICT(key) DO UPDATE SET
                summary = excluded.summary,
                status = excluded.status,
                issue_type = excluded.issue_type,
                project = excluded.project,
                sprint = excluded.sprint,
                priority = excluded.priority,
                updated = excluded.updated,
                description = COALESCE(excluded.description, Tickets.description),
                last_synced = excluded.last_synced,
                fetch_failed = 0
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", (object?)issueType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sprint", (object?)sprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$priority", (object?)priority ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", (object?)updated ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public static void MarkFailed(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Tickets(key, fetch_failed) VALUES ($key, 1)
            ON CONFLICT(key) DO UPDATE SET fetch_failed = 1, last_synced = $now
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Keys that have never been synced and haven't failed — candidates for lazy fetch.</summary>
    public static List<string> UnsyncedKeys(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key FROM Tickets WHERE last_synced IS NULL AND fetch_failed = 0";
        return ReadKeys(cmd);
    }

    /// <summary>
    /// Every known key, including previously-failed ones — the manual "Sync all" is the
    /// recovery path for tickets that 404'd once (created later, transient glitch): a
    /// success clears fetch_failed, a genuine dead key just stays flagged.
    /// </summary>
    public static List<string> AllKeys(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key FROM Tickets";
        return ReadKeys(cmd);
    }

    public static List<Dictionary<string, object?>> List(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.key, t.summary, t.status, t.issue_type AS issueType,
                   t.project, t.sprint, t.priority, t.updated,
                   t.last_synced AS lastSynced, t.fetch_failed AS fetchFailed,
                   (SELECT COUNT(*) FROM SessionTicketLinks l WHERE l.ticket_key = t.key) AS sessionCount,
                   (SELECT COUNT(*) FROM ManualEntries m WHERE m.ticket_key = t.key) AS manualCount
            FROM Tickets t
            -- Latest first: by JIRA "updated" (SQLite sorts NULLs last in DESC), then key
            -- descending as a fallback so unsynced tickets are still newest-first per project.
            ORDER BY t.updated DESC, t.key DESC
            """;
        var rows = new List<Dictionary<string, object?>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> ReadKeys(SqliteCommand cmd)
    {
        var keys = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }
}
