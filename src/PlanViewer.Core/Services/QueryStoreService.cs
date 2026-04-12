using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static class QueryStoreService
{
    /// <summary>
    /// Verifies Query Store is enabled and in a readable state on the target database.
    /// Returns true if Query Store is accessible.
    /// </summary>
    public static async Task<(bool Enabled, string? State)> CheckEnabledAsync(
        string connectionString, CancellationToken ct = default)
    {
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    actual_state_desc
FROM sys.database_query_store_options;";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        var state = (string?)await cmd.ExecuteScalarAsync(ct);

        if (state == null)
            return (false, null);

        var enabled = state.StartsWith("READ", StringComparison.OrdinalIgnoreCase);
        return (enabled, state);
    }

    /// <summary>
    /// Fetches the top N query plans from Query Store, ranked by the specified metric.
    /// Plans are estimated plans from the plan cache — Query Store does not store actual execution plans.
    /// Supported orderBy values: cpu, avg-cpu, duration, avg-duration, reads, avg-reads,
    /// writes, avg-writes, physical-reads, avg-physical-reads, memory, avg-memory, executions.
    /// Optional filter narrows results server-side by query_id, plan_id, query_hash,
    /// query_plan_hash, or module name (schema.name, supports % wildcards).
    /// When <paramref name="startUtc"/>/<paramref name="endUtc"/> are provided they override <paramref name="hoursBack"/>.
    /// </summary>
    public static async Task<List<QueryStorePlan>> FetchTopPlansAsync(
        string connectionString, int topN = 25, string orderBy = "cpu",
        int hoursBack = 24, QueryStoreFilter? filter = null,
        CancellationToken ct = default,
        DateTime? startUtc = null, DateTime? endUtc = null)
    {
        var key = orderBy.ToLowerInvariant();

        // ROW_NUMBER order: pick the "best" plan per query_id.
        // References pre-aggregated columns from #plan_stats temp table.
        // avg- variants still rank by total CPU (most impactful plan).
        var orderClause = key switch
        {
            "cpu"              => "ps.total_cpu_us",
            "duration"         => "ps.total_duration_us",
            "reads"            => "ps.total_reads",
            "writes"           => "ps.total_writes",
            "physical-reads"   => "ps.total_physical_reads",
            "memory"           => "ps.total_memory_pages",
            "executions"       => "ps.total_executions",
            _ => "ps.total_cpu_us"
        };

        // Final ORDER BY — either a total or avg column from ranked CTE.
        var outerOrder = key switch
        {
            "cpu"                => "total_cpu_us",
            "duration"           => "total_duration_us",
            "reads"              => "total_reads",
            "writes"             => "total_writes",
            "physical-reads"     => "total_physical_reads",
            "memory"             => "total_memory_pages",
            "executions"         => "total_executions",
            "avg-cpu"            => "avg_cpu_us",
            "avg-duration"       => "avg_duration_us",
            "avg-reads"          => "avg_reads",
            "avg-writes"         => "avg_writes",
            "avg-physical-reads" => "avg_physical_reads",
            "avg-memory"         => "avg_memory_pages",
            _ => "total_cpu_us"
        };

        // Build optional WHERE clauses from filter (parameterized for safety).
        var filterClauses = new List<string>();
        var parameters = new List<SqlParameter>();

        if (filter?.QueryId != null)
        {
            filterClauses.Add("AND q.query_id = @filterQueryId");
            parameters.Add(new SqlParameter("@filterQueryId", filter.QueryId.Value));
        }
        if (filter?.PlanId != null)
        {
            filterClauses.Add("AND tp.plan_id = @filterPlanId");
            parameters.Add(new SqlParameter("@filterPlanId", filter.PlanId.Value));
        }
        if (!string.IsNullOrWhiteSpace(filter?.QueryHash))
        {
            filterClauses.Add("AND q.query_hash = CONVERT(binary(8), @filterQueryHash, 1)");
            parameters.Add(new SqlParameter("@filterQueryHash", filter.QueryHash.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(filter?.QueryPlanHash))
        {
            filterClauses.Add("AND p.query_plan_hash = CONVERT(binary(8), @filterPlanHash, 1)");
            parameters.Add(new SqlParameter("@filterPlanHash", filter.QueryPlanHash.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(filter?.ModuleName))
        {
            // Support wildcards (%) like sp_QuickieStore. If no wildcard, exact match.
            var moduleVal = filter.ModuleName.Trim();
            if (moduleVal.Contains('%'))
            {
                filterClauses.Add("AND OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) LIKE @filterModule");
            }
            else
            {
                filterClauses.Add("AND OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) = @filterModule");
            }
            parameters.Add(new SqlParameter("@filterModule", moduleVal));
        }

        var rnClause = filter?.PlanId != null ? "" : "AND r.rn = 1";
        var filterSql = filterClauses.Count > 0
            ? "\n" + string.Join("\n", filterClauses)
            : "";

        // Time-range filter: always filter on interval start_time (indexed).
        // The hoursBack fallback also uses interval start_time instead of
        // rs.last_execution_time to avoid scanning all of runtime_stats.
        string intervalWhereClause;
        if (startUtc.HasValue && endUtc.HasValue)
        {
            intervalWhereClause = "WHERE rsi.start_time >= @rangeStart AND rsi.start_time < @rangeEnd";
            parameters.Add(new SqlParameter("@rangeStart", startUtc.Value));
            parameters.Add(new SqlParameter("@rangeEnd", endUtc.Value));
        }
        else
        {
            intervalWhereClause = "WHERE rsi.start_time >= DATEADD(HOUR, -@hoursBack, GETUTCDATE())";
            parameters.Add(new SqlParameter("@hoursBack", hoursBack));
        }

        // Multi-phase approach modeled on sp_QuickieStore (see GitHub issue #143):
        //
        // Phase 1: Materialize matching interval IDs into #intervals (tiny table,
        //          clustered PK). All subsequent phases reference this via EXISTS
        //          semi-join instead of re-evaluating the time predicate.
        //
        // Phase 2: Aggregate runtime_stats by plan_id into #plan_stats (clustered
        //          PK on plan_id). Uses EXISTS against #intervals — no direct join
        //          to the interval table, letting the optimizer use a semi-join.
        //
        // Phase 3: Rank plans per query_id, pick best per query, materialize TOP N
        //          into #top_plans. Still no nvarchar(max) columns.
        //
        // Phase 4: Final SELECT — join only the TOP N winners to query_text, plan
        //          XML, and query metadata. Uses OUTER APPLY + TRY_CONVERT for
        //          safe plan XML retrieval.
        //
        // OPTION (RECOMPILE) on aggregation phases prevents parameter sniffing on
        // date range parameters producing bad plans for different time windows.
        var sql = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

/* Phase 1: Pre-filter matching interval IDs */
DROP TABLE IF EXISTS #intervals;
CREATE TABLE #intervals (
    runtime_stats_interval_id bigint NOT NULL PRIMARY KEY CLUSTERED
);
INSERT INTO #intervals (runtime_stats_interval_id)
SELECT rsi.runtime_stats_interval_id
FROM sys.query_store_runtime_stats_interval AS rsi
{intervalWhereClause}
OPTION (RECOMPILE);

/* Phase 2: Aggregate runtime stats by plan_id */
DROP TABLE IF EXISTS #plan_stats;
CREATE TABLE #plan_stats (
    plan_id bigint NOT NULL PRIMARY KEY CLUSTERED,
    total_cpu_us float NOT NULL,
    total_duration_us float NOT NULL,
    total_reads float NOT NULL,
    total_writes float NOT NULL,
    total_physical_reads float NOT NULL,
    total_memory_pages float NOT NULL,
    total_executions bigint NOT NULL,
    last_execution_time datetimeoffset NOT NULL
);
INSERT INTO #plan_stats
SELECT
    rs.plan_id,
    SUM(rs.avg_cpu_time * rs.count_executions),
    SUM(rs.avg_duration * rs.count_executions),
    SUM(rs.avg_logical_io_reads * rs.count_executions),
    SUM(rs.avg_logical_io_writes * rs.count_executions),
    SUM(rs.avg_physical_io_reads * rs.count_executions),
    SUM(rs.avg_query_max_used_memory * rs.count_executions),
    SUM(rs.count_executions),
    MAX(rs.last_execution_time)
FROM sys.query_store_runtime_stats AS rs
WHERE EXISTS
(
    SELECT 1
    FROM #intervals AS i
    WHERE i.runtime_stats_interval_id = rs.runtime_stats_interval_id
)
GROUP BY rs.plan_id
OPTION (RECOMPILE);

/* Phase 3: Rank best plan per query, materialize TOP N */
DROP TABLE IF EXISTS #top_plans;
WITH ranked AS (
    SELECT
        p.query_id,
        ps.plan_id,
        ps.total_cpu_us,
        ps.total_duration_us,
        ps.total_reads,
        ps.total_writes,
        ps.total_physical_reads,
        ps.total_memory_pages,
        ps.total_executions,
        ps.last_execution_time,
        CASE WHEN ps.total_executions > 0
             THEN ps.total_cpu_us / ps.total_executions ELSE 0 END AS avg_cpu_us,
        CASE WHEN ps.total_executions > 0
             THEN ps.total_duration_us / ps.total_executions ELSE 0 END AS avg_duration_us,
        CASE WHEN ps.total_executions > 0
             THEN ps.total_reads / ps.total_executions ELSE 0 END AS avg_reads,
        CASE WHEN ps.total_executions > 0
             THEN ps.total_writes / ps.total_executions ELSE 0 END AS avg_writes,
        CASE WHEN ps.total_executions > 0
             THEN ps.total_physical_reads / ps.total_executions ELSE 0 END AS avg_physical_reads,
        CASE WHEN ps.total_executions > 0
             THEN ps.total_memory_pages / ps.total_executions ELSE 0 END AS avg_memory_pages,
        ROW_NUMBER() OVER (PARTITION BY p.query_id ORDER BY {orderClause} DESC) AS rn
    FROM #plan_stats AS ps
    JOIN sys.query_store_plan AS p ON ps.plan_id = p.plan_id
)
SELECT TOP ({topN})
    r.query_id,
    r.plan_id,
    r.avg_cpu_us,
    r.avg_duration_us,
    r.avg_reads,
    r.avg_writes,
    r.avg_physical_reads,
    r.avg_memory_pages,
    r.total_executions,
    CAST(r.total_cpu_us AS bigint) AS total_cpu_us,
    CAST(r.total_duration_us AS bigint) AS total_duration_us,
    CAST(r.total_reads AS bigint) AS total_reads,
    CAST(r.total_writes AS bigint) AS total_writes,
    CAST(r.total_physical_reads AS bigint) AS total_physical_reads,
    CAST(r.total_memory_pages AS bigint) AS total_memory_pages,
    r.last_execution_time
INTO #top_plans
FROM ranked AS r
WHERE 1 = 1 {rnClause}
ORDER BY {outerOrder} DESC;

/* Phase 4: Hydrate winners with text, plan XML, and metadata */
SELECT
    tp.query_id,
    tp.plan_id,
    qt.query_sql_text,
    TRY_CONVERT(nvarchar(max), p.query_plan) AS query_plan,
    tp.avg_cpu_us,
    tp.avg_duration_us,
    tp.avg_reads,
    tp.avg_writes,
    tp.avg_physical_reads,
    tp.avg_memory_pages,
    tp.total_executions,
    tp.total_cpu_us,
    tp.total_duration_us,
    tp.total_reads,
    tp.total_writes,
    tp.total_physical_reads,
    tp.total_memory_pages,
    tp.last_execution_time,
    CONVERT(varchar(18), q.query_hash, 1),
    CONVERT(varchar(18), p.query_plan_hash, 1),
    CASE
        WHEN q.object_id <> 0
        THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
        ELSE N''
    END
FROM #top_plans AS tp
JOIN sys.query_store_plan AS p ON tp.plan_id = p.plan_id
JOIN sys.query_store_query AS q ON p.query_id = q.query_id
JOIN sys.query_store_query_text AS qt ON q.query_text_id = qt.query_text_id
WHERE 1 = 1{filterSql}
ORDER BY {outerOrder} DESC;";

        var plans = new List<QueryStorePlan>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var planXml = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (string.IsNullOrEmpty(planXml))
                continue;

            plans.Add(new QueryStorePlan
            {
                QueryId = reader.GetInt64(0),
                PlanId = reader.GetInt64(1),
                QueryText = reader.GetString(2),
                PlanXml = planXml,
                AvgCpuTimeUs = reader.GetDouble(4),
                AvgDurationUs = reader.GetDouble(5),
                AvgLogicalIoReads = reader.GetDouble(6),
                AvgLogicalIoWrites = reader.GetDouble(7),
                AvgPhysicalIoReads = reader.GetDouble(8),
                AvgMemoryGrantPages = reader.GetDouble(9),
                CountExecutions = reader.GetInt64(10),
                TotalCpuTimeUs = reader.GetInt64(11),
                TotalDurationUs = reader.GetInt64(12),
                TotalLogicalIoReads = reader.GetInt64(13),
                TotalLogicalIoWrites = reader.GetInt64(14),
                TotalPhysicalIoReads = reader.GetInt64(15),
                TotalMemoryGrantPages = reader.GetInt64(16),
                LastExecutedUtc = ((DateTimeOffset)reader.GetValue(17)).UtcDateTime,
                QueryHash = reader.IsDBNull(18) ? "" : reader.GetString(18),
                QueryPlanHash = reader.IsDBNull(19) ? "" : reader.GetString(19),
                ModuleName = reader.IsDBNull(20) ? "" : reader.GetString(20),
            });
        }

        return plans;
    }

    public static async Task<List<QueryStoreHistoryRow>> FetchHistoryAsync(
        string connectionString, long queryId, int hoursBack = 24,
        CancellationToken ct = default)
    {
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    p.plan_id,
    CONVERT(varchar(18), MAX(p.query_plan_hash), 1),
    rsi.start_time,
    SUM(rs.count_executions),
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_duration * rs.count_executions) / SUM(rs.count_executions) / 1000.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_cpu_time * rs.count_executions) / SUM(rs.count_executions) / 1000.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_logical_io_reads * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_logical_io_writes * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_physical_io_reads * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_query_max_used_memory * rs.count_executions) / SUM(rs.count_executions) * 8.0 / 1024.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_rowcount * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    SUM(rs.avg_duration * rs.count_executions) / 1000.0,
    SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0,
    SUM(rs.avg_logical_io_reads * rs.count_executions),
    SUM(rs.avg_logical_io_writes * rs.count_executions),
    SUM(rs.avg_physical_io_reads * rs.count_executions),
    MIN(rs.min_dop),
    MAX(rs.max_dop),
    MAX(rs.last_execution_time)
FROM sys.query_store_runtime_stats rs
JOIN sys.query_store_runtime_stats_interval rsi
    ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
JOIN sys.query_store_plan p
    ON rs.plan_id = p.plan_id
WHERE p.query_id = @queryId
AND   rsi.start_time >= DATEADD(HOUR, -@hoursBack, GETUTCDATE())
AND   rs.first_execution_time >= DATEADD(HOUR, -@hoursBack, GETUTCDATE()) --performance: filter runtime_stats by time directly
GROUP BY p.plan_id, rsi.start_time
ORDER BY rsi.start_time, p.plan_id;";

        var rows = new List<QueryStoreHistoryRow>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@queryId", queryId));
        cmd.Parameters.Add(new SqlParameter("@hoursBack", hoursBack));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new QueryStoreHistoryRow
            {
                PlanId = reader.GetInt64(0),
                QueryPlanHash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                IntervalStartUtc = ((DateTimeOffset)reader.GetValue(2)).UtcDateTime,
                CountExecutions = reader.GetInt64(3),
                AvgDurationMs = reader.GetDouble(4),
                AvgCpuMs = reader.GetDouble(5),
                AvgLogicalReads = reader.GetDouble(6),
                AvgLogicalWrites = reader.GetDouble(7),
                AvgPhysicalReads = reader.GetDouble(8),
                AvgMemoryMb = reader.GetDouble(9),
                AvgRowcount = reader.GetDouble(10),
                TotalDurationMs = reader.GetDouble(11),
                TotalCpuMs = reader.GetDouble(12),
                TotalLogicalReads = reader.GetDouble(13),
                TotalLogicalWrites = reader.GetDouble(14),
                TotalPhysicalReads = reader.GetDouble(15),
                MinDop = (int)reader.GetInt64(16),
                MaxDop = (int)reader.GetInt64(17),
                LastExecutionUtc = reader.IsDBNull(18) ? null : ((DateTimeOffset)reader.GetValue(18)).UtcDateTime,
            });
        }

        return rows;
    }

    /// <summary>
    /// Fetches interval-level history rows for all queries sharing the given query_hash.
    /// When <paramref name="startUtc"/>/<paramref name="endUtc"/> are provided they define the
    /// time window (slicer range); otherwise falls back to <paramref name="hoursBack"/>.
    /// </summary>
    public static async Task<List<QueryStoreHistoryRow>> FetchHistoryByHashAsync(
        string connectionString, string queryHash, int hoursBack = 24,
        CancellationToken ct = default,
        DateTime? startUtc = null, DateTime? endUtc = null)
    {
        var parameters = new List<SqlParameter>();
        parameters.Add(new SqlParameter("@queryHash", queryHash.Trim()));

        string timeFilter;
        if (startUtc.HasValue && endUtc.HasValue)
        {
            timeFilter = "AND rsi.start_time >= @rangeStart AND rsi.start_time < @rangeEnd";
            parameters.Add(new SqlParameter("@rangeStart", startUtc.Value));
            parameters.Add(new SqlParameter("@rangeEnd", endUtc.Value));
        }
        else
        {
            timeFilter = "AND rsi.start_time >= DATEADD(HOUR, -@hoursBack, GETUTCDATE())";
            parameters.Add(new SqlParameter("@hoursBack", hoursBack));
        }

        var sql = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    p.plan_id,
    CONVERT(varchar(18), MAX(p.query_plan_hash), 1),
    rsi.start_time,
    SUM(rs.count_executions),
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_duration * rs.count_executions) / SUM(rs.count_executions) / 1000.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_cpu_time * rs.count_executions) / SUM(rs.count_executions) / 1000.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_logical_io_reads * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_logical_io_writes * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_physical_io_reads * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_query_max_used_memory * rs.count_executions) / SUM(rs.count_executions) * 8.0 / 1024.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_rowcount * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    SUM(rs.avg_duration * rs.count_executions) / 1000.0,
    SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0,
    SUM(rs.avg_logical_io_reads * rs.count_executions),
    SUM(rs.avg_logical_io_writes * rs.count_executions),
    SUM(rs.avg_physical_io_reads * rs.count_executions),
    MIN(rs.min_dop),
    MAX(rs.max_dop),
    MAX(rs.last_execution_time)
FROM sys.query_store_runtime_stats rs
JOIN sys.query_store_runtime_stats_interval rsi
    ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
JOIN sys.query_store_plan p
    ON rs.plan_id = p.plan_id
JOIN sys.query_store_query q
    ON p.query_id = q.query_id
WHERE q.query_hash = CONVERT(binary(8), @queryHash, 1)
{timeFilter}
GROUP BY p.plan_id, rsi.start_time
ORDER BY rsi.start_time, p.plan_id;";

        var rows = new List<QueryStoreHistoryRow>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new QueryStoreHistoryRow
            {
                PlanId = reader.GetInt64(0),
                QueryPlanHash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                IntervalStartUtc = ((DateTimeOffset)reader.GetValue(2)).UtcDateTime,
                CountExecutions = reader.GetInt64(3),
                AvgDurationMs = reader.GetDouble(4),
                AvgCpuMs = reader.GetDouble(5),
                AvgLogicalReads = reader.GetDouble(6),
                AvgLogicalWrites = reader.GetDouble(7),
                AvgPhysicalReads = reader.GetDouble(8),
                AvgMemoryMb = reader.GetDouble(9),
                AvgRowcount = reader.GetDouble(10),
                TotalDurationMs = reader.GetDouble(11),
                TotalCpuMs = reader.GetDouble(12),
                TotalLogicalReads = reader.GetDouble(13),
                TotalLogicalWrites = reader.GetDouble(14),
                TotalPhysicalReads = reader.GetDouble(15),
                MinDop = (int)reader.GetInt64(16),
                MaxDop = (int)reader.GetInt64(17),
                LastExecutionUtc = reader.IsDBNull(18) ? null : ((DateTimeOffset)reader.GetValue(18)).UtcDateTime,
            });
        }

        return rows;
    }

    /// <summary>
    /// Fetches interval-level history rows for all queries sharing the given query_hash,
    /// grouped by query_plan_hash and interval start.
    /// Smart aggregation: SUM for totals/executions, weighted AVG for averages, MAX for last_execution.
    /// When <paramref name="startUtc"/>/<paramref name="endUtc"/> are provided they define the
    /// time window; otherwise falls back to <paramref name="hoursBack"/>.
    /// </summary>
    public static async Task<List<QueryStoreHistoryRow>> FetchAggregateHistoryAsync(
        string connectionString, string queryHash, int hoursBack = 24,
        CancellationToken ct = default,
        DateTime? startUtc = null, DateTime? endUtc = null)
    {
        var parameters = new List<SqlParameter>();
        parameters.Add(new SqlParameter("@queryHash", queryHash.Trim()));

        string timeFilter;
        if (startUtc.HasValue && endUtc.HasValue)
        {
            timeFilter = "AND rsi.start_time >= @rangeStart AND rsi.start_time < @rangeEnd";
            parameters.Add(new SqlParameter("@rangeStart", startUtc.Value));
            parameters.Add(new SqlParameter("@rangeEnd", endUtc.Value));
        }
        else
        {
            timeFilter = "AND rsi.start_time >= DATEADD(HOUR, -@hoursBack, GETUTCDATE())";
            parameters.Add(new SqlParameter("@hoursBack", hoursBack));
        }

        var sql = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    CONVERT(varchar(18), p.query_plan_hash, 1),
    rsi.start_time,
    SUM(rs.count_executions),
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_duration * rs.count_executions) / SUM(rs.count_executions) / 1000.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_cpu_time * rs.count_executions) / SUM(rs.count_executions) / 1000.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_logical_io_reads * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_logical_io_writes * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_physical_io_reads * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_query_max_used_memory * rs.count_executions) / SUM(rs.count_executions) * 8.0 / 1024.0
         ELSE 0 END,
    CASE WHEN SUM(rs.count_executions) > 0
         THEN SUM(rs.avg_rowcount * rs.count_executions) / SUM(rs.count_executions)
         ELSE 0 END,
    SUM(rs.avg_duration * rs.count_executions) / 1000.0,
    SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0,
    SUM(rs.avg_logical_io_reads * rs.count_executions),
    SUM(rs.avg_logical_io_writes * rs.count_executions),
    SUM(rs.avg_physical_io_reads * rs.count_executions),
    MIN(rs.min_dop),
    MAX(rs.max_dop),
    MAX(rs.last_execution_time),
    SUM(rs.avg_query_max_used_memory * rs.count_executions)
FROM sys.query_store_runtime_stats rs
JOIN sys.query_store_runtime_stats_interval rsi
    ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
JOIN sys.query_store_plan p
    ON rs.plan_id = p.plan_id
JOIN sys.query_store_query q
    ON p.query_id = q.query_id
WHERE q.query_hash = CONVERT(binary(8), @queryHash, 1)
{timeFilter}
GROUP BY p.query_plan_hash, rsi.start_time
ORDER BY rsi.start_time, p.query_plan_hash;";

        var rows = new List<QueryStoreHistoryRow>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new QueryStoreHistoryRow
            {
                QueryPlanHash = reader.IsDBNull(0) ? "" : reader.GetString(0),
                IntervalStartUtc = ((DateTimeOffset)reader.GetValue(1)).UtcDateTime,
                CountExecutions = reader.GetInt64(2),
                AvgDurationMs = reader.GetDouble(3),
                AvgCpuMs = reader.GetDouble(4),
                AvgLogicalReads = reader.GetDouble(5),
                AvgLogicalWrites = reader.GetDouble(6),
                AvgPhysicalReads = reader.GetDouble(7),
                AvgMemoryMb = reader.GetDouble(8),
                AvgRowcount = reader.GetDouble(9),
                TotalDurationMs = reader.GetDouble(10),
                TotalCpuMs = reader.GetDouble(11),
                TotalLogicalReads = reader.GetDouble(12),
                TotalLogicalWrites = reader.GetDouble(13),
                TotalPhysicalReads = reader.GetDouble(14),
                MinDop = (int)reader.GetInt64(15),
                MaxDop = (int)reader.GetInt64(16),
                LastExecutionUtc = reader.IsDBNull(17) ? null : ((DateTimeOffset)reader.GetValue(17)).UtcDateTime,
                TotalMemoryMb = reader.GetDouble(18),
			});
        }

        return rows;
    }

    /// <summary>
    /// Fetches hourly-aggregated metric data for the time-range slicer.
    /// Limits data to the last <paramref name="daysBack"/> days (default 30).
    /// Returns up to 1000 hourly buckets in chronological order.
    /// </summary>
    public static async Task<List<QueryStoreTimeSlice>> FetchTimeSliceDataAsync(
        string connectionString, string orderByMetric = "cpu",
        int daysBack = 30,
        CancellationToken ct = default)
    {
        var sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0) AS bucket_hour,
    SUM(rs.avg_cpu_time * rs.count_executions) / 1000.0 AS total_cpu_ms,
    SUM(rs.avg_duration * rs.count_executions) / 1000.0 AS total_duration_ms,
    SUM(rs.avg_logical_io_reads * rs.count_executions) AS total_reads,
    SUM(rs.avg_logical_io_writes * rs.count_executions) AS total_writes,
    SUM(rs.avg_physical_io_reads * rs.count_executions) AS total_physical_reads,
    SUM(rs.avg_query_max_used_memory * rs.count_executions) * 8.0 / 1024.0 AS total_memory_mb,
    SUM(rs.count_executions) AS total_executions
FROM sys.query_store_runtime_stats rs
JOIN sys.query_store_runtime_stats_interval rsi
    ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rsi.start_time >= DATEADD(DAY, -@daysBack, GETUTCDATE())
AND   rs.first_execution_time >= DATEADD(DAY, -@daysBack, GETUTCDATE()) --performance: filter runtime_stats directly
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0)
ORDER BY bucket_hour DESC;";

        var rows = new List<QueryStoreTimeSlice>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@daysBack", daysBack));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            // DATEADD returns plain datetime (not datetimeoffset) — read accordingly
            var bucketHour = reader.GetDateTime(0);
            rows.Add(new QueryStoreTimeSlice
            {
                IntervalStartUtc = DateTime.SpecifyKind(bucketHour, DateTimeKind.Utc),
                TotalCpu = reader.GetDouble(1),
                TotalDuration = reader.GetDouble(2),
                TotalReads = reader.GetDouble(3),
                TotalWrites = reader.GetDouble(4),
                TotalPhysicalReads = reader.GetDouble(5),
                TotalMemory = reader.GetDouble(6),
                TotalExecutions = reader.GetInt64(7),
            });
        }

        // Return in chronological order
        rows.Reverse();
        return rows;
    }

    // ── Wait stats ─────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether Query Store wait stats capture is enabled for the connected database.
    /// Returns false on SQL Server 2016 (where the option doesn't exist) or when capture is OFF.
    /// </summary>
    public static async Task<bool> IsWaitStatsCaptureEnabledAsync(
        string connectionString, CancellationToken ct = default)
    {
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT CASE
    WHEN EXISTS (
        SELECT 1 FROM sys.database_query_store_options
        WHERE wait_stats_capture_mode_desc = 'ON'
    ) THEN 1 ELSE 0 END;";

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 10 };
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is int i && i == 1;
        }
        catch
        {
            // Column doesn't exist on SQL 2016, or query store not enabled
            return false;
        }
    }

    // Excluded: 11 = Idle, 18 = User Wait
    private const string WaitCategoryExclusion = "AND ws.wait_category NOT IN (11, 18)";

    /// <summary>
    /// Global wait stats aggregated across all plans for a time range, grouped by category.
    /// WaitRatio = SUM(total_query_wait_time_ms) / interval_duration_ms.
    /// </summary>
    public static async Task<List<WaitCategoryTotal>> FetchGlobalWaitStatsAsync(
        string connectionString, DateTime startUtc, DateTime endUtc,
        CancellationToken ct = default)
    {
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    ws.wait_category,
    ws.wait_category_desc,
    1.0 * SUM(ws.total_query_wait_time_ms)
        / (1000.0 * DATEDIFF(SECOND, @start, @end)) AS wait_ratio
FROM sys.query_store_wait_stats ws
JOIN sys.query_store_runtime_stats_interval rsi
    ON ws.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rsi.start_time >= @start AND rsi.start_time < @end
AND   ws.execution_type = 0
" + WaitCategoryExclusion + @"
GROUP BY ws.wait_category, ws.wait_category_desc
ORDER BY wait_ratio DESC;";

        var rows = new List<WaitCategoryTotal>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@start", startUtc));
        cmd.Parameters.Add(new SqlParameter("@end", endUtc));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WaitCategoryTotal
            {
                WaitCategory = reader.GetInt16(0),
                WaitCategoryDesc = reader.GetString(1),
                WaitRatio = (double)reader.GetDecimal(2),
            });
        }
        return rows;
    }

    /// <summary>
    /// Per-plan wait stats aggregated for a time range, grouped by plan_id + category.
    /// WaitRatio = SUM(total_query_wait_time_ms) / SUM(avg_duration * count_executions).
    /// This differs from the global/hourly WTR (which divides by wall-clock interval) because
    /// at plan level we measure what fraction of actual execution time was spent waiting.
    /// When <paramref name="planIds"/> is provided, only those plan IDs are queried (via temp table).
    /// </summary>
    public static async Task<List<(long PlanId, WaitCategoryTotal Wait)>> FetchPlanWaitStatsAsync(
        string connectionString, DateTime startUtc, DateTime endUtc,
        IEnumerable<long>? planIds = null,
        CancellationToken ct = default)
    {
        var rows = new List<(long, WaitCategoryTotal)>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // When plan IDs are supplied, load them into a temp table for an efficient JOIN filter.
        var planIdFilter = "";
        if (planIds != null)
        {
            var ids = planIds.Distinct().ToList();
            if (ids.Count == 0)
                return rows;

            const string createTmp = @"
CREATE TABLE #plan_ids (plan_id bigint NOT NULL PRIMARY KEY);";
            await using (var createCmd = new SqlCommand(createTmp, conn))
                await createCmd.ExecuteNonQueryAsync(ct);

            // Bulk-insert in batches of 1000 using VALUES rows
            for (int i = 0; i < ids.Count; i += 1000)
            {
                var batch = ids.Skip(i).Take(1000);
                var valuesSql = "INSERT INTO #plan_ids (plan_id) VALUES " +
                    string.Join(",", batch.Select(id => $"({id})")) + ";";
                await using var insertCmd = new SqlCommand(valuesSql, conn);
                await insertCmd.ExecuteNonQueryAsync(ct);
            }

            planIdFilter = "\nAND   EXISTS (SELECT 1 FROM #plan_ids pid WHERE pid.plan_id = ws.plan_id)";
        }

        var sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    ws.plan_id,
    ws.wait_category,
    ws.wait_category_desc,
    1.0 * SUM(ws.total_query_wait_time_ms)
        / NULLIF(SUM(rs.avg_duration*rs.count_executions),0) AS wait_ratio
FROM sys.query_store_wait_stats ws
JOIN sys.query_store_runtime_stats_interval rsi
    ON ws.runtime_stats_interval_id = rsi.runtime_stats_interval_id
JOIN sys.query_store_runtime_stats rs ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id and rs.plan_id=ws.plan_id
WHERE rsi.start_time >= @start AND rsi.start_time < @end
AND   ws.execution_type = 0
" + WaitCategoryExclusion + planIdFilter + @"
GROUP BY ws.plan_id, ws.wait_category, ws.wait_category_desc
ORDER BY ws.plan_id, wait_ratio DESC;";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@start", startUtc));
        cmd.Parameters.Add(new SqlParameter("@end", endUtc));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetInt64(0), new WaitCategoryTotal
            {
                WaitCategory = reader.GetInt16(1),
                WaitCategoryDesc = reader.GetString(2),
                WaitRatio = reader.GetDouble(3),
            }));
        }
        return rows;
    }

    /// <summary>
    /// Hourly wait stats for the ribbon chart, grouped by hour + category.
    /// WaitRatio = SUM(total_query_wait_time_ms) / 3_600_000 (one hour in ms).
    /// </summary>
    public static async Task<List<WaitCategoryTimeSlice>> FetchGlobalWaitStatsRibbonAsync(
        string connectionString, DateTime startUtc, DateTime endUtc,
        CancellationToken ct = default)
    {
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0) AS bucket_hour,
    ws.wait_category,
    ws.wait_category_desc,
    1.0 * SUM(ws.total_query_wait_time_ms) / (3600.0 * 1000.0) AS wait_ratio
FROM sys.query_store_wait_stats ws
JOIN sys.query_store_runtime_stats_interval rsi
    ON ws.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rsi.start_time >= @start AND rsi.start_time < @end
AND   ws.execution_type = 0
" + WaitCategoryExclusion + @"
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, rsi.start_time), 0),
         ws.wait_category, ws.wait_category_desc
ORDER BY bucket_hour, wait_ratio DESC;";

        var rows = new List<WaitCategoryTimeSlice>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new SqlParameter("@start", startUtc));
        cmd.Parameters.Add(new SqlParameter("@end", endUtc));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var bucketHour = reader.GetDateTime(0);
            rows.Add(new WaitCategoryTimeSlice
            {
                IntervalStartUtc = DateTime.SpecifyKind(bucketHour, DateTimeKind.Utc),
                WaitCategory = reader.GetInt16(1),
                WaitCategoryDesc = reader.GetString(2),
                WaitRatio = (double)reader.GetDecimal(3),
            });
        }
        return rows;
    }

    // ── Grouped fetches ──────────────────────────────────────────────────

    /// <summary>
    /// Helper: resolves the metric alias used inside aggregation temp tables.
    /// Returns (planStatsColumn, aggAlias) where planStatsColumn references #plan_stats
    /// and aggAlias is the column name used in GROUP BY aggregation selects.
    /// </summary>
    private static (string PlanStatsCol, string AggAlias) ResolveGroupMetric(string orderBy)
    {
        var key = orderBy.ToLowerInvariant();
        return key switch
        {
            "cpu" or "avg-cpu"                       => ("total_cpu_us",        "total_cpu_us"),
            "duration" or "avg-duration"             => ("total_duration_us",   "total_duration_us"),
            "reads" or "avg-reads"                   => ("total_reads",         "total_reads"),
            "writes" or "avg-writes"                 => ("total_writes",        "total_writes"),
            "physical-reads" or "avg-physical-reads" => ("total_physical_reads","total_physical_reads"),
            "memory" or "avg-memory"                 => ("total_memory_pages",  "total_memory_pages"),
            "executions"                             => ("total_executions",    "total_executions"),
            _ => ("total_cpu_us", "total_cpu_us"),
        };
    }

    /// <summary>
    /// Fetches grouped-by-QueryHash results.
    /// Step 1: Top X query hashes by metric.
    /// Step 2: Top 5 plan hashes per query hash with metrics.
    /// Step 3: Top and bottom QueryId/PlanId per query_hash/plan_hash.
    /// Returns intermediate (plan_hash level) and leaf (query_id/plan_id level) rows.
    /// </summary>
    public static async Task<QueryStoreGroupedResult> FetchGroupedByQueryHashAsync(
        string connectionString, int topN = 25, string orderBy = "cpu",
        QueryStoreFilter? filter = null, CancellationToken ct = default,
        DateTime? startUtc = null, DateTime? endUtc = null)
    {
        var (metricCol, _) = ResolveGroupMetric(orderBy);
        var parameters = new List<SqlParameter>();

        // Time-range filter
        string intervalWhereClause;
        if (startUtc.HasValue && endUtc.HasValue)
        {
            intervalWhereClause = "WHERE rsi.start_time >= @rangeStart AND rsi.start_time < @rangeEnd";
            parameters.Add(new SqlParameter("@rangeStart", startUtc.Value));
            parameters.Add(new SqlParameter("@rangeEnd", endUtc.Value));
        }
        else
        {
            intervalWhereClause = "WHERE rsi.start_time >= DATEADD(HOUR, -24, GETUTCDATE())";
        }

        // Filter clauses
        var filterClauses = new List<string>();
        if (filter?.QueryId != null)
        {
            filterClauses.Add("AND q.query_id = @filterQueryId");
            parameters.Add(new SqlParameter("@filterQueryId", filter.QueryId.Value));
        }
        if (!string.IsNullOrWhiteSpace(filter?.QueryHash))
        {
            filterClauses.Add("AND q.query_hash = CONVERT(binary(8), @filterQueryHash, 1)");
            parameters.Add(new SqlParameter("@filterQueryHash", filter.QueryHash.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(filter?.ModuleName))
        {
            var moduleVal = filter.ModuleName.Trim();
            if (moduleVal.Contains('%'))
                filterClauses.Add("AND OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) LIKE @filterModule");
            else
                filterClauses.Add("AND OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) = @filterModule");
            parameters.Add(new SqlParameter("@filterModule", moduleVal));
        }
        var filterSql = filterClauses.Count > 0 ? "\n" + string.Join("\n", filterClauses) : "";

        var sql = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

/* Phase 1: Pre-filter matching interval IDs */
DROP TABLE IF EXISTS #intervals;
CREATE TABLE #intervals (runtime_stats_interval_id bigint NOT NULL PRIMARY KEY CLUSTERED);
INSERT INTO #intervals (runtime_stats_interval_id)
SELECT rsi.runtime_stats_interval_id
FROM sys.query_store_runtime_stats_interval AS rsi
{intervalWhereClause}
OPTION (RECOMPILE);

/* Phase 2: Aggregate runtime stats by plan_id */
DROP TABLE IF EXISTS #plan_stats;
CREATE TABLE #plan_stats (
    plan_id bigint NOT NULL PRIMARY KEY CLUSTERED,
    total_cpu_us float NOT NULL,
    total_duration_us float NOT NULL,
    total_reads float NOT NULL,
    total_writes float NOT NULL,
    total_physical_reads float NOT NULL,
    total_memory_pages float NOT NULL,
    total_executions bigint NOT NULL,
    last_execution_time datetimeoffset NOT NULL
);
INSERT INTO #plan_stats
SELECT
    rs.plan_id,
    SUM(rs.avg_cpu_time * rs.count_executions),
    SUM(rs.avg_duration * rs.count_executions),
    SUM(rs.avg_logical_io_reads * rs.count_executions),
    SUM(rs.avg_logical_io_writes * rs.count_executions),
    SUM(rs.avg_physical_io_reads * rs.count_executions),
    SUM(rs.avg_query_max_used_memory * rs.count_executions),
    SUM(rs.count_executions),
    MAX(rs.last_execution_time)
FROM sys.query_store_runtime_stats AS rs
WHERE EXISTS (SELECT 1 FROM #intervals AS i WHERE i.runtime_stats_interval_id = rs.runtime_stats_interval_id)
GROUP BY rs.plan_id
OPTION (RECOMPILE);

/* Step 1: Top X query hashes by metric */
DROP TABLE IF EXISTS #top_hashes;
;WITH qh AS (
    SELECT
        q.query_hash,
        SUM(ps.{metricCol}) AS metric_total
    FROM #plan_stats ps
    JOIN sys.query_store_plan p ON ps.plan_id = p.plan_id
    JOIN sys.query_store_query q ON p.query_id = q.query_id
    WHERE 1=1{filterSql}
    GROUP BY q.query_hash
)
SELECT TOP ({topN}) query_hash, metric_total
INTO #top_hashes
FROM qh
ORDER BY metric_total DESC;

/* Step 2: Top 5 plan hashes per query hash with metrics */
DROP TABLE IF EXISTS #plan_hash_rows;
;WITH ph AS (
    SELECT
        CONVERT(varchar(18), q.query_hash, 1) AS query_hash,
        CONVERT(varchar(18), p.query_plan_hash, 1) AS plan_hash,
        CASE WHEN q.object_id <> 0
             THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
             ELSE N'' END AS module_name,
        SUM(ps.total_cpu_us) AS total_cpu_us,
        SUM(ps.total_duration_us) AS total_duration_us,
        SUM(ps.total_reads) AS total_reads,
        SUM(ps.total_writes) AS total_writes,
        SUM(ps.total_physical_reads) AS total_physical_reads,
        SUM(ps.total_memory_pages) AS total_memory_pages,
        SUM(ps.total_executions) AS total_executions,
        MAX(ps.last_execution_time) AS last_execution_time,
        ROW_NUMBER() OVER (PARTITION BY q.query_hash ORDER BY SUM(ps.{metricCol}) DESC) AS rnum
    FROM #plan_stats ps
    JOIN sys.query_store_plan p ON ps.plan_id = p.plan_id
    JOIN sys.query_store_query q ON p.query_id = q.query_id
    WHERE EXISTS (SELECT 1 FROM #top_hashes th WHERE th.query_hash = q.query_hash)
    GROUP BY q.query_hash, p.query_plan_hash,
             CASE WHEN q.object_id <> 0
                  THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                  ELSE N'' END
)
SELECT query_hash, plan_hash, module_name,
       CAST(total_cpu_us AS bigint) AS total_cpu_us,
       CAST(total_duration_us AS bigint) AS total_duration_us,
       CAST(total_reads AS bigint) AS total_reads,
       CAST(total_writes AS bigint) AS total_writes,
       CAST(total_physical_reads AS bigint) AS total_physical_reads,
       CAST(total_memory_pages AS bigint) AS total_memory_pages,
       total_executions,
       last_execution_time
INTO #plan_hash_rows
FROM ph WHERE rnum <= 5;

/* Step 3: Top and bottom QueryId/PlanId per query_hash/plan_hash */
;WITH ranked AS (
    SELECT
        CONVERT(varchar(18), q.query_hash, 1) AS query_hash,
        CONVERT(varchar(18), p.query_plan_hash, 1) AS plan_hash,
        q.query_id,
        ps.plan_id,
        q.query_text_id,
        CASE WHEN q.object_id <> 0
             THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
             ELSE N'' END AS module_name,
        CAST(ps.total_cpu_us AS bigint) AS total_cpu_us,
        CAST(ps.total_duration_us AS bigint) AS total_duration_us,
        CAST(ps.total_reads AS bigint) AS total_reads,
        CAST(ps.total_writes AS bigint) AS total_writes,
        CAST(ps.total_physical_reads AS bigint) AS total_physical_reads,
        CAST(ps.total_memory_pages AS bigint) AS total_memory_pages,
        ps.total_executions,
        ps.last_execution_time,
        ROW_NUMBER() OVER (PARTITION BY q.query_hash, p.query_plan_hash ORDER BY ps.{metricCol} DESC) AS rn_top,
        ROW_NUMBER() OVER (PARTITION BY q.query_hash, p.query_plan_hash ORDER BY ps.{metricCol} ASC) AS rn_bottom
    FROM #plan_stats ps
    JOIN sys.query_store_plan p ON ps.plan_id = p.plan_id
    JOIN sys.query_store_query q ON p.query_id = q.query_id
    JOIN sys.query_store_query_text qt ON q.query_text_id = qt.query_text_id
    WHERE EXISTS (SELECT 1 FROM #plan_hash_rows phr
                  WHERE phr.query_hash = CONVERT(varchar(18), q.query_hash, 1)
                    AND phr.plan_hash = CONVERT(varchar(18), p.query_plan_hash, 1))
)
SELECT *
into #ranked_light
FROM ranked
WHERE rn_top = 1 OR rn_bottom = 1;

/* Final select: join heavy elements (query_text, plan_xml) only for the top/bottom representatives */
SELECT 
r.query_hash, 
r.plan_hash, 
r.query_id, 
r.plan_id, 
qt.query_sql_text, 
TRY_CONVERT(nvarchar(max), p.query_plan) AS plan_xml,
r.module_name, 
r.total_cpu_us, 
r.total_duration_us, 
r.total_reads, 
r.total_writes,
r.total_physical_reads, 
r.total_memory_pages, 
r.total_executions, 
r.last_execution_time,
CASE WHEN r.rn_top = 1 THEN 1 ELSE 0 END AS is_top
FROM #ranked_light r
JOIN sys.query_store_query_text qt ON r.query_text_id = qt.query_text_id
JOIN sys.query_store_plan p ON r.plan_id = p.plan_id;

/* Return intermediate rows (result set 1) */
SELECT * FROM #plan_hash_rows ORDER BY query_hash, total_executions DESC;
";

        var result = new QueryStoreGroupedResult();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        foreach (var p in parameters) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Result set 1: Leaf rows (top/bottom per query_hash/plan_hash)
        while (await reader.ReadAsync(ct))
        {
            result.LeafRows.Add(new QueryStoreGroupedPlanRow
            {
                QueryHash = reader.IsDBNull(0) ? "" : reader.GetString(0),
                QueryPlanHash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                QueryId = reader.GetInt64(2),
                PlanId = reader.GetInt64(3),
                QueryText = reader.IsDBNull(4) ? "" : reader.GetString(4),
                PlanXml = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ModuleName = reader.IsDBNull(6) ? "" : reader.GetString(6),
                TotalCpuTimeUs = reader.GetInt64(7),
                TotalDurationUs = reader.GetInt64(8),
                TotalLogicalIoReads = reader.GetInt64(9),
                TotalLogicalIoWrites = reader.GetInt64(10),
                TotalPhysicalIoReads = reader.GetInt64(11),
                TotalMemoryGrantPages = reader.GetInt64(12),
                CountExecutions = reader.GetInt64(13),
                LastExecutedUtc = ((DateTimeOffset)reader.GetValue(14)).UtcDateTime,
                IsTopRepresentative = reader.GetInt32(15) == 1,
            });
        }

        // Result set 2: Intermediate rows (plan_hash level aggregated)
        if (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                result.IntermediateRows.Add(new QueryStoreGroupedPlanRow
                {
                    QueryHash = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    QueryPlanHash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    ModuleName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    TotalCpuTimeUs = reader.GetInt64(3),
                    TotalDurationUs = reader.GetInt64(4),
                    TotalLogicalIoReads = reader.GetInt64(5),
                    TotalLogicalIoWrites = reader.GetInt64(6),
                    TotalPhysicalIoReads = reader.GetInt64(7),
                    TotalMemoryGrantPages = reader.GetInt64(8),
                    CountExecutions = reader.GetInt64(9),
                    LastExecutedUtc = ((DateTimeOffset)reader.GetValue(10)).UtcDateTime,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Fetches grouped-by-Module results.
    /// Step 1: Top X modules by metric.
    /// Step 2: Top 5 query hashes per module with metrics.
    /// Step 3: Top and bottom QueryId/PlanId per module/query_hash.
    /// Returns intermediate (query_hash level) and leaf (query_id/plan_id level) rows.
    /// </summary>
    public static async Task<QueryStoreGroupedResult> FetchGroupedByModuleAsync(
        string connectionString, int topN = 25, string orderBy = "cpu",
        QueryStoreFilter? filter = null, CancellationToken ct = default,
        DateTime? startUtc = null, DateTime? endUtc = null)
    {
        var (metricCol, _) = ResolveGroupMetric(orderBy);
        var parameters = new List<SqlParameter>();

        // Time-range filter
        string intervalWhereClause;
        if (startUtc.HasValue && endUtc.HasValue)
        {
            intervalWhereClause = "WHERE rsi.start_time >= @rangeStart AND rsi.start_time < @rangeEnd";
            parameters.Add(new SqlParameter("@rangeStart", startUtc.Value));
            parameters.Add(new SqlParameter("@rangeEnd", endUtc.Value));
        }
        else
        {
            intervalWhereClause = "WHERE rsi.start_time >= DATEADD(HOUR, -24, GETUTCDATE())";
        }

        // Filter clauses
        var filterClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter?.ModuleName))
        {
            var moduleVal = filter.ModuleName.Trim();
            if (moduleVal.Contains('%'))
                filterClauses.Add("AND OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) LIKE @filterModule");
            else
                filterClauses.Add("AND OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) = @filterModule");
            parameters.Add(new SqlParameter("@filterModule", moduleVal));
        }
        if (!string.IsNullOrWhiteSpace(filter?.QueryHash))
        {
            filterClauses.Add("AND q.query_hash = CONVERT(binary(8), @filterQueryHash, 1)");
            parameters.Add(new SqlParameter("@filterQueryHash", filter.QueryHash.Trim()));
        }
        var filterSql = filterClauses.Count > 0 ? "\n" + string.Join("\n", filterClauses) : "";

        var sql = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

/* Phase 1: Pre-filter matching interval IDs */
DROP TABLE IF EXISTS #intervals;
CREATE TABLE #intervals (runtime_stats_interval_id bigint NOT NULL PRIMARY KEY CLUSTERED);
INSERT INTO #intervals (runtime_stats_interval_id)
SELECT rsi.runtime_stats_interval_id
FROM sys.query_store_runtime_stats_interval AS rsi
{intervalWhereClause}
OPTION (RECOMPILE);

/* Phase 2: Aggregate runtime stats by plan_id */
DROP TABLE IF EXISTS #plan_stats;
CREATE TABLE #plan_stats (
    plan_id bigint NOT NULL PRIMARY KEY CLUSTERED,
    total_cpu_us float NOT NULL,
    total_duration_us float NOT NULL,
    total_reads float NOT NULL,
    total_writes float NOT NULL,
    total_physical_reads float NOT NULL,
    total_memory_pages float NOT NULL,
    total_executions bigint NOT NULL,
    last_execution_time datetimeoffset NOT NULL
);
INSERT INTO #plan_stats
SELECT
    rs.plan_id,
    SUM(rs.avg_cpu_time * rs.count_executions),
    SUM(rs.avg_duration * rs.count_executions),
    SUM(rs.avg_logical_io_reads * rs.count_executions),
    SUM(rs.avg_logical_io_writes * rs.count_executions),
    SUM(rs.avg_physical_io_reads * rs.count_executions),
    SUM(rs.avg_query_max_used_memory * rs.count_executions),
    SUM(rs.count_executions),
    MAX(rs.last_execution_time)
FROM sys.query_store_runtime_stats AS rs
WHERE EXISTS (SELECT 1 FROM #intervals AS i WHERE i.runtime_stats_interval_id = rs.runtime_stats_interval_id)
GROUP BY rs.plan_id
OPTION (RECOMPILE);

/* Step 1: Top X modules by metric */
DROP TABLE IF EXISTS #top_modules;
;WITH md AS (
    SELECT
        CASE WHEN q.object_id <> 0
             THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
             ELSE N'' END AS module_name,
        SUM(ps.{metricCol}) AS metric_total
    FROM #plan_stats ps
    JOIN sys.query_store_plan p ON ps.plan_id = p.plan_id
    JOIN sys.query_store_query q ON p.query_id = q.query_id
    WHERE q.object_id <> 0{filterSql}
    GROUP BY CASE WHEN q.object_id <> 0
                  THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                  ELSE N'' END
)
SELECT TOP ({topN}) module_name, metric_total
INTO #top_modules
FROM md
ORDER BY metric_total DESC;

/* Step 2: Top 5 query hashes per module with metrics */
DROP TABLE IF EXISTS #qhash_rows;
;WITH qh AS (
    SELECT
        CASE WHEN q.object_id <> 0
             THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
             ELSE N'' END AS module_name,
        CONVERT(varchar(18), q.query_hash, 1) AS query_hash,
        SUM(ps.total_cpu_us) AS total_cpu_us,
        SUM(ps.total_duration_us) AS total_duration_us,
        SUM(ps.total_reads) AS total_reads,
        SUM(ps.total_writes) AS total_writes,
        SUM(ps.total_physical_reads) AS total_physical_reads,
        SUM(ps.total_memory_pages) AS total_memory_pages,
        SUM(ps.total_executions) AS total_executions,
        MAX(ps.last_execution_time) AS last_execution_time,
        ROW_NUMBER() OVER (PARTITION BY
            CASE WHEN q.object_id <> 0
                 THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                 ELSE N'' END
            ORDER BY SUM(ps.{metricCol}) DESC) AS rnum
    FROM #plan_stats ps
    JOIN sys.query_store_plan p ON ps.plan_id = p.plan_id
    JOIN sys.query_store_query q ON p.query_id = q.query_id
    WHERE EXISTS (SELECT 1 FROM #top_modules tm
                  WHERE tm.module_name = CASE WHEN q.object_id <> 0
                       THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                       ELSE N'' END)
    GROUP BY CASE WHEN q.object_id <> 0
                  THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                  ELSE N'' END,
             q.query_hash
)
SELECT module_name, query_hash,
       CAST(total_cpu_us AS bigint) AS total_cpu_us,
       CAST(total_duration_us AS bigint) AS total_duration_us,
       CAST(total_reads AS bigint) AS total_reads,
       CAST(total_writes AS bigint) AS total_writes,
       CAST(total_physical_reads AS bigint) AS total_physical_reads,
       CAST(total_memory_pages AS bigint) AS total_memory_pages,
       total_executions,
       last_execution_time
INTO #qhash_rows
FROM qh WHERE rnum <= 5;

/* Step 3: Top and bottom QueryId/PlanId per module/query_hash */
;WITH ranked AS (
    SELECT
        CASE WHEN q.object_id <> 0
             THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
             ELSE N'' END AS module_name,
        CONVERT(varchar(18), q.query_hash, 1) AS query_hash,
        CONVERT(varchar(18), p.query_plan_hash, 1) AS plan_hash,
        q.query_id,
        ps.plan_id,
        qt.query_sql_text,
        TRY_CONVERT(nvarchar(max), p.query_plan) AS plan_xml,
        CAST(ps.total_cpu_us AS bigint) AS total_cpu_us,
        CAST(ps.total_duration_us AS bigint) AS total_duration_us,
        CAST(ps.total_reads AS bigint) AS total_reads,
        CAST(ps.total_writes AS bigint) AS total_writes,
        CAST(ps.total_physical_reads AS bigint) AS total_physical_reads,
        CAST(ps.total_memory_pages AS bigint) AS total_memory_pages,
        ps.total_executions,
        ps.last_execution_time,
        ROW_NUMBER() OVER (PARTITION BY
            CASE WHEN q.object_id <> 0
                 THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                 ELSE N'' END,
            q.query_hash
            ORDER BY ps.{metricCol} DESC) AS rn_top,
        ROW_NUMBER() OVER (PARTITION BY
            CASE WHEN q.object_id <> 0
                 THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                 ELSE N'' END,
            q.query_hash
            ORDER BY ps.{metricCol} ASC) AS rn_bottom
    FROM #plan_stats ps
    JOIN sys.query_store_plan p ON ps.plan_id = p.plan_id
    JOIN sys.query_store_query q ON p.query_id = q.query_id
    JOIN sys.query_store_query_text qt ON q.query_text_id = qt.query_text_id
    WHERE EXISTS (SELECT 1 FROM #qhash_rows qhr
                  WHERE qhr.module_name = CASE WHEN q.object_id <> 0
                       THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                       ELSE N'' END
                    AND qhr.query_hash = CONVERT(varchar(18), q.query_hash, 1))
)
SELECT module_name, query_hash, plan_hash, query_id, plan_id, query_sql_text, plan_xml,
       total_cpu_us, total_duration_us, total_reads, total_writes,
       total_physical_reads, total_memory_pages, total_executions, last_execution_time,
       CASE WHEN rn_top = 1 THEN 1 ELSE 0 END AS is_top
FROM ranked
WHERE rn_top = 1 OR rn_bottom = 1;

/* Return intermediate rows (result set 2) */
SELECT * FROM #qhash_rows ORDER BY module_name, total_executions DESC;
";

        var result = new QueryStoreGroupedResult();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        foreach (var p in parameters) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Result set 1: Leaf rows (top/bottom per module/query_hash)
        while (await reader.ReadAsync(ct))
        {
            result.LeafRows.Add(new QueryStoreGroupedPlanRow
            {
                ModuleName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                QueryHash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                QueryPlanHash = reader.IsDBNull(2) ? "" : reader.GetString(2),
                QueryId = reader.GetInt64(3),
                PlanId = reader.GetInt64(4),
                QueryText = reader.IsDBNull(5) ? "" : reader.GetString(5),
                PlanXml = reader.IsDBNull(6) ? "" : reader.GetString(6),
                TotalCpuTimeUs = reader.GetInt64(7),
                TotalDurationUs = reader.GetInt64(8),
                TotalLogicalIoReads = reader.GetInt64(9),
                TotalLogicalIoWrites = reader.GetInt64(10),
                TotalPhysicalIoReads = reader.GetInt64(11),
                TotalMemoryGrantPages = reader.GetInt64(12),
                CountExecutions = reader.GetInt64(13),
                LastExecutedUtc = ((DateTimeOffset)reader.GetValue(14)).UtcDateTime,
                IsTopRepresentative = reader.GetInt32(15) == 1,
            });
        }

        // Result set 2: Intermediate rows (query_hash level aggregated under module)
        if (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                result.IntermediateRows.Add(new QueryStoreGroupedPlanRow
                {
                    ModuleName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    QueryHash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    TotalCpuTimeUs = reader.GetInt64(2),
                    TotalDurationUs = reader.GetInt64(3),
                    TotalLogicalIoReads = reader.GetInt64(4),
                    TotalLogicalIoWrites = reader.GetInt64(5),
                    TotalPhysicalIoReads = reader.GetInt64(6),
                    TotalMemoryGrantPages = reader.GetInt64(7),
                    CountExecutions = reader.GetInt64(8),
                    LastExecutedUtc = ((DateTimeOffset)reader.GetValue(9)).UtcDateTime,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a WaitProfile from raw category totals.
    /// Top 3 categories are kept; everything else is consolidated into "Others".
    /// </summary>
    public static WaitProfile BuildWaitProfile(IEnumerable<WaitCategoryTotal> waits)
    {
        var sorted = waits.OrderByDescending(w => w.WaitRatio).ToList();
        var grand = sorted.Sum(w => w.WaitRatio);
        if (grand <= 0) return new WaitProfile();

        var profile = new WaitProfile { GrandTotalRatio = grand };
        double othersRatio = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (i < 3)
            {
                profile.Segments.Add(new WaitProfileSegment
                {
                    Category = sorted[i].WaitCategoryDesc,
                    WaitRatio = sorted[i].WaitRatio,
                    Ratio = sorted[i].WaitRatio / grand,
                    IsNamed = true,
                });
            }
            else
            {
                othersRatio += sorted[i].WaitRatio;
            }
        }
        if (othersRatio > 0)
        {
            profile.Segments.Add(new WaitProfileSegment
            {
                Category = "Others",
                WaitRatio = othersRatio,
                Ratio = othersRatio / grand,
                IsNamed = false,
            });
        }
        return profile;
    }
}
