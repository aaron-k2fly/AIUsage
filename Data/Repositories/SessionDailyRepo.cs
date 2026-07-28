using AIUsage.Scanner;
using Microsoft.Data.Sqlite;

namespace AIUsage.Data.Repositories;

/// <summary>
/// The <c>SessionDailyTokens</c> table — per-session token spend split by local calendar day.
/// Accumulation mirrors <see cref="SessionRepo.Upsert"/> exactly: additive, so the incremental
/// scanner can add each newly-read slice of a transcript, and zeroed by
/// <see cref="DeleteForFile"/> before a full reparse so nothing double-counts. Rows are only written
/// for sessions that already exist in Sessions (the FK parent), so a transcript naming an unknown
/// session is skipped rather than throwing.
/// </summary>
public static class SessionDailyRepo
{
    /// <summary>Add a parsed aggregate's per-day tokens to the stored buckets. Runs inside the
    /// caller's transaction, after the session row itself has been upserted.</summary>
    public static void Accumulate(SqliteConnection conn, SessionAggregate a)
    {
        foreach (var (day, tokens) in a.DailyTokens)
        {
            using var cmd = conn.CreateCommand();
            // INSERT..SELECT..WHERE EXISTS guards the FK; the WHERE clause is also what lets SQLite
            // parse the trailing ON CONFLICT unambiguously after a SELECT source.
            cmd.CommandText = """
                INSERT INTO SessionDailyTokens(session_id, file_path, day, input_tokens, output_tokens)
                SELECT $sid, $fp, $day, $in, $out
                WHERE EXISTS(SELECT 1 FROM Sessions WHERE id = $sid)
                ON CONFLICT(session_id, day) DO UPDATE SET
                    input_tokens = input_tokens + excluded.input_tokens,
                    output_tokens = output_tokens + excluded.output_tokens,
                    file_path = excluded.file_path
                """;
            cmd.Parameters.AddWithValue("$sid", a.SessionId);
            cmd.Parameters.AddWithValue("$fp", a.FilePath);
            cmd.Parameters.AddWithValue("$day", day);
            cmd.Parameters.AddWithValue("$in", tokens.In);
            cmd.Parameters.AddWithValue("$out", tokens.Out);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Drop a file's buckets before a full reparse (the counterpart of
    /// <see cref="SessionRepo.ResetCountersForFile"/>).</summary>
    public static void DeleteForFile(SqliteConnection conn, string filePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SessionDailyTokens WHERE file_path = $fp";
        cmd.Parameters.AddWithValue("$fp", filePath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Replace a file's buckets outright with a full-file re-derivation (set semantics) —
    /// used by the one-time v7 backfill, which re-parses transcripts the incremental scanner had
    /// already consumed and so must not add to whatever is stored.</summary>
    public static void ReplaceForFile(SqliteConnection conn, string filePath, IEnumerable<SessionAggregate> aggregates)
    {
        DeleteForFile(conn, filePath);
        foreach (var a in aggregates)
            Accumulate(conn, a);
    }

    /// <summary>
    /// Tokens (input + output, cache excluded as everywhere else) spent over a rolling window ending
    /// today — <paramref name="days"/> = 7 means today plus the six days before it. Because the
    /// buckets are per-day, a session that began before the window still contributes exactly the part
    /// of its spend that falls inside it.
    /// </summary>
    public static long RollingTokens(SqliteConnection conn, int days)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(input_tokens + output_tokens), 0)
            FROM SessionDailyTokens
            WHERE day >= date('now', 'localtime', $from)
            """;
        cmd.Parameters.AddWithValue("$from", $"-{Math.Max(days - 1, 0)} days");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
