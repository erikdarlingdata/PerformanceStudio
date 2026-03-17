using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static class ServerMetadataService
{
    private const string ServerMetadataQuery = @"
SELECT @@SERVERNAME,
       SERVERPROPERTY('ProductVersion'),
       SERVERPROPERTY('ProductLevel'),
       SERVERPROPERTY('Edition');

SELECT cpu_count, physical_memory_kb FROM sys.dm_os_sys_info;

SELECT name, CAST(value_in_use AS bigint)
FROM sys.configurations
WHERE name IN (
    'max degree of parallelism',
    'cost threshold for parallelism',
    'max server memory (MB)'
);";

    private const string DatabaseMetadataQuery = @"
SELECT
    d.name,
    d.compatibility_level,
    d.collation_name,
    d.snapshot_isolation_state,
    d.is_read_committed_snapshot_on,
    d.is_auto_create_stats_on,
    d.is_auto_update_stats_on,
    d.is_auto_update_stats_async_on,
    d.is_parameterization_forced
FROM sys.databases AS d
WHERE d.database_id = DB_ID();";

    private const string ScopedConfigQuery = @"
SELECT name,
       CAST(value AS nvarchar(256)),
       CAST(value_for_secondary AS nvarchar(256))
FROM sys.database_scoped_configurations
WHERE is_value_default = 0;";

    public static async Task<ServerMetadata> FetchServerMetadataAsync(
        string connectionString,
        bool isAzure,
        CancellationToken cancellationToken = default)
    {
        var metadata = new ServerMetadata { IsAzure = isAzure };

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(ServerMetadataQuery, conn);
        cmd.CommandTimeout = 10;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        // Result set 1: server properties
        if (await reader.ReadAsync(cancellationToken))
        {
            metadata.ServerName = reader.IsDBNull(0) ? null : reader.GetString(0);
            metadata.ProductVersion = reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString();
            metadata.ProductLevel = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString();
            metadata.Edition = reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString();
        }

        // Result set 2: hardware
        if (await reader.NextResultAsync(cancellationToken) &&
            await reader.ReadAsync(cancellationToken))
        {
            metadata.CpuCount = reader.GetInt32(0);
            metadata.PhysicalMemoryMB = reader.GetInt64(1) / 1024;
        }

        // Result set 3: configurations
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var value = reader.GetInt64(1);

                switch (name)
                {
                    case "max degree of parallelism":
                        metadata.MaxDop = (int)value;
                        break;
                    case "cost threshold for parallelism":
                        metadata.CostThresholdForParallelism = (int)value;
                        break;
                    case "max server memory (MB)":
                        metadata.MaxServerMemoryMB = value;
                        break;
                }
            }
        }

        return metadata;
    }

    public static async Task<DatabaseMetadata> FetchDatabaseMetadataAsync(
        string connectionString,
        bool supportsScoped,
        CancellationToken cancellationToken = default)
    {
        var db = new DatabaseMetadata();

        var query = supportsScoped
            ? DatabaseMetadataQuery + ScopedConfigQuery
            : DatabaseMetadataQuery;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(query, conn);
        cmd.CommandTimeout = 10;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        // Result set 1: sys.databases
        if (await reader.ReadAsync(cancellationToken))
        {
            db.Name = reader.GetString(0);                          // name
            db.CompatibilityLevel = reader.GetByte(1);              // compatibility_level
            db.CollationName = reader.GetString(2);                 // collation_name
            db.SnapshotIsolationState = reader.GetByte(3);          // snapshot_isolation_state
            db.IsReadCommittedSnapshotOn = reader.GetBoolean(4);    // is_read_committed_snapshot_on
            db.IsAutoCreateStatsOn = reader.GetBoolean(5);          // is_auto_create_stats_on
            db.IsAutoUpdateStatsOn = reader.GetBoolean(6);          // is_auto_update_stats_on
            db.IsAutoUpdateStatsAsyncOn = reader.GetBoolean(7);     // is_auto_update_stats_async_on
            db.IsParameterizationForced = reader.GetBoolean(8);     // is_parameterization_forced
        }

        // Result set 2: scoped configs (optional)
        if (supportsScoped &&
            await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                db.NonDefaultScopedConfigs.Add(new ScopedConfigItem
                {
                    Name = reader.GetString(0),
                    Value = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    ValueForSecondary = reader.IsDBNull(2) ? null : reader.GetString(2)
                });
            }
        }

        return db;
    }
}
