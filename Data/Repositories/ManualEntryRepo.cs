using Microsoft.Data.Sqlite;

namespace AIUsage.Data.Repositories;

public static class ManualEntryRepo
{
    public static long Create(SqliteConnection conn, string ticketKey, string entryDate,
        long? categoryId, string? description, string? toolUsed)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Tickets(key) VALUES ($key);
            INSERT INTO ManualEntries(ticket_key, entry_date, category_id, description, tool_used, created_at)
            VALUES ($key, $date, $cat, $desc, $tool, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$key", ticketKey);
        cmd.Parameters.AddWithValue("$date", entryDate);
        cmd.Parameters.AddWithValue("$cat", (object?)categoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tool", (object?)toolUsed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public static void Delete(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ManualEntries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static List<Dictionary<string, object?>> List(SqliteConnection conn, int limit = 100)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.ticket_key AS ticketKey, m.entry_date AS entryDate,
                   c.name AS category, m.description, m.tool_used AS toolUsed,
                   t.summary AS ticketSummary
            FROM ManualEntries m
            LEFT JOIN ActivityCategories c ON c.id = m.category_id
            LEFT JOIN Tickets t ON t.key = m.ticket_key
            ORDER BY m.entry_date DESC, m.id DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
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

    public static List<Dictionary<string, object?>> Categories(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM ActivityCategories ORDER BY id";
        var rows = new List<Dictionary<string, object?>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new Dictionary<string, object?> { ["id"] = reader.GetInt64(0), ["name"] = reader.GetString(1) });
        return rows;
    }
}
