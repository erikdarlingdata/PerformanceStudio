using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Output;

/// <summary>
/// Maps parsed plan models to the structured CLI output format.
/// </summary>
public static class ResultMapper
{
    /// <summary>
    /// How many statistics <c>statistics_loaded</c> carries per statement, published beside
    /// <c>statistics_returned</c> / <c>total_statistics</c> / <c>statistics_truncated</c> so the cut is never silent.
    /// </summary>
    public const int StatisticsLoadedCap = 10;

    /// <summary>
    /// The <c>statistics_note</c> of a statement whose plan carries a QueryPlan but no OptimizerStatsUsage
    /// element (<c>statistics_status</c> = <c>not_in_plan</c>).
    /// </summary>
    public const string StatisticsNotInPlanNote = "This plan has no record of the statistics it loaded (SQL Server before 2017 / 2016 SP2, or a plan that loaded none), so their state at compile is unknown.";

    public static AnalysisResult Map(
        ParsedPlan plan,
        string source,
        ServerMetadata? metadata = null,
        string? capturedQueryText = null) =>
        MapCancellable(plan, source, metadata, capturedQueryText, CancellationToken.None);

    internal static AnalysisResult MapCancellable(
        ParsedPlan plan,
        string source,
        ServerMetadata? metadata,
        string? capturedQueryText,
        CancellationToken cancellationToken)
    {
        var result = new AnalysisResult
        {
            PlanSource = source,
            SqlServerVersion = plan.BuildVersion,
            SqlServerBuild = plan.Build
        };

        /* #455: includes statements nested inside a stored procedure or UDF body. Without this the
           summary counted the EXEC and nothing else, so a procedure carrying dozens of statement
           plans reported total_statements 1 and max_estimated_cost 0. */
        var statements = new List<PlanStatement>();
        foreach (var stmt in PlanStatements.EnumerateAll(plan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            statements.Add(stmt);
        }

        /* #502 follow-up: the plan's own copy of the statement stops at SQL Server's 4,000-character
           showplan cap, and every advice surface — Advice for Humans, Advice for Robots, the MCP
           tools — reads its statement text from this mapping, so this is where the query the session
           actually ran goes back in. Single-statement plans only, same reasoning as the viewer's
           PlanOrCapturedStatementText: the captured text is the whole batch and showplan records no
           statement offsets, so with more than one statement there is no way to say which slice
           belongs to which row, and a confidently wrong query is worse than a short one. */
        var recoveredFullText = statements.Count == 1
            && statements[0].IsTextTruncated
            && !string.IsNullOrEmpty(capturedQueryText)
                ? capturedQueryText
                : null;

        foreach (var stmt in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Statements.Add(MapStatement(stmt, recoveredFullText, cancellationToken));
        }

        result.Summary = BuildSummary(result, cancellationToken);

        if (metadata != null)
        {
            result.ServerContext = new ServerContextResult
            {
                ServerName = metadata.ServerName,
                ProductVersion = metadata.ProductVersion,
                ProductLevel = metadata.ProductLevel,
                Edition = metadata.Edition,
                IsAzure = metadata.IsAzure,
                CpuCount = metadata.CpuCount,
                PhysicalMemoryMB = metadata.PhysicalMemoryMB,
                MaxDop = metadata.MaxDop,
                CostThresholdForParallelism = metadata.CostThresholdForParallelism,
                MaxServerMemoryMB = metadata.MaxServerMemoryMB
            };

            if (metadata.Database != null)
            {
                var dbm = metadata.Database;
                result.ServerContext.Database = new DatabaseContextResult
                {
                    Name = dbm.Name,
                    CompatibilityLevel = dbm.CompatibilityLevel,
                    CollationName = dbm.CollationName,
                    SnapshotIsolationState = dbm.SnapshotIsolationState,
                    ReadCommittedSnapshot = dbm.IsReadCommittedSnapshotOn,
                    AutoCreateStats = dbm.IsAutoCreateStatsOn,
                    AutoUpdateStats = dbm.IsAutoUpdateStatsOn,
                    AutoUpdateStatsAsync = dbm.IsAutoUpdateStatsAsyncOn,
                    ParameterizationForced = dbm.IsParameterizationForced
                };

                foreach (var sc in dbm.NonDefaultScopedConfigs)
                {
                    result.ServerContext.Database.NonDefaultScopedConfigs.Add(new ScopedConfigResult
                    {
                        Name = sc.Name,
                        Value = sc.Value,
                        ValueForSecondary = sc.ValueForSecondary
                    });
                }
            }
        }

        return result;
    }

    private static StatementResult MapStatement(
        PlanStatement stmt,
        string? recoveredFullText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        /* #482: every consumer of the analysis — advice for humans, advice for robots, the HTML
           export, the comparison report, the MCP tools — reads its statement text from here, so this
           is where the parameter values go back in. #467 substituted at the two copy paths it was
           looking at, which left the same @0 in every one of these.

           The parser's PlanStatement is deliberately untouched: the analyzer's rules regex over that
           text (OPTIMIZE FOR UNKNOWN, NOT IN, MAXDOP hints), the properties panel shows what the plan
           records, and none of that should start reading manufactured literals. That is also why the
           #502 recovery lands here rather than on the model: the full captured query replaces the
           plan's truncated copy in the OUTPUT only, and Rule 39 plus the properties panel keep
           reporting what the plan itself records. */
        var sourceText = recoveredFullText ?? stmt.StatementText;
        var runnable = ParameterSubstitution.Apply(sourceText, stmt.Parameters);

        var result = new StatementResult
        {
            StatementText = runnable.Text,

            /* Carried, not discarded — this is the text that matches the plan cache and Query Store,
               and get_repro_script has to pair a query body with a declared parameter list. Null when
               nothing was substituted so it never appears on the overwhelming majority of statements
               that have nothing to say here. */
            ParameterizedStatementText = runnable.SubstitutionCount > 0 ? sourceText : null,
            StatementType = stmt.StatementType,
            EstimatedCost = stmt.StatementSubTreeCost,
            EstimatedRows = stmt.StatementEstRows,
            OptimizationLevel = stmt.StatementOptmLevel,
            EarlyAbortReason = stmt.StatementOptmEarlyAbortReason,
            CardinalityEstimationModel = stmt.CardinalityEstimationModelVersion,
            CompileTimeMs = stmt.CompileTimeMs,
            CompileMemoryKB = stmt.CompileMemoryKB,
            CachedPlanSizeKB = stmt.CachedPlanSizeKB,
            DegreeOfParallelism = stmt.DegreeOfParallelism,
            NonParallelReason = stmt.NonParallelPlanReason,
            QueryHash = stmt.QueryHash,
            QueryPlanHash = stmt.QueryPlanHash,
            BatchModeOnRowStore = stmt.BatchModeOnRowStoreUsed
        };

        // Memory grant
        if (stmt.MemoryGrant != null)
        {
            result.MemoryGrant = new MemoryGrantResult
            {
                RequestedKB = stmt.MemoryGrant.RequestedMemoryKB,
                GrantedKB = stmt.MemoryGrant.GrantedMemoryKB,
                MaxUsedKB = stmt.MemoryGrant.MaxUsedMemoryKB,
                GrantWaitMs = stmt.MemoryGrant.GrantWaitTimeMs,
                FeedbackAdjusted = stmt.MemoryGrant.IsMemoryGrantFeedbackAdjusted,
                EstimatedAvailableMemoryGrantKB = stmt.HardwareProperties?.EstimatedAvailableMemoryGrant ?? 0,
                DesiredKB = stmt.MemoryGrant.DesiredMemoryKB,
                SerialRequiredKB = stmt.MemoryGrant.SerialRequiredMemoryKB
            };
        }

        // Query time (actual plans)
        if (stmt.QueryTimeStats != null)
        {
            long externalWaitMs = 0;
            foreach (var w in stmt.WaitStats)
            {
                if (BenefitScorer.IsExternalWait(w.WaitType))
                    externalWaitMs += w.WaitTimeMs;
            }

            result.QueryTime = new QueryTimeResult
            {
                CpuTimeMs = stmt.QueryTimeStats.CpuTimeMs,
                ElapsedTimeMs = stmt.QueryTimeStats.ElapsedTimeMs,
                ExternalWaitMs = externalWaitMs
            };
        }

        // Wait stats (actual plans only)
        foreach (var w in stmt.WaitStats)
        {
            result.WaitStats.Add(new WaitStatResult
            {
                WaitType = w.WaitType,
                WaitTimeMs = w.WaitTimeMs,
                WaitCount = w.WaitCount
            });
        }

        // Wait stat benefits
        foreach (var wb in stmt.WaitBenefits)
        {
            result.WaitBenefits.Add(new WaitBenefitResult
            {
                WaitType = wb.WaitType,
                MaxBenefitPercent = wb.MaxBenefitPercent,
                Category = wb.Category
            });
        }

        // Parameters — flag potential sniffing issues
        foreach (var p in stmt.Parameters)
        {
            var pr = new ParameterResult
            {
                Name = p.Name,
                DataType = p.DataType,
                CompiledValue = p.CompiledValue,
                RuntimeValue = p.RuntimeValue
            };

            // Sniffing flag: compiled and runtime values both present but differ
            if (!string.IsNullOrEmpty(p.CompiledValue) &&
                !string.IsNullOrEmpty(p.RuntimeValue) &&
                p.CompiledValue != p.RuntimeValue)
            {
                pr.SniffingIssue = true;
            }

            result.Parameters.Add(pr);
        }

        // Statement-level warnings
        foreach (var w in stmt.PlanWarnings)
        {
            /* When the full query was recovered from the session, Rule 39's stock message would
               describe the one thing this result no longer shows — a shortened statement — right
               next to the complete text. Swap in what actually happened; the model's warning is
               untouched, so surfaces that read the plan directly still get the plan's truth. */
            var message = recoveredFullText != null && w.WarningType == "Truncated Query Text"
                ? "SQL Server capped this query's text at 4,000 characters when it wrote the plan, "
                    + "but the source this plan came from still had the original query, so the "
                    + "statement text shown here is the complete query. The plan's own copy of the "
                    + "text remains shortened."
                : w.Message;

            result.Warnings.Add(new WarningResult
            {
                Type = w.WarningType,
                Severity = w.Severity.ToString(),
                Message = message,
                MaxBenefitPercent = w.MaxBenefitPercent,
                ActionableFix = w.ActionableFix,
                Rule = w.RuleNumber,
                IsLegacy = w.IsLegacy,
                IsSuppressed = w.IsSuppressed,
                SuppressionReason = w.SuppressionReason,
                Source = w.Source.ToString(),
                OriginNodeIds = w.OriginNodeIds
            });
        }

        // Missing indexes
        foreach (var mi in stmt.MissingIndexes)
        {
            result.MissingIndexes.Add(new MissingIndexResult
            {
                Database = mi.Database,
                SchemaName = mi.Schema,
                BareTable = mi.Table,
                Table = $"{mi.Database}.{mi.Schema}.{mi.Table}",
                Impact = mi.Impact,
                EqualityColumns = mi.EqualityColumns.ToList(),
                InequalityColumns = mi.InequalityColumns.ToList(),
                IncludeColumns = mi.IncludeColumns.ToList(),
                CreateStatement = mi.CreateStatement
            });
        }

        // Operator tree
        if (stmt.RootNode != null)
        {
            result.OperatorTree = MapNode(stmt.RootNode, cancellationToken);
        }

        // Plan guide
        if (!string.IsNullOrEmpty(stmt.PlanGuideName))
            result.PlanGuide = $"{stmt.PlanGuideDB}.{stmt.PlanGuideName}";

        // Query Store hints
        if (!string.IsNullOrEmpty(stmt.QueryStoreStatementHintText))
            result.QueryStoreHint = stmt.QueryStoreStatementHintText;

        // Trace flags
        foreach (var tf in stmt.TraceFlags)
            result.TraceFlags.Add($"TF{tf.Value} ({tf.Scope}{(tf.IsCompileTime ? ", compile-time" : "")})");

        // Cursor
        if (!string.IsNullOrEmpty(stmt.CursorName))
        {
            result.Cursor = new CursorResult
            {
                Name = stmt.CursorName,
                ActualType = stmt.CursorActualType,
                RequestedType = stmt.CursorRequestedType,
                Concurrency = stmt.CursorConcurrency,
                ForwardOnly = stmt.CursorForwardOnly
            };
        }

        MapStatisticsLoaded(stmt, result);

        return result;
    }

    /// <summary>
    /// The statistics the optimizer loaded while compiling the statement (the plan's OptimizerStatsUsage;
    /// the plan does not say which of them drove the estimates), most-modified first and cut at
    /// <see cref="StatisticsLoadedCap"/>, with the truncation observed against the statement's whole list.
    /// </summary>
    private static void MapStatisticsLoaded(PlanStatement stmt, StatementResult result)
    {
        result.StatisticsStatus = !stmt.HasQueryPlan ? "no_query_plan" : stmt.HasStatsUsage ? "recorded" : "not_in_plan";
        result.StatisticsNote = result.StatisticsStatus == "not_in_plan" ? StatisticsNotInPlanNote : null;
        result.StatisticsCap = StatisticsLoadedCap;
        result.StatisticsReturned = Math.Min(stmt.StatsUsage.Count, StatisticsLoadedCap);
        result.TotalStatistics = stmt.StatsUsage.Count;
        result.StatisticsTruncated = stmt.StatsUsage.Count > StatisticsLoadedCap;

        if (stmt.StatsUsage.Count == 0)
            return;

        var nodes = new List<PlanNode>();
        if (stmt.RootNode != null)
            CollectNodes(stmt.RootNode, nodes);

        foreach (var item in stmt.StatsUsage
                     .OrderByDescending(i => i.ModificationCount)
                     .ThenBy(i => i.DatabaseName, StringComparer.Ordinal)
                     .ThenBy(i => i.SchemaName, StringComparer.Ordinal)
                     .ThenBy(i => i.TableName, StringComparer.Ordinal)
                     .ThenBy(i => i.StatisticsName, StringComparer.Ordinal)
                     .Take(StatisticsLoadedCap))
        {
            var tableRows = TableRowsFor(item, nodes);
            result.StatisticsLoaded.Add(new StatisticLoadedResult
            {
                Database = item.DatabaseName,
                Schema = item.SchemaName,
                Table = item.TableName,
                Statistic = item.StatisticsName,
                ModificationCount = item.ModificationCount,
                TableRows = tableRows,
                ModifiedPercent = tableRows is > 0 ? Math.Round(item.ModificationCount * 100.0 / tableRows.Value, 1) : null,
                StaleAtCompile = tableRows == null ? null : item.ModificationCount > AutoUpdateThreshold(tableRows.Value),
                LastUpdate = item.LastUpdate,
                SamplingPercent = item.SamplingPercent
            });
        }
    }

    /// <summary>
    /// The SQL Server 2016+ automatic update threshold at compatibility level 130 or higher: 500 for 500
    /// rows or fewer, otherwise the smaller of 500 + 20% of the rows and sqrt(1000 x rows).
    /// </summary>
    private static double AutoUpdateThreshold(double rows) =>
        rows <= 500 ? 500 : Math.Min(500 + 0.2 * rows, Math.Sqrt(1000.0 * rows));

    /// <summary>
    /// The row count of the statistic's table as the plan saw it: the largest TableCardinality among the
    /// operators that read that table and recorded one, or null when none does.
    /// </summary>
    private static double? TableRowsFor(OptimizerStatsUsageItem item, List<PlanNode> nodes)
    {
        var qualified = $"{item.SchemaName}.{item.TableName}";
        double? rows = null;
        foreach (var n in nodes)
        {
            if (n.HasTableCardinality
                && string.Equals(n.DatabaseName, item.DatabaseName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(n.ObjectName, qualified, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows is null ? n.TableCardinality : Math.Max(rows.Value, n.TableCardinality);
            }
        }

        return rows;
    }

    private static void CollectNodes(PlanNode node, List<PlanNode> nodes)
    {
        var pending = new Stack<PlanNode>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            nodes.Add(current);
            foreach (var child in current.Children)
                pending.Push(child);
        }
    }

    private static OperatorResult MapNode(PlanNode root, CancellationToken cancellationToken)
    {
        /* #589: a loop with its own stack. Recursion here used about 1 KB of stack per operator
           level, so a plan near MaxParseDepth (1,000) needed most of a 1 MB thread, the size the CLI
           and the UI run on. Each node's children are still added in plan order. */
        var rootResult = MapSingleNode(root, cancellationToken);
        var pending = new Stack<(PlanNode Node, OperatorResult Result)>();
        pending.Push((root, rootResult));
        while (pending.Count > 0)
        {
            var (node, result) = pending.Pop();
            foreach (var child in node.Children)
            {
                var childResult = MapSingleNode(child, cancellationToken);
                result.Children.Add(childResult);
                pending.Push((child, childResult));
            }
        }
        return rootResult;
    }

    private static OperatorResult MapSingleNode(PlanNode node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new OperatorResult
        {
            NodeId = node.NodeId,
            PhysicalOp = node.PhysicalOp,
            LogicalOp = node.LogicalOp,
            CostPercent = node.CostPercent,
            EstimatedRows = node.EstimateRows,
            EstimatedCost = node.EstimatedOperatorCost,
            EstimatedIO = node.EstimateIO,
            EstimatedCPU = node.EstimateCPU,
            EstimatedRowSize = node.EstimatedRowSize,
            ObjectName = node.FullObjectName ?? node.ObjectName,
            BareObjectName = node.ObjectName,
            IndexName = node.IndexName,
            DatabaseName = node.DatabaseName,
            SeekPredicates = node.SeekPredicates,
            Predicate = node.Predicate,
            OutputColumns = node.OutputColumns,
            HashKeysBuild = node.HashKeysBuild,
            HashKeysProbe = node.HashKeysProbe,
            OuterReferences = node.OuterReferences,
            OrderBy = node.OrderBy,
            GroupBy = node.GroupBy,
            Parallel = node.Parallel,
            ExecutionMode = node.ExecutionMode,
            ActualExecutionMode = node.ActualExecutionMode
        };

        // Actual stats (only include when present)
        if (node.HasActualStats)
        {
            result.ActualRows = node.ActualRows;
            result.ActualExecutions = node.ActualExecutions;
            result.ExpectedRows = RowEstimateHelper.GetExpectedRows(node);
            result.ActualElapsedMs = node.ActualElapsedMs;
            result.ActualCpuMs = node.ActualCPUMs;
            result.OwnElapsedMs = PlanAnalyzer.GetOperatorOwnElapsedMs(node);
            result.OwnCpuMs = PlanAnalyzer.GetOperatorOwnCpuMs(node);
            result.ActualLogicalReads = node.ActualLogicalReads;
            result.ActualPhysicalReads = node.ActualPhysicalReads;
        }

        // Operator warnings
        foreach (var w in node.Warnings)
        {
            result.Warnings.Add(new WarningResult
            {
                Type = w.WarningType,
                Severity = w.Severity.ToString(),
                Message = w.Message,
                Operator = OperatorLabel.Format(node),
                NodeId = node.NodeId,
                MaxBenefitPercent = w.MaxBenefitPercent,
                ActionableFix = w.ActionableFix,
                Rule = w.RuleNumber,
                IsLegacy = w.IsLegacy,
                IsSuppressed = w.IsSuppressed,
                SuppressionReason = w.SuppressionReason,
                Source = w.Source.ToString(),
                OriginNodeIds = w.OriginNodeIds
            });
        }

        return result;
    }

    private static AnalysisSummary BuildSummary(
        AnalysisResult result,
        CancellationToken cancellationToken)
    {
        var allWarnings = new List<WarningResult>();

        foreach (var stmt in result.Statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            allWarnings.AddRange(stmt.Warnings);
            CollectNodeWarnings(stmt.OperatorTree, allWarnings, cancellationToken);
        }

        // Suppressed findings (#651) stay in the output but do not count toward the totals.
        var activeWarnings = allWarnings.Where(w => !w.IsSuppressed).ToList();

        return new AnalysisSummary
        {
            TotalStatements = result.Statements.Count,
            TotalWarnings = activeWarnings.Count,
            CriticalWarnings = activeWarnings.Count(w => w.Severity == "Critical"),
            SuppressedWarnings = allWarnings.Count - activeWarnings.Count,
            MissingIndexes = result.Statements.Sum(s => s.MissingIndexes.Count),
            HasActualStats = result.Statements.Any(s => s.QueryTime != null),
            MaxEstimatedCost = result.Statements.Count > 0
                ? result.Statements.Max(s => s.EstimatedCost)
                : 0,
            WarningTypes = activeWarnings.Select(w => w.Type).Distinct().OrderBy(t => t).ToList()
        };
    }

    private static void CollectNodeWarnings(
        OperatorResult? node,
        List<WarningResult> warnings,
        CancellationToken cancellationToken)
    {
        if (node == null)
            return;

        var pending = new Stack<OperatorResult>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            warnings.AddRange(current.Warnings);
            for (var index = current.Children.Count - 1; index >= 0; index--)
                pending.Push(current.Children[index]);
        }
    }

}
