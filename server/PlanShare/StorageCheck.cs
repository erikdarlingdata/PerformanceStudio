using Microsoft.Data.Sqlite;

namespace PlanShare;

/// <summary>
/// Tells whether the plan database has reached its size limit. Size means the pages in use:
/// (page_count - freelist_count) * page_size. The freelist is left out on purpose, because
/// deleting expired plans returns their pages to it and the file itself does not shrink, so
/// the file length would stay above the limit after cleanup had freed the room.
/// </summary>
internal sealed class StorageCheck
{
    private readonly string _connectionString;
    private readonly long _maxBytes;

    public StorageCheck(string connectionString, long maxBytes)
    {
        _connectionString = connectionString;
        _maxBytes = maxBytes;
    }

    public long UsedBytes()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var pageCount = Pragma(conn, "page_count");
        var freePages = Pragma(conn, "freelist_count");
        var pageSize = Pragma(conn, "page_size");
        return (pageCount - freePages) * pageSize;
    }

    /// <summary>True at or above the limit.</summary>
    public bool IsFull() => UsedBytes() >= _maxBytes;

    private static long Pragma(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {name};";
        return (long)cmd.ExecuteScalar()!;
    }
}
