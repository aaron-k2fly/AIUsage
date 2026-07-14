using Microsoft.Data.Sqlite;

namespace AIUsage.Data;

public static class Db
{
    public static string DbPath { get; private set; } = "";

    public static void Initialize(string? dbPath = null)
    {
        DbPath = dbPath ?? ResolveDefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var conn = Open();
        Migrations.Run(conn);
    }

    public static SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Portable-first resolution: the DB lives next to the exe so the whole folder can
    /// be copied between machines. Falls back to %APPDATA% only when the exe directory
    /// isn't writable (e.g. installed under Program Files). An existing %APPDATA% DB is
    /// copied over once; the original is left behind as a backup.
    /// </summary>
    private static string ResolveDefaultPath()
    {
        var exeDir = AppContext.BaseDirectory;
        var portablePath = Path.Combine(exeDir, "aiusage.db");
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIUsage", "aiusage.db");

        if (File.Exists(portablePath)) return portablePath;

        if (IsWritable(exeDir))
        {
            if (File.Exists(appDataPath))
                CopyDbFiles(appDataPath, portablePath);
            return portablePath;
        }

        return appDataPath;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDbFiles(string from, string to)
    {
        // -wal/-shm hold not-yet-checkpointed writes; they must travel with the DB
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(from + suffix))
                File.Copy(from + suffix, to + suffix, overwrite: true);
        }
    }
}
