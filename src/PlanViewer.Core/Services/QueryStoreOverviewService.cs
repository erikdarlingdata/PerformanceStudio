using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Service for fetching Query Store overview data across multiple databases in parallel.
/// </summary>
public static class QueryStoreOverviewService
{
    /// <summary>
    /// Fetches Query Store state for all user databases in parallel.
    /// </summary>
    public static async Task<List<DatabaseQueryStoreState>> FetchAllStatesAsync(
        string masterConnectionString,
        int maxDegreeOfParallelism = 8,
        CancellationToken ct = default)
    {
        var databases = await GetUserDatabasesAsync(masterConnectionString, ct);
        var results = new ConcurrentBag<DatabaseQueryStoreState>();
        var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

        var tasks = databases.Select(async db =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var state = await GetQueryStoreStateAsync(masterConnectionString, db, ct);
                results.Add(new DatabaseQueryStoreState { DatabaseName = db, State = state });
            }
            catch
            {
                results.Add(new DatabaseQueryStoreState { DatabaseName = db, State = QueryStoreState.Off });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.DatabaseName).ToList();
    }

    /// <summary>
    /// Fetches aggregated metrics for active Query Store databases in parallel.
    /// </summary>
    public static async Task<List<DatabaseMetrics>> FetchAllMetricsAsync(
        string masterConnectionString,
        List<string> activeDatabases,
        DateTime startUtc,
        DateTime endUtc,
        int maxDegreeOfParallelism = 8,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<DatabaseMetrics>();
        var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

        var tasks = activeDatabases.Select(async db =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var metrics = await FetchDatabaseMetricsAsync(masterConnectionString, db, startUtc, endUtc, ct);
                results.Add(metrics);
            }
            catch
            {
                results.Add(new DatabaseMetrics { DatabaseName = db });
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.DatabaseName).ToList();
    }

    /// <summary>
    /// Fetches time slice data for active Query Store databases in parallel.
    /// </summary>
    public static async Task<List<DatabaseTimeSlice>> FetchAllTimeSlicesAsync(
        string masterConnectionString,
        List<string> activeDatabases,
        int daysBack = 30,
        int maxDegreeOfParallelism = 8,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<DatabaseTimeSlice>();
        var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

        var tasks = activeDatabases.Select(async db =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var slices = await FetchDatabaseTimeSlicesAsync(masterConnectionString, db, daysBack, ct);
                foreach (var s in slices)
                    results.Add(s);
            }
            catch { /* skip database on error */ }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.IntervalStartUtc).ToList();
    }

    /// <summary>
    /// Fetches wait stats ribbon data for active databases in parallel.
    /// </summary>
    public static async Task<List<DatabaseWaitAmountTimeSlice>> FetchAllWaitStatsAsync(
        string masterConnectionString,
        List<string> activeDatabases,
        DateTime startUtc,
        DateTime endUtc,
        int maxDegreeOfParallelism = 8,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<DatabaseWaitAmountTimeSlice>();
        var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

        var tasks = activeDatabases.Select(async db =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var slices = await FetchDatabaseWaitAmountAsync(masterConnectionString, db, startUtc, endUtc, ct);
                foreach (var s in slices)
                    results.Add(s);
            }
            catch { /* skip database on error */ }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.IntervalStartUtc).ToList();
    }

    /// <summary>
    /// Fetches wait stats with error reporting per database.
    /// </summary>
    public static async Task<(List<DatabaseWaitAmountTimeSlice> Slices, List<(string Database, string Error)> Errors)> FetchAllWaitStatsWithErrorsAsync(
        string masterConnectionString,
        List<string> activeDatabases,
        DateTime startUtc,
        DateTime endUtc,
        int maxDegreeOfParallelism = 8,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<DatabaseWaitAmountTimeSlice>();
        var errors = new ConcurrentBag<(string Database, string Error)>();
        var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

        var tasks = activeDatabases.Select(async db =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var slices = await FetchDatabaseWaitAmountAsync(masterConnectionString, db, startUtc, endUtc, ct);
                foreach (var s in slices)
                    results.Add(s);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add((db, ex.Message));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return (results.OrderBy(r => r.IntervalStartUtc).ToList(), errors.ToList());
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static async Task<List<string>> GetUserDatabasesAsync(
        string connectionString, CancellationToken ct)
    {
        const string sql = @"
SELECT name FROM sys.databases
WHERE database_id > 4
  AND state_desc = 'ONLINE'
  AND is_read_only = 0
ORDER BY name;";

        var databases = new List<string>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            databases.Add(reader.GetString(0));
        return databases;
    }

    private static string BuildDbConnectionString(string masterConnectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = database
        };
        return builder.ConnectionString;
    }

    private static async Task<QueryStoreState> GetQueryStoreStateAsync(
        string masterConnectionString, string database, CancellationToken ct)
    {
        var connStr = BuildDbConnectionString(masterConnectionString, database);
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT actual_state_desc FROM sys.database_query_store_options;";

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 10 };
        var state = (string?)await cmd.ExecuteScalarAsync(ct);

        return state switch
        {
            "READ_WRITE" => QueryStoreState.ReadWrite,
            "READ_ONLY" => QueryStoreState.ReadOnly,
            _ => QueryStoreState.Off
        };
    }

    private static async Task<DatabaseMetrics> FetchDatabaseMetricsAsync(
        string masterConnectionString, string database,
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var connStr = BuildDbConnectionString(masterConnectionString, database);
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0,
    SUM(rs.avg_duration * rs.count_executions) / 1000.0,
    SUM(rs.count_executions),
    SUM(rs.avg_logical_io_reads * rs.count_executions),
    SUM(rs.avg_logical_io_writes * rs.count_executions),
    SUM(rs.avg_physical_io_reads * rs.count_executions),
    SUM(rs.avg_query_max_used_memory * rs.count_executions) * 8.0 / 1024.0
FROM sys.query_store_runtime_stats rs
JOIN sys.query_store_runtime_stats_interval rsi
    ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rsi.start_time >= @start AND rsi.start_time < @end;";

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@start", startUtc));
        cmd.Parameters.Add(new SqlParameter("@end", endUtc));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var metrics = new DatabaseMetrics { DatabaseName = database };
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
        {
            metrics.TotalCpu = reader.GetDouble(0);
            metrics.TotalDuration = reader.GetDouble(1);
            metrics.TotalExecutions = reader.GetInt64(2);
            metrics.TotalReads = reader.GetDouble(3);
            metrics.TotalWrites = reader.GetDouble(4);
            metrics.TotalPhysicalReads = reader.GetDouble(5);
            metrics.TotalMemory = reader.GetDouble(6);
        }
        return metrics;
    }

    private static async Task<List<DatabaseTimeSlice>> FetchDatabaseTimeSlicesAsync(
        string masterConnectionString, string database, int daysBack, CancellationToken ct)
    {
        var connStr = BuildDbConnectionString(masterConnectionString, database);
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0) AS bucket_hour,
    SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0,
    SUM(rs.avg_duration * rs.count_executions) / 1000.0,
    SUM(rs.avg_logical_io_reads * rs.count_executions),
    SUM(rs.avg_logical_io_writes * rs.count_executions),
    SUM(rs.avg_physical_io_reads * rs.count_executions),
    SUM(rs.avg_query_max_used_memory * rs.count_executions) * 8.0 / 1024.0,
    SUM(rs.count_executions)
FROM sys.query_store_runtime_stats rs
JOIN sys.query_store_runtime_stats_interval rsi
    ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rsi.start_time >= DATEADD(DAY, -@daysBack, GETUTCDATE())
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0)
ORDER BY bucket_hour;";

        var rows = new List<DatabaseTimeSlice>();
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@daysBack", daysBack));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DatabaseTimeSlice
            {
                DatabaseName = database,
                IntervalStartUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                TotalCpu = reader.GetDouble(1),
                TotalDuration = reader.GetDouble(2),
                TotalReads = reader.GetDouble(3),
                TotalWrites = reader.GetDouble(4),
                TotalPhysicalReads = reader.GetDouble(5),
                TotalMemory = reader.GetDouble(6),
                TotalExecutions = reader.GetInt64(7),
            });
        }
        return rows;
    }

    private static async Task<List<DatabaseWaitCategoryTimeSlice>> FetchDatabaseWaitStatsAsync(
        string masterConnectionString, string database,
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var connStr = BuildDbConnectionString(masterConnectionString, database);
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0) AS bucket_hour,
    ws.wait_category,
    ws.wait_category_desc,
    cast(1.0 * SUM(ws.total_query_wait_time_ms) / (3600.0 * 1000.0) AS float) AS wait_ratio
FROM sys.query_store_wait_stats ws
JOIN sys.query_store_runtime_stats_interval rsi
    ON ws.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rsi.start_time >= @start AND rsi.start_time < @end
AND   ws.execution_type = 0
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0),
         ws.wait_category, ws.wait_category_desc
ORDER BY bucket_hour, wait_ratio DESC;";

        var rows = new List<DatabaseWaitCategoryTimeSlice>();
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@start", startUtc));
        cmd.Parameters.Add(new SqlParameter("@end", endUtc));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DatabaseWaitCategoryTimeSlice
            {
                DatabaseName = database,
                IntervalStartUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                WaitCategory = reader.GetInt16(1),
                WaitCategoryDesc = reader.GetString(2),
                WaitRatio = reader.GetDouble(3),
            });
        }
        return rows;
    }

	private static async Task<List<DatabaseWaitAmountTimeSlice>> FetchDatabaseWaitAmountAsync(
		string masterConnectionString, string database,
		DateTime startUtc, DateTime endUtc, CancellationToken ct)
	{
		var connStr = BuildDbConnectionString(masterConnectionString, database);
		const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0) AS bucket_hour,
    cast(COALESCE(1.0 * SUM(ws.total_query_wait_time_ms) / (3600.0 * 1000.0),0.0) AS float) AS wait_ratio
FROM sys.query_store_wait_stats ws
JOIN sys.query_store_runtime_stats_interval rsi
    ON ws.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rsi.start_time >= @start AND rsi.start_time < @end
AND   ws.execution_type = 0
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0)
ORDER BY bucket_hour, wait_ratio;";

		var rows = new List<DatabaseWaitAmountTimeSlice>();
		await using var conn = new SqlConnection(connStr);
		await conn.OpenAsync(ct);
		await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
		cmd.Parameters.Add(new SqlParameter("@start", startUtc));
		cmd.Parameters.Add(new SqlParameter("@end", endUtc));
		await using var reader = await cmd.ExecuteReaderAsync(ct);

		while (await reader.ReadAsync(ct))
		{
			rows.Add(new DatabaseWaitAmountTimeSlice
			{
				DatabaseName = database,
				IntervalStartUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
				WaitRatio = reader.GetDouble(1),
			});
		}
		return rows;
	}
}
