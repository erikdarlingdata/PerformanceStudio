using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

public static partial class PlanAnalyzer
{
    private static void AnalyzeStatement(PlanStatement stmt, AnalyzerConfig cfg, ServerMetadata? serverMetadata = null)
    {
        // Rule 3: Serial plan with reason
        // Skip: cost < 1 (CTFP is an integer so cost < 1 can never go parallel),
        // TRIVIAL optimization (can't go parallel anyway),
        // and 0ms actual elapsed time (not worth flagging).
        if (!cfg.IsRuleDisabled(3) && !string.IsNullOrEmpty(stmt.NonParallelPlanReason)
            && stmt.StatementSubTreeCost >= 1.0
            && stmt.StatementOptmLevel != "TRIVIAL"
            && !(stmt.QueryTimeStats != null && stmt.QueryTimeStats.ElapsedTimeMs == 0))
        {
            var reason = stmt.NonParallelPlanReason switch
            {
                // User/config forced serial
                "MaxDOPSetToOne" => "MAXDOP is set to 1",
                "QueryHintNoParallelSet" => "OPTION (MAXDOP 1) hint forces serial execution",
                "ParallelismDisabledByTraceFlag" => "Parallelism disabled by trace flag",

                // Passive — optimizer chose serial, nothing wrong
                "EstimatedDOPIsOne" => "Estimated DOP is 1 (the plan's estimated cost was below the cost threshold for parallelism)",

                // Edition/environment limitations
                "NoParallelPlansInDesktopOrExpressEdition" => "Express/Desktop edition does not support parallelism",
                "NoParallelCreateIndexInNonEnterpriseEdition" => "Parallel index creation requires Enterprise edition",
                "NoParallelPlansDuringUpgrade" => "Parallel plans disabled during upgrade",
                "NoParallelForPDWCompilation" => "Parallel plans not supported for PDW compilation",
                "NoParallelForCloudDBReplication" => "Parallel plans not supported during cloud DB replication",

                // Query constructs that block parallelism (actionable)
                "CouldNotGenerateValidParallelPlan" => "Optimizer could not generate a valid parallel plan. Common causes: scalar UDFs, inserts into table variables, certain system functions, or OPTION (MAXDOP 1) hints",
                "TSQLUserDefinedFunctionsNotParallelizable" => "T-SQL scalar UDF prevents parallelism. Rewrite as an inline table-valued function, or on SQL Server 2019+ check if the UDF is eligible for automatic inlining",
                "CLRUserDefinedFunctionRequiresDataAccess" => "CLR UDF with data access prevents parallelism",
                "NonParallelizableIntrinsicFunction" => "Non-parallelizable intrinsic function in the query",
                "TableVariableTransactionsDoNotSupportParallelNestedTransaction" => "Table variable transaction prevents parallelism. Consider using a #temp table instead",
                "UpdatingWritebackVariable" => "Updating a writeback variable prevents parallelism",
                "DMLQueryReturnsOutputToClient" => "DML with OUTPUT clause returning results to client prevents parallelism",
                "MixedSerialAndParallelOnlineIndexBuildNotSupported" => "Mixed serial/parallel online index build not supported",
                "NoRangesResumableCreate" => "Resumable index create cannot use parallelism for this operation",

                // Cursor limitations
                "NoParallelCursorFetchByBookmark" => "Cursor fetch by bookmark cannot use parallelism",
                "NoParallelDynamicCursor" => "Dynamic cursors cannot use parallelism",
                "NoParallelFastForwardCursor" => "Fast-forward cursors cannot use parallelism",

                // Memory-optimized / natively compiled
                "NoParallelForMemoryOptimizedTables" => "Memory-optimized tables do not support parallel plans",
                "NoParallelForDmlOnMemoryOptimizedTable" => "DML on memory-optimized tables cannot use parallelism",
                "NoParallelForNativelyCompiledModule" => "Natively compiled modules do not support parallelism",

                // Remote queries
                "NoParallelWithRemoteQuery" => "Remote queries cannot use parallelism",
                "NoRemoteParallelismForMatrix" => "Remote parallelism not available for this query shape",

                _ => stmt.NonParallelPlanReason
            };

            // Actionable: user forced serial, or something in the query blocks parallelism
            // that could potentially be rewritten. Info: passive (cost too low) or
            // environmental (edition, upgrade, cursor type, memory-optimized).
            var isActionable = stmt.NonParallelPlanReason is
                "MaxDOPSetToOne" or "QueryHintNoParallelSet" or "ParallelismDisabledByTraceFlag"
                or "CouldNotGenerateValidParallelPlan"
                or "TSQLUserDefinedFunctionsNotParallelizable"
                or "CLRUserDefinedFunctionRequiresDataAccess"
                or "NonParallelizableIntrinsicFunction"
                or "TableVariableTransactionsDoNotSupportParallelNestedTransaction"
                or "UpdatingWritebackVariable"
                or "DMLQueryReturnsOutputToClient"
                or "NoParallelCursorFetchByBookmark"
                or "NoParallelDynamicCursor"
                or "NoParallelFastForwardCursor"
                or "NoParallelWithRemoteQuery"
                or "NoRemoteParallelismForMatrix";

            // MaxDOPSetToOne needs special handling: check whether the user explicitly
            // set MAXDOP 1 in the query text, or if it's a server/db/RG setting.
            // SQL Server truncates StatementText at ~4,000 characters in plan XML.
            if (stmt.NonParallelPlanReason == "MaxDOPSetToOne")
            {
                var text = stmt.StatementText ?? "";
                var hasMaxdop1InText = Regex.IsMatch(text, @"MAXDOP\s+1\b", RegexOptions.IgnoreCase);
                var isTruncated = text.Length >= 3990;

                if (hasMaxdop1InText)
                {
                    // User explicitly set MAXDOP 1 in the query — warn
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Serial Plan",
                        Message = "Query running serially: MAXDOP is set to 1 using a query hint.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }
                else if (isTruncated)
                {
                    // Query text was truncated — can't tell if MAXDOP 1 is in the query
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Serial Plan",
                        Message = $"Query running serially: {reason}. MAXDOP 1 may be set at the server, database, resource governor, or query level (query text was truncated).",
                        Severity = PlanWarningSeverity.Info
                    });
                }
                // else: not truncated, no MAXDOP 1 in text — server/db/RG setting, suppress entirely
            }
            else
            {
                stmt.PlanWarnings.Add(new PlanWarning
                {
                    WarningType = "Serial Plan",
                    Message = $"Query running serially: {reason}.",
                    Severity = isActionable ? PlanWarningSeverity.Warning : PlanWarningSeverity.Info
                });
            }
        }

        // Rule 9: Memory grant issues (statement-level)
        if (!cfg.IsRuleDisabled(9) && stmt.MemoryGrant != null)
        {
            var grant = stmt.MemoryGrant;

            // Excessive grant — granted far more than actually used
            if (grant.GrantedMemoryKB > 0 && grant.MaxUsedMemoryKB > 0)
            {
                var wasteRatio = (double)grant.GrantedMemoryKB / grant.MaxUsedMemoryKB;
                if (wasteRatio >= 10 && grant.GrantedMemoryKB >= 1048576)
                {
                    var grantMB = grant.GrantedMemoryKB / 1024.0;
                    var usedMB = grant.MaxUsedMemoryKB / 1024.0;
                    var message = $"Granted {grantMB:N0} MB but only used {usedMB:N0} MB ({wasteRatio:F0}x overestimate). The unused memory is reserved and unavailable to other queries.";

                    // Note adaptive joins that chose Nested Loops at runtime — the grant
                    // was sized for a hash join that never happened.
                    if (stmt.RootNode != null && HasAdaptiveJoinChoseNestedLoop(stmt.RootNode))
                        message += " An adaptive join in this plan executed as a Nested Loop at runtime — the memory grant was sized for the hash join alternative that wasn't used.";

                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Excessive Memory Grant",
                        Message = message,
                        Severity = PlanWarningSeverity.Warning
                    });
                }
            }

            // Grant wait — query had to wait for memory
            if (grant.GrantWaitTimeMs > 0)
            {
                stmt.PlanWarnings.Add(new PlanWarning
                {
                    WarningType = "Memory Grant Wait",
                    Message = $"Query waited {grant.GrantWaitTimeMs:N0}ms for a memory grant before it could start running. Other queries were using all available workspace memory.",
                    Severity = grant.GrantWaitTimeMs >= 5000 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
                });
            }

            // Large memory grant with top consumers
            if (grant.GrantedMemoryKB >= 1048576 && stmt.RootNode != null)
            {
                var consumers = new List<string>();
                FindMemoryConsumers(stmt.RootNode, consumers);

                var grantMB = grant.GrantedMemoryKB / 1024.0;
                var guidance = "";
                if (consumers.Count > 0)
                {
                    // Show only the top 3 consumers — listing 20+ is noise
                    var shown = consumers.Take(3);
                    var remaining = consumers.Count - 3;
                    guidance = $" Largest consumers: {string.Join(", ", shown)}";
                    if (remaining > 0)
                        guidance += $", and {remaining} more";
                    guidance += ".";
                }

                stmt.PlanWarnings.Add(new PlanWarning
                {
                    WarningType = "Large Memory Grant",
                    Message = $"Query granted {grantMB:F0} MB of memory.{guidance}",
                    Severity = grantMB >= 4096 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
                });
            }
        }

        // Rule 18: Compile memory exceeded (early abort)
        if (!cfg.IsRuleDisabled(18) && stmt.StatementOptmEarlyAbortReason == "MemoryLimitExceeded")
        {
            stmt.PlanWarnings.Add(new PlanWarning
            {
                WarningType = "Compile Memory Exceeded",
                Message = "Optimization was aborted early because the compile memory limit was exceeded. The plan is likely suboptimal. Simplify the query by breaking it into smaller steps using #temp tables.",
                Severity = PlanWarningSeverity.Critical
            });
        }

        // Rule 19: High compile CPU
        if (!cfg.IsRuleDisabled(19) && stmt.CompileCPUMs >= 1000)
        {
            stmt.PlanWarnings.Add(new PlanWarning
            {
                WarningType = "High Compile CPU",
                Message = $"Query took {stmt.CompileCPUMs:N0}ms of CPU just to compile a plan (before any data was read). Simplify the query by breaking it into smaller steps using #temp tables.",
                Severity = stmt.CompileCPUMs >= 5000 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
            });
        }

        // Rule 4 (statement-level): UDF execution timing from QueryTimeStats
        // Some plans report UDF timing only at the statement level, not per-node.
        if (!cfg.IsRuleDisabled(4) && (stmt.QueryUdfCpuTimeMs > 0 || stmt.QueryUdfElapsedTimeMs > 0))
        {
            stmt.PlanWarnings.Add(new PlanWarning
            {
                WarningType = "UDF Execution",
                Message = $"Scalar UDF cost in this statement: {stmt.QueryUdfElapsedTimeMs:N0}ms elapsed, {stmt.QueryUdfCpuTimeMs:N0}ms CPU. Scalar UDFs run once per row and prevent parallelism. Options: rewrite as an inline table-valued function, assign the result to a variable if only one row is needed, dump results to a #temp table and apply the UDF to the final result set, or on SQL Server 2019+ check if the UDF is eligible for automatic scalar UDF inlining.",
                Severity = stmt.QueryUdfElapsedTimeMs >= 1000 ? PlanWarningSeverity.Critical : PlanWarningSeverity.Warning
            });
        }

        // Rule 20: Local variables without RECOMPILE
        // Parameters with no CompiledValue are likely local variables — the optimizer
        // cannot sniff their values and uses density-based ("unknown") estimates.
        // Skip statements with cost < 1 (can't go parallel, estimate quality rarely matters).
        if (!cfg.IsRuleDisabled(20) && stmt.Parameters.Count > 0 && stmt.StatementSubTreeCost >= 1.0)
        {
            var unsnifffedParams = stmt.Parameters
                .Where(p => string.IsNullOrEmpty(p.CompiledValue))
                .ToList();

            if (unsnifffedParams.Count > 0)
            {
                var hasRecompile = stmt.StatementText?.Contains("RECOMPILE", StringComparison.OrdinalIgnoreCase) == true;
                if (!hasRecompile)
                {
                    var names = string.Join(", ", unsnifffedParams.Select(p => p.Name));
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Local Variables",
                        Message = $"Local variables detected: {names}. SQL Server cannot sniff local variable values at compile time, so it uses average density estimates instead of your actual values. Test with OPTION (RECOMPILE) to see if the plan improves. For a permanent fix, use dynamic SQL or a stored procedure to pass the values as parameters instead of local variables.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }
            }
        }

        // Rule 21 (CTE referenced multiple times) removed per Joe's #215 feedback:
        // for actual plans, SQL Server runtime stats show exactly where time was
        // spent, so a statement-text-pattern warning about CTE reuse is guessing.

        // Rule 27: OPTIMIZE FOR UNKNOWN in statement text
        if (!cfg.IsRuleDisabled(27) && !string.IsNullOrEmpty(stmt.StatementText) &&
            Regex.IsMatch(stmt.StatementText, @"OPTIMIZE\s+FOR\s+UNKNOWN", RegexOptions.IgnoreCase))
        {
            stmt.PlanWarnings.Add(new PlanWarning
            {
                WarningType = "Optimize For Unknown",
                Message = "OPTIMIZE FOR UNKNOWN uses average density estimates instead of sniffed parameter values. This can help when parameter sniffing causes plan instability, but may produce suboptimal plans for skewed data distributions.",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Rule 36: Dynamic cursor (#215 E1). Dynamic cursors can prevent index usage
        // because they must tolerate underlying data changes between fetches, forcing
        // scans and extra work per fetch. Switching to FAST_FORWARD, STATIC, or KEYSET
        // often delivers a dramatic improvement.
        if (!cfg.IsRuleDisabled(36)
            && string.Equals(stmt.CursorActualType, "Dynamic", StringComparison.OrdinalIgnoreCase))
        {
            var cursorLabel = string.IsNullOrEmpty(stmt.CursorName) ? "Cursor" : $"Cursor \"{stmt.CursorName}\"";
            stmt.PlanWarnings.Add(new PlanWarning
            {
                WarningType = "Dynamic Cursor",
                Message = $"{cursorLabel} is a dynamic cursor. Dynamic cursors tolerate underlying data changes between fetches, which prevents many index uses and forces extra work per fetch. If you don't need that semantic, switching to FAST_FORWARD (or STATIC / KEYSET, depending on requirements) typically gives a large performance improvement.",
                Severity = PlanWarningSeverity.Warning
            });
        }

        // Rule 37: CURSOR declaration without LOCAL (#215 E3). Default cursor scope
        // is GLOBAL in SQL Server, which puts cursors in a shared namespace and can
        // bloat the plan cache (Erik's writeup:
        // https://erikdarling.com/cursor-declarations-that-use-openjson-can-bloat-your-plan-cache/).
        if (!cfg.IsRuleDisabled(37) && !string.IsNullOrEmpty(stmt.StatementText))
        {
            // DECLARE <name> [INSENSITIVE|SCROLL] CURSOR [qualifier(s)] FOR ...
            // In the T-SQL extended syntax, LOCAL/GLOBAL appear AFTER the CURSOR
            // keyword (only INSENSITIVE/SCROLL are legal before it), so the LOCAL
            // qualifier must be looked for between CURSOR and the FOR that introduces
            // the SELECT. Capturing tokens *before* CURSOR never sees LOCAL and would
            // fire on every cursor, including ones already declared LOCAL.
            var cursorDeclMatch = Regex.Match(
                stmt.StatementText,
                @"\bDECLARE\s+\w+\s+(?:INSENSITIVE\s+|SCROLL\s+)*CURSOR\b(.*?)\bFOR\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (cursorDeclMatch.Success)
            {
                var qualifiers = cursorDeclMatch.Groups[1].Value;
                if (!Regex.IsMatch(qualifiers, @"\bLOCAL\b", RegexOptions.IgnoreCase))
                {
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Cursor Missing LOCAL",
                        Message = "CURSOR declaration is missing the LOCAL keyword. Default cursor scope is GLOBAL, which puts the cursor in a shared namespace and can bloat the plan cache (see https://erikdarling.com/cursor-declarations-that-use-openjson-can-bloat-your-plan-cache/). Adding LOCAL is cheap and usually right.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }
            }
        }

        // Rule 38: Standard Edition DOP 2 limitation with batch mode
        // SQL Server Standard Edition limits DOP to 2 when batch mode operators are present.
        if (!cfg.IsRuleDisabled(38) && stmt.DegreeOfParallelism == 2 && stmt.RootNode != null
            && HasBatchModeNode(stmt.RootNode))
        {
            // Suppress when the user explicitly set MAXDOP 2 as a query hint — the DOP
            // cap is intentional, not the Standard Edition batch-mode limitation.
            var hasMaxdop2Hint = !string.IsNullOrEmpty(stmt.StatementText)
                && Regex.IsMatch(stmt.StatementText, @"MAXDOP\s+2\b", RegexOptions.IgnoreCase);

            if (!hasMaxdop2Hint)
            {
                var editionKnown = !string.IsNullOrEmpty(serverMetadata?.Edition);
                if (editionKnown
                    && serverMetadata!.Edition!.Contains("Standard", StringComparison.OrdinalIgnoreCase))
                {
                    // Server context confirms Standard Edition — check MAXDOP
                    if (serverMetadata.MaxDop > 2)
                    {
                        stmt.PlanWarnings.Add(new PlanWarning
                        {
                            WarningType = "Standard Edition DOP Limitation",
                            Message = $"DOP is limited to 2 because SQL Server Standard Edition caps parallelism at 2 when batch mode operators are present, even though MAXDOP is set to {serverMetadata.MaxDop}. Developer or Enterprise Edition would allow higher DOP in the same conditions.",
                            Severity = PlanWarningSeverity.Warning
                        });
                    }
                }
                else if (!editionKnown)
                {
                    // No server context, or edition unknown (e.g. collection failure) — suspect the limitation
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Standard Edition DOP Limitation",
                        Message = "DOP is limited to 2 and the plan uses batch mode operators. This may be caused by the SQL Server Standard Edition limitation, which caps parallelism at 2 when batch mode is in use. If this server runs Standard Edition, Developer or Enterprise Edition would allow higher DOP.",
                        Severity = PlanWarningSeverity.Info
                    });
                }
            }
        }

        // Rules 25 (Ineffective Parallelism) and 31 (Parallel Wait Bottleneck) were removed.
        // The CPU:Elapsed ratio is now shown in the runtime summary, and wait stats speak
        // for themselves — no need for meta-warnings guessing at causes.

        // Rule 30: Missing index quality evaluation
        if (!cfg.IsRuleDisabled(30))
        {
            // Detect duplicate suggestions for the same table
            var tableSuggestionCount = stmt.MissingIndexes
                .GroupBy(mi => $"{mi.Schema}.{mi.Table}", StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var mi in stmt.MissingIndexes)
            {
                var keyCount = mi.EqualityColumns.Count + mi.InequalityColumns.Count;
                var includeCount = mi.IncludeColumns.Count;
                var tableKey = $"{mi.Schema}.{mi.Table}";

                // Low-impact suggestion (< 25% improvement)
                if (mi.Impact < 25)
                {
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Low Impact Index",
                        Message = $"Missing index suggestion for {mi.Table} has only {mi.Impact:F0}% estimated impact. Low-impact indexes add maintenance overhead (insert/update/delete cost) that may not justify the modest query improvement.",
                        Severity = PlanWarningSeverity.Info
                    });
                }

                // Wide INCLUDE columns (> 5)
                if (includeCount > 5)
                {
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Wide Index Suggestion",
                        Message = $"Missing index suggestion for {mi.Table} has {includeCount} INCLUDE columns. This is a \"kitchen sink\" index — SQL Server suggests covering every column the query touches, but the resulting index would be very wide and expensive to maintain. Evaluate which columns are actually needed, or consider a narrower index with fewer includes.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }
                // Wide key columns (> 4)
                else if (keyCount > 4)
                {
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Wide Index Suggestion",
                        Message = $"Missing index suggestion for {mi.Table} has {keyCount} key columns ({mi.EqualityColumns.Count} equality + {mi.InequalityColumns.Count} inequality). Wide key columns increase index size and maintenance cost. Evaluate whether all key columns are needed for seek predicates.",
                        Severity = PlanWarningSeverity.Warning
                    });
                }

                // Multiple suggestions for same table
                if (tableSuggestionCount.TryGetValue(tableKey, out var count))
                {
                    stmt.PlanWarnings.Add(new PlanWarning
                    {
                        WarningType = "Duplicate Index Suggestions",
                        Message = $"{count} missing index suggestions target {mi.Table}. Multiple suggestions for the same table often overlap — consolidate into fewer, broader indexes rather than creating all of them.",
                        Severity = PlanWarningSeverity.Warning
                    });
                    // Only warn once per table
                    tableSuggestionCount.Remove(tableKey);
                }
            }
        }

        // Rule 22 (statement-level): Table variable warnings
        // Walk the tree to find table variable references, then emit statement-level warnings
        if (!cfg.IsRuleDisabled(22) && stmt.RootNode != null)
        {
            var hasTableVar = false;
            var isModification = stmt.StatementType is "INSERT" or "UPDATE" or "DELETE" or "MERGE";
            var modifiesTableVar = false;
            CheckForTableVariables(stmt.RootNode, isModification, ref hasTableVar, ref modifiesTableVar);

            if (hasTableVar && !modifiesTableVar)
            {
                stmt.PlanWarnings.Add(new PlanWarning
                {
                    WarningType = "Table Variable",
                    Message = "Table variable detected. Table variables lack column-level statistics, which causes bad row estimates, join choices, and memory grant decisions. Replace with a #temp table.",
                    Severity = PlanWarningSeverity.Warning
                });
            }

            if (modifiesTableVar)
            {
                stmt.PlanWarnings.Add(new PlanWarning
                {
                    WarningType = "Table Variable",
                    Message = "This query modifies a table variable, which forces the entire plan to run single-threaded. SQL Server cannot use parallelism for modifications to table variables. Replace with a #temp table to allow parallel execution.",
                    Severity = PlanWarningSeverity.Critical
                });
            }
        }
    }
}
