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
