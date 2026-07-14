using System.Text;
using AIUsage.Data;
using AIUsage.Data.Repositories;
using AIUsage.Settings;

namespace AIUsage.Scanner;

public sealed record ScanResult(int Sessions, int NewFiles, int UpdatedFiles, int SkippedFiles);

/// <summary>
/// Incremental scanner over Claude Code transcript stores. Transcript files are
/// append-only JSONL; ScanState remembers the last parsed byte offset per file so
/// re-scans only read what's new. Session-named subdirectories hold sidechain
/// (subagent) transcripts and are skipped in v1.
/// </summary>
public sealed class TranscriptScanner
{
    private static readonly object ScanLock = new();

    public ScanResult Run()
    {
        lock (ScanLock)
        {
            return RunCore();
        }
    }

    private static ScanResult RunCore()
    {
        var inferrer = new TicketKeyInferrer(SettingsStore.ProjectKeyAllowlist());
        var aggregator = new SessionAggregator(inferrer);
        var backfillFrom = SettingsStore.BackfillFrom();

        int newFiles = 0, updatedFiles = 0, skippedFiles = 0;

        using var conn = Db.Open();

        foreach (var root in SettingsStore.ScanRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var projectDir in Directory.EnumerateDirectories(root))
            {
                foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(file);
                    if (backfillFrom is not null && fi.LastWriteTimeUtc < backfillFrom)
                    {
                        skippedFiles++;
                        continue;
                    }

                    var mtime = fi.LastWriteTimeUtc.ToString("o");

                    // cheap pre-check outside the transaction so unchanged files
                    // don't take the DB write lock at all
                    var quick = GetScanState(conn, file);
                    if (quick is not null && quick.Value.Size == fi.Length && quick.Value.Mtime == mtime)
                        continue;

                    // BEGIN IMMEDIATE (Microsoft.Data.Sqlite default): the read of
                    // ScanState, the additive upserts, and the offset save commit
                    // atomically. A concurrent scanner — even in another process —
                    // blocks here, then re-reads the updated offset and skips the
                    // range this scan already counted.
                    using var tx = conn.BeginTransaction();

                    var state = GetScanState(conn, file);
                    if (state is not null && state.Value.Size == fi.Length && state.Value.Mtime == mtime)
                        continue; // another scanner got here first; tx disposes as rollback

                    long startOffset = 0;
                    var fullReparse = false;
                    if (state is not null && fi.Length >= state.Value.Size && state.Value.Offset <= fi.Length)
                    {
                        startOffset = state.Value.Offset;
                    }
                    else if (state is not null)
                    {
                        // file shrank or was rewritten — reparse fully, but first zero the
                        // additive counters so accumulation doesn't double-count
                        fullReparse = true;
                        SessionRepo.ResetCountersForFile(conn, file);
                    }

                    var (lines, newOffset) = ReadCompleteLines(file, startOffset);
                    var sessions = aggregator.Aggregate(lines, file);
                    foreach (var agg in sessions.Values)
                    {
                        SessionRepo.Upsert(conn, agg);
                        foreach (var (key, source) in agg.TicketKeys)
                            SessionRepo.AddAutoLink(conn, agg.SessionId, key, source);
                    }

                    if (fullReparse)
                        SessionRepo.DeleteSessionsNotIn(conn, file, sessions.Keys);

                    SaveScanState(conn, file, newOffset, mtime, fi.Length);
                    tx.Commit();

                    if (state is null) newFiles++;
                    else updatedFiles++;
                }
            }
        }

        return new ScanResult(CountSessions(conn), newFiles, updatedFiles, skippedFiles);
    }

    /// <summary>
    /// Read whole lines from a byte offset. A writer may be mid-append, so anything
    /// after the last newline is left for the next scan (offset stops before it).
    /// </summary>
    private static (List<string> Lines, long NewOffset) ReadCompleteLines(string path, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > fs.Length) offset = 0;
        fs.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[fs.Length - offset];
        fs.ReadExactly(buffer);

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
        if (lastNewline < 0) return ([], offset);

        var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        return (lines, offset + lastNewline + 1);
    }

    private static (long Offset, string Mtime, long Size)? GetScanState(Microsoft.Data.Sqlite.SqliteConnection conn, string file)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_offset, last_mtime, last_size FROM ScanState WHERE file_path = $fp";
        cmd.Parameters.AddWithValue("$fp", file);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt64(2));
    }

    private static void SaveScanState(Microsoft.Data.Sqlite.SqliteConnection conn, string file, long offset, string mtime, long size)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ScanState(file_path, last_offset, last_mtime, last_size)
            VALUES ($fp, $off, $mt, $size)
            ON CONFLICT(file_path) DO UPDATE SET
                last_offset = excluded.last_offset,
                last_mtime = excluded.last_mtime,
                last_size = excluded.last_size
            """;
        cmd.Parameters.AddWithValue("$fp", file);
        cmd.Parameters.AddWithValue("$off", offset);
        cmd.Parameters.AddWithValue("$mt", mtime);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.ExecuteNonQuery();
    }

    private static int CountSessions(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Sessions";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
