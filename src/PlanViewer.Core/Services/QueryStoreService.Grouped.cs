using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class QueryStoreService
{
    /// <summary>
    /// Fetches grouped-by-QueryHash results.
    /// Step 1: Top X query hashes by metric.
    /// Step 2: Top 5 plan hashes per query hash with metrics.
    /// Step 3: Top and bottom QueryId/PlanId per query_hash/plan_hash.
    /// Final : Fetch Query Text and Plan XML for the identified QueryId/PlanId.
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
    RTRIM(CAST(SUBSTRING(MAX(
        CONVERT(char(27), CAST(rs.last_execution_time AS datetime2(7)), 121)
        + CAST(ISNULL(rs.execution_type_desc, '') AS char(60))
    ), 28, 60) AS nvarchar(60)))
FROM sys.query_store_runtime_stats AS rs
WHERE EXISTS (SELECT 1 FROM #intervals AS i WHERE i.runtime_stats_interval_id = rs.runtime_stats_interval_id){phase2ExecutionTypeClause}
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
        MAX(ps.execution_type_desc) AS execution_type_desc,
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
       last_execution_time,
       execution_type_desc
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
        ps.execution_type_desc,
        ROW_NUMBER() OVER (PARTITION BY q.query_hash, p.query_plan_hash ORDER BY ps.{metricCol} DESC) AS rn_top,
        ROW_NUMBER() OVER (PARTITION BY q.query_hash, p.query_plan_hash ORDER BY ps.{metricCol} ASC) AS rn_bottom
    FROM #plan_stats ps
    JOIN sys.query_store_plan p ON ps.plan_id = p.plan_id
    JOIN sys.query_store_query q ON p.query_id = q.query_id
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
p.query_plan AS plan_xml,
r.module_name, 
r.total_cpu_us, 
r.total_duration_us, 
r.total_reads, 
r.total_writes,
r.total_physical_reads, 
r.total_memory_pages, 
r.total_executions, 
r.last_execution_time,
CASE WHEN r.rn_top = 1 THEN 1 ELSE 0 END AS is_top,
r.execution_type_desc
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
                ExecutionTypeDesc = reader.IsDBNull(16) ? "" : reader.GetString(16),
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
                    ExecutionTypeDesc = reader.IsDBNull(11) ? "" : reader.GetString(11),
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
    /// Final Step: Fetch Query Text and Plan XML for the identified QueryId/PlanId.
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
    RTRIM(CAST(SUBSTRING(MAX(
        CONVERT(char(27), CAST(rs.last_execution_time AS datetime2(7)), 121)
        + CAST(ISNULL(rs.execution_type_desc, '') AS char(60))
    ), 28, 60) AS nvarchar(60)))
FROM sys.query_store_runtime_stats AS rs
WHERE EXISTS (SELECT 1 FROM #intervals AS i WHERE i.runtime_stats_interval_id = rs.runtime_stats_interval_id){phase2ExecutionTypeClause}
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
        MAX(ps.execution_type_desc) AS execution_type_desc,
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
       last_execution_time,
       execution_type_desc
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
        q.query_text_id,
        CAST(ps.total_cpu_us AS bigint) AS total_cpu_us,
        CAST(ps.total_duration_us AS bigint) AS total_duration_us,
        CAST(ps.total_reads AS bigint) AS total_reads,
        CAST(ps.total_writes AS bigint) AS total_writes,
        CAST(ps.total_physical_reads AS bigint) AS total_physical_reads,
        CAST(ps.total_memory_pages AS bigint) AS total_memory_pages,
        ps.total_executions,
        ps.last_execution_time,
        ps.execution_type_desc,
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
    WHERE EXISTS (SELECT 1 FROM #qhash_rows qhr
                  WHERE qhr.module_name = CASE WHEN q.object_id <> 0
                       THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
                       ELSE N'' END
                    AND qhr.query_hash = CONVERT(varchar(18), q.query_hash, 1))
)
SELECT *
into #ranked_light
FROM ranked
WHERE rn_top = 1 OR rn_bottom = 1;

/* Final select: join heavy elements (query_text, plan_xml) only for the top/bottom representatives */
SELECT 
    r.module_name, 
    r.query_hash, 
    r.plan_hash, 
    r.query_id, 
    r.plan_id, 
    qt.query_sql_text, 
    p.query_plan AS plan_xml,
    r.total_cpu_us, 
    r.total_duration_us, 
    r.total_reads, 
    r.total_writes,
    r.total_physical_reads, 
    r.total_memory_pages, 
    r.total_executions, 
    r.last_execution_time,
    CASE WHEN r.rn_top = 1 THEN 1 ELSE 0 END AS is_top,
    r.execution_type_desc
FROM #ranked_light r
JOIN sys.query_store_query_text qt ON r.query_text_id = qt.query_text_id
JOIN sys.query_store_plan p ON r.plan_id = p.plan_id;

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
                ExecutionTypeDesc = reader.IsDBNull(16) ? "" : reader.GetString(16),
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
                    ExecutionTypeDesc = reader.IsDBNull(10) ? "" : reader.GetString(10),
                });
            }
        }

        return result;
    }
}
