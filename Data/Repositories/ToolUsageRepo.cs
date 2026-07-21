using AIUsage.Scanner;
using Microsoft.Data.Sqlite;

namespace AIUsage.Data.Repositories;

/// <summary>
/// The <c>ToolUsage</c> table (sub-agent / skill / MCP / hook counts per session), populated with
/// set semantics from <see cref="SessionAggregator.ReadToolUsage"/> — the scanner replaces a session's
/// rows whenever its transcript changes, so counts are always the current full-file truth (no additive
/// accumulation, no double-count). Rows are only written for sessions that already exist in Sessions
/// (the FK parent), so a transcript naming an unknown session is skipped rather than throwing.
/// </summary>
public static class ToolUsageRepo
{
    /// <summary>Replace the ToolUsage rows for every session in <paramref name="perSession"/> with the
    /// given counts (delete-then-insert per session). Runs inside the caller's transaction.</summary>
    public static void ReplaceForFile(SqliteConnection conn, IReadOnlyDictionary<string, SessionAggregator.ToolUsageCounts> perSession)
    {
        foreach (var (sessionId, counts) in perSession)
        {
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM ToolUsage WHERE session_id = $sid";
                del.Parameters.AddWithValue("$sid", sessionId);
                del.ExecuteNonQuery();
            }

            foreach (var (category, name, count) in counts.Flatten())
            {
                using var ins = conn.CreateCommand();
                // INSERT guarded by the parent row's existence so an orphan sessionId can't violate the FK.
                ins.CommandText = """
                    INSERT INTO ToolUsage(session_id, category, name, count)
                    SELECT $sid, $cat, $name, $count
                    WHERE EXISTS(SELECT 1 FROM Sessions WHERE id = $sid)
                    """;
                ins.Parameters.AddWithValue("$sid", sessionId);
                ins.Parameters.AddWithValue("$cat", category);
                ins.Parameters.AddWithValue("$name", name);
                ins.Parameters.AddWithValue("$count", count);
                ins.ExecuteNonQuery();
            }
        }
    }
}
