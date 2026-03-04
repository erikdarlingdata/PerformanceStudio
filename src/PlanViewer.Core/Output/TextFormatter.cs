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
        else if (result.SqlServerVersion != null)
        {
            writer.WriteLine($"SQL Server: {result.SqlServerVersion} (build {result.SqlServerBuild})");
        }

        writer.WriteLine($"=== Statements: {result.Summary.TotalStatements} ===");
        writer.WriteLine();

        // Summary right after statement count
        writer.WriteLine("=== Summary ===");
        writer.WriteLine($"Warnings: {result.Summary.TotalWarnings} ({result.Summary.CriticalWarnings} critical)");
        writer.WriteLine($"Missing indexes: {result.Summary.MissingIndexes}");
        writer.WriteLine($"Actual stats: {(result.Summary.HasActualStats ? "yes" : "no (estimated plan)")}");
        if (result.Summary.WarningTypes.Count > 0)
            writer.WriteLine($"Warning types: {string.Join(", ", result.Summary.WarningTypes)}");
        writer.WriteLine();

        for (int i = 0; i < result.Statements.Count; i++)
        {
            var stmt = result.Statements[i];
            writer.WriteLine($"=== Statement {i + 1}: ===");
            writer.WriteLine(stmt.StatementText);
            writer.WriteLine();
            writer.WriteLine($"Estimated cost: {stmt.EstimatedCost:F4}");

            if (stmt.DegreeOfParallelism > 0)
                writer.WriteLine($"DOP: {stmt.DegreeOfParallelism}");
            if (stmt.NonParallelReason != null)
                writer.WriteLine($"Serial reason: {stmt.NonParallelReason}");
            if (stmt.QueryTime != null)
                writer.WriteLine($"Runtime: {stmt.QueryTime.ElapsedTimeMs:N0}ms elapsed, {stmt.QueryTime.CpuTimeMs:N0}ms CPU");
            if (stmt.MemoryGrant != null && stmt.MemoryGrant.GrantedKB > 0)
            {
                var grantedMB = stmt.MemoryGrant.GrantedKB / 1024.0;
                var usedMB = stmt.MemoryGrant.MaxUsedKB / 1024.0;
                var pctContext = "";
                if (result.ServerContext?.MaxServerMemoryMB > 0)
                    pctContext = $" ({grantedMB / result.ServerContext.MaxServerMemoryMB * 100:N1}% of max server memory)";
                writer.WriteLine($"Memory grant: {grantedMB:N1} MB granted, {usedMB:N1} MB used{pctContext}");
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
                writer.WriteLine("Warnings:");
                foreach (var w in stmt.Warnings)
                    writer.WriteLine($"  [{w.Severity}] {w.Type}: {w.Message}");
            }

            if (stmt.OperatorTree != null)
            {
                var nodeWarnings = new List<WarningResult>();
                CollectNodeWarnings(stmt.OperatorTree, nodeWarnings);
                if (nodeWarnings.Count > 0)
                {
                    writer.WriteLine();
                    writer.WriteLine("Operator warnings:");
                    foreach (var w in nodeWarnings)
                        writer.WriteLine($"  [{w.Severity}] {w.Operator}: {w.Message}");
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

            // Expensive operators — actual plans only, ranked by actual elapsed time.
            // Row mode: elapsed is cumulative (parent includes children), so subtract
            // children's time to isolate each operator's own work.
            // Batch mode: elapsed is already per-operator.
            // Estimated plans: no actual stats, nothing to rank — skip entirely.
            if (stmt.OperatorTree != null)
            {
                var nodeTimings = new List<(OperatorResult Node, long OwnElapsedMs)>();
                CollectNodeTimings(stmt.OperatorTree, nodeTimings);

                var topNodes = nodeTimings
                    .Where(t => t.OwnElapsedMs > 0)
                    .OrderByDescending(t => t.OwnElapsedMs)
                    .Take(5)
                    .ToList();

                if (topNodes.Count > 0)
                {
                    writer.WriteLine();
                    writer.WriteLine("Expensive operators (by actual duration):");
                    foreach (var (n, ownMs) in topNodes)
                    {
                        var label = n.ObjectName != null
                            ? $"{n.PhysicalOp} ({n.ObjectName})"
                            : n.PhysicalOp;

                        var pctStr = stmt.QueryTime != null && stmt.QueryTime.ElapsedTimeMs > 0
                            ? $" ({ownMs * 100.0 / stmt.QueryTime.ElapsedTimeMs:N0}% of total)"
                            : "";
                        writer.WriteLine($"  {label}:");
                        writer.WriteLine($"   * {ownMs:N0}ms{pctStr}");
                        if (n.ActualRows > 0)
                            writer.WriteLine($"   * {n.ActualRows:N0} rows");
                        if (n.ActualLogicalReads > 0)
                            writer.WriteLine($"   * {n.ActualLogicalReads:N0} logical reads");
                        if (n.ActualPhysicalReads > 0)
                            writer.WriteLine($"   * {n.ActualPhysicalReads:N0} physical reads");
                    }
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

    private static void CollectNodeTimings(OperatorResult node, List<(OperatorResult Node, long OwnElapsedMs)> timings)
    {
        if (node.ActualElapsedMs.HasValue)
        {
            var mode = node.ActualExecutionMode ?? node.ExecutionMode;
            long ownMs;
            if (mode == "Batch")
            {
                // Batch mode: elapsed is per-operator
                ownMs = node.ActualElapsedMs.Value;
            }
            else if (node.PhysicalOp == "Parallelism")
            {
                // Parallel exchanges have misleading inflated times — skip them.
                // Their own work is negligible; the time they report is mostly
                // waiting on producers/consumers, not doing real work.
                ownMs = -1; // sentinel: don't add to timings
            }
            else
            {
                // Row mode: elapsed is cumulative, subtract ALL direct children.
                // Exchange children have unreliable times — skip through to their
                // real child for the elapsed value.
                var childSum = GetChildElapsedSum(node);
                ownMs = Math.Max(0, node.ActualElapsedMs.Value - childSum);
            }

            if (ownMs >= 0)
                timings.Add((node, ownMs));
        }

        foreach (var child in node.Children)
            CollectNodeTimings(child, timings);
    }

    /// <summary>
    /// Sums elapsed time from all direct children, skipping through exchange
    /// operators (their times are unreliable — they accumulate downstream wait
    /// time from e.g. spilling sorts). For exchanges, uses the max elapsed of
    /// the exchange's own children as the effective elapsed value.
    /// </summary>
    private static long GetChildElapsedSum(OperatorResult node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            long childTime;
            if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
            {
                // Exchange: skip through, use max of its children
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
                // No stats (e.g. Compute Scalar) — transparent, skip through
                childTime = GetChildElapsedSum(child);
            }
            sum += childTime;
        }
        return sum;
    }
}
