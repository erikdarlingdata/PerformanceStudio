using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Output;

/// <summary>
/// Maps parsed plan models to the structured CLI output format.
/// </summary>
public static class ResultMapper
{
    public static AnalysisResult Map(ParsedPlan plan, string source, ServerMetadata? metadata = null)
    {
        var result = new AnalysisResult
        {
            PlanSource = source,
            SqlServerVersion = plan.BuildVersion,
            SqlServerBuild = plan.Build
        };

        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                result.Statements.Add(MapStatement(stmt));
            }
        }

        result.Summary = BuildSummary(result);

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

    private static StatementResult MapStatement(PlanStatement stmt)
    {
        var result = new StatementResult
        {
            StatementText = stmt.StatementText,
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
            result.Warnings.Add(new WarningResult
            {
                Type = w.WarningType,
                Severity = w.Severity.ToString(),
                Message = w.Message,
                MaxBenefitPercent = w.MaxBenefitPercent,
                ActionableFix = w.ActionableFix,
                IsLegacy = w.IsLegacy
            });
        }

        // Missing indexes
        foreach (var mi in stmt.MissingIndexes)
        {
            result.MissingIndexes.Add(new MissingIndexResult
            {
                Table = $"{mi.Database}.{mi.Schema}.{mi.Table}",
                Impact = mi.Impact,
                EqualityColumns = mi.EqualityColumns,
                InequalityColumns = mi.InequalityColumns,
                IncludeColumns = mi.IncludeColumns,
                CreateStatement = mi.CreateStatement
            });
        }

        // Operator tree
        if (stmt.RootNode != null)
        {
            result.OperatorTree = MapNode(stmt.RootNode);
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

        return result;
    }

    private static OperatorResult MapNode(PlanNode node)
    {
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
            result.ActualElapsedMs = node.ActualElapsedMs;
            result.ActualCpuMs = node.ActualCPUMs;
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
                Operator = FormatOperatorLabel(node),
                NodeId = node.NodeId,
                MaxBenefitPercent = w.MaxBenefitPercent,
                ActionableFix = w.ActionableFix,
                IsLegacy = w.IsLegacy
            });
        }

        // Children
        foreach (var child in node.Children)
            result.Children.Add(MapNode(child));

        return result;
    }

    private static AnalysisSummary BuildSummary(AnalysisResult result)
    {
        var allWarnings = new List<WarningResult>();

        foreach (var stmt in result.Statements)
        {
            allWarnings.AddRange(stmt.Warnings);
            CollectNodeWarnings(stmt.OperatorTree, allWarnings);
        }

        return new AnalysisSummary
        {
            TotalStatements = result.Statements.Count,
            TotalWarnings = allWarnings.Count,
            CriticalWarnings = allWarnings.Count(w => w.Severity == "Critical"),
            MissingIndexes = result.Statements.Sum(s => s.MissingIndexes.Count),
            HasActualStats = result.Statements.Any(s => s.QueryTime != null),
            MaxEstimatedCost = result.Statements.Count > 0
                ? result.Statements.Max(s => s.EstimatedCost)
                : 0,
            WarningTypes = allWarnings.Select(w => w.Type).Distinct().OrderBy(t => t).ToList()
        };
    }

    private static void CollectNodeWarnings(OperatorResult? node, List<WarningResult> warnings)
    {
        if (node == null) return;
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectNodeWarnings(child, warnings);
    }

    /// <summary>
    /// Formats an operator label for the Operator field on warnings.
    /// Includes object name for data access operators (scans, seeks, lookups)
    /// where it helps identify which table/index is involved.
    /// </summary>
    private static string FormatOperatorLabel(PlanNode node)
    {
        if (!string.IsNullOrEmpty(node.ObjectName))
        {
            var objRef = !string.IsNullOrEmpty(node.DatabaseName)
                ? $"{node.DatabaseName}.{node.ObjectName}"
                : node.ObjectName;
            return $"{node.PhysicalOp} on {objRef} (Node {node.NodeId})";
        }

        return $"{node.PhysicalOp} (Node {node.NodeId})";
    }
}
