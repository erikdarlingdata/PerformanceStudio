using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class PlanAnalyzer
{
    private static void AnalyzeNodeTree(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        AnalyzeNode(node, stmt, cfg);

        foreach (var child in node.Children)
            AnalyzeNodeTree(child, stmt, cfg);
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
        Rule22_TableVariables(node, stmt, cfg);
        Rule23_TableValuedFunctions(node, stmt, cfg);
        Rule24_TopAboveScan(node, stmt, cfg);
        Rule26_RowGoal(node, stmt, cfg);
        Rule28_RowCountSpool(node, stmt, cfg);
        Rule29_ImplicitConversionSeek(node, stmt, cfg);
        Rule35_ExpensiveOperator(node, stmt, cfg);
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
            var message = "SQL Server is building a temporary index in TempDB at runtime because no suitable permanent index exists. This is expensive — it builds the index from scratch on every execution. Create a permanent index on the underlying table to eliminate this operator entirely.";
            if (!string.IsNullOrEmpty(node.SuggestedIndex))
                message += $"\n\nCreate this index:\n{node.SuggestedIndex}";

            node.Warnings.Add(new PlanWarning
            {
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
                WarningType = "UDF Execution",
                Message = $"Scalar UDF executing on this operator ({node.UdfElapsedTimeMs:N0}ms elapsed, {node.UdfCpuTimeMs:N0}ms CPU). Scalar UDFs run once per row and prevent parallelism. Options: rewrite as an inline table-valued function, assign the result to a variable if only one row is needed, dump results to a #temp table and apply the UDF to the final result set, or on SQL Server 2019+ check if the UDF is eligible for automatic scalar UDF inlining.",
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
        if (!cfg.IsRuleDisabled(5) && node.HasActualStats && node.EstimateRows > 0
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
                        WarningType = "Row Estimate Mismatch",
                        Message = $"Estimated {node.EstimateRows:N0} rows but actual 0 rows returned. SQL Server allocated resources for rows that never materialized.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }
            }
            else
            {
                // Compare per-execution actuals to estimates (SQL Server estimates are per-execution)
                var executions = node.ActualExecutions > 0 ? node.ActualExecutions : 1;
                var actualPerExec = (double)node.ActualRows / executions;
                var ratio = actualPerExec / node.EstimateRows;
                if (ratio >= 10.0 || ratio <= 0.1)
                {
                    var harm = AssessEstimateHarm(node, ratio);
                    if (harm != null)
                    {
                        var direction = ratio >= 10.0 ? "underestimated" : "overestimated";
                        var factor = ratio >= 10.0 ? ratio : 1.0 / ratio;
                        var actualDisplay = executions > 1
                            ? $"Actual {node.ActualRows:N0} ({actualPerExec:N0} rows x {executions:N0} executions)"
                            : $"Actual {node.ActualRows:N0}";
                        node.Warnings.Add(new PlanWarning
                        {
                            WarningType = "Row Estimate Mismatch",
                            Message = $"Estimated {node.EstimateRows:N0} vs {actualDisplay} — {factor:F0}x {direction}. {harm}",
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
        var serialPlanCoversUdf = stmt.NonParallelPlanReason is
            "TSQLUserDefinedFunctionsNotParallelizable"
            or "CLRUserDefinedFunctionRequiresDataAccess"
            or "CouldNotGenerateValidParallelPlan";
        if (!cfg.IsRuleDisabled(6) && !serialPlanCoversUdf)
        foreach (var udf in node.ScalarUdfs)
        {
            var type = udf.IsClrFunction ? "CLR" : "T-SQL";
            node.Warnings.Add(new PlanWarning
            {
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
                        w.Message += $" Operator time: {operatorMs:N0}ms ({pct * 100:N0}% of statement).";
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
                    w.Message += $" Operator time: {operatorMs:N0}ms ({pct * 100:N0}% of statement).";

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
                    var message = $"Thread {maxThread.ThreadId} processed {skewRatio * 100:N0}% of rows ({maxThread.ActualRows:N0}/{totalRows:N0}). Work is heavily skewed to one thread, so parallelism isn't helping much.";
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
            var message = "RID Lookup — this table is a heap (no clustered index). SQL Server found rows via a nonclustered index but had to follow row identifiers back to unordered heap pages. Heap lookups are more expensive than key lookups because pages are not sorted and may have forwarding pointers. Add a clustered index to the table.";
            if (!string.IsNullOrEmpty(node.Predicate))
                message += $" Predicate: {Truncate(node.Predicate, 200)}";

            node.Warnings.Add(new PlanWarning
            {
                WarningType = "RID Lookup",
                Message = message,
                Severity = PlanWarningSeverity.Warning
            });
        }
        else if (!cfg.IsRuleDisabled(10) && node.Lookup)
        {
            var lookupMsg = "Key Lookup — SQL Server found rows via a nonclustered index but had to go back to the clustered index for additional columns.";

            // Show what columns the lookup is fetching
            if (!string.IsNullOrEmpty(node.OutputColumns))
                lookupMsg += $"\nColumns fetched: {Truncate(node.OutputColumns, 200)}";

            // Only call out the predicate if it actually filters rows
            if (!string.IsNullOrEmpty(node.Predicate))
            {
                var predicateFilters = node.HasActualStats && node.ActualExecutions > 0
                    && node.ActualRows < node.ActualExecutions;
                if (predicateFilters)
                    lookupMsg += $"\nResidual predicate (filtered {node.ActualExecutions - node.ActualRows:N0} rows): {Truncate(node.Predicate, 200)}";
            }

            lookupMsg += "\nTo eliminate the lookup, consider adding the needed columns as INCLUDE columns on the nonclustered index. This widens the index, so weigh the read benefit against write and storage overhead.";

            node.Warnings.Add(new PlanWarning
            {
                WarningType = "Key Lookup",
                Message = lookupMsg,
                Severity = PlanWarningSeverity.Critical
            });
        }

    }

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
                    "Leading wildcard LIKE prevents an index seek — SQL Server must scan every row. If substring search performance is critical, consider a full-text index or a trigram-based approach.",
                "CASE expression in predicate" =>
                    "CASE expression in a predicate prevents an index seek. Rewrite using separate WHERE clauses combined with OR, or split into multiple queries.",
                _ when nonSargableReason.StartsWith("Function call") =>
                    $"{nonSargableReason} prevents an index seek. Remove the function from the column side — apply it to the parameter instead, or create a computed column with the expression and index that.",
                _ =>
                    $"{nonSargableReason} prevents an index seek, forcing a scan."
            };

            node.Warnings.Add(new PlanWarning
            {
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
            !IsProbeOnly(node.Predicate) && !(node.HasActualStats && node.ActualExecutions == 0))
        {
            var displayPredicate = StripProbeExpressions(node.Predicate);
            var details = BuildScanImpactDetails(node, stmt);
            var severity = PlanWarningSeverity.Warning;

            // Elevate to Critical if the scan dominates the plan
            if (details.CostPct >= 90 || details.ElapsedPct >= 90)
                severity = PlanWarningSeverity.Critical;

            var message = "Scan with residual predicate — SQL Server is reading every row and filtering after the fact.";
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
            if (HasSignificantIoWaits(stmt.WaitStats) && details.CostPct >= 50
                && severity != PlanWarningSeverity.Critical)
                severity = PlanWarningSeverity.Critical;

            message += $"\nPredicate: {Truncate(displayPredicate, 200)}";

            node.Warnings.Add(new PlanWarning
            {
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
                    WarningType = "Scan Cardinality Misestimate",
                    Message = $"Estimated {node.EstimateRows:N0} rows but only {node.ActualRows:N0} returned ({selectivity * 100:N3}% of {node.ActualRowsRead:N0} rows read). " +
                              $"The {overestimateRatio:N0}x overestimate likely caused the optimizer to choose a scan instead of a seek. " +
                              $"An index on the predicate columns could dramatically reduce I/O.",
                    Severity = PlanWarningSeverity.Critical
                });
            }
        }

    }

    private static void Rule33_CeGuessDetection(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 33: Estimated plan CE guess detection — scans with telltale default selectivity
        // When the optimizer uses a local variable or can't sniff, it falls back to density-based
        // guesses: 30% (equality), 10% (inequality), 9% (LIKE/between), ~16.43% (sqrt(30%)),
        // 1% (multi-inequality). On large tables, these guesses can hide the need for an index.
        if (!cfg.IsRuleDisabled(33) && !node.HasActualStats && IsRowstoreScan(node)
            && node.TableCardinality >= 100_000 && node.EstimateRows > 0
            && !string.IsNullOrEmpty(node.Predicate))
        {
            var impact = BuildScanImpactDetails(node, stmt);
            if (impact.CostPct >= 50)
            {
                var guessDesc = DetectCeGuess(node.EstimateRows, node.TableCardinality);
                if (guessDesc != null)
                {
                    node.Warnings.Add(new PlanWarning
                    {
                        WarningType = "Estimated Plan CE Guess",
                        Message = $"Estimated {node.EstimateRows:N0} rows from {node.TableCardinality:N0} row table — {guessDesc}. " +
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
            var isSignificant = node.HasActualStats
                ? GetOperatorOwnElapsedMs(node) > 0
                : node.CostPercent >= 20;

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
                        WarningType = "Bare Scan",
                        Message = $"{scanKind} reads the full table with no predicate, outputting {colCount} column(s): {Truncate(node.OutputColumns, 200)}. {indexAdvice} For analytical workloads, a columnstore index may be a better fit.",
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
                        WarningType = "Bare Scan",
                        Message = $"{scanKind} reads the full table with no predicate, outputting {colCount} columns. A nonclustered rowstore index isn't a great fit for wide outputs, but if this is an analytical or aggregate-style query, a columnstore index (CCI or NCCI) can scan the same data far more cheaply — column count doesn't penalize columnstore the way it does rowstore indexes.",
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
                var reason = hasMismatch
                    ? "Mismatched data types between the column and the parameter/literal. SQL Server is converting every row to compare, preventing index seeks. Match your data types — don't pass nvarchar to a varchar column, or int to a bigint column."
                    : "CONVERT/CAST wrapping a column in the predicate. SQL Server is converting every row to compare, preventing index seeks. Match your data types — convert the parameter/literal instead of the column.";

                node.Warnings.Add(new PlanWarning
                {
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
                    ? $"{rewinds / rebinds:F1}x rewinds (cache hits) per rebind (cache miss)"
                    : "no rewinds (cache hits) at all";

                node.Warnings.Add(new PlanWarning
                {
                    WarningType = "Lazy Spool Ineffective",
                    Message = $"Lazy spool has low cache hit ratio ({source}): {rebinds:N0} rebinds (cache misses), {rewinds:N0} rewinds (cache hits) — {ratio}. The spool is caching results but rarely reusing them, adding overhead for no benefit.",
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
                .Count(c => c.PhysicalOp == "Constant Scan" ||
                            (c.PhysicalOp == "Compute Scalar" &&
                             c.Children.Any(gc => gc.PhysicalOp == "Constant Scan")));

            if (constantScanBranches >= 2 && IsOrExpansionChain(node))
            {
                node.Warnings.Add(new PlanWarning
                {
                    WarningType = "Join OR Clause",
                    Message = $"OR in a join predicate. SQL Server rewrote the OR as {constantScanBranches} separate lookups, each evaluated independently — this multiplies the work on the inner side. Rewrite as separate queries joined with UNION ALL. For example, change \"FROM a JOIN b ON a.x = b.x OR a.y = b.y\" to \"FROM a JOIN b ON a.x = b.x UNION ALL FROM a JOIN b ON a.y = b.y\".",
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
                details.Add($"Nested Loops inner side executed {innerChild.ActualExecutions:N0} times (DOP {dop}).");

                // Outer side estimate mismatch — explains WHY the optimizer chose NL
                if (outerChild.HasActualStats && outerChild.EstimateRows > 0)
                {
                    var outerExecs = outerChild.ActualExecutions > 0 ? outerChild.ActualExecutions : 1;
                    var outerActualPerExec = (double)outerChild.ActualRows / outerExecs;
                    var outerRatio = outerActualPerExec / outerChild.EstimateRows;
                    if (outerRatio >= 10.0)
                    {
                        details.Add($"Outer side: estimated {outerChild.EstimateRows:N0} rows, actual {outerActualPerExec:N0} ({outerRatio:F0}x underestimate). The optimizer chose Nested Loops expecting far fewer iterations.");
                    }
                }

                // Inner side cost — reads and time spent doing the repeated work
                long innerReads = SumSubtreeReads(innerChild);
                if (innerReads > 0)
                    details.Add($"Inner side total: {innerReads:N0} logical reads.");

                if (innerChild.ActualElapsedMs > 0)
                {
                    var stmtMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0;
                    if (stmtMs > 0)
                    {
                        var pct = (double)innerChild.ActualElapsedMs / stmtMs * 100;
                        details.Add($"Inner side time: {innerChild.ActualElapsedMs:N0}ms ({pct:N0}% of statement).");
                    }
                    else
                    {
                        details.Add($"Inner side time: {innerChild.ActualElapsedMs:N0}ms.");
                    }
                }

                // Cause/recommendation
                var hasParams = stmt.Parameters.Count > 0;
                if (hasParams)
                    details.Add("This may be caused by parameter sniffing — the optimizer chose Nested Loops based on a sniffed value that produced far fewer outer rows.");
                else
                    details.Add("Consider whether a hash or merge join would be more appropriate for this row count.");

                node.Warnings.Add(new PlanWarning
                {
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
                WarningType = "Many-to-Many Merge Join",
                Message = node.HasActualStats
                    ? $"Many-to-many Merge Join — SQL Server created a worktable in TempDB ({node.ActualLogicalReads:N0} logical reads) because both sides have duplicate values in the join columns."
                    : "Many-to-many Merge Join — SQL Server will create a worktable in TempDB because both sides have duplicate values in the join columns.",
                Severity = PlanWarningSeverity.Warning
            });
        }

    }

    private static void Rule22_TableVariables(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 22: Table variables (Object name starts with @)
        if (!cfg.IsRuleDisabled(22) && !string.IsNullOrEmpty(node.ObjectName) &&
            node.ObjectName.StartsWith("@"))
        {
            var isModificationOp = node.PhysicalOp.Contains("Insert", StringComparison.OrdinalIgnoreCase)
                || node.PhysicalOp.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || node.PhysicalOp.Contains("Delete", StringComparison.OrdinalIgnoreCase);

            node.Warnings.Add(new PlanWarning
            {
                WarningType = "Table Variable",
                Message = isModificationOp
                    ? "Modifying a table variable forces the entire plan to run single-threaded. Replace with a #temp table to allow parallel execution."
                    : "Table variable detected. Table variables lack column-level statistics, which causes bad row estimates, join choices, and memory grant decisions. Replace with a #temp table.",
                Severity = isModificationOp ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
            });
        }

    }

    private static void Rule23_TableValuedFunctions(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 23: Table-valued functions
        if (!cfg.IsRuleDisabled(23) && node.LogicalOp == "Table-valued function")
        {
            var funcName = node.ObjectName ?? node.PhysicalOp;
            node.Warnings.Add(new PlanWarning
            {
                WarningType = "Table-Valued Function",
                Message = $"Table-valued function: {funcName}. Multi-statement TVFs have no statistics — SQL Server guesses 1 row (pre-2017) or 100 rows (2017+) regardless of actual size. Rewrite as an inline table-valued function if possible, or dump the function results into a #temp table and join to that instead.",
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

                if (IsScanOperator(scanCandidate))
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
                    var executions = node.ActualExecutions > 0 ? node.ActualExecutions : 1;
                    var actualPerExec = (double)node.ActualRows / executions;
                    rowGoalWorked = actualPerExec <= node.EstimateRows;
                }

                if (!rowGoalWorked)
                {
                    // Try to identify the specific row goal cause from the statement text
                    var cause = IdentifyRowGoalCause(stmt.StatementText);

                    node.Warnings.Add(new PlanWarning
                    {
                        WarningType = "Row Goal",
                        Message = $"Row goal active: estimate reduced from {node.EstimateRowsWithoutRowGoal:N0} to {node.EstimateRows:N0} ({reduction:N0}x reduction) due to {cause}. The optimizer chose this plan shape expecting to stop reading early. If the query reads all rows anyway, the plan choice may be suboptimal.",
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
                    WarningType = "NOT IN with Nullable Column",
                    Message = $"Row Count Spool with {rewinds:N0} rewinds. This pattern occurs when NOT IN is used with a nullable column — SQL Server cannot use an efficient Anti Semi Join because it must check for NULL values on every outer row. Rewrite as NOT EXISTS, or add WHERE column IS NOT NULL to the subquery.",
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

    private static void Rule35_ExpensiveOperator(PlanNode node, PlanStatement stmt, AnalyzerConfig cfg)
    {
        // Rule 35: Expensive Operator — always show operators that take a significant
        // share of statement time even when no other rule has something to say. Joe
        // (#215 C8) wanted expensive scans that the tool had nothing to suggest on
        // to still surface as top items. Threshold: self-time >= 20% of statement
        // elapsed. Only emits if no other warning is already on the node to avoid
        // doubling up. The benefit % is just the self-time share.
        if (!cfg.IsRuleDisabled(35) && node.HasActualStats && node.Warnings.Count == 0
            && stmt.QueryTimeStats != null && stmt.QueryTimeStats.ElapsedTimeMs > 0)
        {
            var selfMs = GetOperatorOwnElapsedMs(node);
            var pct = (double)selfMs / stmt.QueryTimeStats.ElapsedTimeMs * 100;
            if (pct >= 20.0)
            {
                node.Warnings.Add(new PlanWarning
                {
                    WarningType = "Expensive Operator",
                    Message = $"{node.PhysicalOp} took {selfMs:N0}ms ({pct:N1}% of statement elapsed) but no specific rule identified a fix. Worth investigating: is the row volume necessary? Are upstream estimates driving this operator harder than it should be?",
                    Severity = pct >= 50 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning,
                    MaxBenefitPercent = Math.Round(Math.Min(100.0, pct), 1)
                });
            }
        }
    }
}
