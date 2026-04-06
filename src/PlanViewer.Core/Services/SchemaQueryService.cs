using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace PlanViewer.Core.Services;

public sealed class IndexInfo
{
    public required string IndexName { get; init; }
    public required string IndexType { get; init; }
    public required bool IsUnique { get; init; }
    public required bool IsPrimaryKey { get; init; }
    public required string KeyColumns { get; init; }
    public required string IncludeColumns { get; init; }
    public required string? FilterDefinition { get; init; }
    public required long RowCount { get; init; }
    public required double SizeMB { get; init; }
    public long UserSeeks { get; init; }
    public long UserScans { get; init; }
    public long UserLookups { get; init; }
    public long UserUpdates { get; init; }
    public int FillFactor { get; init; }
    public bool IsPadded { get; init; }
    public bool AllowRowLocks { get; init; } = true;
    public bool AllowPageLocks { get; init; } = true;
    public bool IsDisabled { get; init; }
    public required string DataCompression { get; init; }
    public string? PartitionScheme { get; init; }
    public string? PartitionColumn { get; init; }
}

public sealed class ColumnInfo
{
    public required int OrdinalPosition { get; init; }
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }
    public required bool IsNullable { get; init; }
    public required bool IsIdentity { get; init; }
    public required bool IsComputed { get; init; }
    public required string? DefaultValue { get; init; }
    public required string? ComputedDefinition { get; init; }
    public required long IdentitySeed { get; init; }
    public required long IdentityIncrement { get; init; }
}

/// <summary>
/// Fetches schema information (indexes, columns, object definitions) from a connected SQL Server.
/// </summary>
public static class SchemaQueryService
{
    private const string IndexQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    i.name AS index_name,
    i.type_desc AS index_type,
    i.is_unique,
    i.is_primary_key,
    STUFF((
        SELECT ', ' + c.name + CASE WHEN ic2.is_descending_key = 1 THEN ' DESC' ELSE '' END
        FROM sys.index_columns AS ic2
        JOIN sys.columns AS c ON c.object_id = ic2.object_id AND c.column_id = ic2.column_id
        WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0
        ORDER BY ic2.key_ordinal
        FOR XML PATH('')
    ), 1, 2, '') AS key_columns,
    ISNULL(STUFF((
        SELECT ', ' + c.name
        FROM sys.index_columns AS ic2
        JOIN sys.columns AS c ON c.object_id = ic2.object_id AND c.column_id = ic2.column_id
        WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 1
        ORDER BY c.name
        FOR XML PATH('')
    ), 1, 2, ''), '') AS include_columns,
    i.filter_definition,
    p.row_count,
    CAST(ROUND(p.reserved_page_count * 8.0 / 1024, 2) AS float) AS size_mb,
    ISNULL(us.user_seeks, 0) AS user_seeks,
    ISNULL(us.user_scans, 0) AS user_scans,
    ISNULL(us.user_lookups, 0) AS user_lookups,
    ISNULL(us.user_updates, 0) AS user_updates,
    CAST(i.fill_factor AS int),
    i.is_padded,
    i.allow_row_locks,
    i.allow_page_locks,
    i.is_disabled,
    ISNULL(p.data_compression_desc, 'NONE') AS data_compression,
    psch.name AS partition_scheme,
    pc.name AS partition_column
FROM sys.indexes AS i
CROSS APPLY (
    SELECT SUM(ps.row_count) AS row_count,
           SUM(ps.reserved_page_count) AS reserved_page_count,
           MAX(pt.data_compression_desc) AS data_compression_desc
    FROM sys.dm_db_partition_stats AS ps
    JOIN sys.partitions AS pt ON pt.partition_id = ps.partition_id
    WHERE ps.object_id = i.object_id AND ps.index_id = i.index_id
) AS p
LEFT JOIN sys.dm_db_index_usage_stats AS us
    ON us.object_id = i.object_id AND us.index_id = i.index_id AND us.database_id = DB_ID()
LEFT JOIN sys.partition_schemes AS psch
    ON psch.data_space_id = i.data_space_id
LEFT JOIN sys.index_columns AS pic
    ON pic.object_id = i.object_id AND pic.index_id = i.index_id AND pic.partition_ordinal > 0
LEFT JOIN sys.columns AS pc
    ON pc.object_id = pic.object_id AND pc.column_id = pic.column_id
WHERE i.object_id = OBJECT_ID(@objectName)
    AND i.type > 0
ORDER BY i.index_id;";

    private const string ColumnQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    c.column_id AS ordinal_position,
    c.name AS column_name,
    tp.name +
        CASE
            WHEN tp.name IN ('varchar','nvarchar','char','nchar','binary','varbinary')
                THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(
                    CASE WHEN tp.name IN ('nvarchar','nchar') THEN c.max_length / 2 ELSE c.max_length END
                AS varchar) END + ')'
            WHEN tp.name IN ('decimal','numeric')
                THEN '(' + CAST(c.precision AS varchar) + ',' + CAST(c.scale AS varchar) + ')'
            WHEN tp.name IN ('datetime2','datetimeoffset','time')
                THEN '(' + CAST(c.scale AS varchar) + ')'
            ELSE ''
        END AS data_type,
    c.is_nullable,
    c.is_identity,
    c.is_computed,
    dc.definition AS default_value,
    cc.definition AS computed_definition,
    CAST(ISNULL(ic.seed_value, 0) AS bigint) AS identity_seed,
    CAST(ISNULL(ic.increment_value, 0) AS bigint) AS identity_increment
FROM sys.columns AS c
JOIN sys.types AS tp ON tp.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE c.object_id = OBJECT_ID(@objectName)
ORDER BY c.column_id;";

    private const string ObjectDefinitionQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT OBJECT_DEFINITION(OBJECT_ID(@objectName));";

    public static async Task<IReadOnlyList<IndexInfo>> FetchIndexesAsync(
        string connectionString, string objectName, CancellationToken ct = default)
    {
        var results = new List<IndexInfo>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(IndexQuery, conn);
        cmd.CommandTimeout = 10;
        cmd.Parameters.AddWithValue("@objectName", objectName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new IndexInfo
            {
                IndexName = reader.GetString(0),
                IndexType = reader.GetString(1),
                IsUnique = reader.GetBoolean(2),
                IsPrimaryKey = reader.GetBoolean(3),
                KeyColumns = reader.IsDBNull(4) ? "" : reader.GetString(4),
                IncludeColumns = reader.IsDBNull(5) ? "" : reader.GetString(5),
                FilterDefinition = reader.IsDBNull(6) ? null : reader.GetString(6),
                RowCount = reader.GetInt64(7),
                SizeMB = reader.GetDouble(8),
                UserSeeks = reader.GetInt64(9),
                UserScans = reader.GetInt64(10),
                UserLookups = reader.GetInt64(11),
                UserUpdates = reader.GetInt64(12),
                FillFactor = reader.GetInt32(13),
                IsPadded = reader.GetBoolean(14),
                AllowRowLocks = reader.GetBoolean(15),
                AllowPageLocks = reader.GetBoolean(16),
                IsDisabled = reader.GetBoolean(17),
                DataCompression = reader.GetString(18),
                PartitionScheme = reader.IsDBNull(19) ? null : reader.GetString(19),
                PartitionColumn = reader.IsDBNull(20) ? null : reader.GetString(20)
            });
        }

        return results;
    }

    public static async Task<IReadOnlyList<ColumnInfo>> FetchColumnsAsync(
        string connectionString, string objectName, CancellationToken ct = default)
    {
        var results = new List<ColumnInfo>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(ColumnQuery, conn);
        cmd.CommandTimeout = 10;
        cmd.Parameters.AddWithValue("@objectName", objectName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new ColumnInfo
            {
                OrdinalPosition = reader.GetInt32(0),
                ColumnName = reader.GetString(1),
                DataType = reader.GetString(2),
                IsNullable = reader.GetBoolean(3),
                IsIdentity = reader.GetBoolean(4),
                IsComputed = reader.GetBoolean(5),
                DefaultValue = reader.IsDBNull(6) ? null : reader.GetString(6),
                ComputedDefinition = reader.IsDBNull(7) ? null : reader.GetString(7),
                IdentitySeed = reader.GetInt64(8),
                IdentityIncrement = reader.GetInt64(9)
            });
        }

        return results;
    }

    public static async Task<string?> FetchObjectDefinitionAsync(
        string connectionString, string objectName, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(ObjectDefinitionQuery, conn);
        cmd.CommandTimeout = 10;
        cmd.Parameters.AddWithValue("@objectName", objectName);

        var result = await cmd.ExecuteScalarAsync(ct);

        return result as string;
    }
}
