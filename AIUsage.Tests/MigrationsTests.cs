using AIUsage.Data;
using AIUsage.Tests.Helpers;

namespace AIUsage.Tests;

public class MigrationsTests
{
    [Fact]
    public void Run_stamps_the_current_schema_version()
    {
        using var db = new TestDb();
        Assert.Equal(6, db.Scalar<int>("SELECT version FROM SchemaVersion"));
    }

    [Theory]
    [InlineData("Sessions")]
    [InlineData("Tickets")]
    [InlineData("SessionTicketLinks")]
    [InlineData("ToolUsage")]
    [InlineData("ManualEntries")]
    [InlineData("Settings")]
    [InlineData("ActivityCategories")]
    [InlineData("ScanState")]
    [InlineData("SchemaVersion")]
    public void Run_creates_the_expected_tables(string table)
    {
        using var db = new TestDb();
        var count = db.Scalar<long>(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'");
        Assert.Equal(1, count);
    }

    [Fact]
    public void Run_seeds_the_activity_categories()
    {
        using var db = new TestDb();
        Assert.Equal(7, db.Scalar<long>("SELECT COUNT(*) FROM ActivityCategories"));
        Assert.Equal(1, db.Scalar<long>(
            "SELECT COUNT(*) FROM ActivityCategories WHERE name='Debugged'"));
    }

    [Fact]
    public void Run_is_idempotent()
    {
        using var db = new TestDb();

        // TestDb already ran migrations once; running again must not throw or duplicate anything.
        Migrations.Run(db.Conn);
        Migrations.Run(db.Conn);

        Assert.Equal(6, db.Scalar<int>("SELECT version FROM SchemaVersion"));
        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM SchemaVersion"));
        Assert.Equal(7, db.Scalar<long>("SELECT COUNT(*) FROM ActivityCategories"));
    }

    [Fact]
    public void Run_on_a_fresh_db_does_not_flag_a_toolusage_backfill()
    {
        using var db = new TestDb();
        // Backfill is only for DBs upgrading from an older version that already had sessions.
        var flag = db.Scalar<string>(
            "SELECT value FROM Settings WHERE key='toolusage_backfill_pending'");
        Assert.Null(flag);
    }
}
