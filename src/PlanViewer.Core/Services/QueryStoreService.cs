using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class QueryStoreService
{
    /// <summary>
    /// Decides whether Query Store data can be read, given the local Query Store state and
    /// whether the database is a read-only replica that already holds captured data.
    /// A READ* state (READ_ONLY / READ_WRITE / READ_CAPTURE_SECONDARY) is readable directly.
    /// A read-only replica reports its local state as OFF even though the primary's captured
    /// data is replicated and readable, so a read-only replica that holds data is also
    /// readable. A writable database whose Query Store is OFF/ERROR is not. See issue #378.
    /// </summary>
    public static bool IsQueryStoreReadable(string? state, bool readOnlyReplica, bool hasData) =>
        (state != null && state.StartsWith("READ", StringComparison.OrdinalIgnoreCase))
        || (readOnlyReplica && hasData);

    /// <summary>
    /// Verifies Query Store is enabled and in a readable state on the target database.
    /// Returns true if Query Store data is accessible.
    /// </summary>
    /// <remarks>
    /// On a readable secondary replica (an Always On AG secondary, or an Azure SQL
    /// geo-replica / read-scale-out replica) the database is read-only, so the local Query
    /// Store capture engine can't run and <c>actual_state_desc</c> reports OFF — or, on
    /// SQL 2022+/Azure with the secondary-replica feature enabled, READ_CAPTURE_SECONDARY.
    /// Either way the primary's captured data is replicated into the database's internal
    /// tables and is fully readable through the sys.query_store_* views (SSMS reads it there
    /// too). So when the database is a read-only replica that already holds Query Store data,
    /// treat it as enabled-for-reading even though the local state isn't a READ* state;
    /// refusing to open would block a scenario that works. See issue #378.
    /// </remarks>
    public static async Task<(bool Enabled, string? State, bool ReadOnlyReplica)> CheckEnabledAsync(
        string connectionString, CancellationToken ct = default)
    {
        const string sql = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    actual_state_desc,
    read_only_replica =
        CONVERT(bit, CASE WHEN DATABASEPROPERTYEX(DB_NAME(), 'Updateability') = 'READ_ONLY' THEN 1 ELSE 0 END),
    has_query_store_data =
        CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.query_store_query) THEN 1 ELSE 0 END)
FROM sys.database_query_store_options;";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // No row means Query Store has never been configured on this database.
        if (!await reader.ReadAsync(ct))
            return (false, null, false);

        var state = reader.IsDBNull(0) ? null : reader.GetString(0);
        var readOnlyReplica = !reader.IsDBNull(1) && reader.GetBoolean(1);
        var hasData = !reader.IsDBNull(2) && reader.GetBoolean(2);

        var enabled = IsQueryStoreReadable(state, readOnlyReplica, hasData);
        return (enabled, state, readOnlyReplica);
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
        // Filters are applied in Phase 3 (BEFORE TOP N) so the user's target
        // query isn't excluded just because it's not in the top N by CPU.
        // Aliases here reference Phase 3's CTE: ps (#plan_stats),
        // p (sys.query_store_plan), q (sys.query_store_query).
        var filterClauses = new List<string>();
        var parameters = new List<SqlParameter>();
        var needsQueryJoin = false;

        if (filter?.QueryId != null)
        {
            filterClauses.Add("AND p.query_id = @filterQueryId");
            parameters.Add(new SqlParameter("@filterQueryId", filter.QueryId.Value));
        }
        if (filter?.PlanId != null)
        {
            filterClauses.Add("AND ps.plan_id = @filterPlanId");
            parameters.Add(new SqlParameter("@filterPlanId", filter.PlanId.Value));
        }
        if (!string.IsNullOrWhiteSpace(filter?.QueryHash))
        {
            filterClauses.Add("AND q.query_hash = CONVERT(binary(8), @filterQueryHash, 1)");
            parameters.Add(new SqlParameter("@filterQueryHash", filter.QueryHash.Trim()));
            needsQueryJoin = true;
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
            needsQueryJoin = true;
        }

        var rnClause = filter?.PlanId != null ? "" : "AND r.rn = 1";
        var filterSql = filterClauses.Count > 0
            ? "\n        " + string.Join("\n        ", filterClauses)
            : "";
        var phase3QueryJoin = needsQueryJoin
            ? "    JOIN sys.query_store_query AS q ON p.query_id = q.query_id\n"
            : "";
        var phase2ExecutionTypeClause = "";
        if (filter?.ExecutionTypeDescs?.Length > 0)
        {
            var etParamNames = filter.ExecutionTypeDescs
                .Select((_, i) => $"@executionType{i}")
                .ToList();
            phase2ExecutionTypeClause = $"\nAND rs.execution_type_desc IN ({string.Join(", ", etParamNames)})";
            for (var i = 0; i < filter.ExecutionTypeDescs.Length; i++)
                parameters.Add(new SqlParameter($"@executionType{i}", filter.ExecutionTypeDescs[i]));
        }

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
        //          XML, and query metadata. query_plan is nvarchar(max) on the
        //          catalog view, so it's referenced directly without conversion.
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
    last_execution_time datetimeoffset NOT NULL,
    execution_type_desc nvarchar(60) NOT NULL
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
    MAX(rs.last_execution_time),
    -- Pick execution_type_desc from the most-recently-executed interval to avoid
    -- alphabetical bias: MAX would choose 'Regular' over 'Aborted'.
    RTRIM(CAST(SUBSTRING(MAX(
        CONVERT(char(27), CAST(rs.last_execution_time AS datetime2(7)), 121)
        + CAST(ISNULL(rs.execution_type_desc, '') AS char(60))
    ), 28, 60) AS nvarchar(60)))
FROM sys.query_store_runtime_stats AS rs
WHERE EXISTS
(
    SELECT 1
    FROM #intervals AS i
    WHERE i.runtime_stats_interval_id = rs.runtime_stats_interval_id
){phase2ExecutionTypeClause}
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
        ps.execution_type_desc,
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
{phase3QueryJoin}    WHERE 1 = 1{filterSql}
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
    r.last_execution_time,
    r.execution_type_desc
INTO #top_plans
FROM ranked AS r
WHERE 1 = 1 {rnClause}
ORDER BY {outerOrder} DESC;

/* Phase 4: Hydrate winners with text, plan XML, and metadata */
SELECT
    tp.query_id,
    tp.plan_id,
    qt.query_sql_text,
    p.query_plan,
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
    END,
    tp.execution_type_desc
FROM #top_plans AS tp
JOIN sys.query_store_plan AS p ON tp.plan_id = p.plan_id
JOIN sys.query_store_query AS q ON p.query_id = q.query_id
JOIN sys.query_store_query_text AS qt ON q.query_text_id = qt.query_text_id
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
                ExecutionTypeDesc = reader.IsDBNull(21) ? "" : reader.GetString(21),
            });
        }

        return plans;
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
    SUM(rs.avg_query_max_used_memory * rs.count_executions),
    MAX(rs.execution_type_desc)
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
                ExecutionTypeDesc = reader.IsDBNull(19) ? "" : reader.GetString(19),
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


    // Excluded: 11 = Idle, 18 = User Wait
    private const string WaitCategoryExclusion = "AND ws.wait_category NOT IN (11, 18)";


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
    /// Fetches the plan XML for a given query_plan_hash.
    /// When <paramref name="oldest"/> is true, returns the plan with the smallest plan_id (first created);
    /// otherwise returns the one with the largest plan_id (most recent).
    /// Returns null if no matching plan is found.
    /// </summary>
    public static async Task<QueryStorePlan?> FetchPlanByHashAsync(
        string connectionString, string queryPlanHash, bool oldest,
        CancellationToken ct = default)
    {
        var orderDir = oldest ? "ASC" : "DESC";
        var sql = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 1
    p.plan_id,
    q.query_id,
    CONVERT(varchar(18), q.query_hash, 1),
    CONVERT(varchar(18), p.query_plan_hash, 1),
    TRY_CAST(p.query_plan AS nvarchar(max)),
    qt.query_sql_text
FROM sys.query_store_plan p
JOIN sys.query_store_query q ON p.query_id = q.query_id
JOIN sys.query_store_query_text qt ON q.query_text_id = qt.query_text_id
WHERE p.query_plan_hash = CONVERT(binary(8), @planHash, 1)
ORDER BY p.plan_id {orderDir};";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.Add(new SqlParameter("@planHash", queryPlanHash.Trim()));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new QueryStorePlan
        {
            PlanId = reader.GetInt64(0),
            QueryId = reader.GetInt64(1),
            QueryHash = reader.IsDBNull(2) ? "" : reader.GetString(2),
            QueryPlanHash = reader.IsDBNull(3) ? "" : reader.GetString(3),
            PlanXml = reader.IsDBNull(4) ? "" : reader.GetString(4),
            QueryText = reader.IsDBNull(5) ? "" : reader.GetString(5),
        };
    }


}
