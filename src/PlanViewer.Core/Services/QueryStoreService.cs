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
    /// </summary>
    public static async Task<List<QueryStorePlan>> FetchTopPlansAsync(
        string connectionString, int topN = 25, string orderBy = "cpu",
        int hoursBack = 24, QueryStoreFilter? filter = null,
        CancellationToken ct = default)
    {
        var key = orderBy.ToLowerInvariant();

        // ROW_NUMBER order: pick the "best" plan per query_id.
        // References pre-aggregated columns from plan_agg CTE.
        // avg- variants still rank by total CPU (most impactful plan).
        var orderClause = key switch
        {
            "cpu"              => "pa.total_cpu_us",
            "duration"         => "pa.total_duration_us",
            "reads"            => "pa.total_reads",
            "writes"           => "pa.total_writes",
            "physical-reads"   => "pa.total_physical_reads",
            "memory"           => "pa.total_memory_pages",
            "executions"       => "pa.total_executions",
            _ => "pa.total_cpu_us"
        };

        // Final ORDER BY — either a total or avg column from ranked CTE.
        var outerOrder = key switch
        {
            "cpu"              => "r.total_cpu_us",
            "duration"         => "r.total_duration_us",
            "reads"            => "r.total_reads",
            "writes"           => "r.total_writes",
            "physical-reads"   => "r.total_physical_reads",
            "memory"           => "r.total_memory_pages",
            "executions"       => "r.total_executions",
            "avg-cpu"          => "r.avg_cpu_us",
            "avg-duration"     => "r.avg_duration_us",
            "avg-reads"        => "r.avg_reads",
            "avg-writes"       => "r.avg_writes",
            "avg-physical-reads" => "r.avg_physical_reads",
            "avg-memory"       => "r.avg_memory_pages",
            _ => "r.total_cpu_us"
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
            filterClauses.Add("AND r.plan_id = @filterPlanId");
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

        // 1. plan_agg: aggregate runtime_stats by plan_id only (cheapest grouping,
        //    avoids joining query_text for the entire dataset).
        // 2. ranked: join the small aggregated result to plan to get query_id,
        //    ROW_NUMBER to pick best plan per query.
        // 3. Final SELECT: TOP N, then join query_text + plan XML only for winners.
        //    Filter clauses applied here where q/p are available.
        var sql = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

WITH plan_agg AS (
    SELECT
        rs.plan_id,
        SUM(rs.avg_cpu_time * rs.count_executions) AS total_cpu_us,
        SUM(rs.avg_duration * rs.count_executions) AS total_duration_us,
        SUM(rs.avg_logical_io_reads * rs.count_executions) AS total_reads,
        SUM(rs.avg_logical_io_writes * rs.count_executions) AS total_writes,
        SUM(rs.avg_physical_io_reads * rs.count_executions) AS total_physical_reads,
        SUM(rs.avg_query_max_used_memory * rs.count_executions) AS total_memory_pages,
        SUM(rs.count_executions) AS total_executions,
        MAX(rs.last_execution_time) AS last_execution_time
    FROM sys.query_store_runtime_stats rs
    WHERE rs.last_execution_time >= DATEADD(HOUR, -{hoursBack}, GETUTCDATE())
    GROUP BY rs.plan_id
),
ranked AS (
    SELECT
        p.query_id,
        pa.plan_id,
        pa.total_cpu_us,
        pa.total_duration_us,
        pa.total_reads,
        pa.total_writes,
        pa.total_physical_reads,
        pa.total_memory_pages,
        pa.total_executions,
        pa.last_execution_time,
        CASE WHEN pa.total_executions > 0
             THEN pa.total_cpu_us / pa.total_executions ELSE 0 END AS avg_cpu_us,
        CASE WHEN pa.total_executions > 0
             THEN pa.total_duration_us / pa.total_executions ELSE 0 END AS avg_duration_us,
        CASE WHEN pa.total_executions > 0
             THEN pa.total_reads / pa.total_executions ELSE 0 END AS avg_reads,
        CASE WHEN pa.total_executions > 0
             THEN pa.total_writes / pa.total_executions ELSE 0 END AS avg_writes,
        CASE WHEN pa.total_executions > 0
             THEN pa.total_physical_reads / pa.total_executions ELSE 0 END AS avg_physical_reads,
        CASE WHEN pa.total_executions > 0
             THEN pa.total_memory_pages / pa.total_executions ELSE 0 END AS avg_memory_pages,
        ROW_NUMBER() OVER (PARTITION BY p.query_id ORDER BY {orderClause} DESC) AS rn
    FROM plan_agg pa
    JOIN sys.query_store_plan p ON pa.plan_id = p.plan_id
    WHERE p.query_plan IS NOT NULL
)
SELECT TOP ({topN})
    r.query_id,
    r.plan_id,
    qt.query_sql_text,
    CAST(p.query_plan AS nvarchar(max)) AS query_plan,
    r.avg_cpu_us,
    r.avg_duration_us,
    r.avg_reads,
    r.avg_writes,
    r.avg_physical_reads,
    r.avg_memory_pages,
    r.total_executions,
    CAST(r.total_cpu_us AS bigint),
    CAST(r.total_duration_us AS bigint),
    CAST(r.total_reads AS bigint),
    CAST(r.total_writes AS bigint),
    CAST(r.total_physical_reads AS bigint),
    CAST(r.total_memory_pages AS bigint),
    r.last_execution_time,
    CONVERT(varchar(18), q.query_hash, 1),
    CONVERT(varchar(18), p.query_plan_hash, 1),
    CASE
        WHEN q.object_id <> 0
        THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id)
        ELSE N''
    END
FROM ranked r
JOIN sys.query_store_plan p ON r.plan_id = p.plan_id
JOIN sys.query_store_query q ON p.query_id = q.query_id
JOIN sys.query_store_query_text qt ON q.query_text_id = qt.query_text_id
WHERE 1 = 1 {rnClause}{filterSql}
ORDER BY {outerOrder} DESC
OPTION (LOOP JOIN);";

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
}
