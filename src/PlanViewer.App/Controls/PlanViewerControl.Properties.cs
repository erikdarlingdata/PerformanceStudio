using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    // Properties panel sizing. The width is static so a width the user drags out survives
    // closing the panel and switching plan tabs, the same way the minimap remembers its size.
    private const double DefaultPropertiesWidth = 380;
    private const double MinPropertiesWidth = 280;
    private const double MaxPropertiesWidth = 800;
    private const double PropertiesSplitterWidth = 6;
    private static double _propertiesPanelWidth = DefaultPropertiesWidth;
    private bool _propertiesChromeWired;

    // Accent fill for the properties splitter while the pointer is over it.
    private static readonly SolidColorBrush SplitterHoverBrush = new(Color.FromRgb(0x2E, 0xAE, 0xF1));

    // The amber this panel already uses for Warning-severity warnings.
    private static readonly SolidColorBrush PropWarningBrush = new(Color.FromRgb(0xFF, 0xB3, 0x47));

    private static readonly FontFamily CodeFontFamily = new("Consolas");

    /// <summary>
    /// One row of the properties panel: what it says, what the filter box matches it against,
    /// and what the copy menu hands back. Recorded while the panel is built because the panel
    /// is raw controls with no bindings behind them, so once a row is in the visual tree its
    /// text is the only thing left to work from.
    /// </summary>
    private sealed class PropertyPanelRow
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
        public bool IsCode { get; init; }

        /// <summary>
        /// Plain text for rows that are not a label/value pair - the per-thread breakdown.
        /// Null for ordinary rows, which the copy menu renders from Label and Value.
        /// </summary>
        public string? BlockText { get; init; }

        public string SearchText { get; init; } = "";

        /// <summary>Every control the row occupies, so the filter can hide all of them.</summary>
        public List<Control> Controls { get; } = new();
    }

    private sealed class PropertyPanelSection
    {
        public string Title { get; init; } = "";
        public Expander Expander { get; init; } = null!;
        public List<PropertyPanelRow> Rows { get; } = new();
    }

    private readonly List<PropertyPanelSection> _propertySections = new();
    private PropertyPanelSection? _currentSection;

    private void ShowPropertiesPanel(PlanNode node)
    {
        EnsurePropertiesChrome();
        PropertiesContent.Children.Clear();
        _sectionLabelColumns.Clear();
        _propertySections.Clear();
        _currentSection = null;
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
        AddPropertyRow("Operator Cost", $"{MetricFormatter.FormatCost(node.EstimatedOperatorCost)} ({node.CostPercent}%)");
        AddPropertyRow("Subtree Cost", MetricFormatter.FormatCost(node.EstimatedTotalSubtreeCost));
        AddPropertyRow("I/O Cost", MetricFormatter.FormatCost(node.EstimateIO));
        AddPropertyRow("CPU Cost", MetricFormatter.FormatCost(node.EstimateCPU));

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
            if (node.ActualRowsRead > 0)
                AddPropertyRow("Actual Rows Read", $"{node.ActualRowsRead:N0}");
            AddPropertyRow("Actual Executions", $"{node.ActualExecutions:N0}");
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

            // Rows and executions list every thread, idle ones included: a thread sitting at
            // zero while its siblings work is the whole point of looking at the breakdown.
            AddPerThreadBreakdown(node,
                ("Rows", t => t.ActualRows, true, ""),
                ("Rows Read", t => t.ActualRowsRead, false, ""),
                ("Executions", t => t.ActualExecutions, true, ""));

            // Timing
            if (node.ActualElapsedMs > 0 || node.ActualCPUMs > 0
                || node.UdfCpuTimeMs > 0 || node.UdfElapsedTimeMs > 0)
            {
                AddPropertySection("Actual Timing");
                if (node.ActualElapsedMs > 0)
                    AddPropertyRow("Elapsed Time", $"{node.ActualElapsedMs:N0} ms");
                if (node.ActualCPUMs > 0)
                    AddPropertyRow("CPU Time", $"{node.ActualCPUMs:N0} ms");
                if (node.UdfElapsedTimeMs > 0)
                    AddPropertyRow("UDF Elapsed", $"{node.UdfElapsedTimeMs:N0} ms");
                if (node.UdfCpuTimeMs > 0)
                    AddPropertyRow("UDF CPU", $"{node.UdfCpuTimeMs:N0} ms");

                AddPerThreadBreakdown(node,
                    ("Elapsed", t => t.ActualElapsedMs, false, " ms"),
                    ("CPU", t => t.ActualCPUMs, false, " ms"));
            }

            // I/O
            var hasIo = node.ActualLogicalReads > 0 || node.ActualPhysicalReads > 0
                || node.ActualScans > 0 || node.ActualReadAheads > 0
                || node.ActualSegmentReads > 0 || node.ActualSegmentSkips > 0;
            if (hasIo)
            {
                AddPropertySection("Actual I/O");
                AddPropertyRow("Logical Reads", $"{node.ActualLogicalReads:N0}");
                if (node.ActualPhysicalReads > 0)
                    AddPropertyRow("Physical Reads", $"{node.ActualPhysicalReads:N0}");
                if (node.ActualScans > 0)
                    AddPropertyRow("Scans", $"{node.ActualScans:N0}");
                if (node.ActualReadAheads > 0)
                    AddPropertyRow("Read-Ahead Reads", $"{node.ActualReadAheads:N0}");
                if (node.ActualSegmentReads > 0)
                    AddPropertyRow("Segment Reads", $"{node.ActualSegmentReads:N0}");
                if (node.ActualSegmentSkips > 0)
                    AddPropertyRow("Segment Skips", $"{node.ActualSegmentSkips:N0}");

                AddPerThreadBreakdown(node,
                    ("Logical Reads", t => t.ActualLogicalReads, false, ""),
                    ("Physical Reads", t => t.ActualPhysicalReads, false, ""),
                    ("Scans", t => t.ActualScans, false, ""),
                    ("Read-Ahead Reads", t => t.ActualReadAheads, false, ""));
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
                // Without its own section these two rows land in whichever grid was built last,
                // which reads as an unrelated section growing two mystery rows.
                AddPropertySection("Template Plan Guide");
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
                var planWarningRows = new List<PropertyPanelRow>();
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
                    var sourceTag = WarningSourceTag(w);
                    var planWarnHeader = w.MaxBenefitPercent.HasValue
                        ? $"\u26A0 {w.WarningType}{sourceTag}{legacyTag} \u2014 up to {FormatBenefitPercent(w.MaxBenefitPercent.Value)}% benefit"
                        : $"\u26A0 {w.WarningType}{sourceTag}{legacyTag}";
                    var planWarnHeaderBlock = new TextBlock
                    {
                        Text = planWarnHeader,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse(warnColor))
                    };
                    AttachOriginNavigation(planWarnHeaderBlock, planWarnHeader, w.OriginNodeIds);
                    warnPanel.Children.Add(planWarnHeaderBlock);
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
                    planWarningRows.Add(NewWarningRow(planWarnHeader, w.Message, w.ActionableFix, warnPanel));
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
                RegisterPropertySection("Plan Warnings", planWarningsExpander).Rows.AddRange(planWarningRows);
            }

            /* === Operator Warnings (#440) ===
               Every warning hanging off an operator anywhere in this statement, gathered in one
               place and each one a link to its operator.

               This section is the point of #440. The reporter's case was "the plan is huge and the
               warning origin is murky", and the operator warnings are exactly the ones with a
               murky origin - but until now the only way to see one was to already have clicked the
               operator it was on, which is no help when you do not know which operator to click.
               Nothing is removed from the per-operator panel; this is an index into it. */
            var operatorWarnings = WarningIndex.CollectOperatorWarnings(s.RootNode);
            if (operatorWarnings.Count > 0)
            {
                var operatorWarningsPanel = new StackPanel();
                var operatorWarningRows = new List<PropertyPanelRow>();
                foreach (var (originNode, w) in operatorWarnings
                             .OrderByDescending(x => x.Warning.MaxBenefitPercent ?? -1)
                             .ThenByDescending(x => x.Warning.Severity)
                             .ThenBy(x => x.Warning.WarningType))
                {
                    var opWarnColor = w.Severity == PlanWarningSeverity.Critical ? "#E57373"
                        : w.Severity == PlanWarningSeverity.Warning ? "#FFB347" : "#6BB5FF";
                    var opWarnPanel = new StackPanel { Margin = new Thickness(10, 2, 10, 2) };
                    var opBenefit = w.MaxBenefitPercent.HasValue
                        ? $" \u2014 up to {FormatBenefitPercent(w.MaxBenefitPercent.Value)}% benefit"
                        : "";
                    var opHeaderText =
                        $"\u26A0 {w.WarningType}{WarningSourceTag(w)}{(w.IsLegacy ? " [legacy]" : "")}{opBenefit}";
                    var opHeader = new TextBlock
                    {
                        Text = opHeaderText,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse(opWarnColor))
                    };
                    AttachOriginNavigation(opHeader, opHeaderText, w.OriginNodeIds);
                    opWarnPanel.Children.Add(opHeader);
                    opWarnPanel.Children.Add(new TextBlock
                    {
                        Text = OperatorOriginLabel(originNode),
                        FontSize = 11,
                        Foreground = SectionHeaderBrush,
                        Margin = new Thickness(16, 0, 0, 0)
                    });
                    operatorWarningsPanel.Children.Add(opWarnPanel);
                    operatorWarningRows.Add(
                        NewWarningRow(opHeaderText, OperatorOriginLabel(originNode), null, opWarnPanel));
                }

                var operatorWarningsExpander = new Expander
                {
                    /* Collapsed by default, unlike Plan Warnings. On a large plan this is the
                       longest section in the panel, and expanding it by default would push the
                       statement's own details off screen - the opposite of the problem #440 is
                       about. */
                    IsExpanded = false,
                    Header = new TextBlock
                    {
                        Text = $"Operator Warnings ({operatorWarnings.Count})",
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 11,
                        Foreground = SectionHeaderBrush
                    },
                    Content = operatorWarningsPanel,
                    Margin = new Thickness(0, 2, 0, 0),
                    Padding = new Thickness(0),
                    Foreground = SectionHeaderBrush,
                    Background = new SolidColorBrush(Color.FromArgb(0x18, 0x4F, 0xA3, 0xFF)),
                    BorderBrush = PropSeparatorBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                PropertiesContent.Children.Add(operatorWarningsExpander);
                RegisterPropertySection($"Operator Warnings ({operatorWarnings.Count})", operatorWarningsExpander)
                    .Rows.AddRange(operatorWarningRows);
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
            var nodeWarningRows = new List<PropertyPanelRow>();
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
                var nodeSourceTag = WarningSourceTag(w);
                var nodeWarnHeader = w.MaxBenefitPercent.HasValue
                    ? $"\u26A0 {w.WarningType}{nodeSourceTag}{nodeLegacyTag} \u2014 up to {FormatBenefitPercent(w.MaxBenefitPercent.Value)}% benefit"
                    : $"\u26A0 {w.WarningType}{nodeSourceTag}{nodeLegacyTag}";
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
                nodeWarningRows.Add(NewWarningRow(nodeWarnHeader, w.Message, null, warnPanel));
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
            RegisterPropertySection("Warnings", warningsExpander).Rows.AddRange(nodeWarningRows);
        }

        /* Show the panel. The width is set only when the panel is opening: setting it on every
           selection threw away whatever width the user had dragged, on every single click. */
        if (!PropertiesPanel.IsVisible)
        {
            _propertiesColumn.MinWidth = MinPropertiesWidth;
            _propertiesColumn.MaxWidth = MaxPropertiesWidth;
            _propertiesColumn.Width = new GridLength(
                Math.Clamp(_propertiesPanelWidth, MinPropertiesWidth, MaxPropertiesWidth));
            _splitterColumn.Width = new GridLength(PropertiesSplitterWidth);
            PropertiesSplitter.IsVisible = true;
            PropertiesPanel.IsVisible = true;
        }
    }

    /// <summary>
    /// One-time wiring for the panel chrome that lives in AXAML: the splitter's hover
    /// feedback, and remembering the width the user drags the panel to.
    /// </summary>
    private void EnsurePropertiesChrome()
    {
        if (_propertiesChromeWired) return;
        _propertiesChromeWired = true;

        var splitterIdleBrush = PropertiesSplitter.Background ?? Brushes.Transparent;
        PropertiesSplitter.PointerEntered += (_, _) => PropertiesSplitter.Background = SplitterHoverBrush;
        PropertiesSplitter.PointerExited += (_, _) => PropertiesSplitter.Background = splitterIdleBrush;

        // The splitter writes the dragged size straight onto the column, so that is where the
        // remembered width comes from - no drag tracking of our own.
        _propertiesColumn.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name != "Width" || !PropertiesPanel.IsVisible) return;
            var width = _propertiesColumn.Width;
            if (width.IsAbsolute && width.Value > 0)
                _propertiesPanelWidth = width.Value;
        };
    }

    /// <summary>
    /// Moves a section's per-thread numbers out of the flat row list and into one collapsed
    /// sub-expander, grouped under a small header per metric.
    ///
    /// <para>Every actual metric used to emit one indented "Thread N" row per thread inline,
    /// right under its own summary row. A DOP 4 hash match produced about thirty of them and
    /// DOP 8 produces hundreds, so the summary numbers most people open this panel for were
    /// buried in a scroll marathon of detail almost nobody wants expanded by default.</para>
    ///
    /// <para>Metrics with no per-thread data at all are skipped, so a section only shows the
    /// breakdown when there is something in it.</para>
    /// </summary>
    private void AddPerThreadBreakdown(
        PlanNode node,
        params (string Metric, Func<PerThreadRuntimeInfo, long> Value, bool IncludeIdleThreads, string Unit)[] metrics)
    {
        if (_currentSectionGrid == null || _currentSection == null || node.PerThreadStats.Count <= 1)
            return;

        var panel = new StackPanel { Margin = new Thickness(10, 2, 6, 4) };
        var copyText = new StringBuilder();
        var searchText = new StringBuilder();
        var groupCount = 0;

        foreach (var metric in metrics)
        {
            var threads = node.PerThreadStats
                .Where(t => metric.IncludeIdleThreads || metric.Value(t) > 0)
                .ToList();
            if (threads.Count == 0 || threads.All(t => metric.Value(t) == 0))
                continue;

            groupCount++;
            panel.Children.Add(new TextBlock
            {
                Text = metric.Metric,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = SectionHeaderBrush,
                Margin = new Thickness(0, groupCount == 1 ? 0 : 5, 0, 1)
            });
            copyText.Append("    ").AppendLine(metric.Metric);
            searchText.Append(metric.Metric).Append(' ');

            var threadGrid = new Grid { Margin = new Thickness(8, 0, 0, 0) };
            threadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            threadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var rowIndex = 0;
            foreach (var t in threads)
            {
                threadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var threadValue = $"{metric.Value(t):N0}{metric.Unit}";

                var threadLabelBlock = new TextBlock
                {
                    Text = $"Thread {t.ThreadId}",
                    FontSize = 10,
                    Foreground = TooltipFgBrush
                };
                Grid.SetRow(threadLabelBlock, rowIndex);
                Grid.SetColumn(threadLabelBlock, 0);
                threadGrid.Children.Add(threadLabelBlock);

                var threadValueBlock = new TextBlock
                {
                    Text = threadValue,
                    FontSize = 10,
                    Foreground = TooltipFgBrush,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetRow(threadValueBlock, rowIndex);
                Grid.SetColumn(threadValueBlock, 1);
                threadGrid.Children.Add(threadValueBlock);

                copyText.Append("      Thread ").Append(t.ThreadId).Append(": ").AppendLine(threadValue);
                searchText.Append("Thread ").Append(t.ThreadId).Append(' ').Append(threadValue).Append(' ');
                rowIndex++;
            }

            panel.Children.Add(threadGrid);
        }

        if (groupCount == 0) return;

        var headerText = $"Per-thread breakdown ({node.PerThreadStats.Count} threads)";
        var (isSkewed, maxRows, minRows) = ThreadRowSkew(node);
        var skewSuffix = isSkewed ? $"(skewed: {maxRows:N0} max / {minRows:N0} min)" : "";

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = headerText,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = SectionHeaderBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (isSkewed)
        {
            header.Children.Add(new TextBlock
            {
                Text = skewSuffix,
                FontWeight = FontWeight.SemiBold,
                FontSize = 11,
                Foreground = PropWarningBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            });
        }

        var breakdown = new Expander
        {
            IsExpanded = false,
            Header = header,
            Content = panel,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(0),
            Foreground = SectionHeaderBrush,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var entry = new PropertyPanelRow
        {
            Label = headerText,
            BlockText = $"  {headerText} {skewSuffix}".TrimEnd()
                + Environment.NewLine + copyText.ToString().TrimEnd(),
            SearchText = $"{headerText} {skewSuffix} {searchText}"
        };
        AddSectionRowControl(breakdown, entry, fullWidth: true);
        _currentSection.Rows.Add(entry);
    }

    /// <summary>
    /// Per-thread row skew, for the breakdown header.
    ///
    /// <para>The share test mirrors PlanAnalyzer's Rule 8 (Parallel Skew) so this header never
    /// disagrees with the warning the same plan raises. The idle-thread test is additional: a
    /// thread that returned no rows at all while its siblings did real work is the shape people
    /// read as skew on sight, and Rule 8 stays quiet about it whenever the busiest thread is
    /// still under its share threshold.</para>
    /// </summary>
    private static (bool IsSkewed, long MaxRows, long MinRows) ThreadRowSkew(PlanNode node)
    {
        // Thread 0 is the coordinator and normally moves no rows in a parallel operator.
        var workers = node.PerThreadStats.Where(t => t.ThreadId > 0).ToList();
        if (workers.Count < 2) workers = node.PerThreadStats;
        if (workers.Count < 2) return (false, 0, 0);

        var maxRows = workers.Max(t => t.ActualRows);
        var minRows = workers.Min(t => t.ActualRows);
        var totalRows = workers.Sum(t => t.ActualRows);

        // Below this there are too few rows to distribute for a split to mean anything.
        if (totalRows < workers.Count * 1000L) return (false, maxRows, minRows);

        // At DOP 2 a 60/40 split is normal, so that case needs a higher bar.
        var shareThreshold = workers.Count <= 2 ? 0.80 : 0.50;
        var isSkewed = (double)maxRows / totalRows >= shareThreshold || minRows == 0;
        return (isSkewed, maxRows, minRows);
    }

    /// <summary>
    /// Wraps one already-built warning panel as a filterable, copyable row.
    /// </summary>
    private static PropertyPanelRow NewWarningRow(
        string header, string body, string? fix, Control panel)
    {
        var text = new StringBuilder();
        text.Append("  ").AppendLine(header);
        if (!string.IsNullOrEmpty(body)) text.Append("    ").AppendLine(body);
        if (!string.IsNullOrEmpty(fix)) text.Append("    ").AppendLine(fix);

        var row = new PropertyPanelRow
        {
            Label = header,
            Value = body,
            BlockText = text.ToString().TrimEnd(),
            SearchText = $"{header} {body} {fix}"
        };
        row.Controls.Add(panel);
        return row;
    }

    /// <summary>
    /// Registers an expander that was built by hand rather than through
    /// <see cref="AddPropertySection"/> - the warning lists, which are stacked panels of prose
    /// rather than label/value grids - so the filter and the copy menu cover them too.
    /// </summary>
    private PropertyPanelSection RegisterPropertySection(string title, Expander expander)
    {
        var section = new PropertyPanelSection { Title = title, Expander = expander };
        _propertySections.Add(section);
        return section;
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

        /* The label/value drag handle, in the 4px gap column. It used to be created with the
           section's first row; it lives here now because a section can open with a full-width
           code row, and that row has no label column for the handle to sit beside.

           ZIndex keeps it under its siblings so the full-width rows own their strip of it -
           nothing else is ever in column 1, so over an ordinary row it still takes the press. */
        var labelSplitter = new GridSplitter
        {
            Width = 4,
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ZIndex = -1,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast)
        };
        Grid.SetColumn(labelSplitter, 1);
        Grid.SetRow(labelSplitter, 0);
        Grid.SetRowSpan(labelSplitter, 100);
        sectionGrid.Children.Add(labelSplitter);

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

        _currentSection = new PropertyPanelSection { Title = title, Expander = expander };
        _propertySections.Add(_currentSection);
    }

    private void AddPropertyRow(string label, string value, bool isCode = false, bool indent = false)
    {
        if (_currentSectionGrid == null || _currentSection == null) return;

        var entry = new PropertyPanelRow
        {
            Label = label,
            Value = value,
            IsCode = isCode,
            SearchText = $"{label} {value}"
        };

        if (isCode)
        {
            /* Code values get the whole panel width, label on its own line above them. In the
               label|value split a seek predicate or an output column list wraps inside a ~180px
               column and comes out a tower of [Database].[schema].[fragment] pieces, one or two
               per line, which is the least readable thing in this panel by a distance. */
            if (!string.IsNullOrEmpty(label))
                AddSectionRowControl(NewPropertyLabel(label, indent), entry, fullWidth: true);

            AddSectionRowControl(new SelectableTextBlock
            {
                Text = value,
                FontFamily = CodeFontFamily,
                FontSize = indent ? 10 : 11,
                Foreground = TooltipFgBrush,
                TextWrapping = TextWrapping.Wrap,
                // Without a background a text block is only hit-testable where its glyphs
                // landed, so presses in the margins never start a selection (#503).
                Background = Brushes.Transparent,
                Margin = new Thickness(indent ? 20 : 10, 0, 4, 3)
            }, entry, fullWidth: true);
        }
        else
        {
            var row = NextSectionRow();
            AddSectionRowControl(NewPropertyLabel(label, indent), entry, fullWidth: false, row: row);
            AddSectionRowControl(new SelectableTextBlock
            {
                Text = value,
                FontSize = indent ? 10 : 11,
                Foreground = TooltipFgBrush,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Top
            }, entry, fullWidth: false, row: row, column: 2);
        }

        _currentSection.Rows.Add(entry);
    }

    private static TextBlock NewPropertyLabel(string label, bool indent) => new()
    {
        Text = label,
        FontSize = indent ? 10 : 11,
        Foreground = TooltipFgBrush,
        VerticalAlignment = VerticalAlignment.Top,
        TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent,
        Margin = new Thickness(indent ? 16 : 4, 2, 0, 2)
    };

    private int NextSectionRow()
    {
        _currentSectionGrid!.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return _currentSectionRowIndex++;
    }

    /// <summary>
    /// Places a control in the current section's grid and records it on <paramref name="entry"/>,
    /// which is what lets the filter hide the row later. Full-width controls take a row of their
    /// own spanning all three columns.
    /// </summary>
    private void AddSectionRowControl(
        Control control, PropertyPanelRow entry, bool fullWidth, int row = -1, int column = 0)
    {
        if (fullWidth)
        {
            row = NextSectionRow();
            Grid.SetColumnSpan(control, 3);
        }

        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        _currentSectionGrid!.Children.Add(control);
        entry.Controls.Add(control);
    }

    private void CloseProperties_Click(object? sender, RoutedEventArgs e)
    {
        ClosePropertiesPanel();
    }

    private void ClosePropertiesPanel()
    {
        PropertiesPanel.IsVisible = false;
        PropertiesSplitter.IsVisible = false;
        // Clear the open-state bounds first: MinWidth clamps the column whatever its Width
        // says, so leaving it set would hold a 280px strip open on a closed panel.
        _propertiesColumn.MinWidth = 0;
        _propertiesColumn.MaxWidth = double.PositiveInfinity;
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

    private void UpdateInsightsHeader()
    {
        InsightsPanel.IsVisible = true;
        InsightsHeader.Text = "  Plan Insights";
    }

}
