using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

#pragma warning disable CA1707 // Identifiers should not contain underscores (MCP snake_case convention)

namespace PlanViewer.App.Mcp;

[McpServerToolType]
public sealed class McpQueryStoreTools
{
    public static Task<string> CheckQueryStore(
        ConnectionStore connectionStore,
        ICredentialService credentialService,
        string connection_name,
        string database) =>
        CheckQueryStore(
            connectionStore,
            credentialService,
            connection_name,
            database,
            CancellationToken.None);

    [McpServerTool(Name = "check_query_store")]
    [Description("Checks whether Query Store is enabled and accessible on a database. " +
        "Use this before calling get_query_store_top to verify the target database supports Query Store.")]
    public static async Task<string> CheckQueryStore(
        ConnectionStore connectionStore,
        ICredentialService credentialService,
        [Description("Server name from get_connections.")] string connection_name,
        [Description("Database name to check.")] string database,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var conn = FindConnection(connectionStore, connection_name);
            if (conn == null)
                return ConnectionNotFound(connectionStore, connection_name);

            var connectionString = conn.GetConnectionString(credentialService, database);
            var (enabled, state, readOnlyReplica) = await QueryStoreService
                .CheckEnabledAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                server = conn.ServerName,
                database,
                query_store_enabled = enabled,
                state,
                read_only_replica = readOnlyReplica
            }, McpHelpers.JsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("check_query_store", ex);
        }
    }

    public static Task<string> GetQueryStoreTop(
        PlanSessionManager sessionManager,
        ConnectionStore connectionStore,
        ICredentialService credentialService,
        string connection_name,
        string database,
        int top = 10,
        string order_by = "cpu",
        int hours_back = 24,
        long? query_id = null,
        long? plan_id = null,
        string? query_hash = null,
        string? plan_hash = null,
        string? module = null,
        string? execution_type = null) =>
        GetQueryStoreTop(
            sessionManager,
            new PlanOperations(sessionManager, AnalyzerConfig.Default),
            connectionStore,
            credentialService,
            connection_name,
            database,
            CancellationToken.None,
            top,
            order_by,
            hours_back,
            query_id,
            plan_id,
            query_hash,
            plan_hash,
            module,
            execution_type);

    [McpServerTool(Name = "get_query_store_top")]
    [Description("Fetches the top N queries from Query Store ranked by the specified metric. " +
        "Uses the application's built-in Query Store query — no arbitrary SQL is executed. " +
        "Each fetched plan is automatically loaded into the application for further analysis " +
        "with analyze_plan, get_plan_warnings, etc. Returns summary stats and session IDs. " +
        "Optional filters narrow results server-side by query_id, plan_id, query_hash, " +
        "plan_hash, or module name (schema.name, supports % wildcards).")]
    public static async Task<string> GetQueryStoreTop(
        PlanSessionManager sessionManager,
        PlanOperations operations,
        ConnectionStore connectionStore,
        ICredentialService credentialService,
        [Description("Server name from get_connections.")] string connection_name,
        [Description("Database name to query.")] string database,
        CancellationToken cancellationToken,
        [Description("Number of top queries to return. Default 10, max 50.")] int top = 10,
        [Description("Ranking metric: cpu, avg-cpu, duration, avg-duration, reads, avg-reads, " +
            "writes, avg-writes, physical-reads, avg-physical-reads, memory, avg-memory, executions. " +
            "Default: cpu.")] string order_by = "cpu",
        [Description("Hours of history to include. Default 24, max 168.")] int hours_back = 24,
        [Description("Filter by Query Store query ID.")] long? query_id = null,
        [Description("Filter by Query Store plan ID.")] long? plan_id = null,
        [Description("Filter by query hash (hex, e.g. 0x1AB2C3D4).")] string? query_hash = null,
        [Description("Filter by query plan hash (hex, e.g. 0x1AB2C3D4).")] string? plan_hash = null,
        [Description("Filter by module name (schema.name, supports % wildcards).")] string? module = null,
        [Description("Filter by execution type: regular, aborted, exception, or failed (= aborted + exception).")] string? execution_type = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

            string[]? executionTypes;
            try
            {
                executionTypes = QueryStoreFilter.ParseExecutionType(execution_type);
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }

            QueryStoreFilter? filter = null;
            if (query_id != null || plan_id != null ||
                query_hash != null || plan_hash != null || module != null ||
                executionTypes != null)
            {
                filter = new QueryStoreFilter
                {
                    QueryId = query_id,
                    PlanId = plan_id,
                    QueryHash = query_hash,
                    QueryPlanHash = plan_hash,
                    ModuleName = module,
                    ExecutionTypeDescs = executionTypes,
                };
            }

            var connectionString = conn.GetConnectionString(credentialService, database);

            // Check Query Store is enabled first
            var (enabled, state, readOnlyReplica) = await QueryStoreService
                .CheckEnabledAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);
            if (!enabled)
                return readOnlyReplica
                    ? $"[{database}] is a read-only replica with no Query Store data to read (state: {state ?? "unknown"}). Enable Query Store on the primary replica."
                    : $"Query Store is not enabled on [{database}]. State: {state ?? "unknown"}.";

            // Fetch plans using the app's built-in query
            var plans = await QueryStoreService.FetchTopPlansAsync(
                connectionString,
                top,
                order_by,
                hours_back,
                filter,
                cancellationToken).ConfigureAwait(false);

            if (plans.Count == 0)
                return $"No Query Store data found in [{database}] for the last {hours_back} hours.";

            // Fetch server metadata for Rule 38 (Standard Edition DOP limitation)
            ServerMetadata? serverMetadata = null;
            try
            {
                var isAzure = conn.ServerName.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
                              conn.ServerName.Contains(".database.azure.com", StringComparison.OrdinalIgnoreCase);
                serverMetadata = await ServerMetadataService
                    .FetchServerMetadataAsync(connectionString, isAzure, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Non-fatal: analysis continues without server context
            }

            // Parse and register each plan with PlanSessionManager
            var results = plans.Select(qsPlan =>
            {
                var sessionId = Guid.NewGuid().ToString();
                var label = $"QS:{database} Q{qsPlan.QueryId} P{qsPlan.PlanId}";

                try
                {
                    var xml = qsPlan.PlanXml
                        .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
                    cancellationToken.ThrowIfCancellationRequested();
                    var parsed = PlanAnalysisPipeline.Analyze(
                        xml,
                        AnalyzerConfig.Default,
                        serverMetadata,
                        cancellationToken);
                    var session = CaptureSession(
                        sessionId,
                        label,
                        parsed,
                        qsPlan.QueryText,
                        conn.ServerName);
                    operations.AdmitSnapshot(
                        session.ToCore(),
                        Encoding.UTF8.GetByteCount(xml),
                        cancellationToken);

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
                        warning_count = session.WarningCount,
                        missing_index_count = session.MissingIndexCount,
                        last_executed_utc = qsPlan.LastExecutedUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                        loaded = true,
                        load_error = (string?)null
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Return bounded failure context without exposing a stack trace.
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
                        loaded = false,
                        load_error = McpHelpers.Truncate(ex.Message, 512)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_query_store_top", ex);
        }
    }

    internal static PlanSession CaptureSession(
        string sessionId,
        string label,
        ParsedPlan parsed,
        string? queryText,
        string connectionInfo)
    {
        var analysis = ResultMapper.Map(parsed, "query-store");
        var allStatements = parsed.Batches.SelectMany(batch => batch.Statements).ToList();
        var executableStatement = allStatements.FirstOrDefault(statement => statement.RootNode is not null);
        var session = new PlanSession
        {
            SessionId = sessionId,
            Label = label,
            Source = "query-store",
            Plan = parsed,
            Analysis = analysis,
            RawPlanXml = parsed.RawXml,
            DatabaseName = executableStatement?.RootNode?.DatabaseName,
            QueryText = queryText,
            ConnectionInfo = connectionInfo,
            StatementCount = allStatements.Count,
            HasActualStats = false,
            WarningCount = allStatements.Sum(statement => statement.PlanWarnings.Count),
            CriticalWarningCount = allStatements.Sum(statement =>
                statement.PlanWarnings.Count(warning => warning.Severity == Core.Models.PlanWarningSeverity.Critical)),
            MissingIndexCount = parsed.AllMissingIndexes.Count
        };

        parsed.RawXml = string.Empty;
        parsed.Batches.Clear();
        return session;
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
