using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using ModelContextProtocol.Server;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

#pragma warning disable CA1707 // Identifiers should not contain underscores (MCP snake_case convention)

namespace PlanViewer.App.Mcp;

[McpServerToolType]
public sealed class McpPlanTools
{
    [McpServerTool(Name = "list_plans")]
    [Description("Lists all execution plans currently loaded in the application. Returns session IDs, labels, " +
        "statement counts, warning counts, and source type. Use this first to discover available plans.")]
    public static string ListPlans(PlanSessionManager sessionManager)
    {
        var sessions = sessionManager.GetAllSessions();
        if (sessions.Count == 0)
            return "No plans are currently loaded in the application. Open a .sqlplan file or paste plan XML to get started.";

        return JsonSerializer.Serialize(new { plans = sessions }, McpHelpers.JsonOptions);
    }

    [McpServerTool(Name = "get_connections")]
    [Description("Lists saved SQL Server connections. Returns server names and authentication types only — " +
        "credentials are never exposed. Use connection names with Query Store tools.")]
    public static string GetConnections(ConnectionStore connectionStore)
    {
        var connections = connectionStore.Load();
        if (connections.Count == 0)
            return "No saved connections. Add a connection in the application via the query editor toolbar.";

        var safe = connections.Select(c => new
        {
            name = c.ServerName,
            display_name = string.IsNullOrEmpty(c.DisplayName) ? c.ServerName : c.DisplayName,
            auth_type = c.AuthenticationDisplay
        });

        return JsonSerializer.Serialize(new { connections = safe }, McpHelpers.JsonOptions);
    }

    [McpServerTool(Name = "analyze_plan")]
    [Description("Returns the full JSON analysis result for a loaded plan. Includes all statements, warnings, " +
        "missing indexes, parameters, operator tree, memory grants, and wait stats. " +
        "This is the primary tool for understanding plan quality. Use list_plans first to get session_id values.")]
    public static string AnalyzePlan(
        PlanSessionManager sessionManager,
        [Description("The session_id from list_plans.")] string session_id)
    {
        var session = sessionManager.GetSession(session_id);
        if (session == null)
            return SessionNotFound(sessionManager, session_id);

        try
        {
            var result = ResultMapper.Map(session.Plan, session.Source);
            return JsonSerializer.Serialize(result, McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("analyze_plan", ex);
        }
    }

    [McpServerTool(Name = "get_plan_summary")]
    [Description("Returns a concise human-readable text summary of a loaded plan: statement count, warnings, " +
        "missing indexes, cost, DOP, memory grants. Faster than analyze_plan for quick assessment.")]
    public static string GetPlanSummary(
        PlanSessionManager sessionManager,
        [Description("The session_id from list_plans.")] string session_id)
    {
        var session = sessionManager.GetSession(session_id);
        if (session == null)
            return SessionNotFound(sessionManager, session_id);

        try
        {
            var result = ResultMapper.Map(session.Plan, session.Source);
            return TextFormatter.Format(result);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_plan_summary", ex);
        }
    }

    [McpServerTool(Name = "get_plan_warnings")]
    [Description("Returns only the warnings and analysis findings for a loaded plan. " +
        "Optionally filter by severity (Critical, Warning, or Info).")]
    public static string GetPlanWarnings(
        PlanSessionManager sessionManager,
        [Description("The session_id from list_plans.")] string session_id,
        [Description("Optional severity filter: Critical, Warning, or Info.")] string? severity = null)
    {
        var session = sessionManager.GetSession(session_id);
        if (session == null)
            return SessionNotFound(sessionManager, session_id);

        try
        {
            var result = ResultMapper.Map(session.Plan, session.Source);
            var allWarnings = result.Statements
                .SelectMany(s => s.Warnings.Select(w => new
                {
                    severity = w.Severity,
                    type = w.Type,
                    message = w.Message,
                    node_id = w.NodeId,
                    @operator = w.Operator,
                    statement = McpHelpers.Truncate(s.StatementText, 200)
                }))
                .Where(w => severity == null ||
                    w.severity.Equals(severity, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (allWarnings.Count == 0)
            {
                return severity != null
                    ? $"No {severity} warnings found in this plan."
                    : "No warnings found in this plan.";
            }

            return JsonSerializer.Serialize(new { warning_count = allWarnings.Count, warnings = allWarnings },
                McpHelpers.JsonOptions);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_plan_warnings", ex);
        }
    }

    [McpServerTool(Name = "get_missing_indexes")]
    [Description("Returns missing index suggestions from a loaded plan with impact scores and " +
        "ready-to-run CREATE INDEX statements.")]
    public static string GetMissingIndexes(
        PlanSessionManager sessionManager,
        [Description("The session_id from list_plans.")] string session_id)
    {
        var session = sessionManager.GetSession(session_id);
        if (session == null)
            return SessionNotFound(sessionManager, session_id);

        var indexes = session.Plan.AllMissingIndexes;
        if (indexes.Count == 0)
            return "No missing index suggestions in this plan.";

        var result = indexes.Select(idx => new
        {
            database = idx.Database,
            schema_name = idx.Schema,
            table = idx.Table,
            impact = idx.Impact,
            equality_columns = idx.EqualityColumns,
            inequality_columns = idx.InequalityColumns,
            include_columns = idx.IncludeColumns,
            create_statement = idx.CreateStatement
        });

        return JsonSerializer.Serialize(new { missing_index_count = indexes.Count, indexes = result },
            McpHelpers.JsonOptions);
    }

    [McpServerTool(Name = "get_plan_parameters")]
    [Description("Returns parameter details from a loaded plan including names, data types, " +
        "compiled values, and runtime values. Highlights parameter sniffing when compiled and runtime values differ.")]
    public static string GetPlanParameters(
        PlanSessionManager sessionManager,
        [Description("The session_id from list_plans.")] string session_id)
    {
        var session = sessionManager.GetSession(session_id);
        if (session == null)
            return SessionNotFound(sessionManager, session_id);

        var statements = session.Plan.Batches
            .SelectMany(b => b.Statements)
            .Where(s => s.Parameters.Count > 0)
            .Select(s => new
            {
                statement = McpHelpers.Truncate(s.StatementText, 200),
                parameters = s.Parameters.Select(p => new
                {
                    name = p.Name,
                    data_type = p.DataType,
                    compiled_value = p.CompiledValue,
                    runtime_value = p.RuntimeValue,
                    sniffing_mismatch = p.CompiledValue != null && p.RuntimeValue != null
                        && p.CompiledValue != p.RuntimeValue
                })
            })
            .ToList();

        if (statements.Count == 0)
            return "No parameters found in this plan (ad-hoc query or local variables only).";

        return JsonSerializer.Serialize(new { statements }, McpHelpers.JsonOptions);
    }

    [McpServerTool(Name = "get_expensive_operators")]
    [Description("Returns the top N most expensive operators from a loaded plan, ranked by cost percentage " +
        "or actual elapsed time (if available). Useful for quickly finding bottleneck operators.")]
    public static string GetExpensiveOperators(
        PlanSessionManager sessionManager,
        [Description("The session_id from list_plans.")] string session_id,
        [Description("Number of operators to return. Default 10.")] int top = 10)
    {
        var session = sessionManager.GetSession(session_id);
        if (session == null)
            return SessionNotFound(sessionManager, session_id);

        var topError = McpHelpers.ValidateTop(top);
        if (topError != null) return topError;

        var allNodes = new List<(PlanNode Node, string Statement)>();
        foreach (var stmt in session.Plan.Batches.SelectMany(b => b.Statements))
        {
            if (stmt.RootNode == null) continue;
            CollectNodes(stmt.RootNode, McpHelpers.Truncate(stmt.StatementText, 100) ?? "", allNodes);
        }

        var hasActuals = allNodes.Any(n => n.Node.ActualElapsedMs > 0);
        var ranked = hasActuals
            ? allNodes.OrderByDescending(n => n.Node.ActualElapsedMs)
            : allNodes.OrderByDescending(n => n.Node.CostPercent);

        var result = ranked.Take(top).Select(n => new
        {
            node_id = n.Node.NodeId,
            physical_op = n.Node.PhysicalOp,
            logical_op = n.Node.LogicalOp,
            cost_percent = n.Node.CostPercent,
            estimated_rows = n.Node.EstimateRows,
            actual_rows = n.Node.ActualRows,
            actual_elapsed_ms = n.Node.ActualElapsedMs,
            actual_cpu_ms = n.Node.ActualCPUMs,
            logical_reads = n.Node.ActualLogicalReads,
            physical_reads = n.Node.ActualPhysicalReads,
            object_name = n.Node.ObjectName,
            statement = n.Statement
        });

        return JsonSerializer.Serialize(new { ranked_by = hasActuals ? "actual_elapsed_ms" : "cost_percent", operators = result },
            McpHelpers.JsonOptions);
    }

    [McpServerTool(Name = "get_plan_xml")]
    [Description("Returns the raw showplan XML for a loaded plan. Useful when you need to examine " +
        "plan details not captured in the structured analysis. Truncated at 500KB.")]
    public static string GetPlanXml(
        PlanSessionManager sessionManager,
        [Description("The session_id from list_plans.")] string session_id)
    {
        var session = sessionManager.GetSession(session_id);
        if (session == null)
            return SessionNotFound(sessionManager, session_id);

        return McpHelpers.Truncate(session.Plan.RawXml, 512_000) ?? "No plan XML available.";
    }

    [McpServerTool(Name = "compare_plans")]
    [Description("Compares two loaded plans side by side. Returns differences in cost, DOP, warnings, " +
        "memory grants, runtime stats, and operator shapes.")]
    public static string ComparePlans(
        PlanSessionManager sessionManager,
        [Description("Session ID of the first plan (from list_plans).")] string session_id_a,
        [Description("Session ID of the second plan (from list_plans).")] string session_id_b)
    {
        var sessionA = sessionManager.GetSession(session_id_a);
        if (sessionA == null)
            return SessionNotFound(sessionManager, session_id_a);

        var sessionB = sessionManager.GetSession(session_id_b);
        if (sessionB == null)
            return SessionNotFound(sessionManager, session_id_b);

        try
        {
            var resultA = ResultMapper.Map(sessionA.Plan, sessionA.Source);
            var resultB = ResultMapper.Map(sessionB.Plan, sessionB.Source);
            return ComparisonFormatter.Compare(resultA, resultB, sessionA.Label, sessionB.Label);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("compare_plans", ex);
        }
    }

    [McpServerTool(Name = "get_repro_script")]
    [Description("Generates a paste-ready T-SQL reproduction script from a loaded plan. " +
        "Extracts parameters, SET options, and database context into a runnable sp_executesql call.")]
    public static string GetReproScript(
        PlanSessionManager sessionManager,
        [Description("The session_id from list_plans.")] string session_id)
    {
        var session = sessionManager.GetSession(session_id);
        if (session == null)
            return SessionNotFound(sessionManager, session_id);

        try
        {
            var stmt = session.Plan.Batches
                .SelectMany(b => b.Statements)
                .FirstOrDefault(s => s.RootNode != null);

            if (stmt == null)
                return "No executable statement found in this plan.";

            var queryText = session.QueryText ?? stmt.StatementText ?? "";

            // Extract database from first operator node's DatabaseName property
            string? databaseName = null;
            if (stmt.RootNode?.DatabaseName != null)
                databaseName = stmt.RootNode.DatabaseName;

            return ReproScriptBuilder.BuildReproScript(
                queryText, databaseName, session.Plan.RawXml, null);
        }
        catch (Exception ex)
        {
            return McpHelpers.FormatError("get_repro_script", ex);
        }
    }

    private static string SessionNotFound(PlanSessionManager sessionManager, string sessionId)
    {
        var available = sessionManager.GetAllSessions();
        if (available.Count == 0)
            return "No plans are currently loaded in the application.";
        return $"Session '{sessionId}' not found. Use list_plans to see available sessions.";
    }

    private static void CollectNodes(PlanNode node, string statement, List<(PlanNode, string)> nodes)
    {
        nodes.Add((node, statement));
        foreach (var child in node.Children)
            CollectNodes(child, statement, nodes);
    }
}
