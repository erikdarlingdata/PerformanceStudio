using Microsoft.Data.Sqlite;
using PlanShare;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The share server stops taking uploads when the plan database reaches its size limit. Size is the
/// pages in use, (page_count - freelist_count) * page_size, so these tests run against a real SQLite
/// file with a small limit instead of a mock.
/// </summary>
public class PlanShareStorageCheckTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "planshare-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _connectionString;

    public PlanShareStorageCheckTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "plans.db");
        // No pooling, so the file is closed and can be deleted as soon as a command finishes
        _connectionString = $"Data Source={_dbPath};Pooling=False";
        Execute("CREATE TABLE plans (id INTEGER PRIMARY KEY, data BLOB NOT NULL);");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void UsedBytes_IsTheFileLength_WhenNoPagesAreFree()
    {
        AddPlans(rows: 30, bytesPerRow: 8000);

        var used = new StorageCheck(_connectionString, long.MaxValue).UsedBytes();

        Assert.True(used > 30 * 8000);
        Assert.Equal(new FileInfo(_dbPath).Length, used);
    }

    [Fact]
    public void UsedBytes_LeavesOutFreePages_AfterRowsAreDeleted()
    {
        AddPlans(rows: 30, bytesPerRow: 8000);
        var length = new FileInfo(_dbPath).Length;

        Execute("DELETE FROM plans;");

        var used = new StorageCheck(_connectionString, long.MaxValue).UsedBytes();
        Assert.Equal(length, new FileInfo(_dbPath).Length);
        Assert.True(used < length / 4, $"used {used} of {length}");
    }

    [Fact]
    public void IsFull_IsTrueAtTheLimit_AndFalseJustBelowIt()
    {
        AddPlans(rows: 30, bytesPerRow: 8000);
        var used = new StorageCheck(_connectionString, long.MaxValue).UsedBytes();

        Assert.True(new StorageCheck(_connectionString, used).IsFull());
        Assert.True(new StorageCheck(_connectionString, used - 1).IsFull());
        Assert.False(new StorageCheck(_connectionString, used + 1).IsFull());
    }

    [Fact]
    public void IsFull_GoesBackToFalse_WhenDeletedPlansFreeTheirPagesAndTheFileDoesNotShrink()
    {
        AddPlans(rows: 30, bytesPerRow: 8000);
        var limit = 100 * 1024;
        var check = new StorageCheck(_connectionString, limit);
        Assert.True(check.IsFull());

        Execute("DELETE FROM plans;");

        Assert.True(new FileInfo(_dbPath).Length >= limit);
        Assert.False(check.IsFull());
    }

    private void AddPlans(int rows, int bytesPerRow)
    {
        Execute($"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {rows}) " +
                $"INSERT INTO plans (data) SELECT zeroblob({bytesPerRow}) FROM n;");
    }

    private void Execute(string sql)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
