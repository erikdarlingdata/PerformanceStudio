using System.IO;

namespace PlanViewer.Core.Output;

public static class TextFormatter
{
    public static string Format(AnalysisResult result)
    {
        using var writer = new StringWriter();
        WriteText(result, writer);
        return writer.ToString();
    }

    public static void WriteText(AnalysisResult result, TextWriter writer)
    {
        // Server context (connected mode only)
        if (result.ServerContext != null)
        {
            WriteServerContext(result.ServerContext, writer);
        }
        else if (result.SqlServerBuild != null)
        {
            writer.WriteLine($"SQL Server: {result.SqlServerBuild}");
        }

        writer.WriteLine("=== Summary ===");
        writer.WriteLine($"Statements: {result.Summary.TotalStatements}");
        writer.WriteLine($"Warnings: {result.Summary.TotalWarnings} ({result.Summary.CriticalWarnings} critical)");
        writer.WriteLine($"Missing indexes: {result.Summary.MissingIndexes}");
        writer.WriteLine($"Actual stats: {(result.Summary.HasActualStats ? "yes" : "no (estimated plan)")}");
        if (result.Summary.WarningTypes.Count > 0)
        {
            writer.WriteLine("Warning types:");
            foreach (var wt in result.Summary.WarningTypes)
                writer.WriteLine($"  {wt}");
        }
        writer.WriteLine();

        for (int i = 0; i < result.Statements.Count; i++)
        {
            var stmt = result.Statements[i];
            writer.WriteLine($"=== Statement {i + 1}: ===");
            writer.WriteLine(stmt.StatementText);
            writer.WriteLine();
            writer.WriteLine($"Estimated cost: {stmt.EstimatedCost:F4}");

            if (stmt.DegreeOfParallelism > 0)
            {
                var dopLine = $"DOP: {stmt.DegreeOfParallelism}";
                if (stmt.QueryTime != null && stmt.QueryTime.ElapsedTimeMs > 0 && stmt.QueryTime.CpuTimeMs > 0)
                {
                    var idealCpu = stmt.QueryTime.ElapsedTimeMs * stmt.DegreeOfParallelism;
                    var efficiency = Math.Min(100.0, stmt.QueryTime.CpuTimeMs * 100.0 / idealCpu);
                    dopLine += $" ({efficiency:N0}% efficient)";
                }
                writer.WriteLine(dopLine);
            }
            if (stmt.NonParallelReason != null)
                writer.WriteLine($"Serial reason: {stmt.NonParallelReason}");
            if (stmt.QueryTime != null)
                writer.WriteLine($"Runtime: {stmt.QueryTime.ElapsedTimeMs:N0}ms elapsed, {stmt.QueryTime.CpuTimeMs:N0}ms CPU");
            if (stmt.MemoryGrant != null && stmt.MemoryGrant.GrantedKB > 0)
            {
                var grantedMB = stmt.MemoryGrant.GrantedKB / 1024.0;
                var usedMB = stmt.MemoryGrant.MaxUsedKB / 1024.0;
                var pctUsed = grantedMB > 0 ? usedMB / grantedMB * 100 : 0;
                var pctContext = "";
                if (result.ServerContext?.MaxServerMemoryMB > 0)
                    pctContext = $", {grantedMB / result.ServerContext.MaxServerMemoryMB * 100:N1}% of max server memory";
                writer.WriteLine($"Memory grant: {grantedMB:N1} MB granted, {usedMB:N1} MB used ({pctUsed:N0}% utilized{pctContext})");
            }

            // Expensive operators — promoted to right after memory grant.
            // Answers "where did the time go?" before drilling into waits/warnings.
            if (stmt.OperatorTree != null)
            {
                var nodeTimings = new List<(OperatorResult Node, long OwnCpuMs, long OwnElapsedMs)>();
                CollectNodeTimings(stmt.OperatorTree, nodeTimings);

                var topNodes = nodeTimings
                    .Where(t => t.OwnCpuMs > 0 || t.OwnElapsedMs > 0)
                    .OrderByDescending(t => Math.Max(t.OwnCpuMs, t.OwnElapsedMs))
                    .Take(5)
                    .ToList();

                if (topNodes.Count > 0)
                {
                    writer.WriteLine("Expensive operators:");
                    var totalCpu = stmt.QueryTime?.CpuTimeMs > 0 ? stmt.QueryTime.CpuTimeMs : 0;
                    var totalElapsed = stmt.QueryTime?.ElapsedTimeMs ?? 0;
                    foreach (var (n, ownCpu, ownElapsed) in topNodes)
                    {
                        var label = n.ObjectName != null
                            ? $"{n.PhysicalOp} ({n.ObjectName})"
                            : n.PhysicalOp;
                        var nodeId = $" (Node {n.NodeId})";

                        writer.WriteLine($"  {label}{nodeId}:");

                        // Timing on its own line
                        var timeParts = new List<string>();
                        if (ownCpu > 0)
                        {
                            var cpuPct = totalCpu > 0 ? $" ({ownCpu * 100.0 / totalCpu:N0}%)" : "";
                            timeParts.Add($"{ownCpu:N0}ms CPU{cpuPct}");
                        }
                        if (ownElapsed > 0)
                        {
                            var elPct = totalElapsed > 0 ? $" ({ownElapsed * 100.0 / totalElapsed:N0}%)" : "";
                            timeParts.Add($"{ownElapsed:N0}ms elapsed{elPct}");
                        }
                        if (timeParts.Count > 0)
                            writer.WriteLine($"    {string.Join(", ", timeParts)}");

                        var details = new List<string>();
                        if (n.ActualRows > 0)
                            details.Add($"{n.ActualRows:N0} rows");
                        if (n.ActualLogicalReads > 0)
                            details.Add($"{n.ActualLogicalReads:N0} logical reads");
                        if (n.ActualPhysicalReads > 0)
                            details.Add($"{n.ActualPhysicalReads:N0} physical reads");
                        if (details.Count > 0)
                            writer.WriteLine($"    {string.Join(", ", details)}");
                    }
                }
            }

            if (stmt.WaitStats.Count > 0)
            {
                writer.WriteLine("Wait stats:");
                foreach (var w in stmt.WaitStats.OrderByDescending(w => w.WaitTimeMs))
                    writer.WriteLine($"  {w.WaitType}: {w.WaitTimeMs:N0}ms");
            }

            if (stmt.Parameters.Count > 0)
            {
                writer.WriteLine("Parameters:");
                foreach (var p in stmt.Parameters)
                {
                    var sniff = p.SniffingIssue ? " [SNIFFING]" : "";
                    writer.WriteLine($"  {p.Name} {p.DataType} = {p.CompiledValue ?? "?"}{sniff}");
                }
            }

            if (stmt.Warnings.Count > 0)
            {
                writer.WriteLine();
                writer.WriteLine("Plan warnings:");
                var hasDetailedMemoryGrant = stmt.Warnings.Any(w =>
                    w.Type == "Excessive Memory Grant" || w.Type == "Large Memory Grant");
                foreach (var w in stmt.Warnings)
                {
                    // Skip raw XML "Memory Grant" when analyzer provides better context
                    if (w.Type == "Memory Grant" && hasDetailedMemoryGrant)
                        continue;
                    writer.WriteLine($"  [{w.Severity}] {w.Type}: {EscapeNewlines(w.Message)}");
                }
            }

            if (stmt.OperatorTree != null)
            {
                var nodeWarnings = new List<WarningResult>();
                CollectNodeWarnings(stmt.OperatorTree, nodeWarnings);
                if (nodeWarnings.Count > 0)
                {
                    writer.WriteLine();
                    writer.WriteLine("Operator warnings:");
                    WriteGroupedOperatorWarnings(nodeWarnings, writer);
                }
            }

            if (stmt.MissingIndexes.Count > 0)
            {
                writer.WriteLine();
                writer.WriteLine("Missing indexes:");
                foreach (var mi in stmt.MissingIndexes)
                {
                    writer.WriteLine($"  {mi.Table} (impact: {mi.Impact:F0}%)");
                    writer.WriteLine($"    {mi.CreateStatement}");
                }
            }

            writer.WriteLine();
        }
    }

    private static void WriteServerContext(ServerContextResult ctx, TextWriter writer)
    {
        writer.WriteLine("=== Server Context ===");

        // Server line: name + edition + version
        var editionShort = ctx.Edition;
        if (editionShort != null)
        {
            // Trim " (64-bit)" suffix if present
            var parenIdx = editionShort.IndexOf(" (64-bit)");
            if (parenIdx > 0) editionShort = editionShort[..parenIdx];
        }

        var versionParts = new List<string>();
        if (ctx.ProductVersion != null) versionParts.Add(ctx.ProductVersion);
        if (ctx.ProductLevel != null && ctx.ProductLevel != "RTM") versionParts.Add(ctx.ProductLevel);
        var versionStr = versionParts.Count > 0 ? $", {string.Join(" ", versionParts)}" : "";

        if (ctx.IsAzure)
            writer.WriteLine($"  Server: {ctx.ServerName} (Azure SQL{versionStr})");
        else if (editionShort != null)
            writer.WriteLine($"  Server: {ctx.ServerName} ({editionShort}{versionStr})");
        else
            writer.WriteLine($"  Server: {ctx.ServerName}");

        // Hardware
        if (ctx.CpuCount > 0)
            writer.WriteLine($"  Hardware: {ctx.CpuCount} CPUs, {ctx.PhysicalMemoryMB:N0} MB RAM");

        // Instance settings
        writer.WriteLine($"  MAXDOP: {ctx.MaxDop}  |  Cost threshold: {ctx.CostThresholdForParallelism}  |  Max memory: {ctx.MaxServerMemoryMB:N0} MB");

        // Database
        if (ctx.Database != null)
        {
            var db = ctx.Database;
            writer.WriteLine($"  Database: {db.Name} (compat {db.CompatibilityLevel}, {db.CollationName})");

            // Notable settings — only show when they deviate from healthy defaults
            var notable = new List<string>();

            // Isolation — show if on
            if (db.SnapshotIsolationState > 0)
                notable.Add("Snapshot isolation: ON");
            if (db.ReadCommittedSnapshot)
                notable.Add("RCSI: ON");

            // Stats — warn if off
            if (!db.AutoCreateStats)
                notable.Add("Auto create stats: OFF");
            if (!db.AutoUpdateStats)
                notable.Add("Auto update stats: OFF");
            if (db.AutoUpdateStatsAsync)
                notable.Add("Auto update stats async: ON");

            // Parameterization
            if (db.ParameterizationForced)
                notable.Add("Forced parameterization: ON");

            if (notable.Count > 0)
            {
                foreach (var n in notable)
                    writer.WriteLine($"    {n}");
            }

            if (db.NonDefaultScopedConfigs.Count > 0)
            {
                writer.WriteLine("  Non-default scoped configs:");
                foreach (var sc in db.NonDefaultScopedConfigs)
                    writer.WriteLine($"    {sc.Name} = {sc.Value}");
            }
        }

        writer.WriteLine();
    }

    private static void CollectNodeWarnings(OperatorResult node, List<WarningResult> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectNodeWarnings(child, warnings);
    }

    private static void WriteGroupedOperatorWarnings(List<WarningResult> warnings, TextWriter writer)
    {
        // Split each message into "data | explanation" at the last sentence boundary
        // that starts with "The " (the harm assessment). Group by shared explanation.
        var entries = new List<(string Severity, string Operator, string Data, string? Explanation)>();
        foreach (var w in warnings)
        {
            var msg = w.Message;
            string data;
            string? explanation = null;

            // Find the harm sentence: ". The overestimate/underestimate..."
            var splitIdx = msg.LastIndexOf(". The ", StringComparison.Ordinal);
            if (splitIdx > 0)
            {
                data = msg[..splitIdx].TrimEnd('.');
                explanation = msg[(splitIdx + 2)..];
            }
            else
            {
                data = msg;
            }

            entries.Add((w.Severity, w.Operator ?? "?", data, explanation));
        }

        // Group entries that share the same severity, type, and explanation
        var grouped = entries
            .GroupBy(e => (e.Severity, e.Explanation ?? ""))
            .ToList();

        foreach (var group in grouped)
        {
            var items = group.ToList();
            if (items.Count > 1 && group.Key.Item2 != "")
            {
                // Multiple operators with the same explanation — list compactly
                foreach (var item in items)
                    writer.WriteLine($"  [{item.Severity}] {item.Operator}: {EscapeNewlines(item.Data)}");
                writer.WriteLine($"  -> {group.Key.Item2}");
            }
            else
            {
                // Unique explanation or no explanation — write individually
                foreach (var item in items)
                {
                    var full = item.Explanation != null ? $"{item.Data}. {item.Explanation}" : item.Data;
                    writer.WriteLine($"  [{item.Severity}] {item.Operator}: {EscapeNewlines(full)}");
                }
            }
        }
    }

    /// <summary>
    /// Replaces newlines with unit separator (U+001F) so multi-line warning messages
    /// survive the top-level line split in AdviceContentBuilder.Build().
    /// CreateWarningBlock splits on U+001F to restore the internal structure.
    /// </summary>
    private static string EscapeNewlines(string text) => text.Replace('\n', '\x1F');

    private static void CollectNodeTimings(OperatorResult node, List<(OperatorResult Node, long OwnCpuMs, long OwnElapsedMs)> timings)
    {
        // Skip exchanges — negligible own work, misleading elapsed times
        if (node.PhysicalOp != "Parallelism")
        {
            var mode = node.ActualExecutionMode ?? node.ExecutionMode;

            // Compute own CPU
            long ownCpu = 0;
            if (node.ActualCpuMs.HasValue)
            {
                if (mode == "Batch")
                    ownCpu = node.ActualCpuMs.Value;
                else
                {
                    var childCpuSum = GetChildCpuSum(node);
                    ownCpu = Math.Max(0, node.ActualCpuMs.Value - childCpuSum);
                }
            }

            // Compute own elapsed
            long ownElapsed = 0;
            if (node.ActualElapsedMs.HasValue)
            {
                if (mode == "Batch")
                    ownElapsed = node.ActualElapsedMs.Value;
                else
                {
                    var childSum = GetChildElapsedSum(node);
                    ownElapsed = Math.Max(0, node.ActualElapsedMs.Value - childSum);
                }
            }

            // When CPU data is available, only include operators that did real CPU work.
            // Row-mode elapsed with 0 CPU is a cumulative timing artifact, not real work.
            if (node.ActualCpuMs.HasValue)
            {
                if (ownCpu > 0)
                    timings.Add((node, ownCpu, ownElapsed));
            }
            else if (ownElapsed > 0)
            {
                // No CPU data (estimated plans) — fall back to elapsed only
                timings.Add((node, 0, ownElapsed));
            }
        }

        foreach (var child in node.Children)
            CollectNodeTimings(child, timings);
    }

    /// <summary>
    /// Sums CPU time from all direct children, skipping through transparent
    /// operators (Compute Scalar, etc.) that have no runtime stats.
    /// </summary>
    private static long GetChildCpuSum(OperatorResult node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            if (child.ActualCpuMs.HasValue)
            {
                sum += child.ActualCpuMs.Value;
            }
            else
            {
                // Transparent operator (e.g. Compute Scalar) — skip through
                sum += GetChildCpuSum(child);
            }
        }
        return sum;
    }

    /// <summary>
    /// Sums elapsed time from all direct children, skipping through exchange
    /// and transparent operators.
    /// </summary>
    private static long GetChildElapsedSum(OperatorResult node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            long childTime;
            if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
            {
                childTime = child.Children
                    .Where(c => c.ActualElapsedMs.HasValue)
                    .Select(c => c.ActualElapsedMs!.Value)
                    .DefaultIfEmpty(0)
                    .Max();
            }
            else if (child.ActualElapsedMs.HasValue)
            {
                childTime = child.ActualElapsedMs.Value;
            }
            else
            {
                childTime = GetChildElapsedSum(child);
            }
            sum += childTime;
        }
        return sum;
    }

}
