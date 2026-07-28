using Microsoft.Data.Sqlite;

namespace AIUsage.Data;

public static class Migrations
{
    public static void Run(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Sessions (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                project_dir TEXT,
                git_branch TEXT,
                title TEXT,
                title_is_custom INTEGER NOT NULL DEFAULT 0,
                model TEXT,
                started_at TEXT,
                ended_at TEXT,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                edit_count INTEGER NOT NULL DEFAULT 0,
                write_count INTEGER NOT NULL DEFAULT 0,
                read_count INTEGER NOT NULL DEFAULT 0,
                bash_count INTEGER NOT NULL DEFAULT 0,
                other_tool_count INTEGER NOT NULL DEFAULT 0,
                user_message_count INTEGER NOT NULL DEFAULT 0,
                cc_version TEXT,
                review_state TEXT NOT NULL DEFAULT 'pending'
            );

            CREATE TABLE IF NOT EXISTS ScanState (
                file_path TEXT PRIMARY KEY,
                last_offset INTEGER NOT NULL DEFAULT 0,
                last_mtime TEXT,
                last_size INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Tickets (
                key TEXT PRIMARY KEY,
                summary TEXT,
                status TEXT,
                issue_type TEXT,
                project TEXT,
                sprint TEXT,
                priority TEXT,
                updated TEXT,
                description TEXT,
                last_synced TEXT,
                fetch_failed INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS ActivityCategories (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS SessionTicketLinks (
                session_id TEXT NOT NULL REFERENCES Sessions(id) ON DELETE CASCADE,
                ticket_key TEXT NOT NULL,
                source TEXT NOT NULL,
                inferred_from TEXT,
                category_id INTEGER REFERENCES ActivityCategories(id),
                PRIMARY KEY (session_id, ticket_key)
            );

            -- Per-session usage of sub-agents / skills / MCP servers / hooks, derived (set
            -- semantics) from a full transcript parse. Powers the dashboard's automation charts;
            -- the token/tool-bucket counters in Sessions are unaffected. category ∈
            -- agent|skill|mcp|hook. Rows cascade-delete with their session.
            CREATE TABLE IF NOT EXISTS ToolUsage (
                session_id TEXT NOT NULL REFERENCES Sessions(id) ON DELETE CASCADE,
                category TEXT NOT NULL,
                name TEXT NOT NULL,
                count INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (session_id, category, name)
            );

            -- Per-session, per-LOCAL-day token spend (v7). Sessions.input_tokens/output_tokens are a
            -- whole-session total attributed to started_at, which misreports any windowed figure for
            -- the multi-day sessions Claude Code produces on resume. These buckets are additive in
            -- exactly the same way, just keyed by the day each message was actually sent, so a rolling
            -- "last 7 days" sum is exact. day is 'yyyy-MM-dd'; rows cascade-delete with their session.
            CREATE TABLE IF NOT EXISTS SessionDailyTokens (
                session_id TEXT NOT NULL REFERENCES Sessions(id) ON DELETE CASCADE,
                file_path TEXT NOT NULL,
                day TEXT NOT NULL,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (session_id, day)
            );

            CREATE TABLE IF NOT EXISTS ManualEntries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ticket_key TEXT NOT NULL,
                entry_date TEXT NOT NULL,
                category_id INTEGER REFERENCES ActivityCategories(id),
                description TEXT,
                tool_used TEXT,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Settings (
                key TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_links_ticket ON SessionTicketLinks(ticket_key);
            CREATE INDEX IF NOT EXISTS idx_manual_ticket ON ManualEntries(ticket_key);
            CREATE INDEX IF NOT EXISTS idx_sessions_started ON Sessions(started_at);
            CREATE INDEX IF NOT EXISTS idx_toolusage_cat ON ToolUsage(category, name);
            CREATE INDEX IF NOT EXISTS idx_dailytokens_day ON SessionDailyTokens(day);
            """;
        cmd.ExecuteNonQuery();

        // ToolUsage arrived in v6 and isn't part of the incremental token scan, so already-scanned
        // sessions have no rows. Flag a one-time backfill (a full ToolUsage-only re-parse of every
        // transcript) for the next scan — but only when there's existing data to backfill.
        var oldVersion = CurrentVersion(conn);
        if (oldVersion is > 0 and < 6 && HasSessions(conn))
            SetSetting(conn, "toolusage_backfill_pending", "1");

        // Same story for SessionDailyTokens in v7: the incremental scanner only buckets lines it
        // reads from here on, so already-scanned transcripts need one full re-parse to get their
        // per-day rows.
        if (oldVersion is > 0 and < 7 && HasSessions(conn))
            SetSetting(conn, "dailytokens_backfill_pending", "1");

        AddColumnIfMissing(conn, "Sessions", "title_is_custom", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Tickets", "project", "TEXT");
        AddColumnIfMissing(conn, "Tickets", "sprint", "TEXT");
        AddColumnIfMissing(conn, "Tickets", "priority", "TEXT");
        AddColumnIfMissing(conn, "Tickets", "updated", "TEXT");
        AddColumnIfMissing(conn, "Tickets", "description", "TEXT");

        Seed(conn);
        SetVersion(conn, 7);
    }

    /// <summary>Current stored schema version, or 0 if none recorded yet (fresh DB).</summary>
    private static int CurrentVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM SchemaVersion LIMIT 1";
        return cmd.ExecuteScalar() is { } v ? Convert.ToInt32(v) : 0;
    }

    private static bool HasSessions(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM Sessions)";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static void SetSetting(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Idempotent ALTER TABLE for columns added after a table's initial CREATE shipped.</summary>
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column";
        check.Parameters.AddWithValue("$table", table);
        check.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private static void Seed(SqliteConnection conn)
    {
        var categories = new[]
        {
            "Generated code", "Wrote tests", "Refactored", "Debugged",
            "Reviewed", "Wrote docs", "Investigated"
        };
        foreach (var name in categories)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO ActivityCategories(name) VALUES ($name)";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
    }

    private static void SetVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM SchemaVersion;
            INSERT INTO SchemaVersion(version) VALUES ($v);
            """;
        cmd.Parameters.AddWithValue("$v", version);
        cmd.ExecuteNonQuery();
    }
}
