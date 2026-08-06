using AIUsage.Scanner;
using Microsoft.Data.Sqlite;

namespace AIUsage.Data.Repositories;

public static class SessionRepo
{
    /// <summary>Zero the additive counters of a file's sessions before a full reparse.</summary>
    public static void ResetCountersForFile(SqliteConnection conn, string filePath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Sessions SET
                input_tokens = 0, output_tokens = 0, cache_creation_tokens = 0, cache_read_tokens = 0,
                edit_count = 0, write_count = 0, read_count = 0, bash_count = 0,
                other_tool_count = 0, user_message_count = 0,
                started_at = NULL, ended_at = NULL
            WHERE file_path = $fp
            """;
        cmd.Parameters.AddWithValue("$fp", filePath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// After a full reparse of a rewritten file, remove sessions that no longer exist
    /// in it — otherwise they linger as zeroed ghost rows with stale ticket links
    /// (links cascade via the FK).
    /// </summary>
    public static void DeleteSessionsNotIn(SqliteConnection conn, string filePath, IReadOnlyCollection<string> keepIds)
    {
        using var cmd = conn.CreateCommand();
        if (keepIds.Count == 0)
        {
            cmd.CommandText = "DELETE FROM Sessions WHERE file_path = $fp";
        }
        else
        {
            var placeholders = string.Join(",", keepIds.Select((_, i) => $"$id{i}"));
            cmd.CommandText = $"DELETE FROM Sessions WHERE file_path = $fp AND id NOT IN ({placeholders})";
            var i = 0;
            foreach (var id in keepIds)
                cmd.Parameters.AddWithValue($"$id{i++}", id);
        }
        cmd.Parameters.AddWithValue("$fp", filePath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insert or accumulate a parsed aggregate into the session row.</summary>
    public static void Upsert(SqliteConnection conn, SessionAggregate a)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Sessions (
                id, file_path, project_dir, git_branch, title, title_is_custom, model, started_at, ended_at,
                input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens,
                edit_count, write_count, read_count, bash_count, other_tool_count,
                user_message_count, cc_version)
            VALUES ($id, $fp, $dir, $branch, $title, $titleCustom, $model, $start, $end,
                $in, $out, $cc, $cr, $edit, $write, $read, $bash, $other, $umc, $ver)
            ON CONFLICT(id) DO UPDATE SET
                file_path = excluded.file_path,
                project_dir = COALESCE(excluded.project_dir, project_dir),
                git_branch = COALESCE(excluded.git_branch, git_branch),
                -- a stored custom title is never overwritten by a later AI-generated one
                title = CASE
                    WHEN title_is_custom = 1 AND excluded.title_is_custom = 0 THEN title
                    ELSE COALESCE(excluded.title, title) END,
                title_is_custom = MAX(title_is_custom, excluded.title_is_custom),
                model = COALESCE(excluded.model, model),
                started_at = CASE
                    WHEN started_at IS NULL THEN excluded.started_at
                    WHEN excluded.started_at IS NOT NULL AND excluded.started_at < started_at THEN excluded.started_at
                    ELSE started_at END,
                ended_at = CASE
                    WHEN ended_at IS NULL THEN excluded.ended_at
                    WHEN excluded.ended_at IS NOT NULL AND excluded.ended_at > ended_at THEN excluded.ended_at
                    ELSE ended_at END,
                input_tokens = input_tokens + excluded.input_tokens,
                output_tokens = output_tokens + excluded.output_tokens,
                cache_creation_tokens = cache_creation_tokens + excluded.cache_creation_tokens,
                cache_read_tokens = cache_read_tokens + excluded.cache_read_tokens,
                edit_count = edit_count + excluded.edit_count,
                write_count = write_count + excluded.write_count,
                read_count = read_count + excluded.read_count,
                bash_count = bash_count + excluded.bash_count,
                other_tool_count = other_tool_count + excluded.other_tool_count,
                user_message_count = user_message_count + excluded.user_message_count,
                cc_version = COALESCE(excluded.cc_version, cc_version)
            """;
        cmd.Parameters.AddWithValue("$id", a.SessionId);
        cmd.Parameters.AddWithValue("$fp", a.FilePath);
        cmd.Parameters.AddWithValue("$dir", (object?)a.ProjectDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch", (object?)a.GitBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)a.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$titleCustom", a.TitleIsCustom ? 1 : 0);
        cmd.Parameters.AddWithValue("$model", (object?)a.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", (object?)a.StartedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$end", (object?)a.EndedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$in", a.InputTokens);
        cmd.Parameters.AddWithValue("$out", a.OutputTokens);
        cmd.Parameters.AddWithValue("$cc", a.CacheCreationTokens);
        cmd.Parameters.AddWithValue("$cr", a.CacheReadTokens);
        cmd.Parameters.AddWithValue("$edit", a.EditCount);
        cmd.Parameters.AddWithValue("$write", a.WriteCount);
        cmd.Parameters.AddWithValue("$read", a.ReadCount);
        cmd.Parameters.AddWithValue("$bash", a.BashCount);
        cmd.Parameters.AddWithValue("$other", a.OtherToolCount);
        cmd.Parameters.AddWithValue("$umc", a.UserMessageCount);
        cmd.Parameters.AddWithValue("$ver", (object?)a.CcVersion ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static void AddAutoLink(SqliteConnection conn, string sessionId, string ticketKey, string inferredFrom)
    {
        ticketKey = TicketKey.Require(ticketKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Tickets(key) VALUES ($key);
            INSERT OR IGNORE INTO SessionTicketLinks(session_id, ticket_key, source, inferred_from)
                VALUES ($sid, $key, 'auto', $src);
            UPDATE Sessions SET review_state = 'linked' WHERE id = $sid AND review_state = 'pending';
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$key", ticketKey);
        cmd.Parameters.AddWithValue("$src", inferredFrom);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Link a Live Code session to the ticket it was started for, before the transcript is scanned.
    /// Inserts a placeholder Sessions row (keyed by the launched --session-id) so the FK holds; the
    /// scanner later accumulates the real tokens into the same row (ON CONFLICT(id)). The link uses
    /// source 'livecode' so the allowlist purge (which only removes 'auto') never drops it.
    /// </summary>
    public static void LinkLiveCodeSession(SqliteConnection conn, string sessionId, string filePath,
        string? projectDir, string ticketKey)
    {
        // Validated here as well as at the handler so no future caller can reintroduce the gap that
        // made this the only unconstrained writer of ticket_key (2026-08 audit, AIU-04).
        ticketKey = TicketKey.Require(ticketKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Sessions (id, file_path, project_dir, review_state)
                VALUES ($sid, $fp, $dir, 'linked');
            UPDATE Sessions SET file_path = $fp, project_dir = COALESCE($dir, project_dir),
                                review_state = 'linked'
                WHERE id = $sid;
            INSERT OR IGNORE INTO Tickets(key) VALUES ($key);
            INSERT INTO SessionTicketLinks(session_id, ticket_key, source, inferred_from)
                VALUES ($sid, $key, 'livecode', NULL)
                ON CONFLICT(session_id, ticket_key) DO UPDATE SET source = 'livecode';
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$fp", filePath);
        cmd.Parameters.AddWithValue("$dir", (object?)projectDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", ticketKey);
        cmd.ExecuteNonQuery();
    }

    public static void AssignTicket(SqliteConnection conn, string sessionId, string ticketKey)
    {
        ticketKey = TicketKey.Require(ticketKey);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Tickets(key) VALUES ($key);
            INSERT INTO SessionTicketLinks(session_id, ticket_key, source, inferred_from)
                VALUES ($sid, $key, 'manual', NULL)
                ON CONFLICT(session_id, ticket_key) DO UPDATE SET source = 'manual';
            UPDATE Sessions SET review_state = 'linked' WHERE id = $sid;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$key", ticketKey);
        cmd.ExecuteNonQuery();
    }

    public static void ConfirmLink(SqliteConnection conn, string sessionId, string ticketKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE SessionTicketLinks SET source = 'confirmed'
            WHERE session_id = $sid AND ticket_key = $key AND source = 'auto'
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$key", ticketKey);
        cmd.ExecuteNonQuery();
    }

    public static void RemoveLink(SqliteConnection conn, string sessionId, string ticketKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM SessionTicketLinks WHERE session_id = $sid AND ticket_key = $key;
            UPDATE Sessions SET review_state = 'pending'
            WHERE id = $sid AND review_state = 'linked'
              AND NOT EXISTS (SELECT 1 FROM SessionTicketLinks WHERE session_id = $sid);
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$key", ticketKey);
        cmd.ExecuteNonQuery();
    }

    public static void SetReviewState(SqliteConnection conn, string sessionId, string state)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Sessions SET review_state = $state WHERE id = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.ExecuteNonQuery();
    }

    public static List<Dictionary<string, object?>> List(SqliteConnection conn, string filter)
    {
        using var cmd = conn.CreateCommand();
        var where = filter switch
        {
            "pending" => "WHERE s.review_state = 'pending'",
            "not_ticket_related" => "WHERE s.review_state = 'not_ticket_related'",
            _ => ""
        };
        cmd.CommandText = $"""
            SELECT s.id, s.title, s.project_dir, s.git_branch, s.model,
                   s.started_at, s.ended_at, s.input_tokens, s.output_tokens,
                   s.edit_count, s.write_count, s.read_count, s.bash_count, s.other_tool_count,
                   s.user_message_count, s.review_state,
                   (SELECT GROUP_CONCAT(l.ticket_key || '|' || l.source, ';')
                      FROM SessionTicketLinks l WHERE l.session_id = s.id) AS links
            FROM Sessions s
            {where}
            ORDER BY COALESCE(s.ended_at, s.started_at) DESC, s.started_at DESC
            """;

        var rows = new List<Dictionary<string, object?>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[ToCamel(reader.GetName(i))] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Full stored row for one session (detail page), with its ticket links and any
    /// explicit activity-category name. Returns null if the session id is unknown.</summary>
    public static Dictionary<string, object?>? Get(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.title, s.project_dir, s.git_branch, s.model, s.file_path,
                   s.started_at, s.ended_at, s.input_tokens, s.output_tokens,
                   s.cache_creation_tokens, s.cache_read_tokens,
                   s.edit_count, s.write_count, s.read_count, s.bash_count, s.other_tool_count,
                   s.user_message_count, s.cc_version, s.review_state,
                   (SELECT ac.name FROM SessionTicketLinks l
                      LEFT JOIN ActivityCategories ac ON ac.id = l.category_id
                      WHERE l.session_id = s.id AND l.category_id IS NOT NULL LIMIT 1) AS category_name,
                   (SELECT GROUP_CONCAT(l.ticket_key || '|' || l.source, ';')
                      FROM SessionTicketLinks l WHERE l.session_id = s.id) AS links
            FROM Sessions s
            WHERE s.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
            row[ToCamel(reader.GetName(i))] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private static string ToCamel(string snake)
    {
        var parts = snake.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
