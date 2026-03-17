using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

#pragma warning disable CA1707 // Identifiers should not contain underscores (MCP snake_case convention)

namespace PlanViewer.App.Mcp;

[McpServerToolType]
public sealed class McpQueryStoreTools
{
    [McpServerTool(Name = "check_query_store")]
    [Description("Checks whether Query Store is enabled and accessible on a database. " +
        "Use this before calling get_query_store_top to verify the target database supports Query Store.")]
    public static async Task<string> CheckQueryStore(
        ConnectionStore connectionStore,
        ICredentialService credentialService,
        [Description("Server name from get_connections.")] string connection_name,
        [Description("Database name to check.")] string database)
    {
        try
        {
            var conn = FindConnection(connectionStore, connection_name);
            if (conn == null)
                return ConnectionNotFound(connectionStore, connection_name);

            var connectionString = conn.GetConnectionString(credentialService, database);
            var (enabled, state) = await QueryStoreService.CheckEnabledAsync(connectionString);

            return JsonSerializer.Serialize(new
            {
                server = conn.ServerName,
                database,
                query_store_enabled = enabled,
                state
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("check_query_store", ex);
        }
    }

    [McpServerTool(Name = "get_query_store_top")]
    [Description("Fetches the top N queries from Query Store ranked by the specified metric. " +
        "Uses the application's built-in Query Store query — no arbitrary SQL is executed. " +
        "Each fetched plan is automatically loaded into the application for further analysis " +
        "with analyze_plan, get_plan_warnings, etc. Returns summary stats and session IDs. " +
        "Optional filters narrow results server-side by query_id, plan_id, query_hash, " +
        "plan_hash, or module name (schema.name, supports % wildcards).")]
    public static async Task<string> GetQueryStoreTop(
        PlanSessionManager sessionManager,
        ConnectionStore connectionStore,
        ICredentialService credentialService,
        [Description("Server name from get_connections.")] string connection_name,
        [Description("Database name to query.")] string database,
        [Description("Number of top queries to return. Default 10, max 50.")] int top = 10,
        [Description("Ranking metric: cpu, avg-cpu, duration, avg-duration, reads, avg-reads, " +
            "writes, avg-writes, physical-reads, avg-physical-reads, memory, avg-memory, executions. " +
            "Default: cpu.")] string order_by = "cpu",
        [Description("Hours of history to include. Default 24, max 168.")] int hours_back = 24,
        [Description("Filter by Query Store query ID.")] long? query_id = null,
        [Description("Filter by Query Store plan ID.")] long? plan_id = null,
        [Description("Filter by query hash (hex, e.g. 0x1AB2C3D4).")] string? query_hash = null,
        [Description("Filter by query plan hash (hex, e.g. 0x1AB2C3D4).")] string? plan_hash = null,
        [Description("Filter by module name (schema.name, supports % wildcards).")] string? module = null)
    {
        try
        {
            var conn = FindConnection(connectionStore, connection_name);
            if (conn == null)
                return ConnectionNotFound(connectionStore, connection_name);

            // Validate parameters
            if (top < 1 || top > 50)
                return "Invalid top value. Must be between 1 and 50.";
            if (hours_back < 1 || hours_back > 168)
                return "Invalid hours_back value. Must be between 1 and 168.";

            QueryStoreFilter? filter = null;
            if (query_id != null || plan_id != null ||
                query_hash != null || plan_hash != null || module != null)
            {
                filter = new QueryStoreFilter
                {
                    QueryId = query_id,
                    PlanId = plan_id,
                    QueryHash = query_hash,
                    QueryPlanHash = plan_hash,
                    ModuleName = module,
                };
            }

            var connectionString = conn.GetConnectionString(credentialService, database);

            // Check Query Store is enabled first
            var (enabled, state) = await QueryStoreService.CheckEnabledAsync(connectionString);
            if (!enabled)
                return $"Query Store is not enabled on [{database}]. State: {state ?? "unknown"}.";

            // Fetch plans using the app's built-in query
            var plans = await QueryStoreService.FetchTopPlansAsync(
                connectionString, top, order_by, hours_back, filter);

            if (plans.Count == 0)
                return $"No Query Store data found in [{database}] for the last {hours_back} hours.";

            // Parse and register each plan with PlanSessionManager
            var results = plans.Select(qsPlan =>
            {
                var sessionId = Guid.NewGuid().ToString();
                var label = $"QS:{database} Q{qsPlan.QueryId} P{qsPlan.PlanId}";

                try
                {
                    var xml = qsPlan.PlanXml
                        .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
                    var parsed = ShowPlanParser.Parse(xml);
                    PlanAnalyzer.Analyze(parsed);

                    var allStatements = parsed.Batches.SelectMany(b => b.Statements).ToList();

                    sessionManager.Register(sessionId, new PlanSession
                    {
                        SessionId = sessionId,
                        Label = label,
                        Source = "query-store",
                        Plan = parsed,
                        QueryText = qsPlan.QueryText,
                        ConnectionInfo = conn.ServerName,
                        StatementCount = allStatements.Count,
                        HasActualStats = false, // Query Store plans are always estimated
                        WarningCount = allStatements.Sum(s => s.PlanWarnings.Count),
                        CriticalWarningCount = allStatements.Sum(s =>
                            s.PlanWarnings.Count(w => w.Severity == Core.Models.PlanWarningSeverity.Critical)),
                        MissingIndexCount = parsed.AllMissingIndexes.Count
                    });

                    return new
                    {
                        session_id = sessionId,
                        query_id = qsPlan.QueryId,
                        plan_id = qsPlan.PlanId,
                        query_hash = qsPlan.QueryHash,
                        query_plan_hash = qsPlan.QueryPlanHash,
                        module_name = string.IsNullOrEmpty(qsPlan.ModuleName) ? (string?)null : qsPlan.ModuleName,
                        label,
                        query_text = McpHelpers.Truncate(qsPlan.QueryText, 500),
                        executions = qsPlan.CountExecutions,
                        total_cpu_ms = qsPlan.TotalCpuTimeUs / 1000.0,
                        avg_cpu_ms = qsPlan.AvgCpuTimeUs / 1000.0,
                        total_duration_ms = qsPlan.TotalDurationUs / 1000.0,
                        avg_duration_ms = qsPlan.AvgDurationUs / 1000.0,
                        total_logical_reads = qsPlan.TotalLogicalIoReads,
                        avg_logical_reads = qsPlan.AvgLogicalIoReads,
                        warning_count = allStatements.Sum(s => s.PlanWarnings.Count),
                        missing_index_count = parsed.AllMissingIndexes.Count,
                        last_executed_utc = qsPlan.LastExecutedUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                        loaded = true
                    };
                }
                catch
                {
                    // Plan XML couldn't be parsed — return stats without loading
                    return new
                    {
                        session_id = (string)"",
                        query_id = qsPlan.QueryId,
                        plan_id = qsPlan.PlanId,
                        query_hash = qsPlan.QueryHash,
                        query_plan_hash = qsPlan.QueryPlanHash,
                        module_name = string.IsNullOrEmpty(qsPlan.ModuleName) ? (string?)null : qsPlan.ModuleName,
                        label,
                        query_text = McpHelpers.Truncate(qsPlan.QueryText, 500),
                        executions = qsPlan.CountExecutions,
                        total_cpu_ms = qsPlan.TotalCpuTimeUs / 1000.0,
                        avg_cpu_ms = qsPlan.AvgCpuTimeUs / 1000.0,
                        total_duration_ms = qsPlan.TotalDurationUs / 1000.0,
                        avg_duration_ms = qsPlan.AvgDurationUs / 1000.0,
                        total_logical_reads = qsPlan.TotalLogicalIoReads,
                        avg_logical_reads = qsPlan.AvgLogicalIoReads,
                        warning_count = 0,
                        missing_index_count = 0,
                        last_executed_utc = qsPlan.LastExecutedUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                        loaded = false
                    };
                }
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                server = conn.ServerName,
                database,
                order_by,
                hours_back,
                plan_count = results.Count,
                plans = results
            }, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_query_store_top", ex);
        }
    }

    private static Core.Models.ServerConnection? FindConnection(
        ConnectionStore store, string name)
    {
        var connections = store.Load();
        return connections.FirstOrDefault(c =>
            c.ServerName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(c.DisplayName) &&
             c.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ConnectionNotFound(ConnectionStore store, string name)
    {
        var connections = store.Load();
        if (connections.Count == 0)
            return "No saved connections. Add a connection in the application via the query editor toolbar.";
        var available = string.Join(", ", connections.Select(c =>
            string.IsNullOrEmpty(c.DisplayName) ? c.ServerName : $"{c.DisplayName} ({c.ServerName})"));
        return $"Connection '{name}' not found. Available: {available}";
    }
}
