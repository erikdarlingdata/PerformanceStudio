using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class PlanViewerControl : UserControl
{
    private void ShowPropertiesPanel(PlanNode node)
    {
        PropertiesContent.Children.Clear();
        _sectionLabelColumns.Clear();
        _currentSectionGrid = null;
        _currentSectionRowIndex = 0;

        // Header
        var headerText = node.PhysicalOp;
        if (node.LogicalOp != node.PhysicalOp && !string.IsNullOrEmpty(node.LogicalOp)
            && !node.PhysicalOp.Contains(node.LogicalOp, StringComparison.OrdinalIgnoreCase))
            headerText += $" ({node.LogicalOp})";
        PropertiesHeader.Text = headerText;
        PropertiesSubHeader.Text = $"Node ID: {node.NodeId}";

        // === General Section ===
        AddPropertySection("General");
        AddPropertyRow("Physical Operation", node.PhysicalOp);
        AddPropertyRow("Logical Operation", node.LogicalOp);
        AddPropertyRow("Node ID", $"{node.NodeId}");
        if (!string.IsNullOrEmpty(node.ExecutionMode))
            AddPropertyRow("Execution Mode", node.ExecutionMode);
        if (!string.IsNullOrEmpty(node.ActualExecutionMode) && node.ActualExecutionMode != node.ExecutionMode)
            AddPropertyRow("Actual Exec Mode", node.ActualExecutionMode);
        AddPropertyRow("Parallel", node.Parallel ? "True" : "False");
        if (node.Partitioned)
            AddPropertyRow("Partitioned", "True");
        if (node.EstimatedDOP > 0)
            AddPropertyRow("Estimated DOP", $"{node.EstimatedDOP}");

        // Scan/seek-related properties
        if (!string.IsNullOrEmpty(node.FullObjectName))
        {
            AddPropertyRow("Ordered", node.Ordered ? "True" : "False");
            if (!string.IsNullOrEmpty(node.ScanDirection))
                AddPropertyRow("Scan Direction", node.ScanDirection);
            AddPropertyRow("Forced Index", node.ForcedIndex ? "True" : "False");
            AddPropertyRow("ForceScan", node.ForceScan ? "True" : "False");
            AddPropertyRow("ForceSeek", node.ForceSeek ? "True" : "False");
            AddPropertyRow("NoExpandHint", node.NoExpandHint ? "True" : "False");
            if (node.Lookup)
                AddPropertyRow("Lookup", "True");
            if (node.DynamicSeek)
                AddPropertyRow("Dynamic Seek", "True");
        }

        if (!string.IsNullOrEmpty(node.StorageType))
            AddPropertyRow("Storage", node.StorageType);
        if (node.IsAdaptive)
            AddPropertyRow("Adaptive", "True");
        if (node.SpillOccurredDetail)
            AddPropertyRow("Spill Occurred", "True");

        // === Object Section ===
        if (!string.IsNullOrEmpty(node.FullObjectName))
        {
            AddPropertySection("Object");
            AddPropertyRow("Full Name", node.FullObjectName, isCode: true);
            if (!string.IsNullOrEmpty(node.ServerName))
                AddPropertyRow("Server", node.ServerName);
            if (!string.IsNullOrEmpty(node.DatabaseName))
                AddPropertyRow("Database", node.DatabaseName);
            if (!string.IsNullOrEmpty(node.ObjectAlias))
                AddPropertyRow("Alias", node.ObjectAlias);
            if (!string.IsNullOrEmpty(node.IndexName))
                AddPropertyRow("Index", node.IndexName);
            if (!string.IsNullOrEmpty(node.IndexKind))
                AddPropertyRow("Index Kind", node.IndexKind);
            if (node.FilteredIndex)
                AddPropertyRow("Filtered Index", "True");
            if (node.TableReferenceId > 0)
                AddPropertyRow("Table Ref Id", $"{node.TableReferenceId}");
        }

        // === Operator Details Section ===
        var hasOperatorDetails = !string.IsNullOrEmpty(node.OrderBy)
            || !string.IsNullOrEmpty(node.TopExpression)
            || !string.IsNullOrEmpty(node.GroupBy)
            || !string.IsNullOrEmpty(node.PartitionColumns)
            || !string.IsNullOrEmpty(node.HashKeys)
            || !string.IsNullOrEmpty(node.SegmentColumn)
            || !string.IsNullOrEmpty(node.DefinedValues)
            || !string.IsNullOrEmpty(node.OuterReferences)
            || !string.IsNullOrEmpty(node.InnerSideJoinColumns)
            || !string.IsNullOrEmpty(node.OuterSideJoinColumns)
            || !string.IsNullOrEmpty(node.ActionColumn)
            || node.ManyToMany || node.PhysicalOp == "Merge Join" || node.BitmapCreator
            || node.SortDistinct || node.StartupExpression
            || node.NLOptimized || node.WithOrderedPrefetch || node.WithUnorderedPrefetch
            || node.WithTies || node.Remoting || node.LocalParallelism
            || node.SpoolStack || node.DMLRequestSort || node.NonClusteredIndexCount > 0
            || !string.IsNullOrEmpty(node.OffsetExpression) || node.TopRows > 0
            || !string.IsNullOrEmpty(node.ConstantScanValues)
            || !string.IsNullOrEmpty(node.UdxUsedColumns);

        if (hasOperatorDetails)
        {
            AddPropertySection("Operator Details");
            if (!string.IsNullOrEmpty(node.OrderBy))
                AddPropertyRow("Order By", node.OrderBy, isCode: true);
            if (!string.IsNullOrEmpty(node.TopExpression))
            {
                var topText = node.TopExpression;
                if (node.IsPercent) topText += " PERCENT";
                if (node.WithTies) topText += " WITH TIES";
                AddPropertyRow("Top", topText);
            }
            if (node.SortDistinct)
                AddPropertyRow("Distinct Sort", "True");
            if (node.StartupExpression)
                AddPropertyRow("Startup Expression", "True");
            if (node.NLOptimized)
                AddPropertyRow("Optimized", "True");
            if (node.WithOrderedPrefetch)
                AddPropertyRow("Ordered Prefetch", "True");
            if (node.WithUnorderedPrefetch)
                AddPropertyRow("Unordered Prefetch", "True");
            if (node.BitmapCreator)
                AddPropertyRow("Bitmap Creator", "True");
            if (node.Remoting)
                AddPropertyRow("Remoting", "True");
            if (node.LocalParallelism)
                AddPropertyRow("Local Parallelism", "True");
            if (!string.IsNullOrEmpty(node.GroupBy))
                AddPropertyRow("Group By", node.GroupBy, isCode: true);
            if (!string.IsNullOrEmpty(node.PartitionColumns))
                AddPropertyRow("Partition Columns", node.PartitionColumns, isCode: true);
            if (!string.IsNullOrEmpty(node.HashKeys))
                AddPropertyRow("Hash Keys", node.HashKeys, isCode: true);
            if (!string.IsNullOrEmpty(node.OffsetExpression))
                AddPropertyRow("Offset", node.OffsetExpression);
            if (node.TopRows > 0)
                AddPropertyRow("Rows", $"{node.TopRows}");
            if (node.SpoolStack)
                AddPropertyRow("Stack Spool", "True");
            if (node.PrimaryNodeId > 0)
                AddPropertyRow("Primary Node Id", $"{node.PrimaryNodeId}");
            if (node.DMLRequestSort)
                AddPropertyRow("DML Request Sort", "True");
            if (node.NonClusteredIndexCount > 0)
            {
                AddPropertyRow("NC Indexes Maintained", $"{node.NonClusteredIndexCount}");
                foreach (var ixName in node.NonClusteredIndexNames)
                    AddPropertyRow("", ixName, isCode: true);
            }
            if (!string.IsNullOrEmpty(node.ActionColumn))
                AddPropertyRow("Action Column", node.ActionColumn, isCode: true);
            if (!string.IsNullOrEmpty(node.SegmentColumn))
                AddPropertyRow("Segment Column", node.SegmentColumn, isCode: true);
            if (!string.IsNullOrEmpty(node.DefinedValues))
                AddPropertyRow("Defined Values", node.DefinedValues, isCode: true);
            if (!string.IsNullOrEmpty(node.OuterReferences))
                AddPropertyRow("Outer References", node.OuterReferences, isCode: true);
            if (!string.IsNullOrEmpty(node.InnerSideJoinColumns))
                AddPropertyRow("Inner Join Cols", node.InnerSideJoinColumns, isCode: true);
            if (!string.IsNullOrEmpty(node.OuterSideJoinColumns))
                AddPropertyRow("Outer Join Cols", node.OuterSideJoinColumns, isCode: true);
            if (node.PhysicalOp == "Merge Join")
                AddPropertyRow("Many to Many", node.ManyToMany ? "Yes" : "No");
            else if (node.ManyToMany)
                AddPropertyRow("Many to Many", "Yes");
            if (!string.IsNullOrEmpty(node.ConstantScanValues))
                AddPropertyRow("Values", node.ConstantScanValues, isCode: true);
            if (!string.IsNullOrEmpty(node.UdxUsedColumns))
                AddPropertyRow("UDX Columns", node.UdxUsedColumns, isCode: true);
            if (node.RowCount)
                AddPropertyRow("Row Count", "True");
            if (node.ForceSeekColumnCount > 0)
                AddPropertyRow("ForceSeek Columns", $"{node.ForceSeekColumnCount}");
            if (!string.IsNullOrEmpty(node.PartitionId))
                AddPropertyRow("Partition Id", node.PartitionId, isCode: true);
            if (node.IsStarJoin)
                AddPropertyRow("Star Join Root", "True");
            if (!string.IsNullOrEmpty(node.StarJoinOperationType))
                AddPropertyRow("Star Join Type", node.StarJoinOperationType);
            if (!string.IsNullOrEmpty(node.ProbeColumn))
                AddPropertyRow("Probe Column", node.ProbeColumn, isCode: true);
            if (node.InRow)
                AddPropertyRow("In-Row", "True");
            if (node.ComputeSequence)
                AddPropertyRow("Compute Sequence", "True");
            if (node.RollupHighestLevel > 0)
                AddPropertyRow("Rollup Highest Level", $"{node.RollupHighestLevel}");
            if (node.RollupLevels.Count > 0)
                AddPropertyRow("Rollup Levels", string.Join(", ", node.RollupLevels));
            if (!string.IsNullOrEmpty(node.TvfParameters))
                AddPropertyRow("TVF Parameters", node.TvfParameters, isCode: true);
            if (!string.IsNullOrEmpty(node.OriginalActionColumn))
                AddPropertyRow("Original Action Col", node.OriginalActionColumn, isCode: true);
            if (!string.IsNullOrEmpty(node.TieColumns))
                AddPropertyRow("WITH TIES Columns", node.TieColumns, isCode: true);
            if (!string.IsNullOrEmpty(node.UdxName))
                AddPropertyRow("UDX Name", node.UdxName);
            if (node.GroupExecuted)
                AddPropertyRow("Group Executed", "True");
            if (node.RemoteDataAccess)
                AddPropertyRow("Remote Data Access", "True");
            if (node.OptimizedHalloweenProtectionUsed)
                AddPropertyRow("Halloween Protection", "True");
            if (node.StatsCollectionId > 0)
                AddPropertyRow("Stats Collection Id", $"{node.StatsCollectionId}");
        }

        // === Scalar UDFs ===
        if (node.ScalarUdfs.Count > 0)
        {
            AddPropertySection("Scalar UDFs");
            foreach (var udf in node.ScalarUdfs)
            {
                var udfDetail = udf.FunctionName;
                if (udf.IsClrFunction)
                {
                    udfDetail += " (CLR)";
                    if (!string.IsNullOrEmpty(udf.ClrAssembly))
                        udfDetail += $"\n  Assembly: {udf.ClrAssembly}";
                    if (!string.IsNullOrEmpty(udf.ClrClass))
                        udfDetail += $"\n  Class: {udf.ClrClass}";
                    if (!string.IsNullOrEmpty(udf.ClrMethod))
                        udfDetail += $"\n  Method: {udf.ClrMethod}";
                }
                AddPropertyRow("UDF", udfDetail, isCode: true);
            }
        }

        // === Named Parameters (IndexScan) ===
        if (node.NamedParameters.Count > 0)
        {
            AddPropertySection("Named Parameters");
            foreach (var np in node.NamedParameters)
                AddPropertyRow(np.Name, np.ScalarString ?? "", isCode: true);
        }

        // === Per-Operator Indexed Views ===
        if (node.OperatorIndexedViews.Count > 0)
        {
            AddPropertySection("Operator Indexed Views");
            foreach (var iv in node.OperatorIndexedViews)
                AddPropertyRow("View", iv, isCode: true);
        }

        // === Suggested Index (Eager Spool) ===
        if (!string.IsNullOrEmpty(node.SuggestedIndex))
        {
            AddPropertySection("Suggested Index");
            AddPropertyRow("CREATE INDEX", node.SuggestedIndex, isCode: true);
        }

        // === Remote Operator ===
        if (!string.IsNullOrEmpty(node.RemoteDestination) || !string.IsNullOrEmpty(node.RemoteSource)
            || !string.IsNullOrEmpty(node.RemoteObject) || !string.IsNullOrEmpty(node.RemoteQuery))
        {
            AddPropertySection("Remote Operator");
            if (!string.IsNullOrEmpty(node.RemoteDestination))
                AddPropertyRow("Destination", node.RemoteDestination);
            if (!string.IsNullOrEmpty(node.RemoteSource))
                AddPropertyRow("Source", node.RemoteSource);
            if (!string.IsNullOrEmpty(node.RemoteObject))
                AddPropertyRow("Object", node.RemoteObject, isCode: true);
            if (!string.IsNullOrEmpty(node.RemoteQuery))
                AddPropertyRow("Query", node.RemoteQuery, isCode: true);
        }

        // === Foreign Key References Section ===
        if (node.ForeignKeyReferencesCount > 0 || node.NoMatchingIndexCount > 0 || node.PartialMatchingIndexCount > 0)
        {
            AddPropertySection("Foreign Key References");
            if (node.ForeignKeyReferencesCount > 0)
                AddPropertyRow("FK References", $"{node.ForeignKeyReferencesCount}");
            if (node.NoMatchingIndexCount > 0)
                AddPropertyRow("No Matching Index", $"{node.NoMatchingIndexCount}");
            if (node.PartialMatchingIndexCount > 0)
                AddPropertyRow("Partial Match Index", $"{node.PartialMatchingIndexCount}");
        }

        // === Adaptive Join Section ===
        if (node.IsAdaptive)
        {
            AddPropertySection("Adaptive Join");
            if (!string.IsNullOrEmpty(node.EstimatedJoinType))
                AddPropertyRow("Est. Join Type", node.EstimatedJoinType);
            if (!string.IsNullOrEmpty(node.ActualJoinType))
                AddPropertyRow("Actual Join Type", node.ActualJoinType);
            if (node.AdaptiveThresholdRows > 0)
                AddPropertyRow("Threshold Rows", $"{node.AdaptiveThresholdRows:N1}");
        }

        // === Estimated Costs Section ===
        AddPropertySection("Estimated Costs");
        AddPropertyRow("Operator Cost", $"{node.EstimatedOperatorCost:F6} ({node.CostPercent}%)");
        AddPropertyRow("Subtree Cost", $"{node.EstimatedTotalSubtreeCost:F6}");
        AddPropertyRow("I/O Cost", $"{node.EstimateIO:F6}");
        AddPropertyRow("CPU Cost", $"{node.EstimateCPU:F6}");

        // === Estimated Rows Section ===
        AddPropertySection("Estimated Rows");
        var estExecs = 1 + node.EstimateRebinds;
        AddPropertyRow("Est. Executions", $"{estExecs:N0}");
        AddPropertyRow("Est. Rows Per Exec", $"{node.EstimateRows:N1}");
        AddPropertyRow("Est. Rows All Execs", $"{node.EstimateRows * Math.Max(1, estExecs):N1}");
        if (node.EstimatedRowsRead > 0)
            AddPropertyRow("Est. Rows to Read", $"{node.EstimatedRowsRead:N1}");
        if (node.EstimateRowsWithoutRowGoal > 0)
            AddPropertyRow("Est. Rows (No Row Goal)", $"{node.EstimateRowsWithoutRowGoal:N1}");
        if (node.TableCardinality > 0)
            AddPropertyRow("Table Cardinality", $"{node.TableCardinality:N0}");
        AddPropertyRow("Avg Row Size", $"{node.EstimatedRowSize} B");
        AddPropertyRow("Est. Rebinds", $"{node.EstimateRebinds:N1}");
        AddPropertyRow("Est. Rewinds", $"{node.EstimateRewinds:N1}");

        // === Actual Stats Section (if actual plan) ===
        if (node.HasActualStats)
        {
            AddPropertySection("Actual Statistics");
            AddPropertyRow("Actual Rows", $"{node.ActualRows:N0}");
            if (node.PerThreadStats.Count > 1)
                foreach (var t in node.PerThreadStats)
                    AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualRows:N0}", indent: true);
            if (node.ActualRowsRead > 0)
            {
                AddPropertyRow("Actual Rows Read", $"{node.ActualRowsRead:N0}");
                if (node.PerThreadStats.Count > 1)
                    foreach (var t in node.PerThreadStats.Where(t => t.ActualRowsRead > 0))
                        AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualRowsRead:N0}", indent: true);
            }
            AddPropertyRow("Actual Executions", $"{node.ActualExecutions:N0}");
            if (node.PerThreadStats.Count > 1)
                foreach (var t in node.PerThreadStats)
                    AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualExecutions:N0}", indent: true);
            if (node.ActualRebinds > 0)
                AddPropertyRow("Actual Rebinds", $"{node.ActualRebinds:N0}");
            if (node.ActualRewinds > 0)
                AddPropertyRow("Actual Rewinds", $"{node.ActualRewinds:N0}");

            // Runtime partition summary
            if (node.PartitionsAccessed > 0)
            {
                AddPropertyRow("Partitions Accessed", $"{node.PartitionsAccessed}");
                if (!string.IsNullOrEmpty(node.PartitionRanges))
                    AddPropertyRow("Partition Ranges", node.PartitionRanges);
            }

            // Timing
            if (node.ActualElapsedMs > 0 || node.ActualCPUMs > 0
                || node.UdfCpuTimeMs > 0 || node.UdfElapsedTimeMs > 0)
            {
                AddPropertySection("Actual Timing");
                if (node.ActualElapsedMs > 0)
                {
                    AddPropertyRow("Elapsed Time", $"{node.ActualElapsedMs:N0} ms");
                    if (node.PerThreadStats.Count > 1)
                        foreach (var t in node.PerThreadStats.Where(t => t.ActualElapsedMs > 0))
                            AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualElapsedMs:N0} ms", indent: true);
                }
                if (node.ActualCPUMs > 0)
                {
                    AddPropertyRow("CPU Time", $"{node.ActualCPUMs:N0} ms");
                    if (node.PerThreadStats.Count > 1)
                        foreach (var t in node.PerThreadStats.Where(t => t.ActualCPUMs > 0))
                            AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualCPUMs:N0} ms", indent: true);
                }
                if (node.UdfElapsedTimeMs > 0)
                    AddPropertyRow("UDF Elapsed", $"{node.UdfElapsedTimeMs:N0} ms");
                if (node.UdfCpuTimeMs > 0)
                    AddPropertyRow("UDF CPU", $"{node.UdfCpuTimeMs:N0} ms");
            }

            // I/O
            var hasIo = node.ActualLogicalReads > 0 || node.ActualPhysicalReads > 0
                || node.ActualScans > 0 || node.ActualReadAheads > 0
                || node.ActualSegmentReads > 0 || node.ActualSegmentSkips > 0;
            if (hasIo)
            {
                AddPropertySection("Actual I/O");
                AddPropertyRow("Logical Reads", $"{node.ActualLogicalReads:N0}");
                if (node.PerThreadStats.Count > 1)
                    foreach (var t in node.PerThreadStats.Where(t => t.ActualLogicalReads > 0))
                        AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualLogicalReads:N0}", indent: true);
                if (node.ActualPhysicalReads > 0)
                {
                    AddPropertyRow("Physical Reads", $"{node.ActualPhysicalReads:N0}");
                    if (node.PerThreadStats.Count > 1)
                        foreach (var t in node.PerThreadStats.Where(t => t.ActualPhysicalReads > 0))
                            AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualPhysicalReads:N0}", indent: true);
                }
                if (node.ActualScans > 0)
                {
                    AddPropertyRow("Scans", $"{node.ActualScans:N0}");
                    if (node.PerThreadStats.Count > 1)
                        foreach (var t in node.PerThreadStats.Where(t => t.ActualScans > 0))
                            AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualScans:N0}", indent: true);
                }
                if (node.ActualReadAheads > 0)
                {
                    AddPropertyRow("Read-Ahead Reads", $"{node.ActualReadAheads:N0}");
                    if (node.PerThreadStats.Count > 1)
                        foreach (var t in node.PerThreadStats.Where(t => t.ActualReadAheads > 0))
                            AddPropertyRow($"  Thread {t.ThreadId}", $"{t.ActualReadAheads:N0}", indent: true);
                }
                if (node.ActualSegmentReads > 0)
                    AddPropertyRow("Segment Reads", $"{node.ActualSegmentReads:N0}");
                if (node.ActualSegmentSkips > 0)
                    AddPropertyRow("Segment Skips", $"{node.ActualSegmentSkips:N0}");
            }

            // LOB I/O
            var hasLobIo = node.ActualLobLogicalReads > 0 || node.ActualLobPhysicalReads > 0
                || node.ActualLobReadAheads > 0;
            if (hasLobIo)
            {
                AddPropertySection("Actual LOB I/O");
                if (node.ActualLobLogicalReads > 0)
                    AddPropertyRow("LOB Logical Reads", $"{node.ActualLobLogicalReads:N0}");
                if (node.ActualLobPhysicalReads > 0)
                    AddPropertyRow("LOB Physical Reads", $"{node.ActualLobPhysicalReads:N0}");
                if (node.ActualLobReadAheads > 0)
                    AddPropertyRow("LOB Read-Aheads", $"{node.ActualLobReadAheads:N0}");
            }
        }

        // === Predicates Section ===
        var hasPredicates = !string.IsNullOrEmpty(node.SeekPredicates) || !string.IsNullOrEmpty(node.Predicate)
            || !string.IsNullOrEmpty(node.HashKeysProbe) || !string.IsNullOrEmpty(node.HashKeysBuild)
            || !string.IsNullOrEmpty(node.BuildResidual) || !string.IsNullOrEmpty(node.ProbeResidual)
            || !string.IsNullOrEmpty(node.MergeResidual) || !string.IsNullOrEmpty(node.PassThru)
            || !string.IsNullOrEmpty(node.SetPredicate)
            || node.GuessedSelectivity;
        if (hasPredicates)
        {
            AddPropertySection("Predicates");
            if (!string.IsNullOrEmpty(node.SeekPredicates))
                AddPropertyRow("Seek Predicate", node.SeekPredicates, isCode: true);
            if (!string.IsNullOrEmpty(node.Predicate))
                AddPropertyRow("Predicate", node.Predicate, isCode: true);
            if (!string.IsNullOrEmpty(node.HashKeysBuild))
                AddPropertyRow("Hash Keys (Build)", node.HashKeysBuild, isCode: true);
            if (!string.IsNullOrEmpty(node.HashKeysProbe))
                AddPropertyRow("Hash Keys (Probe)", node.HashKeysProbe, isCode: true);
            if (!string.IsNullOrEmpty(node.BuildResidual))
                AddPropertyRow("Build Residual", node.BuildResidual, isCode: true);
            if (!string.IsNullOrEmpty(node.ProbeResidual))
                AddPropertyRow("Probe Residual", node.ProbeResidual, isCode: true);
            if (!string.IsNullOrEmpty(node.MergeResidual))
                AddPropertyRow("Merge Residual", node.MergeResidual, isCode: true);
            if (!string.IsNullOrEmpty(node.PassThru))
                AddPropertyRow("Pass Through", node.PassThru, isCode: true);
            if (!string.IsNullOrEmpty(node.SetPredicate))
                AddPropertyRow("Set Predicate", node.SetPredicate, isCode: true);
            if (node.GuessedSelectivity)
                AddPropertyRow("Guessed Selectivity", "True (optimizer guessed, no statistics)");
        }

        // === Output Columns ===
        if (!string.IsNullOrEmpty(node.OutputColumns))
        {
            AddPropertySection("Output");
            AddPropertyRow("Columns", node.OutputColumns, isCode: true);
        }

        // === Memory ===
        if (node.MemoryGrantKB > 0 || node.DesiredMemoryKB > 0 || node.MaxUsedMemoryKB > 0
            || node.MemoryFractionInput > 0 || node.MemoryFractionOutput > 0
            || node.InputMemoryGrantKB > 0 || node.OutputMemoryGrantKB > 0 || node.UsedMemoryGrantKB > 0)
        {
            AddPropertySection("Memory");
            if (node.MemoryGrantKB > 0) AddPropertyRow("Granted", $"{node.MemoryGrantKB:N0} KB");
            if (node.DesiredMemoryKB > 0) AddPropertyRow("Desired", $"{node.DesiredMemoryKB:N0} KB");
            if (node.MaxUsedMemoryKB > 0) AddPropertyRow("Max Used", $"{node.MaxUsedMemoryKB:N0} KB");
            if (node.InputMemoryGrantKB > 0) AddPropertyRow("Input Grant", $"{node.InputMemoryGrantKB:N0} KB");
            if (node.OutputMemoryGrantKB > 0) AddPropertyRow("Output Grant", $"{node.OutputMemoryGrantKB:N0} KB");
            if (node.UsedMemoryGrantKB > 0) AddPropertyRow("Used Grant", $"{node.UsedMemoryGrantKB:N0} KB");
            if (node.MemoryFractionInput > 0) AddPropertyRow("Fraction Input", $"{node.MemoryFractionInput:F4}");
            if (node.MemoryFractionOutput > 0) AddPropertyRow("Fraction Output", $"{node.MemoryFractionOutput:F4}");
        }

        // === Root node only: statement-level sections ===
        if (node.Parent == null && _currentStatement != null)
        {
            var s = _currentStatement;

            // === Statement Text ===
            if (!string.IsNullOrEmpty(s.StatementText) || !string.IsNullOrEmpty(s.StmtUseDatabaseName))
            {
                AddPropertySection("Statement");
                if (!string.IsNullOrEmpty(s.StatementText))
                    AddPropertyRow("Text", s.StatementText, isCode: true);
                if (!string.IsNullOrEmpty(s.ParameterizedText) && s.ParameterizedText != s.StatementText)
                    AddPropertyRow("Parameterized", s.ParameterizedText, isCode: true);
                if (!string.IsNullOrEmpty(s.StmtUseDatabaseName))
                    AddPropertyRow("USE Database", s.StmtUseDatabaseName);
            }

            // === Cursor Info ===
            if (!string.IsNullOrEmpty(s.CursorName))
            {
                AddPropertySection("Cursor Info");
                AddPropertyRow("Cursor Name", s.CursorName);
                if (!string.IsNullOrEmpty(s.CursorActualType))
                    AddPropertyRow("Actual Type", s.CursorActualType);
                if (!string.IsNullOrEmpty(s.CursorRequestedType))
                    AddPropertyRow("Requested Type", s.CursorRequestedType);
                if (!string.IsNullOrEmpty(s.CursorConcurrency))
                    AddPropertyRow("Concurrency", s.CursorConcurrency);
                AddPropertyRow("Forward Only", s.CursorForwardOnly ? "True" : "False");
            }

            // === Statement Memory Grant ===
            if (s.MemoryGrant != null)
            {
                var mg = s.MemoryGrant;
                AddPropertySection("Memory Grant Info");
                AddPropertyRow("Granted", $"{mg.GrantedMemoryKB:N0} KB");
                AddPropertyRow("Max Used", $"{mg.MaxUsedMemoryKB:N0} KB");
                AddPropertyRow("Requested", $"{mg.RequestedMemoryKB:N0} KB");
                AddPropertyRow("Desired", $"{mg.DesiredMemoryKB:N0} KB");
                AddPropertyRow("Required", $"{mg.RequiredMemoryKB:N0} KB");
                AddPropertyRow("Serial Required", $"{mg.SerialRequiredMemoryKB:N0} KB");
                AddPropertyRow("Serial Desired", $"{mg.SerialDesiredMemoryKB:N0} KB");
                if (mg.GrantWaitTimeMs > 0)
                    AddPropertyRow("Grant Wait Time", $"{mg.GrantWaitTimeMs:N0} ms");
                if (mg.LastRequestedMemoryKB > 0)
                    AddPropertyRow("Last Requested", $"{mg.LastRequestedMemoryKB:N0} KB");
                if (!string.IsNullOrEmpty(mg.IsMemoryGrantFeedbackAdjusted))
                    AddPropertyRow("Feedback Adjusted", mg.IsMemoryGrantFeedbackAdjusted);
            }

            // === Statement Info ===
            AddPropertySection("Statement Info");
            if (!string.IsNullOrEmpty(s.StatementOptmLevel))
                AddPropertyRow("Optimization Level", s.StatementOptmLevel);
            if (!string.IsNullOrEmpty(s.StatementOptmEarlyAbortReason))
                AddPropertyRow("Early Abort Reason", s.StatementOptmEarlyAbortReason);
            if (s.CardinalityEstimationModelVersion > 0)
                AddPropertyRow("CE Model Version", $"{s.CardinalityEstimationModelVersion}");
            if (s.DegreeOfParallelism > 0)
                AddPropertyRow("DOP", $"{s.DegreeOfParallelism}");
            if (s.EffectiveDOP > 0)
                AddPropertyRow("Effective DOP", $"{s.EffectiveDOP}");
            if (!string.IsNullOrEmpty(s.DOPFeedbackAdjusted))
                AddPropertyRow("DOP Feedback", s.DOPFeedbackAdjusted);
            if (!string.IsNullOrEmpty(s.NonParallelPlanReason))
                AddPropertyRow("Non-Parallel Reason", s.NonParallelPlanReason);
            if (s.MaxQueryMemoryKB > 0)
                AddPropertyRow("Max Query Memory", $"{s.MaxQueryMemoryKB:N0} KB");
            if (s.QueryPlanMemoryGrantKB > 0)
                AddPropertyRow("QueryPlan Memory Grant", $"{s.QueryPlanMemoryGrantKB:N0} KB");
            AddPropertyRow("Compile Time", $"{s.CompileTimeMs:N0} ms");
            AddPropertyRow("Compile CPU", $"{s.CompileCPUMs:N0} ms");
            AddPropertyRow("Compile Memory", $"{s.CompileMemoryKB:N0} KB");
            if (s.CachedPlanSizeKB > 0)
                AddPropertyRow("Cached Plan Size", $"{s.CachedPlanSizeKB:N0} KB");
            AddPropertyRow("Retrieved From Cache", s.RetrievedFromCache ? "True" : "False");
            AddPropertyRow("Batch Mode On RowStore", s.BatchModeOnRowStoreUsed ? "True" : "False");
            AddPropertyRow("Security Policy", s.SecurityPolicyApplied ? "True" : "False");
            AddPropertyRow("Parameterization Type", $"{s.StatementParameterizationType}");
            if (!string.IsNullOrEmpty(s.QueryHash))
                AddPropertyRow("Query Hash", s.QueryHash, isCode: true);
            if (!string.IsNullOrEmpty(s.QueryPlanHash))
                AddPropertyRow("Plan Hash", s.QueryPlanHash, isCode: true);
            if (!string.IsNullOrEmpty(s.StatementSqlHandle))
                AddPropertyRow("SQL Handle", s.StatementSqlHandle, isCode: true);
            AddPropertyRow("DB Settings Id", $"{s.DatabaseContextSettingsId}");
            AddPropertyRow("Parent Object Id", $"{s.ParentObjectId}");

            // Plan Guide
            if (!string.IsNullOrEmpty(s.PlanGuideName))
            {
                AddPropertyRow("Plan Guide", s.PlanGuideName);
                if (!string.IsNullOrEmpty(s.PlanGuideDB))
                    AddPropertyRow("Plan Guide DB", s.PlanGuideDB);
            }
            if (s.UsePlan)
                AddPropertyRow("USE PLAN", "True");

            // Query Store Hints
            if (s.QueryStoreStatementHintId > 0)
            {
                AddPropertyRow("QS Hint Id", $"{s.QueryStoreStatementHintId}");
                if (!string.IsNullOrEmpty(s.QueryStoreStatementHintText))
                    AddPropertyRow("QS Hint", s.QueryStoreStatementHintText, isCode: true);
                if (!string.IsNullOrEmpty(s.QueryStoreStatementHintSource))
                    AddPropertyRow("QS Hint Source", s.QueryStoreStatementHintSource);
            }

            // === Feature Flags ===
            if (s.ContainsInterleavedExecutionCandidates || s.ContainsInlineScalarTsqlUdfs
                || s.ContainsLedgerTables || s.ExclusiveProfileTimeActive || s.QueryCompilationReplay > 0
                || s.QueryVariantID > 0)
            {
                AddPropertySection("Feature Flags");
                if (s.ContainsInterleavedExecutionCandidates)
                    AddPropertyRow("Interleaved Execution", "True");
                if (s.ContainsInlineScalarTsqlUdfs)
                    AddPropertyRow("Inline Scalar UDFs", "True");
                if (s.ContainsLedgerTables)
                    AddPropertyRow("Ledger Tables", "True");
                if (s.ExclusiveProfileTimeActive)
                    AddPropertyRow("Exclusive Profile Time", "True");
                if (s.QueryCompilationReplay > 0)
                    AddPropertyRow("Compilation Replay", $"{s.QueryCompilationReplay}");
                if (s.QueryVariantID > 0)
                    AddPropertyRow("Query Variant ID", $"{s.QueryVariantID}");
            }

            // === PSP Dispatcher ===
            if (s.Dispatcher != null)
            {
                AddPropertySection("PSP Dispatcher");
                if (!string.IsNullOrEmpty(s.DispatcherPlanHandle))
                    AddPropertyRow("Plan Handle", s.DispatcherPlanHandle, isCode: true);
                foreach (var psp in s.Dispatcher.ParameterSensitivePredicates)
                {
                    var range = $"[{psp.LowBoundary:N0} — {psp.HighBoundary:N0}]";
                    var predText = psp.PredicateText ?? "";
                    AddPropertyRow("Predicate", $"{predText} {range}", isCode: true);
                    foreach (var stat in psp.Statistics)
                    {
                        var statLabel = !string.IsNullOrEmpty(stat.TableName)
                            ? $"  {stat.TableName}.{stat.StatisticsName}"
                            : $"  {stat.StatisticsName}";
                        AddPropertyRow(statLabel, $"Modified: {stat.ModificationCount:N0}, Sampled: {stat.SamplingPercent:F1}%", indent: true);
                    }
                }
                foreach (var opt in s.Dispatcher.OptionalParameterPredicates)
                {
                    if (!string.IsNullOrEmpty(opt.PredicateText))
                        AddPropertyRow("Optional Predicate", opt.PredicateText, isCode: true);
                }
            }

            // === Cardinality Feedback ===
            if (s.CardinalityFeedback.Count > 0)
            {
                AddPropertySection("Cardinality Feedback");
                foreach (var cf in s.CardinalityFeedback)
                    AddPropertyRow($"Node {cf.Key}", $"{cf.Value:N0}");
            }

            // === Optimization Replay ===
            if (!string.IsNullOrEmpty(s.OptimizationReplayScript))
            {
                AddPropertySection("Optimization Replay");
                AddPropertyRow("Script", s.OptimizationReplayScript, isCode: true);
            }

            // === Template Plan Guide ===
            if (!string.IsNullOrEmpty(s.TemplatePlanGuideName))
            {
                AddPropertyRow("Template Plan Guide", s.TemplatePlanGuideName);
                if (!string.IsNullOrEmpty(s.TemplatePlanGuideDB))
                    AddPropertyRow("Template Guide DB", s.TemplatePlanGuideDB);
            }

            // === Handles ===
            if (!string.IsNullOrEmpty(s.ParameterizedPlanHandle) || !string.IsNullOrEmpty(s.BatchSqlHandle))
            {
                AddPropertySection("Handles");
                if (!string.IsNullOrEmpty(s.ParameterizedPlanHandle))
                    AddPropertyRow("Parameterized Plan", s.ParameterizedPlanHandle, isCode: true);
                if (!string.IsNullOrEmpty(s.BatchSqlHandle))
                    AddPropertyRow("Batch SQL Handle", s.BatchSqlHandle, isCode: true);
            }

            // === Set Options ===
            if (s.SetOptions != null)
            {
                var so = s.SetOptions;
                AddPropertySection("Set Options");
                AddPropertyRow("ANSI_NULLS", so.AnsiNulls ? "True" : "False");
                AddPropertyRow("ANSI_PADDING", so.AnsiPadding ? "True" : "False");
                AddPropertyRow("ANSI_WARNINGS", so.AnsiWarnings ? "True" : "False");
                AddPropertyRow("ARITHABORT", so.ArithAbort ? "True" : "False");
                AddPropertyRow("CONCAT_NULL", so.ConcatNullYieldsNull ? "True" : "False");
                AddPropertyRow("NUMERIC_ROUNDABORT", so.NumericRoundAbort ? "True" : "False");
                AddPropertyRow("QUOTED_IDENTIFIER", so.QuotedIdentifier ? "True" : "False");
            }

            // === Optimizer Hardware Properties ===
            if (s.HardwareProperties != null)
            {
                var hw = s.HardwareProperties;
                AddPropertySection("Hardware Properties");
                AddPropertyRow("Available Memory", $"{hw.EstimatedAvailableMemoryGrant:N0} KB");
                AddPropertyRow("Pages Cached", $"{hw.EstimatedPagesCached:N0}");
                AddPropertyRow("Available DOP", $"{hw.EstimatedAvailableDOP}");
                if (hw.MaxCompileMemory > 0)
                    AddPropertyRow("Max Compile Memory", $"{hw.MaxCompileMemory:N0} KB");
            }

            // === Plan Version ===
            if (_currentPlan != null && (!string.IsNullOrEmpty(_currentPlan.BuildVersion) || !string.IsNullOrEmpty(_currentPlan.Build)))
            {
                AddPropertySection("Plan Version");
                if (!string.IsNullOrEmpty(_currentPlan.BuildVersion))
                    AddPropertyRow("Build Version", _currentPlan.BuildVersion);
                if (!string.IsNullOrEmpty(_currentPlan.Build))
                    AddPropertyRow("Build", _currentPlan.Build);
                if (_currentPlan.ClusteredMode)
                    AddPropertyRow("Clustered Mode", "True");
            }

            // === Optimizer Stats Usage ===
            if (s.StatsUsage.Count > 0)
            {
                AddPropertySection("Statistics Used");
                foreach (var stat in s.StatsUsage)
                {
                    var statLabel = !string.IsNullOrEmpty(stat.TableName)
                        ? $"{stat.TableName}.{stat.StatisticsName}"
                        : stat.StatisticsName;
                    var statDetail = $"Modified: {stat.ModificationCount:N0}, Sampled: {stat.SamplingPercent:F1}%";
                    if (!string.IsNullOrEmpty(stat.LastUpdate))
                        statDetail += $", Updated: {stat.LastUpdate}";
                    AddPropertyRow(statLabel, statDetail);
                }
            }

            // === Parameters ===
            if (s.Parameters.Count > 0)
            {
                AddPropertySection("Parameters");
                foreach (var p in s.Parameters)
                {
                    var paramText = p.DataType;
                    if (!string.IsNullOrEmpty(p.CompiledValue))
                        paramText += $", Compiled: {p.CompiledValue}";
                    if (!string.IsNullOrEmpty(p.RuntimeValue))
                        paramText += $", Runtime: {p.RuntimeValue}";
                    AddPropertyRow(p.Name, paramText);
                }
            }

            // === Query Time Stats (actual plans) ===
            if (s.QueryTimeStats != null)
            {
                AddPropertySection("Query Time Stats");
                AddPropertyRow("CPU Time", $"{s.QueryTimeStats.CpuTimeMs:N0} ms");
                AddPropertyRow("Elapsed Time", $"{s.QueryTimeStats.ElapsedTimeMs:N0} ms");
                if (s.QueryUdfCpuTimeMs > 0)
                    AddPropertyRow("UDF CPU Time", $"{s.QueryUdfCpuTimeMs:N0} ms");
                if (s.QueryUdfElapsedTimeMs > 0)
                    AddPropertyRow("UDF Elapsed Time", $"{s.QueryUdfElapsedTimeMs:N0} ms");
            }

            // === Thread Stats (actual plans) ===
            if (s.ThreadStats != null)
            {
                AddPropertySection("Thread Stats");
                AddPropertyRow("Branches", $"{s.ThreadStats.Branches}");
                AddPropertyRow("Used Threads", $"{s.ThreadStats.UsedThreads}");
                var totalReserved = s.ThreadStats.Reservations.Sum(r => r.ReservedThreads);
                if (totalReserved > 0)
                {
                    AddPropertyRow("Reserved Threads", $"{totalReserved}");
                    if (totalReserved > s.ThreadStats.UsedThreads)
                        AddPropertyRow("Inactive Threads", $"{totalReserved - s.ThreadStats.UsedThreads}");
                }
                foreach (var res in s.ThreadStats.Reservations)
                    AddPropertyRow($"  Node {res.NodeId}", $"{res.ReservedThreads} reserved");
            }

            // === Wait Stats (actual plans) ===
            if (s.WaitStats.Count > 0)
            {
                AddPropertySection("Wait Stats");
                foreach (var w in s.WaitStats.OrderByDescending(w => w.WaitTimeMs))
                    AddPropertyRow(w.WaitType, $"{w.WaitTimeMs:N0} ms ({w.WaitCount:N0} waits)");
            }

            // === Trace Flags ===
            if (s.TraceFlags.Count > 0)
            {
                AddPropertySection("Trace Flags");
                foreach (var tf in s.TraceFlags)
                {
                    var tfLabel = $"TF {tf.Value}";
                    var tfDetail = $"{tf.Scope}{(tf.IsCompileTime ? ", Compile-time" : ", Runtime")}";
                    AddPropertyRow(tfLabel, tfDetail);
                }
            }

            // === Indexed Views ===
            if (s.IndexedViews.Count > 0)
            {
                AddPropertySection("Indexed Views");
                foreach (var iv in s.IndexedViews)
                    AddPropertyRow("View", iv, isCode: true);
            }

            // === Plan-Level Warnings ===
            if (s.PlanWarnings.Count > 0)
            {
                var planWarningsPanel = new StackPanel();
                var sortedPlanWarnings = s.PlanWarnings
                    .OrderByDescending(w => w.MaxBenefitPercent ?? -1)
                    .ThenByDescending(w => w.Severity)
                    .ThenBy(w => w.WarningType);
                foreach (var w in sortedPlanWarnings)
                {
                    var warnColor = w.Severity == PlanWarningSeverity.Critical ? "#E57373"
                        : w.Severity == PlanWarningSeverity.Warning ? "#FFB347" : "#6BB5FF";
                    var warnPanel = new StackPanel { Margin = new Thickness(10, 2, 10, 2) };
                    var legacyTag = w.IsLegacy ? " [legacy]" : "";
                    var planWarnHeader = w.MaxBenefitPercent.HasValue
                        ? $"\u26A0 {w.WarningType}{legacyTag} \u2014 up to {FormatBenefitPercent(w.MaxBenefitPercent.Value)}% benefit"
                        : $"\u26A0 {w.WarningType}{legacyTag}";
                    warnPanel.Children.Add(new TextBlock
                    {
                        Text = planWarnHeader,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse(warnColor))
                    });
                    warnPanel.Children.Add(new TextBlock
                    {
                        Text = w.Message,
                        FontSize = 11,
                        Foreground = TooltipFgBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(16, 0, 0, 0)
                    });
                    if (!string.IsNullOrEmpty(w.ActionableFix))
                    {
                        warnPanel.Children.Add(new TextBlock
                        {
                            Text = w.ActionableFix,
                            FontSize = 11,
                            FontStyle = FontStyle.Italic,
                            Foreground = TooltipFgBrush,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(16, 2, 0, 0)
                        });
                    }
                    planWarningsPanel.Children.Add(warnPanel);
                }

                var planWarningsExpander = new Expander
                {
                    IsExpanded = true,
                    Header = new TextBlock
                    {
                        Text = "Plan Warnings",
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 11,
                        Foreground = SectionHeaderBrush
                    },
                    Content = planWarningsPanel,
                    Margin = new Thickness(0, 2, 0, 0),
                    Padding = new Thickness(0),
                    Foreground = SectionHeaderBrush,
                    Background = new SolidColorBrush(Color.FromArgb(0x18, 0x4F, 0xA3, 0xFF)),
                    BorderBrush = PropSeparatorBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                PropertiesContent.Children.Add(planWarningsExpander);
            }

            // === Missing Indexes ===
            if (s.MissingIndexes.Count > 0)
            {
                AddPropertySection("Missing Indexes");
                foreach (var mi in s.MissingIndexes)
                {
                    AddPropertyRow($"{mi.Schema}.{mi.Table}", $"Impact: {mi.Impact:F1}%");
                    if (!string.IsNullOrEmpty(mi.CreateStatement))
                        AddPropertyRow("CREATE INDEX", mi.CreateStatement, isCode: true);
                }
            }
        }

        // === Warnings ===
        if (node.HasWarnings)
        {
            var warningsPanel = new StackPanel();
            var sortedNodeWarnings = node.Warnings
                .OrderByDescending(w => w.MaxBenefitPercent ?? -1)
                .ThenByDescending(w => w.Severity)
                .ThenBy(w => w.WarningType);
            foreach (var w in sortedNodeWarnings)
            {
                var warnColor = w.Severity == PlanWarningSeverity.Critical ? "#E57373"
                    : w.Severity == PlanWarningSeverity.Warning ? "#FFB347" : "#6BB5FF";
                var warnPanel = new StackPanel { Margin = new Thickness(10, 2, 10, 2) };
                var nodeLegacyTag = w.IsLegacy ? " [legacy]" : "";
                var nodeWarnHeader = w.MaxBenefitPercent.HasValue
                    ? $"\u26A0 {w.WarningType}{nodeLegacyTag} \u2014 up to {FormatBenefitPercent(w.MaxBenefitPercent.Value)}% benefit"
                    : $"\u26A0 {w.WarningType}{nodeLegacyTag}";
                warnPanel.Children.Add(new TextBlock
                {
                    Text = nodeWarnHeader,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse(warnColor))
                });
                warnPanel.Children.Add(new TextBlock
                {
                    Text = w.Message,
                    FontSize = 11,
                    Foreground = TooltipFgBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 0, 0, 0)
                });
                warningsPanel.Children.Add(warnPanel);
            }

            var warningsExpander = new Expander
            {
                IsExpanded = true,
                Header = new TextBlock
                {
                    Text = "Warnings",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 11,
                    Foreground = SectionHeaderBrush
                },
                Content = warningsPanel,
                Margin = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(0),
                Foreground = SectionHeaderBrush,
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x4F, 0xA3, 0xFF)),
                BorderBrush = PropSeparatorBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            PropertiesContent.Children.Add(warningsExpander);
        }

        // Show the panel
        _propertiesColumn.Width = new GridLength(320);
        _splitterColumn.Width = new GridLength(5);
        PropertiesSplitter.IsVisible = true;
        PropertiesPanel.IsVisible = true;
    }

    private void AddPropertySection(string title)
    {
        var labelCol = new ColumnDefinition { Width = new GridLength(_propertyLabelWidth) };
        _sectionLabelColumns.Add(labelCol);

        // Sync column widths across sections when user drags the GridSplitter
        labelCol.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name != "Width" || _isSyncingColumnWidth) return;
            _isSyncingColumnWidth = true;
            _propertyLabelWidth = labelCol.Width.Value;
            foreach (var col in _sectionLabelColumns)
            {
                if (col != labelCol)
                    col.Width = labelCol.Width;
            }
            _isSyncingColumnWidth = false;
        };

        var sectionGrid = new Grid
        {
            Margin = new Thickness(6, 0, 6, 0)
        };
        sectionGrid.ColumnDefinitions.Add(labelCol);
        sectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        sectionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _currentSectionGrid = sectionGrid;
        _currentSectionRowIndex = 0;

        var expander = new Expander
        {
            IsExpanded = true,
            Header = new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 11,
                Foreground = SectionHeaderBrush
            },
            Content = sectionGrid,
            Margin = new Thickness(0, 2, 0, 0),
            Padding = new Thickness(0),
            Foreground = SectionHeaderBrush,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x4F, 0xA3, 0xFF)),
            BorderBrush = PropSeparatorBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        PropertiesContent.Children.Add(expander);
    }

    private void AddPropertyRow(string label, string value, bool isCode = false, bool indent = false)
    {
        if (_currentSectionGrid == null) return;

        var row = _currentSectionRowIndex++;
        _currentSectionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = indent ? 10 : 11,
            Foreground = TooltipFgBrush,
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent ? 16 : 4, 2, 0, 2)
        };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetRow(labelBlock, row);
        _currentSectionGrid.Children.Add(labelBlock);

        // GridSplitter in column 1 (only in first row per section)
        if (row == 0)
        {
            var splitter = new GridSplitter
            {
                Width = 4,
                Background = Brushes.Transparent,
                Foreground = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
            };
            Grid.SetColumn(splitter, 1);
            Grid.SetRow(splitter, 0);
            Grid.SetRowSpan(splitter, 100); // span all rows
            _currentSectionGrid.Children.Add(splitter);
        }

        var valueBox = new TextBox
        {
            Text = value,
            FontSize = indent ? 10 : 11,
            Foreground = TooltipFgBrush,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Top
        };
        if (isCode) valueBox.FontFamily = new FontFamily("Consolas");
        Grid.SetColumn(valueBox, 2);
        Grid.SetRow(valueBox, row);
        _currentSectionGrid.Children.Add(valueBox);
    }

    private void CloseProperties_Click(object? sender, RoutedEventArgs e)
    {
        ClosePropertiesPanel();
    }

    private void ClosePropertiesPanel()
    {
        PropertiesPanel.IsVisible = false;
        PropertiesSplitter.IsVisible = false;
        _propertiesColumn.Width = new GridLength(0);
        _splitterColumn.Width = new GridLength(0);

        // Deselect node
        if (_selectedNodeBorder != null)
        {
            _selectedNodeBorder.BorderBrush = _selectedNodeOriginalBorder;
            _selectedNodeBorder.BorderThickness = _selectedNodeOriginalThickness;
            _selectedNodeBorder = null;
        }
    }

    private void ShowMissingIndexes(List<MissingIndex> indexes)
    {
        MissingIndexContent.Children.Clear();

        if (indexes.Count > 0)
        {
            // Update expander header with count
            MissingIndexHeader.Text = $"  Missing Index Suggestions ({indexes.Count})";

            // Build each missing index row manually (no ItemsControl template binding)
            foreach (var mi in indexes)
            {
                var itemPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

                var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
                headerRow.Children.Add(new TextBlock
                {
                    Text = mi.Table,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
                    FontSize = 12
                });
                headerRow.Children.Add(new TextBlock
                {
                    Text = $" \u2014 Impact: ",
                    Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
                    FontSize = 12
                });
                headerRow.Children.Add(new TextBlock
                {
                    Text = $"{mi.Impact:F1}%",
                    Foreground = new SolidColorBrush(Color.Parse("#FFB347")),
                    FontSize = 12
                });
                itemPanel.Children.Add(headerRow);

                if (!string.IsNullOrEmpty(mi.CreateStatement))
                {
                    itemPanel.Children.Add(new SelectableTextBlock
                    {
                        Text = mi.CreateStatement,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = TooltipFgBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(12, 2, 0, 0)
                    });
                }

                MissingIndexContent.Children.Add(itemPanel);
            }

            MissingIndexEmpty.IsVisible = false;
        }
        else
        {
            MissingIndexHeader.Text = "Missing Index Suggestions";
            MissingIndexEmpty.IsVisible = true;
        }
    }

    private void ShowParameters(PlanStatement statement)
    {
        ParametersContent.Children.Clear();
        ParametersEmpty.IsVisible = false;

        var parameters = statement.Parameters;

        if (parameters.Count == 0)
        {
            var localVars = FindUnresolvedVariables(statement.StatementText, parameters, statement.RootNode);
            if (localVars.Count > 0)
            {
                ParametersHeader.Text = "Parameters";
                AddParameterAnnotation(
                    $"Local variables detected ({string.Join(", ", localVars)}) — values not captured in plan XML",
                    "#FFB347");
            }
            else
            {
                ParametersHeader.Text = "Parameters";
                ParametersEmpty.IsVisible = true;
            }
            return;
        }

        ParametersHeader.Text = $"Parameters ({parameters.Count})";

        var allCompiledNull = parameters.All(p => p.CompiledValue == null);
        var hasCompiled = parameters.Any(p => p.CompiledValue != null);
        var hasRuntime = parameters.Any(p => p.RuntimeValue != null);

        // Build a 4-column grid: Name | Data Type | Compiled | Runtime
        // Only show Compiled/Runtime columns if at least one param has that value
        var colDef = "Auto,Auto"; // Name, DataType always shown
        int compiledCol = -1, runtimeCol = -1;
        int nextCol = 2;
        if (hasCompiled)
        {
            colDef += ",*";
            compiledCol = nextCol++;
        }
        if (hasRuntime)
        {
            colDef += ",*";
            runtimeCol = nextCol++;
        }
        // If neither compiled nor runtime, still add one value column for "?"
        if (!hasCompiled && !hasRuntime)
        {
            colDef += ",*";
            compiledCol = nextCol++;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(colDef) };
        int rowIndex = 0;

        // Header row
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddParamCell(grid, rowIndex, 0, "Parameter", "#7BCF7B", FontWeight.SemiBold);
        AddParamCell(grid, rowIndex, 1, "Data Type", "#7BCF7B", FontWeight.SemiBold);
        if (compiledCol >= 0)
            AddParamCell(grid, rowIndex, compiledCol, hasCompiled ? "Compiled" : "Value", "#7BCF7B", FontWeight.SemiBold);
        if (runtimeCol >= 0)
            AddParamCell(grid, rowIndex, runtimeCol, "Runtime", "#7BCF7B", FontWeight.SemiBold);
        rowIndex++;

        foreach (var param in parameters)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Name
            AddParamCell(grid, rowIndex, 0, param.Name, "#E4E6EB", FontWeight.SemiBold);

            // Data type
            AddParamCell(grid, rowIndex, 1, param.DataType, "#E4E6EB");

            // Compiled value
            if (compiledCol >= 0)
            {
                var compiledText = param.CompiledValue ?? (allCompiledNull ? "" : "?");
                var compiledColor = param.CompiledValue != null ? "#E4E6EB"
                    : allCompiledNull ? "#E4E6EB" : "#E57373";
                AddParamCell(grid, rowIndex, compiledCol, compiledText, compiledColor);
            }

            // Runtime value — amber if it differs from compiled
            if (runtimeCol >= 0)
            {
                var runtimeText = param.RuntimeValue ?? "";
                var sniffed = param.RuntimeValue != null
                    && param.CompiledValue != null
                    && param.RuntimeValue != param.CompiledValue;
                var runtimeColor = sniffed ? "#FFB347" : "#E4E6EB";
                var tooltip = sniffed
                    ? "Runtime value differs from compiled — possible parameter sniffing"
                    : null;
                AddParamCell(grid, rowIndex, runtimeCol, runtimeText, runtimeColor, tooltip: tooltip);
            }

            rowIndex++;
        }

        ParametersContent.Children.Add(grid);

        // Annotations
        if (allCompiledNull && parameters.Count > 0)
        {
            var hasOptimizeForUnknown = statement.StatementText
                .Contains("OPTIMIZE", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(statement.StatementText, @"OPTIMIZE\s+FOR\s+UNKNOWN", RegexOptions.IgnoreCase);

            if (hasOptimizeForUnknown)
            {
                AddParameterAnnotation(
                    "OPTIMIZE FOR UNKNOWN — optimizer used average density estimates instead of sniffed values",
                    "#6BB5FF");
            }
            else
            {
                AddParameterAnnotation(
                    "OPTION(RECOMPILE) — parameter values embedded as literals, not sniffed",
                    "#FFB347");
            }
        }

        var unresolved = FindUnresolvedVariables(statement.StatementText, parameters, statement.RootNode);
        if (unresolved.Count > 0)
        {
            AddParameterAnnotation(
                $"Unresolved variables: {string.Join(", ", unresolved)} — not in parameter list",
                "#FFB347");
        }
    }

    private static void AddParamCell(Grid grid, int row, int col, string text, string color,
        FontWeight fontWeight = default, string? tooltip = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = fontWeight == default ? FontWeight.Normal : fontWeight,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            Margin = new Thickness(0, 2, 10, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200
        };
        // Name and DataType columns are short — no need for max width
        if (col <= 1)
            tb.MaxWidth = double.PositiveInfinity;
        if (tooltip != null)
            ToolTip.SetTip(tb, tooltip);
        else if (text.Length > 30)
            ToolTip.SetTip(tb, text);
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private void AddParameterAnnotation(string text, string color)
    {
        ParametersContent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
    }

    private static List<string> FindUnresolvedVariables(string queryText, List<PlanParameter> parameters,
        PlanNode? rootNode = null)
    {
        var unresolved = new List<string>();
        if (string.IsNullOrEmpty(queryText))
            return unresolved;

        var extractedNames = new HashSet<string>(
            parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        // Collect table variable names from the plan tree so we don't misreport them as local variables
        var tableVarNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootNode != null)
            CollectTableVariableNames(rootNode, tableVarNames);

        var matches = Regex.Matches(queryText, @"@\w+", RegexOptions.IgnoreCase);
        var seenVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var varName = match.Value;
            if (seenVars.Contains(varName) || extractedNames.Contains(varName))
                continue;
            if (varName.StartsWith("@@", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tableVarNames.Contains(varName))
                continue;

            seenVars.Add(varName);
            unresolved.Add(varName);
        }

        return unresolved;
    }

    private static void CollectTableVariableNames(PlanNode node, HashSet<string> names)
    {
        if (!string.IsNullOrEmpty(node.ObjectName) && node.ObjectName.StartsWith("@"))
        {
            // ObjectName is like "@t.c" — extract the table variable name "@t"
            var dotIdx = node.ObjectName.IndexOf('.');
            var tvName = dotIdx > 0 ? node.ObjectName[..dotIdx] : node.ObjectName;
            names.Add(tvName);
        }
        foreach (var child in node.Children)
            CollectTableVariableNames(child, names);
    }

    private void ShowWaitStats(List<WaitStatInfo> waits, List<WaitBenefit> benefits, bool isActualPlan)
    {
        WaitStatsContent.Children.Clear();

        if (waits.Count == 0)
        {
            WaitStatsHeader.Text = "Wait Stats";
            WaitStatsEmpty.Text = isActualPlan
                ? "No wait stats recorded"
                : "No wait stats (estimated plan)";
            WaitStatsEmpty.IsVisible = true;
            return;
        }

        WaitStatsEmpty.IsVisible = false;

        // Build benefit lookup
        var benefitLookup = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var wb in benefits)
            benefitLookup[wb.WaitType] = wb.MaxBenefitPercent;

        var sorted = waits.OrderByDescending(w => w.WaitTimeMs).ToList();
        var maxWait = sorted[0].WaitTimeMs;
        var totalWait = sorted.Sum(w => w.WaitTimeMs);

        // Update expander header with total
        WaitStatsHeader.Text = $"  Wait Stats \u2014 {totalWait:N0}ms total";

        // Build a single Grid for all rows so columns align
        // Name, bar, duration, and benefit columns
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };
        for (int i = 0; i < sorted.Count; i++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int i = 0; i < sorted.Count; i++)
        {
            var w = sorted[i];
            var barFraction = maxWait > 0 ? (double)w.WaitTimeMs / maxWait : 0;
            var color = GetWaitCategoryColor(GetWaitCategory(w.WaitType));

            // Wait type name — colored by category
            var nameText = new TextBlock
            {
                Text = w.WaitType,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(color)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2)
            };
            Grid.SetRow(nameText, i);
            Grid.SetColumn(nameText, 0);
            grid.Children.Add(nameText);

            // Bar — semi-transparent category color, compact proportional indicator
            var barColor = Color.Parse(color);
            var colorBar = new Border
            {
                Width = Math.Max(4, barFraction * 60),
                Height = 14,
                Background = new SolidColorBrush(Color.FromArgb(0x60, barColor.R, barColor.G, barColor.B)),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 8, 2)
            };
            Grid.SetRow(colorBar, i);
            Grid.SetColumn(colorBar, 1);
            grid.Children.Add(colorBar);

            // Duration text
            var durationText = new TextBlock
            {
                Text = $"{w.WaitTimeMs:N0}ms ({w.WaitCount:N0} waits)",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 8, 2)
            };
            Grid.SetRow(durationText, i);
            Grid.SetColumn(durationText, 2);
            grid.Children.Add(durationText);

            // Benefit % (if available)
            if (benefitLookup.TryGetValue(w.WaitType, out var benefitPct) && benefitPct > 0)
            {
                var benefitText = new TextBlock
                {
                    Text = $"up to {benefitPct:N0}%",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#8b949e")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                Grid.SetRow(benefitText, i);
                Grid.SetColumn(benefitText, 3);
                grid.Children.Add(benefitText);
            }
        }

        WaitStatsContent.Children.Add(grid);

    }

    private void ShowRuntimeSummary(PlanStatement statement)
    {
        RuntimeSummaryContent.Children.Clear();

        var labelColor = "#E4E6EB";
        var valueColor = "#E4E6EB";

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        int rowIndex = 0;

        void AddRow(string label, string value, string? color = null)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(labelColor)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 8, 1)
            };
            Grid.SetRow(labelText, rowIndex);
            Grid.SetColumn(labelText, 0);
            grid.Children.Add(labelText);

            var valueText = new TextBlock
            {
                Text = value,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(color ?? valueColor)),
                Margin = new Thickness(0, 1, 0, 1)
            };
            Grid.SetRow(valueText, rowIndex);
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);

            rowIndex++;
        }

        // Efficiency thresholds: white >= 40%, orange >= 20%, red < 20%.
        // Loosened per Joe's feedback (#215 C1): for memory grants, moderate
        // utilization (e.g. 60%) is fine — operators can spill near their max,
        // so we shouldn't flag anything above a real over-grant threshold.
        static string EfficiencyColor(double pct) => pct >= 40 ? "#E4E6EB"
            : pct >= 20 ? "#FFB347" : "#E57373";

        // Memory grant color tiers (#215 C1 + E8 + E9): over-used grant (red),
        // any operator spilled (orange), otherwise tier by utilization.
        static string MemoryGrantColor(double pctUsed, bool hasSpill)
        {
            if (pctUsed > 100) return "#E57373";
            if (hasSpill) return "#FFB347";
            if (pctUsed >= 40) return "#E4E6EB";
            if (pctUsed >= 20) return "#FFB347";
            return "#E57373";
        }

        // E7: rename the panel title for estimated plans
        var isEstimated = statement.QueryTimeStats == null;
        RuntimeSummaryTitle.Text = isEstimated ? "Predicted Runtime" : "Runtime Summary";

        var hasSpillInTree = statement.RootNode != null && HasSpillInPlanTree(statement.RootNode);

        // E11: order — Elapsed → CPU:Elapsed → DOP → CPU → Compile → Memory → Used → Optimization → CE Model → Cost.
        // Extra Avalonia-only rows (threads, UDF, cached plan size) kept near their logical neighbors.

        if (statement.QueryTimeStats != null)
        {
            AddRow("Elapsed", $"{statement.QueryTimeStats.ElapsedTimeMs:N0}ms");
            if (statement.QueryTimeStats.ElapsedTimeMs > 0)
            {
                long externalWaitMs = 0;
                foreach (var w in statement.WaitStats)
                    if (BenefitScorer.IsExternalWait(w.WaitType))
                        externalWaitMs += w.WaitTimeMs;
                var effectiveCpu = Math.Max(0L, statement.QueryTimeStats.CpuTimeMs - externalWaitMs);
                var ratio = (double)effectiveCpu / statement.QueryTimeStats.ElapsedTimeMs;
                AddRow("CPU:Elapsed", ratio.ToString("N2"));
            }
        }

        // DOP + parallelism efficiency
        if (statement.DegreeOfParallelism > 0)
        {
            var dopText = statement.DegreeOfParallelism.ToString();
            string? dopColor = null;
            if (statement.QueryTimeStats != null &&
                statement.QueryTimeStats.ElapsedTimeMs > 0 &&
                statement.QueryTimeStats.CpuTimeMs > 0 &&
                statement.DegreeOfParallelism > 1)
            {
                long externalWaitMs = 0;
                foreach (var w in statement.WaitStats)
                    if (BenefitScorer.IsExternalWait(w.WaitType))
                        externalWaitMs += w.WaitTimeMs;
                var effectiveCpu = Math.Max(0, statement.QueryTimeStats.CpuTimeMs - externalWaitMs);
                var speedup = (double)effectiveCpu / statement.QueryTimeStats.ElapsedTimeMs;
                var efficiency = Math.Min(100.0, (speedup - 1.0) / (statement.DegreeOfParallelism - 1.0) * 100.0);
                efficiency = Math.Max(0.0, efficiency);
                dopText += $" ({efficiency:N0}% efficient)";
                dopColor = EfficiencyColor(efficiency);
            }
            AddRow("DOP", dopText, dopColor);
        }
        else if (statement.NonParallelPlanReason != null)
            AddRow("Serial", statement.NonParallelPlanReason);

        if (statement.QueryTimeStats != null)
        {
            AddRow("CPU", $"{statement.QueryTimeStats.CpuTimeMs:N0}ms");
            if (statement.QueryUdfCpuTimeMs > 0)
                AddRow("UDF CPU", $"{statement.QueryUdfCpuTimeMs:N0}ms");
            if (statement.QueryUdfElapsedTimeMs > 0)
                AddRow("UDF elapsed", $"{statement.QueryUdfElapsedTimeMs:N0}ms");
        }

        // Compile stats (category B plan-level property)
        if (statement.CompileTimeMs > 0)
            AddRow("Compile", $"{statement.CompileTimeMs:N0}ms");
        if (statement.CachedPlanSizeKB > 0)
            AddRow("Cached plan size", $"{statement.CachedPlanSizeKB:N0} KB");

        // Memory grant — color per new tiers, spill indicator if any operator spilled
        if (statement.MemoryGrant != null)
        {
            var mg = statement.MemoryGrant;
            var grantPct = mg.GrantedMemoryKB > 0
                ? (double)mg.MaxUsedMemoryKB / mg.GrantedMemoryKB * 100 : 100;
            var grantColor = MemoryGrantColor(grantPct, hasSpillInTree);
            var spillTag = hasSpillInTree ? " ⚠ spill" : "";
            AddRow("Memory grant",
                $"{TextFormatter.FormatMemoryGrantKB(mg.GrantedMemoryKB)} granted, {TextFormatter.FormatMemoryGrantKB(mg.MaxUsedMemoryKB)} used ({grantPct:N0}%){spillTag}",
                grantColor);
            if (mg.GrantWaitTimeMs > 0)
                AddRow("Grant wait", $"{mg.GrantWaitTimeMs:N0}ms", "#E57373");
        }

        // Thread stats
        if (statement.ThreadStats != null)
        {
            var ts = statement.ThreadStats;
            AddRow("Branches", ts.Branches.ToString());
            var totalReserved = ts.Reservations.Sum(r => r.ReservedThreads);
            if (totalReserved > 0)
            {
                var threadPct = (double)ts.UsedThreads / totalReserved * 100;
                var threadColor = EfficiencyColor(threadPct);
                var threadText = ts.UsedThreads == totalReserved
                    ? $"{ts.UsedThreads} used ({totalReserved} reserved)"
                    : $"{ts.UsedThreads} used of {totalReserved} reserved ({totalReserved - ts.UsedThreads} inactive)";
                AddRow("Threads", threadText, threadColor);
            }
            else
            {
                AddRow("Threads", $"{ts.UsedThreads} used");
            }
        }

        // Optimization + CE model
        if (!string.IsNullOrEmpty(statement.StatementOptmLevel))
            AddRow("Optimization", statement.StatementOptmLevel);
        if (!string.IsNullOrEmpty(statement.StatementOptmEarlyAbortReason))
            AddRow("Early abort", statement.StatementOptmEarlyAbortReason);
        if (statement.CardinalityEstimationModelVersion > 0)
            AddRow("CE model", statement.CardinalityEstimationModelVersion.ToString());

        if (grid.Children.Count > 0)
        {
            RuntimeSummaryContent.Children.Add(grid);
            RuntimeSummaryEmpty.IsVisible = false;
        }
        else
        {
            RuntimeSummaryEmpty.IsVisible = true;
        }
        ShowServerContext();
    }

    private void ShowServerContext()
    {
        ServerContextContent.Children.Clear();
        if (_serverMetadata == null)
        {
            ServerContextEmpty.IsVisible = true;
            ServerContextBorder.IsVisible = true;
            return;
        }

        ServerContextEmpty.IsVisible = false;

        var m = _serverMetadata;
        var fgColor = "#E4E6EB";

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        int rowIndex = 0;

        void AddRow(string label, string value)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var lb = new TextBlock
            {
                Text = label, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(fgColor)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 8, 1)
            };
            Grid.SetRow(lb, rowIndex);
            Grid.SetColumn(lb, 0);
            grid.Children.Add(lb);

            var vb = new TextBlock
            {
                Text = value, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(fgColor)),
                Margin = new Thickness(0, 1, 0, 1)
            };
            Grid.SetRow(vb, rowIndex);
            Grid.SetColumn(vb, 1);
            grid.Children.Add(vb);
            rowIndex++;
        }

        // Server name + edition
        var edition = m.Edition;
        if (edition != null)
        {
            var idx = edition.IndexOf(" (64-bit)");
            if (idx > 0) edition = edition[..idx];
        }
        var serverLine = m.ServerName ?? "Unknown";
        if (edition != null) serverLine += $" ({edition})";
        if (m.ProductVersion != null) serverLine += $", {m.ProductVersion}";
        AddRow("Server", serverLine);

        // Hardware
        if (m.CpuCount > 0)
            AddRow("Hardware", $"{m.CpuCount} CPUs, {m.PhysicalMemoryMB:N0} MB RAM");

        // Instance settings
        AddRow("MAXDOP", m.MaxDop.ToString());
        AddRow("Cost threshold", m.CostThresholdForParallelism.ToString());
        AddRow("Max memory", $"{m.MaxServerMemoryMB:N0} MB");

        // Database
        if (m.Database != null)
            AddRow("Database", $"{m.Database.Name} (compat {m.Database.CompatibilityLevel})");

        ServerContextContent.Children.Add(grid);
        ServerContextBorder.IsVisible = true;
    }

    private void UpdateInsightsHeader()
    {
        InsightsPanel.IsVisible = true;
        InsightsHeader.Text = "  Plan Insights";
    }

    private static string GetWaitCategory(string waitType)
    {
        if (waitType.StartsWith("SOS_SCHEDULER_YIELD") ||
            waitType.StartsWith("CXPACKET") ||
            waitType.StartsWith("CXCONSUMER") ||
            waitType.StartsWith("CXSYNC_PORT") ||
            waitType.StartsWith("CXSYNC_CONSUMER"))
            return "CPU";

        if (waitType.StartsWith("PAGEIOLATCH") ||
            waitType.StartsWith("WRITELOG") ||
            waitType.StartsWith("IO_COMPLETION") ||
            waitType.StartsWith("ASYNC_IO_COMPLETION"))
            return "I/O";

        if (waitType.StartsWith("LCK_M_"))
            return "Lock";

        if (waitType == "RESOURCE_SEMAPHORE" || waitType == "CMEMTHREAD")
            return "Memory";

        if (waitType == "ASYNC_NETWORK_IO")
            return "Network";

        return "Other";
    }

    private static string GetWaitCategoryColor(string category)
    {
        return category switch
        {
            "CPU" => "#4FA3FF",
            "I/O" => "#FFB347",
            "Lock" => "#E57373",
            "Memory" => "#9B59B6",
            "Network" => "#2ECC71",
            _ => "#6BB5FF"
        };
    }
}
