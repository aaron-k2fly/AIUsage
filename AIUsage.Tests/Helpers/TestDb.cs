using AIUsage.Data;
using Microsoft.Data.Sqlite;

namespace AIUsage.Tests.Helpers;

/// <summary>
/// A throwaway, in-memory SQLite database migrated to the current schema, for data-layer
/// integration tests. Each instance keeps its own single connection open (an in-memory DB
/// only lives as long as its connection), so tests are fully independent and parallel-safe.
/// The repositories all take an explicit <see cref="SqliteConnection"/>, so no global
/// <c>Db</c> state is touched.
/// </summary>
public sealed class TestDb : IDisposable
{
    public SqliteConnection Conn { get; }

    public TestDb()
    {
        Conn = new SqliteConnection("Data Source=:memory:");
        Conn.Open();
        using (var pragma = Conn.CreateCommand())
        {
            // Match Db.Open's FK enforcement (WAL is N/A for an in-memory DB).
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        Migrations.Run(Conn);
    }

    /// <summary>Scalar helper for terse assertions in tests.</summary>
    public T? Scalar<T>(string sql)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return default;
        return (T)Convert.ChangeType(v, typeof(T));
    }

    /// <summary>Run arbitrary SQL, for tests that need to set up or perturb state directly.</summary>
    public void Exec(string sql)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => Conn.Dispose();
}
