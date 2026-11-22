using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class PlanAnalyzer
{
    private static void AnalyzeNodeTree(
        PlanNode node,
        PlanStatement stmt,
        AnalyzerConfig cfg,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalyzeNode(node, stmt, cfg);

        foreach (var child in node.Children)
            AnalyzeNodeTree(child, stmt, cfg, cancellationToken);
    }

    private static void AnalyzeNode(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        Rule01_FilterOperator(node, stmt, cfg);
        Rule02_EagerIndexSpool(node, stmt, cfg);
        Rule04_UdfTiming(node, stmt, cfg);
        Rule05_RowEstimateMismatch(node, stmt, cfg);
        Rule06_ScalarUdfReference(node, stmt, cfg);
        Rule07_SpillSeverity(node, stmt, cfg);
        Rule08_ParallelThreadSkew(node, stmt, cfg);
        Rule10_LookupResidual(node, stmt, cfg);
        Rule12_NonSargablePredicate(node, stmt, cfg);
        Rule11_ScanResidual(node, stmt, cfg);
        Rule32_CardinalityMisestimateScan(node, stmt, cfg);
        Rule33_CeGuessDetection(node, stmt, cfg);
        Rule34_BareScanNarrowOutput(node, stmt, cfg);
        Rule13_MismatchedDataTypes(node, stmt, cfg);
        Rule14_LazyTableSpoolRebind(node, stmt, cfg);
        Rule15_JoinOrClause(node, stmt, cfg);
        Rule16_NestedLoopsHighExec(node, stmt, cfg);
        Rule17_ManyToManyMerge(node, stmt, cfg);
        // Rule 22 (table variables) is a statement rule, Rule22Stmt_TableVariable: one warning per
        // statement, with the operators that touch the table variable in OriginNodeIds.
        Rule23_TableValuedFunctions(node, stmt, cfg);
        Rule24_TopAboveScan(node, stmt, cfg);
        Rule26_RowGoal(node, stmt, cfg);
        Rule28_RowCountSpool(node, stmt, cfg);
        Rule29_ImplicitConversionSeek(node, stmt, cfg);
        Rule35_ExpensiveOperator(node, stmt, cfg);

        /* #440: an operator warning's origin is the operator it is hanging off, so it is stamped
           here rather than at each of the 26 places above that add one. Same reasoning as the
           provenance stamp in ShowPlanParser: a rule you have to remember at every construction
           site is a rule that eventually gets forgotten, and the one time it is forgotten the UI
           quietly offers no link on a warning that has a perfectly good one.

           Only fills what a rule left empty, so a rule that knows better - one pointing at the
           operator that CAUSED the problem rather than the one reporting it - keeps its own answer. */
        foreach (var warning in node.Warnings)
        {
            if (warning.OriginNodeIds.Count == 0)
                warning.OriginNodeIds.Add(node.NodeId);
        }
    }

    private static void Rule01_FilterOperator(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 1: Filter operators — rows survived the tree just to be discarded
        // Quantify the impact by summing child subtree cost (reads, CPU, time).
        // Suppress when the filter's child subtree is trivial (low I/O, fast, cheap).
        if (!cfg.IsRuleDisabled(1) && node.PhysicalOp == "Filter" && !string.IsNullOrEmpty(node.Predicate)
            && node.Children.Count > 0)
        {
            // Gate: skip trivial filters based on actual stats or estimated cost
            bool isTrivial;
            if (node.HasActualStats)
            {
                long childReads = 0;
                foreach (var child in node.Children)
                    childReads += SumSubtreeReads(child);
                var childElapsed = node.Children.Max(c => c.ActualElapsedMs);
                isTrivial = childReads < 128 && childElapsed < 10;
            }
            else
            {
                var childCost = node.Children.Sum(c => c.EstimatedTotalSubtreeCost);
                isTrivial = childCost < 1.0;
            }

            if (!isTrivial)
            {
                var impact = QuantifyFilterImpact(node);
                var predicate = Truncate(node.Predicate, 200);
                var message = "Filter operator discarding rows late in the plan.";
                if (!string.IsNullOrEmpty(impact))
                    message += $"\n{impact}";
                message += $"\nPredicate: {predicate}";

                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 1,
                    WarningType = "Filter Operator",
                    Message = message,
                    Severity = PlanWarningSeverity.Warning
                });
            }
        }

    }

    private static void Rule02_EagerIndexSpool(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 2: Eager Index Spools — optimizer building temporary indexes on the fly
        if (!cfg.IsRuleDisabled(2) && node.LogicalOp == "Eager Spool" &&
            node.PhysicalOp.Contains("Index", StringComparison.OrdinalIgnoreCase))
        {
            var message = "SQL Server is building a temporary index in TempDB at runtime because no suitable permanent index exists. This is expensive: it builds the index from scratch on every execution. Create a permanent index on the underlying table to eliminate this operator entirely.";
            if (!string.IsNullOrEmpty(node.SuggestedIndex))
                message += $"\n\nCreate this index:\n{node.SuggestedIndex}";

            node.Warnings.Add(new PlanWarning
            {
                RuleNumber = 2,
                WarningType = "Eager Index Spool",
                Message = message,
                Severity = PlanWarningSeverity.Critical
            });
        }

    }

    private static void Rule04_UdfTiming(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 4: UDF timing — any node spending time in UDFs (actual plans)
        if (!cfg.IsRuleDisabled(4) && (node.UdfCpuTimeMs > 0 || node.UdfElapsedTimeMs > 0))
        {
            node.Warnings.Add(new PlanWarning
            {
                RuleNumber = 4,
                WarningType = "UDF Execution",
                Message = string.Create(CultureInfo.InvariantCulture, $"Scalar UDF executing on this operator ({node.UdfElapsedTimeMs:N0}ms elapsed, {node.UdfCpuTimeMs:N0}ms CPU). Scalar UDFs run once per row and prevent parallelism. Options: rewrite as an inline table-valued function, assign the result to a variable if only one row is needed, dump results to a #temp table and apply the UDF to the final result set, or on SQL Server 2019+ check if the UDF is eligible for automatic scalar UDF inlining."),
                Severity = node.UdfElapsedTimeMs >= 1000 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
            });
        }

    }

    private static void Rule05_RowEstimateMismatch(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 5: Large estimate vs actual row gaps (actual plans only)
        // Only warn when the bad estimate actually causes observable harm:
        // - The node itself spilled (Sort/Hash with bad memory grant)
        // - A parent join may have chosen the wrong strategy
        // - Root nodes with no parent to harm are skipped
        // - Nodes whose only parents are Parallelism/Top/Sort (no spill) are skipped
        /* #577: an operator that never executed returned zero rows because it never ran, so its
           zero is no evidence that the estimate was wrong. Rules 11, 12 and 29 skip such
           operators the same way. */
        if (!cfg.IsRuleDisabled(5) && node.HasActualStats && node.EstimateRows > 0
            && node.ActualExecutions > 0
            && !node.Lookup) // Key lookups are point lookups (1 row per execution) — per-execution estimate is misleading
        {
            if (node.ActualRows == 0)
            {
                // Zero rows with a significant estimate — only warn on operators that
                // actually allocate meaningful resources (memory grants for hash/sort/spool).
                // Skip Parallelism, Bitmap, Compute Scalar, Filter, Concatenation, etc.
                // where 0 rows is just a consequence of upstream filtering.
                if (node.EstimateRows >= 100 && AllocatesResources(node))
                {
                    node.Warnings.Add(new PlanWarning
                    {
                        RuleNumber = 5,
                        WarningType = "Row Estimate Mismatch",
                        Message = string.Create(CultureInfo.InvariantCulture, $"Estimated {node.EstimateRows:N0} rows but actual 0 rows returned. SQL Server allocated resources for rows that never materialized."),
                        Severity = PlanWarningSeverity.Warning
                    });
                }
            }
            else
            {
                // #594: compare like with like. EstimateRows is per execution; it is scaled by the
                // executions only on the inner side of a Nested Loops join (or an Adaptive Join's
                // nested-loops input), and then by the node's executions as a whole rather than the
                // per-thread sum — everywhere else (including a non-inner node in a parallel zone,
                // where the sum counts threads) RowEstimateHelper leaves the estimate at one
                // execution instead of inflating it by DOP.
                var isInnerSide = RowEstimateHelper.IsInnerSideOfNestedLoops(node);
                var ratio = RowEstimateHelper.GetRowAccuracyRatio(node);
                if (ratio >= 10.0 || ratio <= 0.1)
                {
                    var harm = AssessEstimateHarm(node, ratio);
                    if (harm != null)
                    {
                        var direction = ratio >= 10.0 ? "underestimated" : "overestimated";
                        var factor = ratio >= 10.0 ? ratio : 1.0 / ratio;
                        // The per-execution count is rounded once, the way N0 rounds, and the noun follows the number
                        // shown: a seek that returned one row on each of 500 executions reads "1 row x 500 executions".
                        // Executions of the operator as a whole, the count the ratio used, not the per-thread sum.
                        var executions = RowEstimateHelper.GetActualExecutions(node);
                        var rowsPerExecution = executions > 0
                            ? (long)Math.Round((double)node.ActualRows / executions, MidpointRounding.ToEven)
                            : 0;
                        var actualDisplay = isInnerSide && executions > 1
                            ? string.Create(CultureInfo.InvariantCulture, $"Actual {node.ActualRows:N0} ({rowsPerExecution:N0} {(rowsPerExecution == 1 ? "row" : "rows")} x {executions:N0} executions)")
                            : string.Create(CultureInfo.InvariantCulture, $"Actual {node.ActualRows:N0}");
                        node.Warnings.Add(new PlanWarning
                        {
                            RuleNumber = 5,
                            WarningType = "Row Estimate Mismatch",
                            Message = string.Create(CultureInfo.InvariantCulture, $"Estimated {node.EstimateRows:N0} vs {actualDisplay}: {factor:F0}x {direction}. {harm}"),
                            Severity = factor >= 100 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
                        });
                    }
                }
            }
        }

    }

    private static void Rule06_ScalarUdfReference(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 6: Scalar UDF references (works on estimated plans too)
        // Suppress when Serial Plan warning is already firing for a UDF-related reason —
        // the Serial Plan warning already explains the issue, this would be redundant.
        /* #576: "already firing" has to be checked, not assumed from the reason. Rule 3 can be
           disabled, and it skips statements that cost under 1, TRIVIAL plans and 0 ms runs, so
           the UDF warning used to vanish with nothing in its place. Statement rules run before
           node rules, so rule 3's finding is on the statement by now if it fired. */
        var serialPlanCoversUdf =
            (stmt.NonParallelPlanReason is "TSQLUserDefinedFunctionsNotParallelizable"
                or "CLRUserDefinedFunctionRequiresDataAccess"
                or "CouldNotGenerateValidParallelPlan")
            && stmt.PlanWarnings.Any(w => w.WarningType == "Serial Plan");
        if (!cfg.IsRuleDisabled(6) && !serialPlanCoversUdf)
            foreach (var udf in node.ScalarUdfs)
            {
                var type = udf.IsClrFunction ? "CLR" : "T-SQL";
                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 6,
                    WarningType = "Scalar UDF",
                    Message = $"Scalar {type} UDF: {udf.FunctionName}. Scalar UDFs run once per row and prevent parallelism. Options: rewrite as an inline table-valued function, assign the result to a variable if only one row is needed, dump results to a #temp table and apply the UDF to the final result set, or on SQL Server 2019+ check if the UDF is eligible for automatic scalar UDF inlining.",
                    Severity = PlanWarningSeverity.Warning
                });
            }

    }

    private static void Rule07_SpillSeverity(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 7: Spill detection — calculate operator time and set severity
        // based on what percentage of statement elapsed time the spill accounts for.
        // Exchange spills on Parallelism operators get special handling since their
        // timing is unreliable but the write count tells the story.
        if (!cfg.IsRuleDisabled(7))
            foreach (var w in node.Warnings.ToList())
            {
                if (w.SpillDetails == null)
                    continue;

                var isExchangeSpill = w.SpillDetails.SpillType == "Exchange";

                if (isExchangeSpill)
                {
                    // Exchange spills: severity based on write count since timing is unreliable
                    var writes = w.SpillDetails.WritesToTempDb;
                    if (writes >= 1_000_000)
                        w.Severity = PlanWarningSeverity.Critical;
                    else if (writes >= 10_000)
                        w.Severity = PlanWarningSeverity.Warning;

                    // Surface Parallelism operator time when available (actual plans)
                    if (node.ActualElapsedMs > 0)
                    {
                        var operatorMs = GetParallelismOperatorElapsedMs(node);
                        var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;
                        if (stmtMs > 0 && operatorMs > 0)
                        {
                            var pct = (double)operatorMs / stmtMs;
                            w.Message += string.Create(CultureInfo.InvariantCulture, $" Operator time: {operatorMs:N0}ms ({pct * 100:N0}% of statement).");
                        }
                    }
                }
                else if (node.ActualElapsedMs > 0)
                {
                    // Sort/Hash spills: severity based on operator time percentage
                    var operatorMs = GetOperatorOwnElapsedMs(node);
                    var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;

                    if (stmtMs > 0)
                    {
                        var pct = (double)operatorMs / stmtMs;
                        w.Message += string.Create(CultureInfo.InvariantCulture, $" Operator time: {operatorMs:N0}ms ({pct * 100:N0}% of statement).");

                        if (pct >= 0.5)
                            w.Severity = PlanWarningSeverity.Critical;
                        else if (pct >= 0.1)
                            w.Severity = PlanWarningSeverity.Warning;
                    }
                }
            }

    }

    private static void Rule08_ParallelThreadSkew(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 8: Parallel thread skew (actual plans with per-thread stats)
        // Only warn when there are enough rows to meaningfully distribute across threads
        // Filter out thread 0 (coordinator) which typically does 0 rows in parallel operators
        if (!cfg.IsRuleDisabled(8) && node.PerThreadStats.Count > 1)
        {
            var workerThreads = node.PerThreadStats.Where(t => t.ThreadId > 0).ToList();
            if (workerThreads.Count < 2) workerThreads = node.PerThreadStats; // fallback
            var totalRows = workerThreads.Sum(t => t.ActualRows);
            var minRowsForSkew = workerThreads.Count * 1000;
            if (totalRows >= minRowsForSkew)
            {
                var maxThread = workerThreads.OrderByDescending(t => t.ActualRows).First();
                var skewRatio = (double)maxThread.ActualRows / totalRows;
                // At DOP 2, a 60/40 split is normal — use higher threshold
                var skewThreshold = workerThreads.Count <= 2 ? 0.80 : 0.50;
                if (skewRatio >= skewThreshold)
                {
                    var message = string.Create(CultureInfo.InvariantCulture, $"Thread {maxThread.ThreadId} processed {skewRatio * 100:N0}% of rows ({maxThread.ActualRows:N0}/{totalRows:N0}). Work is heavily skewed to one thread, so parallelism isn't helping much.");
                    var severity = PlanWarningSeverity.Warning;

                    // Batch mode sorts produce all output on a single thread by design
                    // unless their parent is a batch mode Window Aggregate
                    if (node.PhysicalOp == "Sort"
                        && (node.ActualExecutionMode ?? node.ExecutionMode) == "Batch"
                        && node.Parent?.PhysicalOp != "Window Aggregate")
                    {
                        message += " Batch mode sorts produce all output rows on a single thread by design, unless feeding a batch mode Window Aggregate.";
                        severity = PlanWarningSeverity.Info;
                    }
                    else
                    {
                        // Add practical context — skew is often hard to fix
                        message += " Common causes: uneven data distribution across partitions or hash buckets, or a scan/seek whose predicate sends most rows to one range. Reducing DOP or rewriting the query to avoid the skewed operation may help.";
                    }

                    node.Warnings.Add(new PlanWarning
                    {
                        RuleNumber = 8,
                        WarningType = "Parallel Skew",
                        Message = message,
                        Severity = severity
                    });
                }
            }
        }

    }

    private static void Rule10_LookupResidual(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 10: Key Lookup / RID Lookup with residual predicate
        // Check RID Lookup first — it's more specific (PhysicalOp) and also has Lookup=true
        if (!cfg.IsRuleDisabled(10) && node.PhysicalOp.StartsWith("RID Lookup", StringComparison.OrdinalIgnoreCase))
        {
            var message = "RID Lookup: this table is a heap (no clustered index). SQL Server found rows via a nonclustered index but had to follow row identifiers back to unordered heap pages. Heap lookups are more expensive than key lookups because pages are not sorted and may have forwarding pointers. Add a clustered index to the table.";
            if (!string.IsNullOrEmpty(node.Predicate))
                message += $" Predicate: {Truncate(node.Predicate, 200)}";

            node.Warnings.Add(new PlanWarning
            {
                RuleNumber = 10,
                WarningType = "RID Lookup",
                Message = message,
                Severity = PlanWarningSeverity.Warning
            });
        }
        else if (!cfg.IsRuleDisabled(10) && node.Lookup)
        {
            var lookupMsg = "Key Lookup: SQL Server found rows via a nonclustered index but had to go back to the clustered index for additional columns.";

            // Show what columns the lookup is fetching
            if (!string.IsNullOrEmpty(node.OutputColumns))
                lookupMsg += $"\nColumns fetched: {Truncate(node.OutputColumns, 200)}";

            // Only call out the predicate if it actually filters rows
            if (!string.IsNullOrEmpty(node.Predicate))
            {
                var predicateFilters = node.HasActualStats && node.ActualExecutions > 0
                    && node.ActualRows < node.ActualExecutions;
                if (predicateFilters)
                {
                    var filtered = node.ActualExecutions - node.ActualRows;
                    lookupMsg += string.Create(CultureInfo.InvariantCulture, $"\nResidual predicate (filtered {filtered:N0} {(filtered == 1 ? "row" : "rows")}): {Truncate(node.Predicate, 200)}");
                }
            }

            /* #678: the grade follows what the lookup cost (GradeKeyLookup), and the message states the figure it was graded on. */
            var (lookupSeverity, lookupCostLine) = GradeKeyLookup(node, stmt);
            if (lookupCostLine != null)
                lookupMsg += "\n" + lookupCostLine;

            lookupMsg += "\nTo eliminate the lookup, consider adding the needed columns as INCLUDE columns on the nonclustered index. This widens the index, so weigh the read benefit against write and storage overhead.";

            node.Warnings.Add(new PlanWarning
            {
                RuleNumber = 10,
                WarningType = "Key Lookup",
                Message = lookupMsg,
                Severity = lookupSeverity
            });
        }

    }

    /// <summary>
    /// #678: a Key Lookup's severity follows what the lookup cost, not its presence. In an actual plan that
    /// recorded statement time, the lookup's own time share of the statement is graded on the bands Rule 7 uses
    /// for spills: half or more of the statement is Critical, a tenth or more is Warning, less is Info, so a
    /// lookup that fetched a dozen rows in no measurable time is Info and one that is most of a statement's
    /// time is Critical. When the plan has runtime data but no statement time (QueryTimeStats is absent from
    /// actual plans older than SQL Server 2016), the lookup's executions stand in, since a lookup runs once per
    /// outer row: <see cref="KeyLookupWarningExecutions"/> or more is Warning, fewer is Info, and never Critical,
    /// because without time nothing shows the lookup dominating. An estimated plan has neither, so the
    /// operator's share of the plan's estimated cost grades it with the viewer's own expensive-operator bar
    /// (<see cref="PlanNode.IsExpensive"/>, 25 percent): Warning at or past it, Info below, never Critical,
    /// because cost is an estimate rather than a measurement. Returns the sentence that states the figure the
    /// grade rests on, or null for an estimated plan, whose cost percentage is already shown on the operator.
    /// A severity override from the analyzer config is applied afterwards, as for every rule.
    /// </summary>
    internal static (PlanWarningSeverity Severity, string? CostLine) GradeKeyLookup(PlanNode node, PlanStatement stmt)
    {
        if (!node.HasActualStats)
            return (node.IsExpensive ? PlanWarningSeverity.Warning : PlanWarningSeverity.Info, null);

        var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;
        if (stmtMs > 0)
        {
            var ownMs = GetOperatorOwnElapsedMs(node);
            var pct = (double)ownMs / stmtMs;
            var severity = pct >= 0.5 ? PlanWarningSeverity.Critical
                : pct >= 0.1 ? PlanWarningSeverity.Warning
                : PlanWarningSeverity.Info;
            // Every number in the sentence is written in the invariant culture, so the text reads the same on
            // every desktop, in the CLI's output and in the MCP tools' output.
            return (severity, string.Create(CultureInfo.InvariantCulture, $"Operator time: {ownMs:N0}ms ({pct * 100:N0}% of statement) over {node.ActualExecutions:N0} {(node.ActualExecutions == 1 ? "lookup" : "lookups")}."));
        }

        return (node.ActualExecutions >= KeyLookupWarningExecutions ? PlanWarningSeverity.Warning : PlanWarningSeverity.Info,
            string.Create(CultureInfo.InvariantCulture, $"Lookups performed: {node.ActualExecutions:N0}. The plan recorded no statement time to grade them against."));
    }

    /// <summary>
    /// #678: lookups performed in one execution at or past which a Key Lookup with no statement time to grade
    /// on is a Warning. Each lookup is one clustered-index seek of a few logical reads, so a thousand of them is
    /// a few thousand reads: a real cost, but not one that shows the lookup dominating, which is why this path
    /// stops at Warning. Reasoned, not measured: only the time share in <see cref="GradeKeyLookup"/> measures
    /// the lookup itself. The bar only ever lowers a grade below Critical.
    /// </summary>
    internal const long KeyLookupWarningExecutions = 1_000;

    // Shared by Rule 12 (emits the warning) and Rule 11 (which suppresses its residual-
    // predicate warning when a non-SARGable predicate was already flagged). Pure function
    // of node + cfg, so both rules can compute it independently.
    private static string? GetNonSargableReason(PlanNode node, AnalyzerConfig cfg) =>
        cfg.IsRuleDisabled(12) || (node.HasActualStats && node.ActualExecutions == 0)
            ? null : DetectNonSargablePredicate(node);

    private static void Rule12_NonSargablePredicate(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 12: Non-SARGable predicate on scan
        // Skip for 0-execution nodes — the operator never ran, so the warning is academic
        var nonSargableReason = GetNonSargableReason(node, cfg);
        if (nonSargableReason != null)
        {
            var nonSargableAdvice = nonSargableReason switch
            {
                "Implicit conversion (CONVERT_IMPLICIT)" =>
                    "Implicit conversion (CONVERT_IMPLICIT) prevents an index seek. Match the parameter or variable data type to the column data type.",
                "ISNULL/COALESCE wrapping column" =>
                    "ISNULL/COALESCE wrapping a column prevents an index seek. Rewrite the predicate to avoid wrapping the column, e.g. use \"WHERE col = @val OR col IS NULL\" instead of \"WHERE ISNULL(col, '') = @val\".",
                "Leading wildcard LIKE pattern" =>
                    "Leading wildcard LIKE prevents an index seek: SQL Server must scan every row. If substring search performance is critical, consider a full-text index or a trigram-based approach.",
                "CASE expression in predicate" =>
                    "CASE expression in a predicate prevents an index seek. Rewrite using separate WHERE clauses combined with OR, or split into multiple queries.",
                _ when nonSargableReason.StartsWith("Function call") =>
                    $"{nonSargableReason} prevents an index seek. Remove the function from the column side: apply it to the parameter instead, or create a computed column with the expression and index that.",
                _ =>
                    $"{nonSargableReason} prevents an index seek, forcing a scan."
            };

            node.Warnings.Add(new PlanWarning
            {
                RuleNumber = 12,
                WarningType = "Non-SARGable Predicate",
                Message = $"{nonSargableAdvice}\nPredicate: {Truncate(node.Predicate!, 200)}",
                Severity = PlanWarningSeverity.Warning
            });
        }

    }

    private static void Rule11_ScanResidual(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 11: Scan with residual predicate (skip if non-SARGable already flagged)
        // A PROBE() alone is just a bitmap filter — not a real residual predicate.
        // Skip for 0-execution nodes — the operator never ran
        if (!cfg.IsRuleDisabled(11) && GetNonSargableReason(node, cfg) == null && IsRowstoreScan(node) && !string.IsNullOrEmpty(node.Predicate) &&
            !IsProbeOnly(node.Predicate) && !(node.HasActualStats && node.ActualExecutions == 0) &&
            ScanPredicateIsWorthReporting(node, stmt, out var details))
        {
            var displayPredicate = StripProbeExpressions(node.Predicate);
            var severity = PlanWarningSeverity.Warning;

            /* An actual plan's share is the scan's elapsed time. The cost share is the optimizer's
               estimate, and the plans worth reading are the ones where that estimate was wrong. An
               estimated plan, or an actual plan that carries no statement time, has only the cost
               share. */
            var sharePct = node.HasActualStats && HasStatementElapsed(stmt) ? details.ElapsedPct : details.CostPct;

            // Elevate to Critical if the scan dominates the plan
            if (sharePct >= 90)
                severity = PlanWarningSeverity.Critical;

            var message = node.HasActualStats && node.ActualRowsRead > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Scan with residual predicate: SQL Server read {node.ActualRowsRead:N0} rows to return {node.ActualRows:N0}, filtering after the fact.")
                : "Scan with residual predicate: SQL Server is reading every row and filtering after the fact.";
            if (!string.IsNullOrEmpty(details.Summary))
                message += $" {details.Summary}";

            // #215 E2: if the statement is executing a dynamic cursor, that's usually
            // the reason an index didn't get used. Call it out so the user looks there
            // first rather than hunting for a missing index.
            var isDynamicCursor = string.Equals(stmt.CursorActualType, "Dynamic",
                StringComparison.OrdinalIgnoreCase);
            if (isDynamicCursor)
                message += " This query is running inside a dynamic cursor, which can prevent index usage; changing the cursor type (FAST_FORWARD / STATIC / KEYSET) often fixes scans like this without any indexing change.";
            else
                message += " Check that you have appropriate indexes.";

            // I/O waits specifically confirm the scan is hitting disk — elevate
            if (HasSignificantIoWaits(stmt.WaitStats) && sharePct >= 50
                && severity != PlanWarningSeverity.Critical)
                severity = PlanWarningSeverity.Critical;

            message += $"\nPredicate: {Truncate(displayPredicate, 200)}";

            node.Warnings.Add(new PlanWarning
            {
                RuleNumber = 11,
                WarningType = "Scan With Predicate",
                Message = message,
                Severity = severity
            });
        }

    }

    private static void Rule32_CardinalityMisestimateScan(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 32: Cardinality misestimate on expensive scan — likely preventing index usage
        // When a scan dominates the plan AND the estimate is vastly higher than actual rows,
        // the optimizer chose a scan because it thought it needed most of the table.
        // With accurate estimates, it would likely seek instead.
        if (!cfg.IsRuleDisabled(32) && node.HasActualStats && IsRowstoreScan(node)
            && node.EstimateRows > 0 && node.ActualRows >= 0 && node.ActualRowsRead > 0)
        {
            var impact = BuildScanImpactDetails(node, stmt);
            var overestimateRatio = node.EstimateRows / Math.Max(1.0, node.ActualRows);
            var selectivity = (double)node.ActualRows / node.ActualRowsRead;

            // Fire when: scan is >= 50% of plan, estimate is >= 10x actual, and < 10% selectivity
            if ((impact.CostPct >= 50 || impact.ElapsedPct >= 50)
                && overestimateRatio >= 10.0
                && selectivity < 0.10)
            {
                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 32,
                    WarningType = "Scan Cardinality Misestimate",
                    Message = string.Create(CultureInfo.InvariantCulture, $"Estimated {node.EstimateRows:N0} rows but only {node.ActualRows:N0} returned ({selectivity * 100:N3}% of {node.ActualRowsRead:N0} rows read). ") +
                              string.Create(CultureInfo.InvariantCulture, $"The {overestimateRatio:N0}x overestimate likely caused the optimizer to choose a scan instead of a seek. ") +
                              $"An index on the predicate columns could dramatically reduce I/O.",
                    Severity = PlanWarningSeverity.Critical
                });
            }
        }

    }

    private static void Rule33_CeGuessDetection(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 33: Estimated plan CE guess detection — scans with telltale default selectivity
        // When the optimizer has no statistics to use (a local variable it can't sniff, a column with
        // no statistics, an expression), it falls back on fixed guesses: 30% for an inequality, 9%
        // for BETWEEN or LIKE, ~16.4% for two inequalities, 10% for comparing two columns, and an
        // equality guess that grows with the table. DetectCeGuess has the measured details and
        // which estimator (CE 70 or 120+) each one belongs to. On large tables, these guesses can
        // hide the need for an index.
        if (!cfg.IsRuleDisabled(33) && !node.HasActualStats && IsRowstoreScan(node)
            && node.TableCardinality >= CeGuessMinTableRows && node.EstimateRows > 0
            && !string.IsNullOrEmpty(node.Predicate))
        {
            var impact = BuildScanImpactDetails(node, stmt);
            if (impact.CostPct >= 50)
            {
                var guessDesc = DetectCeGuess(node.EstimateRows, node.TableCardinality, stmt.CardinalityEstimationModelVersion);
                if (guessDesc != null)
                {
                    node.Warnings.Add(new PlanWarning
                    {
                        RuleNumber = 33,
                        WarningType = "Estimated Plan CE Guess",
                        Message = string.Create(CultureInfo.InvariantCulture, $"Estimated {node.EstimateRows:N0} rows from {node.TableCardinality:N0} row table: {guessDesc}. ") +
                                  $"The optimizer may be using a default guess instead of accurate statistics. " +
                                  $"If actual selectivity is much lower, an index on the predicate columns could help significantly.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }
            }
        }

    }

    private static void Rule34_BareScanNarrowOutput(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 34: Bare scan with narrow output — NC index or columnstore candidate.
        // When a Clustered Index Scan or heap Table Scan reads the full table with no
        // predicate but only outputs a few columns, a narrower nonclustered index could
        // cover the query with far less I/O. For analytical workloads, columnstore may
        // be a better fit.
        var isBareScanCandidate = (node.PhysicalOp == "Clustered Index Scan" || node.PhysicalOp == "Table Scan")
            && !node.Lookup
            && string.IsNullOrEmpty(node.Predicate)
            && !string.IsNullOrEmpty(node.OutputColumns);
        if (!cfg.IsRuleDisabled(34) && isBareScanCandidate)
        {
            var colCount = node.OutputColumns!.Split(',').Length;

            /* A scan under the rows floor is a few hundred pages at most, and a narrower index cannot
               make it cheaper. An actual plan is judged on the rows it read and the scan's own share of
               the statement's time, with any own time at all standing in when the plan carries no
               statement time. An estimated plan is judged on the table's cardinality, when the plan
               carries it, and the scan's share of the plan's cost. */
            bool isSignificant;
            if (node.HasActualStats)
            {
                var ownShare = OwnTimeSharePct(node, stmt);
                isSignificant = Math.Max(node.ActualRowsRead, node.ActualRows) >= ScanRuleMinRows
                    && (ownShare.HasValue ? ownShare.Value >= ScanRuleMinOwnTimeSharePct : GetOperatorOwnElapsedMs(node) > 0);
            }
            else
            {
                isSignificant = (!node.HasTableCardinality || node.TableCardinality >= ScanRuleMinRows)
                    && node.CostPercent >= 20;
            }

            if (isSignificant)
            {
                var scanKind = node.PhysicalOp == "Clustered Index Scan"
                    ? "Clustered index scan"
                    : "Heap table scan";

                if (colCount <= 3)
                {
                    // Narrow output: a nonclustered rowstore index can cover this cheaply.
                    var indexAdvice = node.PhysicalOp == "Clustered Index Scan"
                        ? "Consider a nonclustered index on the output columns (as key or INCLUDE) so SQL Server can read a narrower structure."
                        : "Consider a clustered or nonclustered index on the output columns so SQL Server can read a narrower structure.";

                    node.Warnings.Add(new PlanWarning
                    {
                        RuleNumber = 34,
                        WarningType = "Bare Scan",
                        Message = $"{scanKind} reads the full table with no predicate, outputting {colCount} {(colCount == 1 ? "column" : "columns")}: {Truncate(node.OutputColumns, 200)}. {indexAdvice} For analytical workloads, a columnstore index may be a better fit.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }
                else
                {
                    // Wider output: rowstore NC index isn't a great fit (would have to
                    // carry too many columns), but columnstore doesn't care about column
                    // count. Suggest it for analytical / aggregate-style workloads.
                    node.Warnings.Add(new PlanWarning
                    {
                        RuleNumber = 34,
                        WarningType = "Bare Scan",
                        Message = $"{scanKind} reads the full table with no predicate, outputting {colCount} columns. A nonclustered rowstore index isn't a great fit for wide outputs, but if this is an analytical or aggregate-style query, a columnstore index (CCI or NCCI) can scan the same data far more cheaply: column count doesn't penalize columnstore the way it does rowstore indexes.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }
            }
        }

    }

    private static void Rule13_MismatchedDataTypes(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 13: Mismatched data types (GetRangeWithMismatchedTypes / GetRangeThroughConvert)
        if (!cfg.IsRuleDisabled(13) && node.PhysicalOp == "Compute Scalar" && !string.IsNullOrEmpty(node.DefinedValues))
        {
            var hasMismatch = node.DefinedValues.Contains("GetRangeWithMismatchedTypes", StringComparison.OrdinalIgnoreCase);
            var hasConvert = node.DefinedValues.Contains("GetRangeThroughConvert", StringComparison.OrdinalIgnoreCase);

            if (hasMismatch || hasConvert)
            {
                /* GetRangeWithMismatchedTypes and GetRangeThroughConvert are the functions the optimizer
                   uses to build a dynamic seek range through the conversion, so the index is still
                   sought. The cost of the mismatch is the wider range and the guessed estimate for it. */
                var reason = hasMismatch
                    ? "Mismatched data types between the column and the parameter/literal. The optimizer built a dynamic seek range through the conversion, so the index is still sought, but the range is wider than an exact match and the row estimate for it is a guess. Match your data types: don't pass nvarchar to a varchar column, or int to a bigint column."
                    : "CONVERT/CAST wrapping a column in the predicate. The optimizer built a dynamic seek range through the conversion, so the index is still sought, but the range is wider than an exact match and the row estimate for it is a guess. Match your data types: convert the parameter/literal instead of the column.";

                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 13,
                    WarningType = "Data Type Mismatch",
                    Message = reason,
                    Severity = PlanWarningSeverity.Warning
                });
            }
        }

    }

    private static void Rule14_LazyTableSpoolRebind(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 14: Lazy Table Spool unfavorable rebind/rewind ratio
        // Rebinds = cache misses (child re-executes), rewinds = cache hits (reuse cached result)
        // Exclude Lazy Index Spools: they cache by correlated parameter value (like a hash table)
        // so rebind/rewind counts are unreliable. See https://www.sql.kiwi/2025/02/lazy-index-spool/
        if (!cfg.IsRuleDisabled(14) && node.LogicalOp == "Lazy Spool"
            && !node.PhysicalOp.Contains("Index", StringComparison.OrdinalIgnoreCase))
        {
            var rebinds = node.HasActualStats ? (double)node.ActualRebinds : node.EstimateRebinds;
            var rewinds = node.HasActualStats ? (double)node.ActualRewinds : node.EstimateRewinds;
            var source = node.HasActualStats ? "actual" : "estimated";

            if (rebinds > 100 && rewinds < rebinds * 5)
            {
                var severity = rewinds < rebinds
                    ? PlanWarningSeverity.Critical
                    : PlanWarningSeverity.Warning;

                var ratio = rewinds > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{rewinds / rebinds:F1}x rewinds (cache hits) per rebind (cache miss)")
                    : "no rewinds (cache hits) at all";

                // An estimated plan carries fractional rewinds. The count is rounded once, the way N0 rounds (a
                // midpoint to the even neighbour), and the noun follows the number shown, so 1.3 reads "1 rewind"
                // and 1.5 "2 rewinds". Every number in the sentence is written in the invariant culture, so the
                // text reads the same on every desktop, in the CLI's output and in the MCP tools' output.
                var rewindsShown = (long)Math.Round(rewinds, MidpointRounding.ToEven);
                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 14,
                    WarningType = "Lazy Spool Ineffective",
                    Message = string.Create(CultureInfo.InvariantCulture, $"Lazy spool has low cache hit ratio ({source}): {rebinds:N0} rebinds (cache misses), {rewindsShown:N0} {(rewindsShown == 1 ? "rewind (cache hit)" : "rewinds (cache hits)")}, {ratio}. The spool is caching results but rarely reusing them, adding overhead for no benefit."),
                    Severity = severity
                });
            }
        }

    }

    private static void Rule15_JoinOrClause(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 15: Join OR clause
        // Pattern: Nested Loops → Merge Interval → TopN Sort → [Compute Scalar] → Concatenation → [Compute Scalar] → 2+ Constant Scans
        if (!cfg.IsRuleDisabled(15) && node.PhysicalOp == "Concatenation")
        {
            var constantScanBranches = node.Children
                .Where(c => c.PhysicalOp == "Constant Scan" ||
                            (c.PhysicalOp == "Compute Scalar" &&
                             c.Children.Any(gc => gc.PhysicalOp == "Constant Scan")))
                .ToList();

            /* #558: WHERE t.A IN (@p1, @p2) builds the same operator chain, as a dynamic seek over
               the parameter values, and there is no join to rewrite. Only a lookup that takes its
               value from another input's row makes the OR a join OR. */
            if (constantScanBranches.Count >= 2 && IsOrExpansionChain(node) &&
                constantScanBranches.Any(LookupReadsAnotherInput))
            {
                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 15,
                    WarningType = "Join OR Clause",
                    Message = $"OR in a join predicate. SQL Server rewrote the OR as {constantScanBranches.Count} separate lookups, each evaluated independently: this multiplies the work on the inner side. Rewrite as separate queries joined with UNION ALL. For example, change \"FROM a JOIN b ON a.x = b.x OR a.y = b.y\" to \"FROM a JOIN b ON a.x = b.x UNION ALL FROM a JOIN b ON a.y = b.y\".",
                    Severity = PlanWarningSeverity.Warning
                });
            }
        }

    }

    private static void Rule16_NestedLoopsHighExec(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 16: Nested Loops high inner-side execution count
        // Deep analysis: combine execution count + outer estimate mismatch + inner cost
        if (!cfg.IsRuleDisabled(16) && node.PhysicalOp == "Nested Loops" &&
            node.LogicalOp.Contains("Join", StringComparison.OrdinalIgnoreCase) &&
            !node.IsAdaptive &&
            node.Children.Count >= 2)
        {
            var outerChild = node.Children[0];
            var innerChild = node.Children[1];

            if (innerChild.HasActualStats && innerChild.ActualExecutions > 100000)
            {
                var dop = stmt.DegreeOfParallelism > 0 ? stmt.DegreeOfParallelism : 1;
                var details = new List<string>();

                // Core fact
                details.Add(string.Create(CultureInfo.InvariantCulture, $"Nested Loops inner side executed {innerChild.ActualExecutions:N0} times (DOP {dop})."));

                // Outer side estimate mismatch — explains WHY the optimizer chose NL.
                // #594: outerChild is this join's OUTER input, but if this Nested Loops is itself
                // nested inside an ancestor join's inner side, outerChild inherits that — walk the
                // whole ancestor chain (RowEstimateHelper) rather than assuming one execution.
                if (outerChild.HasActualStats && outerChild.EstimateRows > 0)
                {
                    var outerIsInnerSide = RowEstimateHelper.IsInnerSideOfNestedLoops(outerChild);
                    var outerRatio = RowEstimateHelper.GetRowAccuracyRatio(outerChild);
                    if (outerRatio >= 10.0)
                    {
                        var outerExecutions = RowEstimateHelper.GetActualExecutions(outerChild);
                        var outerActualDisplay = outerIsInnerSide && outerExecutions > 0
                            ? (double)outerChild.ActualRows / outerExecutions
                            : outerChild.ActualRows;
                        // The estimate is rounded once, the way N0 rounds, and the noun follows the number shown: a
                        // one-row estimate that fed a loop reads "estimated 1 row".
                        var outerEstimateShown = (long)Math.Round(outerChild.EstimateRows, MidpointRounding.ToEven);
                        details.Add(string.Create(CultureInfo.InvariantCulture, $"Outer side: estimated {outerEstimateShown:N0} {(outerEstimateShown == 1 ? "row" : "rows")}, actual {outerActualDisplay:N0} ({outerRatio:F0}x underestimate). The optimizer chose Nested Loops expecting far fewer iterations."));
                    }
                }

                // Inner side cost — reads and time spent doing the repeated work
                long innerReads = SumSubtreeReads(innerChild);
                if (innerReads > 0)
                    details.Add(string.Create(CultureInfo.InvariantCulture, $"Inner side total: {innerReads:N0} {(innerReads == 1 ? "logical read" : "logical reads")}."));

                if (innerChild.ActualElapsedMs > 0)
                {
                    var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;
                    if (stmtMs > 0)
                    {
                        var pct = (double)innerChild.ActualElapsedMs / stmtMs * 100;
                        details.Add(string.Create(CultureInfo.InvariantCulture, $"Inner side time: {innerChild.ActualElapsedMs:N0}ms ({pct:N0}% of statement)."));
                    }
                    else
                    {
                        details.Add(string.Create(CultureInfo.InvariantCulture, $"Inner side time: {innerChild.ActualElapsedMs:N0}ms."));
                    }
                }

                // Cause/recommendation
                var hasParams = stmt.Parameters.Count > 0;
                if (hasParams)
                    details.Add("This may be caused by parameter sniffing: the optimizer chose Nested Loops based on a sniffed value that produced far fewer outer rows.");
                else
                    details.Add("Consider whether a hash or merge join would be more appropriate for this row count.");

                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 16,
                    WarningType = "Nested Loops High Executions",
                    Message = string.Join(" ", details),
                    Severity = innerChild.ActualExecutions > 1000000
                        ? PlanWarningSeverity.Critical
                        : PlanWarningSeverity.Warning
                });
            }
            // Estimated plans: the optimizer knew the row count and chose Nested Loops
            // deliberately — don't second-guess it without actual execution data.
        }

    }

    private static void Rule17_ManyToManyMerge(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 17: Many-to-many Merge Join
        // In actual plans, the Merge Join operator reports logical reads when the worktable is used.
        // When ActualLogicalReads is 0, the worktable wasn't hit and the warning is noise.
        if (!cfg.IsRuleDisabled(17) && node.ManyToMany && node.PhysicalOp.Contains("Merge", StringComparison.OrdinalIgnoreCase) &&
            (!node.HasActualStats || node.ActualLogicalReads > 0))
        {
            node.Warnings.Add(new PlanWarning
            {
                RuleNumber = 17,
                WarningType = "Many-to-Many Merge Join",
                Message = node.HasActualStats
                    ? string.Create(CultureInfo.InvariantCulture, $"Many-to-many Merge Join: SQL Server created a worktable in TempDB ({node.ActualLogicalReads:N0} {(node.ActualLogicalReads == 1 ? "logical read" : "logical reads")}) because both sides have duplicate values in the join columns.")
                    : "Many-to-many Merge Join: SQL Server will create a worktable in TempDB because both sides have duplicate values in the join columns.",
                Severity = PlanWarningSeverity.Warning
            });
        }

    }

    private static void Rule23_TableValuedFunctions(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 23: Table-valued functions
        /* A function the engine supplies runs as the same operator: STRING_SPLIT, OPENJSON,
           GENERATE_SERIES, and every DMV and DMF (sys.dm_exec_requests is SYSREQUESTS,
           sys.dm_db_index_physical_stats is INDEXANALYSIS). Its Object names no database and no
           schema, and a function a user wrote always has both. The advice below is about code
           the user can rewrite, so the engine's own functions are skipped. */
        var isEngineFunction = string.IsNullOrEmpty(node.DatabaseName) && string.IsNullOrEmpty(node.SchemaName);
        if (!cfg.IsRuleDisabled(23) && node.LogicalOp == "Table-valued function" && !isEngineFunction)
        {
            var funcName = node.ObjectName ?? node.PhysicalOp;
            node.Warnings.Add(new PlanWarning
            {
                RuleNumber = 23,
                WarningType = "Table-Valued Function",
                Message = $"Table-valued function: {funcName}. Multi-statement TVFs have no statistics: SQL Server guesses 1 row (before SQL Server 2014) or 100 rows (SQL Server 2014 and later) regardless of actual size, unless interleaved execution (SQL Server 2017 and later) runs the function first and uses its real row count. Rewrite as an inline table-valued function if possible, or dump the function results into a #temp table and join to that instead.",
                Severity = PlanWarningSeverity.Warning
            });
        }

    }

    private static void Rule24_TopAboveScan(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 24: Top above a scan
        // Detects Top or Top N Sort operators feeding from a scan. This often means the
        // query is scanning the entire table/index and sorting just to return a few rows,
        // when an appropriate index could satisfy the request directly.
        if (!cfg.IsRuleDisabled(24))
        {
            var isTop = node.PhysicalOp == "Top";
            var isTopNSort = node.LogicalOp == "Top N Sort";

            if ((isTop || isTopNSort) && node.Children.Count > 0)
            {
                // Walk through pass-through operators below the Top to find the scan
                var scanCandidate = node.Children[0];
                while ((scanCandidate.PhysicalOp == "Compute Scalar" || scanCandidate.PhysicalOp == "Parallelism")
                    && scanCandidate.Children.Count > 0)
                    scanCandidate = scanCandidate.Children[0];

                /* A plain Top over an Ordered scan, reached through streaming operators only, is the
                   shape SELECT TOP (n) ... ORDER BY <index key> compiles to. The Top stops the scan
                   after n rows, so there is no scan and sort to index away. A Top N Sort reads the
                   whole scan before it returns a row, and an unordered scan carries no such
                   guarantee, so both keep the finding. */
                var topStopsScanEarly = isTop && scanCandidate.Ordered;

                if (IsScanOperator(scanCandidate) && !topStopsScanEarly)
                {
                    var topLabel = isTopNSort ? "Top N Sort" : "Top";
                    var onInner = node.Parent?.PhysicalOp == "Nested Loops" && node.Parent.Children.Count >= 2
                        && node.Parent.Children[1] == node;
                    var innerNote = onInner
                        ? $" This is on the inner side of Nested Loops (Node {node.Parent!.NodeId}), so the scan repeats for every outer row."
                        : "";
                    var predInfo = !string.IsNullOrEmpty(scanCandidate.Predicate)
                        ? " The scan has a residual predicate, so it may read many rows before the Top is satisfied."
                        : "";
                    node.Warnings.Add(new PlanWarning
                    {
                        RuleNumber = 24,
                        WarningType = "Top Above Scan",
                        Message = $"{topLabel} reads from {FormatNodeRef(scanCandidate)}.{innerNote}{predInfo} An index on the ORDER BY columns could eliminate the scan and sort entirely.",
                        Severity = onInner ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
                    });
                }
            }
        }

    }

    private static void Rule26_RowGoal(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 26: Row Goal (informational) — optimizer reduced estimate due to TOP/EXISTS/IN
        // Only surface on data access operators (seeks/scans) where the row goal actually matters
        var isDataAccess = node.PhysicalOp != null &&
            (node.PhysicalOp.Contains("Scan") || node.PhysicalOp.Contains("Seek"));
        if (!cfg.IsRuleDisabled(26) && isDataAccess &&
            node.EstimateRowsWithoutRowGoal > 0 && node.EstimateRows > 0 &&
            node.EstimateRowsWithoutRowGoal > node.EstimateRows)
        {
            var reduction = node.EstimateRowsWithoutRowGoal / node.EstimateRows;
            // Require at least a 2x reduction to be worth mentioning — "1 to 1" or
            // tiny floating-point differences that display identically are noise
            if (reduction >= 2.0)
            {
                // If we have actual stats, check whether the row goal prediction was correct.
                // When actual rows ≤ the row goal estimate, the optimizer stopped early as planned — benign.
                var rowGoalWorked = false;
                if (node.HasActualStats)
                {
                    // #594: compare like with like — see RowEstimateHelper. A scan or seek is
                    // routinely the inner side of a Nested Loops join (a key lookup, for one),
                    // where ActualExecutions is a real per-execution count; everywhere else it
                    // must not be treated as one.
                    rowGoalWorked = RowEstimateHelper.GetRowAccuracyRatio(node) <= 1.0;
                }

                if (!rowGoalWorked)
                {
                    // Try to identify the specific row goal cause from the statement text
                    var cause = IdentifyRowGoalCause(stmt.StatementText);

                    node.Warnings.Add(new PlanWarning
                    {
                        RuleNumber = 26,
                        WarningType = "Row Goal",
                        Message = string.Create(CultureInfo.InvariantCulture, $"Row goal active: estimate reduced from {node.EstimateRowsWithoutRowGoal:N0} to {node.EstimateRows:N0} ({reduction:N0}x reduction) due to {cause}. The optimizer chose this plan shape expecting to stop reading early. If the query reads all rows anyway, the plan choice may be suboptimal."),
                        Severity = PlanWarningSeverity.Info
                    });
                }
            }
        }

    }

    private static void Rule28_RowCountSpool(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 28: Row Count Spool — NOT IN with nullable column
        // Pattern: Row Count Spool with high rewinds, child scan has IS NULL predicate,
        // and statement text contains NOT IN
        if (!cfg.IsRuleDisabled(28) && node.PhysicalOp?.Contains("Row Count Spool") == true)
        {
            var rewinds = node.HasActualStats ? (double)node.ActualRewinds : node.EstimateRewinds;
            if (rewinds > 10000 && HasNotInPattern(node, stmt))
            {
                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 28,
                    WarningType = "NOT IN with Nullable Column",
                    Message = string.Create(CultureInfo.InvariantCulture, $"Row Count Spool with {rewinds:N0} rewinds. This pattern occurs when NOT IN is used with a nullable column: SQL Server cannot use an efficient Anti Semi Join because it must check for NULL values on every outer row. Rewrite as NOT EXISTS, or add WHERE column IS NOT NULL to the subquery."),
                    Severity = rewinds > 1_000_000 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
                });
            }
        }

    }

    private static void Rule29_ImplicitConversionSeek(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 29: Enhance implicit conversion warnings — Seek Plan is more severe
        // Skip for 0-execution nodes — the operator never ran
        if (!cfg.IsRuleDisabled(29) && !(node.HasActualStats && node.ActualExecutions == 0))
            foreach (var w in node.Warnings.ToList())
            {
                if (w.WarningType == "Implicit Conversion" && w.Message.StartsWith("Seek Plan"))
                {
                    w.Severity = PlanWarningSeverity.Critical;
                    w.Message = $"Implicit conversion prevented an index seek, forcing a scan instead. Fix the data type mismatch: ensure the parameter or variable type matches the column type exactly. {w.Message}";
                }
            }

    }

    // Rule 35 needs the statement itself to have run long enough that a 20% share
    // means something. Under this floor, a statement of a few ms is dominated by
    // one or two operators just because there's almost nothing else to divide the
    // time among, so the share points at nothing (#562). Same floor rule 19 uses
    // to fire on compile CPU, and the floor rule 4 uses to call UDF time Critical.
    private const int Rule35MinStatementElapsedMs = 1000;

    private static void Rule35_ExpensiveOperator(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 35: Expensive Operator — always show operators that take a significant
        // share of statement time even when no other rule has something to say. Joe
        // (#215 C8) wanted expensive scans that the tool had nothing to suggest on
        // to still surface as top items. Threshold: self-time >= 20% of statement
        // elapsed. Only emits if no other warning is already on the node to avoid
        // doubling up. The benefit % is just the self-time share.
        // Exchanges (Parallelism) are skipped: their self-time is mostly time spent waiting on the
        // operators that feed them and drain them, not work of their own. On a live plan an exchange
        // feeding a spilling sort showed 21 s of elapsed time on 2.4 s of CPU per thread, and was
        // named as the expensive operator while the sort beside it was the real problem. The text
        // report's "Expensive operators" list skips exchanges for the same reason.
        if (!cfg.IsRuleDisabled(35) && node.HasActualStats && node.Warnings.Count == 0
            && !NodeTimeAttribution.IsExchangeOperator(node)
            && stmt.QueryTimeStats != null && stmt.QueryTimeStats.ElapsedTimeMs >= Rule35MinStatementElapsedMs)
        {
            var selfMs = GetOperatorOwnElapsedMs(node);
            var pct = (double)selfMs / stmt.QueryTimeStats.ElapsedTimeMs * 100;
            if (pct >= 20.0)
            {
                node.Warnings.Add(new PlanWarning
                {
                    RuleNumber = 35,
                    WarningType = "Expensive Operator",
                    Message = string.Create(CultureInfo.InvariantCulture, $"{node.PhysicalOp} took {selfMs:N0}ms ({pct:N1}% of statement elapsed) but no specific rule identified a fix. Worth investigating: is the row volume necessary? Are upstream estimates driving this operator harder than it should be?"),
                    Severity = pct >= 50 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning,
                    MaxBenefitPercent = Math.Round(Math.Min(100.0, pct), 1)
                });
            }
        }
    }
}
