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
            """;
        cmd.ExecuteNonQuery();

        AddColumnIfMissing(conn, "Sessions", "title_is_custom", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Tickets", "project", "TEXT");
        AddColumnIfMissing(conn, "Tickets", "sprint", "TEXT");
        AddColumnIfMissing(conn, "Tickets", "priority", "TEXT");
        AddColumnIfMissing(conn, "Tickets", "updated", "TEXT");

        Seed(conn);
        SetVersion(conn, 4);
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
