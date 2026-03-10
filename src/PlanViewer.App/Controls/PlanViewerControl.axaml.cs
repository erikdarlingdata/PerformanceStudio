using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using PlanViewer.App.Helpers;
using PlanViewer.App.Mcp;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace PlanViewer.App.Controls;

public class StatementRow
{
    public int Index { get; set; }
    public string QueryText { get; set; } = "";
    public string FullQueryText { get; set; } = "";
    public long CpuMs { get; set; }
    public long ElapsedMs { get; set; }
    public long UdfMs { get; set; }
    public double EstCost { get; set; }
    public int Critical { get; set; }
    public int Warnings { get; set; }
    public PlanStatement Statement { get; set; } = null!;

    // Display helpers
    public string CpuDisplay => FormatDuration(CpuMs);
    public string ElapsedDisplay => FormatDuration(ElapsedMs);
    public string UdfDisplay => UdfMs > 0 ? FormatDuration(UdfMs) : "";
    public string CostDisplay => EstCost > 0 ? $"{EstCost:F2}" : "";

    private static string FormatDuration(long ms)
    {
        if (ms < 1000) return $"{ms}ms";
        if (ms < 60_000) return $"{ms / 1000.0:F1}s";
        return $"{ms / 60_000}m {(ms % 60_000) / 1000}s";
    }
}

public partial class PlanViewerControl : UserControl
{
    private readonly string _mcpSessionId = Guid.NewGuid().ToString();
    private ParsedPlan? _currentPlan;
    private PlanStatement? _currentStatement;
    private string? _queryText;
    private ServerMetadata? _serverMetadata;
    private double _zoomLevel = 1.0;
    private const double ZoomStep = 0.15;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 3.0;
    private string _label = "";

    /// <summary>
    /// Full path on disk when the plan was loaded from a file.
    /// </summary>
    public string? SourceFilePath { get; set; }

    // Node selection
    private Border? _selectedNodeBorder;
    private IBrush? _selectedNodeOriginalBorder;
    private Thickness _selectedNodeOriginalThickness;

    // Border -> PlanNode mapping (replaces WPF Tag pattern)
    private readonly Dictionary<Border, PlanNode> _nodeBorderMap = new();

    // Brushes
    private static readonly SolidColorBrush SelectionBrush = new(Color.FromRgb(0x4F, 0xA3, 0xFF));
    private static readonly SolidColorBrush TooltipBgBrush = new(Color.FromRgb(0x1A, 0x1D, 0x23));
    private static readonly SolidColorBrush TooltipBorderBrush = new(Color.FromRgb(0x3A, 0x3D, 0x45));
    private static readonly SolidColorBrush TooltipFgBrush = new(Color.FromRgb(0xE4, 0xE6, 0xEB));
    private static readonly SolidColorBrush EdgeBrush = new(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly SolidColorBrush SectionHeaderBrush = new(Color.FromRgb(0x4F, 0xA3, 0xFF));
    private static readonly SolidColorBrush PropSeparatorBrush = new(Color.FromRgb(0x2A, 0x2D, 0x35));
    private static readonly SolidColorBrush OrangeRedBrush = new(Colors.OrangeRed);
    private static readonly SolidColorBrush OrangeBrush = new(Colors.Orange);


    // Current property section for collapsible groups
    private StackPanel? _currentPropertySection;
    // Track all property section grids for synchronized column resize
    private readonly List<ColumnDefinition> _sectionLabelColumns = new();
    private double _propertyLabelWidth = 140;
    private bool _isSyncingColumnWidth;
    private Grid? _currentSectionGrid;
    private int _currentSectionRowIndex;

    // Non-control named elements that Avalonia codegen doesn't auto-generate fields for
    private readonly ColumnDefinition _statementsColumn;
    private readonly ColumnDefinition _statementsSplitterColumn;
    private readonly ColumnDefinition _splitterColumn;
    private readonly ColumnDefinition _propertiesColumn;
    private readonly ScaleTransform _zoomTransform;

    // Statement grid data
    private List<PlanStatement>? _allStatements;

    // Pan state
    private bool _isPanning;
    private Point _panStart;
    private double _panStartOffsetX;
    private double _panStartOffsetY;

    public PlanViewerControl()
    {
        InitializeComponent();
        PlanScrollViewer.PointerWheelChanged += PlanScrollViewer_PointerWheelChanged;
        // Use Tunnel routing so pan handlers fire before ScrollViewer consumes the events
        PlanScrollViewer.AddHandler(PointerPressedEvent, PlanScrollViewer_PointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PlanScrollViewer.AddHandler(PointerMovedEvent, PlanScrollViewer_PointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PlanScrollViewer.AddHandler(PointerReleasedEvent, PlanScrollViewer_PointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Resolve non-control elements by traversal (Avalonia doesn't support x:Name on these types)
        // The Grid in Row 4 has 5 ColumnDefinitions:
        //   [0]=Statements(0), [1]=StmtSplitter(0), [2]=Canvas(*), [3]=PropsSplitter(0), [4]=Props(0)
        var planGrid = (Grid)PlanScrollViewer.Parent!;
        _statementsColumn = planGrid.ColumnDefinitions[0];
        _statementsSplitterColumn = planGrid.ColumnDefinitions[1];
        _splitterColumn = planGrid.ColumnDefinitions[3];
        _propertiesColumn = planGrid.ColumnDefinitions[4];

        // ScaleTransform is the LayoutTransform of the wrapper around PlanCanvas
        var layoutTransform = this.FindControl<Avalonia.Controls.LayoutTransformControl>("PlanLayoutTransform")!;
        _zoomTransform = (ScaleTransform)layoutTransform.LayoutTransform!;

    }

    /// <summary>
    /// Exposes the raw XML so MainWindow can implement Save functionality.
    /// </summary>
    public string? RawXml => _currentPlan?.RawXml;

    /// <summary>
    /// Exposes the parsed and analyzed plan for advice generation.
    /// </summary>
    public ParsedPlan? CurrentPlan => _currentPlan;

    /// <summary>
    /// Exposes the query text associated with this plan (if any).
    /// </summary>
    public string? QueryText => _queryText;

    /// <summary>
    /// Server metadata for advice generation and Plan Insights display.
    /// </summary>
    public ServerMetadata? Metadata
    {
        get => _serverMetadata;
        set
        {
            _serverMetadata = value;
            if (_currentStatement != null)
                ShowServerContext();
        }
    }

    // Events for MainWindow to wire up advice/repro actions
    public event EventHandler? HumanAdviceRequested;
    public event EventHandler? RobotAdviceRequested;
    public event EventHandler? CopyReproRequested;

    public void LoadPlan(string planXml, string label, string? queryText = null)
    {
        _label = label;
        _queryText = queryText;

        // Query text stored for copy/repro but no longer shown in a
        // separate expander — it's already visible in the Statements grid.

        _currentPlan = ShowPlanParser.Parse(planXml);
        PlanAnalyzer.Analyze(_currentPlan, ConfigLoader.Load());

        var allStatements = _currentPlan.Batches
            .SelectMany(b => b.Statements)
            .Where(s => s.RootNode != null)
            .ToList();

        if (allStatements.Count == 0)
        {
            EmptyState.IsVisible = true;
            PlanScrollViewer.IsVisible = false;
            return;
        }

        EmptyState.IsVisible = false;
        PlanScrollViewer.IsVisible = true;

        // Always show statement grid — useful summary even for single-statement plans
        _allStatements = allStatements;
        PopulateStatementsGrid(allStatements);
        ShowStatementsPanel();
        StatementsGrid.SelectedIndex = 0;

        // Register with MCP session manager for AI tool access
        // Count warnings from both statement-level PlanWarnings and all node Warnings
        int warningCount = 0, criticalCount = 0;
        foreach (var s in allStatements)
        {
            warningCount += s.PlanWarnings.Count;
            criticalCount += s.PlanWarnings.Count(w => w.Severity == PlanWarningSeverity.Critical);
            if (s.RootNode != null)
                CountNodeWarnings(s.RootNode, ref warningCount, ref criticalCount);
        }

        PlanSessionManager.Instance.Register(_mcpSessionId, new PlanSession
        {
            SessionId = _mcpSessionId,
            Label = label,
            Source = "file",
            Plan = _currentPlan,
            QueryText = queryText,
            StatementCount = allStatements.Count,
            HasActualStats = allStatements.Any(s => s.QueryTimeStats != null),
            WarningCount = warningCount,
            CriticalWarningCount = criticalCount,
            MissingIndexCount = _currentPlan.AllMissingIndexes.Count
        });
    }

    public void Clear()
    {
        PlanSessionManager.Instance.Unregister(_mcpSessionId);
        PlanCanvas.Children.Clear();
        _nodeBorderMap.Clear();
        _currentPlan = null;
        _currentStatement = null;
        _queryText = null;
        _selectedNodeBorder = null;
        EmptyState.IsVisible = true;
        PlanScrollViewer.IsVisible = false;
        InsightsPanel.IsVisible = false;
        CostText.Text = "";
        CloseStatementsPanel();
        StatementsButton.IsVisible = false;
        StatementsButtonSeparator.IsVisible = false;
        ClosePropertiesPanel();
    }

    private static void CountNodeWarnings(PlanNode node, ref int total, ref int critical)
    {
        total += node.Warnings.Count;
        critical += node.Warnings.Count(w => w.Severity == PlanWarningSeverity.Critical);
        foreach (var child in node.Children)
            CountNodeWarnings(child, ref total, ref critical);
    }

    private void RenderStatement(PlanStatement statement)
    {
        _currentStatement = statement;
        PlanCanvas.Children.Clear();
        _nodeBorderMap.Clear();
        _selectedNodeBorder = null;

        if (statement.RootNode == null) return;

        // Layout
        PlanLayoutEngine.Layout(statement);
        var (width, height) = PlanLayoutEngine.GetExtents(statement.RootNode);
        PlanCanvas.Width = width;
        PlanCanvas.Height = height;

        // Render edges first (behind nodes)
        RenderEdges(statement.RootNode);

        // Render nodes — pass total warning count to root node for badge
        var allWarnings = new List<PlanWarning>();
        CollectWarnings(statement.RootNode, allWarnings);
        RenderNodes(statement.RootNode, allWarnings.Count);

        // Update banners
        ShowMissingIndexes(statement.MissingIndexes);
        ShowParameters(statement);
        ShowWaitStats(statement.WaitStats, statement.QueryTimeStats != null);
        ShowRuntimeSummary(statement);
        UpdateInsightsHeader();

        // Scroll to top-left so the plan root is immediately visible
        PlanScrollViewer.Offset = new Avalonia.Vector(0, 0);

        // Canvas-level context menu (zoom, advice, repro, save)
        // Set on ScrollViewer, not Canvas — Canvas has no background so it's not hit-testable
        PlanScrollViewer.ContextMenu = BuildCanvasContextMenu();

        CostText.Text = "";
    }

    #region Node Rendering

    private void RenderNodes(PlanNode node, int totalWarningCount = -1)
    {
        var visual = CreateNodeVisual(node, totalWarningCount);
        Canvas.SetLeft(visual, node.X);
        Canvas.SetTop(visual, node.Y);
        PlanCanvas.Children.Add(visual);

        foreach (var child in node.Children)
            RenderNodes(child);
    }

    private Border CreateNodeVisual(PlanNode node, int totalWarningCount = -1)
    {
        var isExpensive = node.IsExpensive;

        var bgBrush = isExpensive
            ? new SolidColorBrush(Color.FromArgb(0x30, 0xE5, 0x73, 0x73))
            : FindBrushResource("BackgroundLightBrush");

        var borderBrush = isExpensive
            ? OrangeRedBrush
            : FindBrushResource("BorderBrush");

        var border = new Border
        {
            Width = PlanLayoutEngine.NodeWidth,
            MinHeight = PlanLayoutEngine.NodeHeightMin,
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(isExpensive ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // Map border to node (replaces WPF Tag)
        _nodeBorderMap[border] = node;

        // Tooltip — root node gets all collected warnings so the tooltip shows them
        if (totalWarningCount > 0)
        {
            var allWarnings = new List<PlanWarning>();
            if (_currentStatement != null)
                allWarnings.AddRange(_currentStatement.PlanWarnings);
            CollectWarnings(node, allWarnings);
            ToolTip.SetTip(border, BuildNodeTooltipContent(node, allWarnings));
        }
        else
        {
            ToolTip.SetTip(border, BuildNodeTooltipContent(node));
        }

        // Click to select + show properties
        border.PointerPressed += Node_Click;

        // Right-click context menu
        border.ContextMenu = BuildNodeContextMenu(node);

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        // Icon row: icon + optional warning/parallel indicators
        var iconRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var iconBitmap = IconHelper.LoadIcon(node.IconName);
        if (iconBitmap != null)
        {
            iconRow.Children.Add(new Image
            {
                Source = iconBitmap,
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        // Warning indicator badge (orange triangle with !)
        if (node.HasWarnings)
        {
            var warnBadge = new Grid
            {
                Width = 20, Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            warnBadge.Children.Add(new AvaloniaPath
            {
                Data = StreamGeometry.Parse("M 10,0 L 20,18 L 0,18 Z"),
                Fill = OrangeBrush
            });
            warnBadge.Children.Add(new TextBlock
            {
                Text = "!",
                FontSize = 12,
                FontWeight = FontWeight.ExtraBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });
            iconRow.Children.Add(warnBadge);
        }

        // Parallel indicator badge (amber circle with arrows)
        if (node.Parallel)
        {
            var parBadge = new Grid
            {
                Width = 20, Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            parBadge.Children.Add(new Ellipse
            {
                Width = 20, Height = 20,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))
            });
            parBadge.Children.Add(new TextBlock
            {
                Text = "\u21C6",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            iconRow.Children.Add(parBadge);
        }

        stack.Children.Add(iconRow);

        // Operator name
        var fgBrush = FindBrushResource("ForegroundBrush");

        // Operator name — for exchanges, show "Parallelism" + "(Gather Streams)" etc.
        var opLabel = node.PhysicalOp;
        if (node.PhysicalOp == "Parallelism" && !string.IsNullOrEmpty(node.LogicalOp)
            && node.LogicalOp != "Parallelism")
        {
            opLabel = $"Parallelism\n({node.LogicalOp})";
        }
        stack.Children.Add(new TextBlock
        {
            Text = opLabel,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = fgBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = PlanLayoutEngine.NodeWidth - 16,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        // Cost percentage — only highlight in estimated plans; actual plans use duration/CPU colors
        IBrush costColor = !node.HasActualStats && node.CostPercent >= 50 ? OrangeRedBrush
            : !node.HasActualStats && node.CostPercent >= 25 ? OrangeBrush
            : fgBrush;

        stack.Children.Add(new TextBlock
        {
            Text = $"Cost: {node.CostPercent}%",
            FontSize = 10,
            Foreground = costColor,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        // Actual plan stats: elapsed time, CPU time, and row counts
        if (node.HasActualStats)
        {
            // Compute own time (subtract children in row mode)
            var ownElapsedMs = GetOwnElapsedMs(node);
            var ownCpuMs = GetOwnCpuMs(node);

            // Elapsed time -- color based on own time, not cumulative
            var ownElapsedSec = ownElapsedMs / 1000.0;
            IBrush elapsedBrush = ownElapsedSec >= 1.0 ? OrangeRedBrush
                : ownElapsedSec >= 0.1 ? OrangeBrush : fgBrush;
            stack.Children.Add(new TextBlock
            {
                Text = $"{ownElapsedSec:F3}s",
                FontSize = 10,
                Foreground = elapsedBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // CPU time -- color based on own time
            var ownCpuSec = ownCpuMs / 1000.0;
            IBrush cpuBrush = ownCpuSec >= 1.0 ? OrangeRedBrush
                : ownCpuSec >= 0.1 ? OrangeBrush : fgBrush;
            stack.Children.Add(new TextBlock
            {
                Text = $"CPU: {ownCpuSec:F3}s",
                FontSize = 10,
                Foreground = cpuBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Actual rows of Estimated rows (accuracy %) -- red if off by 10x+
            var estRows = node.EstimateRows;
            var accuracyRatio = estRows > 0 ? node.ActualRows / estRows : (node.ActualRows > 0 ? double.MaxValue : 1.0);
            IBrush rowBrush = (accuracyRatio < 0.1 || accuracyRatio > 10.0) ? OrangeRedBrush : fgBrush;
            var accuracy = estRows > 0
                ? $" ({accuracyRatio * 100:F0}%)"
                : "";
            stack.Children.Add(new TextBlock
            {
                Text = $"{node.ActualRows:N0} of {estRows:N0}{accuracy}",
                FontSize = 10,
                Foreground = rowBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = PlanLayoutEngine.NodeWidth - 16
            });
        }

        // Object name -- show full object name, wrap if needed
        if (!string.IsNullOrEmpty(node.ObjectName))
        {
            var objBlock = new TextBlock
            {
                Text = node.FullObjectName ?? node.ObjectName,
                FontSize = 10,
                Foreground = fgBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = PlanLayoutEngine.NodeWidth - 16,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(objBlock);
        }

        // Total warning count badge on root node
        if (totalWarningCount > 0)
        {
            var badgeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            badgeRow.Children.Add(new TextBlock
            {
                Text = "\u26A0",
                FontSize = 13,
                Foreground = OrangeBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            badgeRow.Children.Add(new TextBlock
            {
                Text = $"{totalWarningCount} warning{(totalWarningCount == 1 ? "" : "s")}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = OrangeBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(badgeRow);
        }

        border.Child = stack;
        return border;
    }

    #endregion

    #region Edge Rendering

    private void RenderEdges(PlanNode node)
    {
        foreach (var child in node.Children)
        {
            var path = CreateElbowConnector(node, child);
            PlanCanvas.Children.Add(path);

            RenderEdges(child);
        }
    }

    private AvaloniaPath CreateElbowConnector(PlanNode parent, PlanNode child)
    {
        var parentRight = parent.X + PlanLayoutEngine.NodeWidth;
        var parentCenterY = parent.Y + PlanLayoutEngine.GetNodeHeight(parent) / 2;
        var childLeft = child.X;
        var childCenterY = child.Y + PlanLayoutEngine.GetNodeHeight(child) / 2;

        // Arrow thickness based on row estimate (logarithmic)
        var rows = child.HasActualStats ? child.ActualRows : child.EstimateRows;
        var thickness = Math.Max(2, Math.Min(Math.Floor(Math.Log(Math.Max(1, rows))), 12));

        var midX = (parentRight + childLeft) / 2;

        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(parentRight, parentCenterY),
            IsClosed = false
        };
        figure.Segments!.Add(new LineSegment { Point = new Point(midX, parentCenterY) });
        figure.Segments.Add(new LineSegment { Point = new Point(midX, childCenterY) });
        figure.Segments.Add(new LineSegment { Point = new Point(childLeft, childCenterY) });
        geometry.Figures!.Add(figure);

        var path = new AvaloniaPath
        {
            Data = geometry,
            Stroke = EdgeBrush,
            StrokeThickness = thickness,
            StrokeJoin = PenLineJoin.Round
        };
        ToolTip.SetTip(path, BuildEdgeTooltipContent(child));
        return path;
    }

    private object BuildEdgeTooltipContent(PlanNode child)
    {
        var panel = new StackPanel { MinWidth = 240 };

        void AddRow(string label, string value)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var lbl = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontSize = 12,
                Margin = new Thickness(0, 1, 12, 1)
            };
            var val = new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 1, 0, 1)
            };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(val, 1);
            row.Children.Add(lbl);
            row.Children.Add(val);
            panel.Children.Add(row);
        }

        if (child.HasActualStats)
            AddRow("Actual Number of Rows for All Executions", $"{child.ActualRows:N0}");

        AddRow("Estimated Number of Rows Per Execution", $"{child.EstimateRows:N0}");

        var executions = 1.0 + child.EstimateRebinds + child.EstimateRewinds;
        var estimatedRowsAllExec = child.EstimateRows * executions;
        AddRow("Estimated Number of Rows for All Executions", $"{estimatedRowsAllExec:N0}");

        if (child.EstimatedRowSize > 0)
        {
            AddRow("Estimated Row Size", FormatBytes(child.EstimatedRowSize));
            var dataSize = estimatedRowsAllExec * child.EstimatedRowSize;
            AddRow("Estimated Data Size", FormatBytes(dataSize));
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(4),
            Child = panel
        };
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:N0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024 * 1024):N0} MB";
        return $"{bytes / (1024L * 1024 * 1024):N1} GB";
    }

    #endregion

    #region Node Selection & Properties Panel

    private void Node_Click(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border
            && e.GetCurrentPoint(border).Properties.IsLeftButtonPressed
            && _nodeBorderMap.TryGetValue(border, out var node))
        {
            SelectNode(border, node);
            e.Handled = true;
        }
    }

    private void SelectNode(Border border, PlanNode node)
    {
        // Deselect previous
        if (_selectedNodeBorder != null)
        {
            _selectedNodeBorder.BorderBrush = _selectedNodeOriginalBorder;
            _selectedNodeBorder.BorderThickness = _selectedNodeOriginalThickness;
        }

        // Select new
        _selectedNodeOriginalBorder = border.BorderBrush;
        _selectedNodeOriginalThickness = border.BorderThickness;
        _selectedNodeBorder = border;
        border.BorderBrush = SelectionBrush;
        border.BorderThickness = new Thickness(2);

        ShowPropertiesPanel(node);
    }

    private ContextMenu BuildNodeContextMenu(PlanNode node)
    {
        var menu = new ContextMenu();

        var propsItem = new MenuItem { Header = "Properties" };
        propsItem.Click += (_, _) =>
        {
            foreach (var child in PlanCanvas.Children)
            {
                if (child is Border b && _nodeBorderMap.TryGetValue(b, out var n) && n == node)
                {
                    SelectNode(b, node);
                    break;
                }
            }
        };
        menu.Items.Add(propsItem);

        menu.Items.Add(new Separator());

        var copyOpItem = new MenuItem { Header = "Copy Operator Name" };
        copyOpItem.Click += async (_, _) => await SetClipboardTextAsync(node.PhysicalOp);
        menu.Items.Add(copyOpItem);

        if (!string.IsNullOrEmpty(node.FullObjectName))
        {
            var copyObjItem = new MenuItem { Header = "Copy Object Name" };
            copyObjItem.Click += async (_, _) => await SetClipboardTextAsync(node.FullObjectName!);
            menu.Items.Add(copyObjItem);
        }

        if (!string.IsNullOrEmpty(node.Predicate))
        {
            var copyPredItem = new MenuItem { Header = "Copy Predicate" };
            copyPredItem.Click += async (_, _) => await SetClipboardTextAsync(node.Predicate!);
            menu.Items.Add(copyPredItem);
        }

        if (!string.IsNullOrEmpty(node.SeekPredicates))
        {
            var copySeekItem = new MenuItem { Header = "Copy Seek Predicate" };
            copySeekItem.Click += async (_, _) => await SetClipboardTextAsync(node.SeekPredicates!);
            menu.Items.Add(copySeekItem);
        }

        return menu;
    }

    private ContextMenu BuildCanvasContextMenu()
    {
        var menu = new ContextMenu();

        // Zoom
        var zoomInItem = new MenuItem { Header = "Zoom In" };
        zoomInItem.Click += (_, _) => SetZoom(_zoomLevel + ZoomStep);
        menu.Items.Add(zoomInItem);

        var zoomOutItem = new MenuItem { Header = "Zoom Out" };
        zoomOutItem.Click += (_, _) => SetZoom(_zoomLevel - ZoomStep);
        menu.Items.Add(zoomOutItem);

        var fitItem = new MenuItem { Header = "Fit to View" };
        fitItem.Click += ZoomFit_Click;
        menu.Items.Add(fitItem);

        menu.Items.Add(new Separator());

        // Advice
        var humanAdviceItem = new MenuItem { Header = "Human Advice" };
        humanAdviceItem.Click += (_, _) => HumanAdviceRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(humanAdviceItem);

        var robotAdviceItem = new MenuItem { Header = "Robot Advice" };
        robotAdviceItem.Click += (_, _) => RobotAdviceRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(robotAdviceItem);

        menu.Items.Add(new Separator());

        // Repro & Save
        var copyReproItem = new MenuItem { Header = "Copy Repro Script" };
        copyReproItem.Click += (_, _) => CopyReproRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(copyReproItem);

        var saveItem = new MenuItem { Header = "Save .sqlplan" };
        saveItem.Click += SavePlan_Click;
        menu.Items.Add(saveItem);

        return menu;
    }

    private async System.Threading.Tasks.Task SetClipboardTextAsync(string text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
            await topLevel.Clipboard.SetTextAsync(text);
    }

    private void ShowPropertiesPanel(PlanNode node)
    {
        PropertiesContent.Children.Clear();
        _currentPropertySection = null;
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
            || node.SpoolStack || node.DMLRequestSort
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
                foreach (var w in s.PlanWarnings)
                {
                    var warnColor = w.Severity == PlanWarningSeverity.Critical ? "#E57373"
                        : w.Severity == PlanWarningSeverity.Warning ? "#FFB347" : "#6BB5FF";
                    var warnPanel = new StackPanel { Margin = new Thickness(10, 2, 10, 2) };
                    warnPanel.Children.Add(new TextBlock
                    {
                        Text = $"\u26A0 {w.WarningType}",
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
            foreach (var w in node.Warnings)
            {
                var warnColor = w.Severity == PlanWarningSeverity.Critical ? "#E57373"
                    : w.Severity == PlanWarningSeverity.Warning ? "#FFB347" : "#6BB5FF";
                var warnPanel = new StackPanel { Margin = new Thickness(10, 2, 10, 2) };
                warnPanel.Children.Add(new TextBlock
                {
                    Text = $"\u26A0 {w.WarningType}",
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
        _currentPropertySection = null; // No longer used — rows go into _currentSectionGrid
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

    #endregion

    #region Tooltips

    private object BuildNodeTooltipContent(PlanNode node, List<PlanWarning>? allWarnings = null)
    {
        var tipBorder = new Border
        {
            Background = TooltipBgBrush,
            BorderBrush = TooltipBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            MaxWidth = 500
        };

        var stack = new StackPanel();

        // Header
        var headerText = node.PhysicalOp;
        if (node.LogicalOp != node.PhysicalOp && !string.IsNullOrEmpty(node.LogicalOp)
            && !node.PhysicalOp.Contains(node.LogicalOp, StringComparison.OrdinalIgnoreCase))
            headerText += $" ({node.LogicalOp})";
        stack.Children.Add(new TextBlock
        {
            Text = headerText,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = TooltipFgBrush,
            Margin = new Thickness(0, 0, 0, 8)
        });

        // Cost
        AddTooltipSection(stack, "Costs");
        AddTooltipRow(stack, "Cost", $"{node.CostPercent}% of statement ({node.EstimatedOperatorCost:F6})");
        AddTooltipRow(stack, "Subtree Cost", $"{node.EstimatedTotalSubtreeCost:F6}");

        // Rows
        AddTooltipSection(stack, "Rows");
        AddTooltipRow(stack, "Estimated Rows", $"{node.EstimateRows:N1}");
        if (node.HasActualStats)
        {
            AddTooltipRow(stack, "Actual Rows", $"{node.ActualRows:N0}");
            if (node.ActualRowsRead > 0)
                AddTooltipRow(stack, "Actual Rows Read", $"{node.ActualRowsRead:N0}");
            AddTooltipRow(stack, "Actual Executions", $"{node.ActualExecutions:N0}");
        }

        // Rebinds/Rewinds (spools and other operators with rebind/rewind data)
        if (node.EstimateRebinds > 0 || node.EstimateRewinds > 0
            || node.ActualRebinds > 0 || node.ActualRewinds > 0)
        {
            AddTooltipSection(stack, "Rebinds / Rewinds");
            // Always show both estimated values when section is visible
            AddTooltipRow(stack, "Est. Rebinds", $"{node.EstimateRebinds:N1}");
            AddTooltipRow(stack, "Est. Rewinds", $"{node.EstimateRewinds:N1}");
            if (node.ActualRebinds > 0) AddTooltipRow(stack, "Actual Rebinds", $"{node.ActualRebinds:N0}");
            if (node.ActualRewinds > 0) AddTooltipRow(stack, "Actual Rewinds", $"{node.ActualRewinds:N0}");
        }

        // I/O and CPU estimates
        if (node.EstimateIO > 0 || node.EstimateCPU > 0 || node.EstimatedRowSize > 0)
        {
            AddTooltipSection(stack, "Estimates");
            if (node.EstimateIO > 0) AddTooltipRow(stack, "I/O Cost", $"{node.EstimateIO:F6}");
            if (node.EstimateCPU > 0) AddTooltipRow(stack, "CPU Cost", $"{node.EstimateCPU:F6}");
            if (node.EstimatedRowSize > 0) AddTooltipRow(stack, "Avg Row Size", $"{node.EstimatedRowSize} B");
        }

        // Actual I/O
        if (node.HasActualStats && (node.ActualLogicalReads > 0 || node.ActualPhysicalReads > 0))
        {
            AddTooltipSection(stack, "Actual I/O");
            AddTooltipRow(stack, "Logical Reads", $"{node.ActualLogicalReads:N0}");
            if (node.ActualPhysicalReads > 0)
                AddTooltipRow(stack, "Physical Reads", $"{node.ActualPhysicalReads:N0}");
            if (node.ActualScans > 0)
                AddTooltipRow(stack, "Scans", $"{node.ActualScans:N0}");
            if (node.ActualReadAheads > 0)
                AddTooltipRow(stack, "Read-Aheads", $"{node.ActualReadAheads:N0}");
        }

        // Actual timing
        if (node.HasActualStats && (node.ActualElapsedMs > 0 || node.ActualCPUMs > 0))
        {
            AddTooltipSection(stack, "Timing");
            if (node.ActualElapsedMs > 0)
                AddTooltipRow(stack, "Elapsed Time", $"{node.ActualElapsedMs:N0} ms");
            if (node.ActualCPUMs > 0)
                AddTooltipRow(stack, "CPU Time", $"{node.ActualCPUMs:N0} ms");
        }

        // Parallelism
        if (node.Parallel || !string.IsNullOrEmpty(node.ExecutionMode) || !string.IsNullOrEmpty(node.PartitioningType))
        {
            AddTooltipSection(stack, "Parallelism");
            if (node.Parallel) AddTooltipRow(stack, "Parallel", "Yes");
            if (!string.IsNullOrEmpty(node.ExecutionMode))
                AddTooltipRow(stack, "Execution Mode", node.ExecutionMode);
            if (!string.IsNullOrEmpty(node.ActualExecutionMode) && node.ActualExecutionMode != node.ExecutionMode)
                AddTooltipRow(stack, "Actual Exec Mode", node.ActualExecutionMode);
            if (!string.IsNullOrEmpty(node.PartitioningType))
                AddTooltipRow(stack, "Partitioning", node.PartitioningType);
        }

        // Object
        if (!string.IsNullOrEmpty(node.FullObjectName))
        {
            AddTooltipSection(stack, "Object");
            AddTooltipRow(stack, "Name", node.FullObjectName, isCode: true);
            if (node.Ordered) AddTooltipRow(stack, "Ordered", "True");
            if (!string.IsNullOrEmpty(node.ScanDirection))
                AddTooltipRow(stack, "Scan Direction", node.ScanDirection);
        }
        else if (!string.IsNullOrEmpty(node.ObjectName))
        {
            AddTooltipSection(stack, "Object");
            AddTooltipRow(stack, "Name", node.ObjectName, isCode: true);
            if (node.Ordered) AddTooltipRow(stack, "Ordered", "True");
            if (!string.IsNullOrEmpty(node.ScanDirection))
                AddTooltipRow(stack, "Scan Direction", node.ScanDirection);
        }

        // Operator details (key items only in tooltip)
        var hasTooltipDetails = !string.IsNullOrEmpty(node.OrderBy)
            || !string.IsNullOrEmpty(node.TopExpression)
            || !string.IsNullOrEmpty(node.GroupBy)
            || !string.IsNullOrEmpty(node.OuterReferences);
        if (hasTooltipDetails)
        {
            AddTooltipSection(stack, "Details");
            if (!string.IsNullOrEmpty(node.OrderBy))
                AddTooltipRow(stack, "Order By", node.OrderBy, isCode: true);
            if (!string.IsNullOrEmpty(node.TopExpression))
                AddTooltipRow(stack, "Top", node.IsPercent ? $"{node.TopExpression} PERCENT" : node.TopExpression);
            if (!string.IsNullOrEmpty(node.GroupBy))
                AddTooltipRow(stack, "Group By", node.GroupBy, isCode: true);
            if (!string.IsNullOrEmpty(node.OuterReferences))
                AddTooltipRow(stack, "Outer References", node.OuterReferences, isCode: true);
        }

        // Predicates
        if (!string.IsNullOrEmpty(node.SeekPredicates) || !string.IsNullOrEmpty(node.Predicate))
        {
            AddTooltipSection(stack, "Predicates");
            if (!string.IsNullOrEmpty(node.SeekPredicates))
                AddTooltipRow(stack, "Seek", node.SeekPredicates, isCode: true);
            if (!string.IsNullOrEmpty(node.Predicate))
                AddTooltipRow(stack, "Residual", node.Predicate, isCode: true);
        }

        // Output columns
        if (!string.IsNullOrEmpty(node.OutputColumns))
        {
            AddTooltipSection(stack, "Output");
            AddTooltipRow(stack, "Columns", node.OutputColumns, isCode: true);
        }

        // Warnings — use allWarnings (all nodes) for root, node.Warnings for others
        var warnings = allWarnings ?? (node.HasWarnings ? node.Warnings : null);
        if (warnings != null && warnings.Count > 0)
        {
            stack.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });

            if (allWarnings != null)
            {
                // Root node: show distinct warning type names only
                var distinct = warnings
                    .GroupBy(w => w.WarningType)
                    .Select(g => (Type: g.Key, MaxSeverity: g.Max(w => w.Severity), Count: g.Count()))
                    .OrderByDescending(g => g.MaxSeverity)
                    .ThenBy(g => g.Type);

                foreach (var (type, severity, count) in distinct)
                {
                    var warnColor = severity == PlanWarningSeverity.Critical ? "#E57373"
                        : severity == PlanWarningSeverity.Warning ? "#FFB347" : "#6BB5FF";
                    var label = count > 1 ? $"\u26A0 {type} ({count})" : $"\u26A0 {type}";
                    stack.Children.Add(new TextBlock
                    {
                        Text = label,
                        Foreground = new SolidColorBrush(Color.Parse(warnColor)),
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }
            }
            else
            {
                // Individual node: show full warning messages
                foreach (var w in warnings)
                {
                    var warnColor = w.Severity == PlanWarningSeverity.Critical ? "#E57373"
                        : w.Severity == PlanWarningSeverity.Warning ? "#FFB347" : "#6BB5FF";
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"\u26A0 {w.WarningType}: {w.Message}",
                        Foreground = new SolidColorBrush(Color.Parse(warnColor)),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }
            }
        }

        // Footer hint
        stack.Children.Add(new TextBlock
        {
            Text = "Click to view full properties",
            FontSize = 10,
            FontStyle = FontStyle.Italic,
            Foreground = TooltipFgBrush,
            Margin = new Thickness(0, 8, 0, 0)
        });

        tipBorder.Child = stack;
        return tipBorder;
    }

    private static void AddTooltipSection(StackPanel parent, string title)
    {
        parent.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = SectionHeaderBrush,
            Margin = new Thickness(0, 6, 0, 2)
        });
    }

    private static void AddTooltipRow(StackPanel parent, string label, string value, bool isCode = false)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 1, 0, 1)
        };
        var labelBlock = new TextBlock
        {
            Text = $"{label}: ",
            Foreground = TooltipFgBrush,
            FontSize = 11,
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 11,
            Foreground = TooltipFgBrush,
            TextWrapping = TextWrapping.Wrap
        };
        if (isCode) valueBlock.FontFamily = new FontFamily("Consolas");
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(valueBlock);
        parent.Children.Add(row);
    }

    #endregion

    #region Banners

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
            var localVars = FindUnresolvedVariables(statement.StatementText, parameters);
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
            AddParamCell(grid, rowIndex, 1, param.DataType, "#B0B6C0");

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

        var unresolved = FindUnresolvedVariables(statement.StatementText, parameters);
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

    private static List<string> FindUnresolvedVariables(string queryText, List<PlanParameter> parameters)
    {
        var unresolved = new List<string>();
        if (string.IsNullOrEmpty(queryText))
            return unresolved;

        var extractedNames = new HashSet<string>(
            parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        var matches = Regex.Matches(queryText, @"@\w+", RegexOptions.IgnoreCase);
        var seenVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var varName = match.Value;
            if (seenVars.Contains(varName) || extractedNames.Contains(varName))
                continue;
            if (varName.StartsWith("@@", StringComparison.OrdinalIgnoreCase))
                continue;

            seenVars.Add(varName);
            unresolved.Add(varName);
        }

        return unresolved;
    }

    private static void CollectWarnings(PlanNode node, List<PlanWarning> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectWarnings(child, warnings);
    }

    /// <summary>
    /// Computes own CPU time for a node by subtracting child times in row mode.
    /// Batch mode reports own time directly; row mode is cumulative from leaves up.
    /// </summary>
    private static long GetOwnCpuMs(PlanNode node)
    {
        if (node.ActualCPUMs <= 0) return 0;
        var mode = node.ActualExecutionMode ?? node.ExecutionMode;
        if (mode == "Batch") return node.ActualCPUMs;
        var childSum = GetChildCpuMsSum(node);
        return Math.Max(0, node.ActualCPUMs - childSum);
    }

    /// <summary>
    /// Computes own elapsed time for a node by subtracting child times in row mode.
    /// </summary>
    private static long GetOwnElapsedMs(PlanNode node)
    {
        if (node.ActualElapsedMs <= 0) return 0;
        var mode = node.ActualExecutionMode ?? node.ExecutionMode;
        if (mode == "Batch") return node.ActualElapsedMs;

        // Exchange operators: Thread 0 is the coordinator whose elapsed time is the
        // wall clock for the entire parallel branch — not the operator's own work.
        if (IsExchangeOperator(node))
        {
            // If we have worker thread data, use max of worker threads
            var workerMax = node.PerThreadStats
                .Where(t => t.ThreadId > 0)
                .Select(t => t.ActualElapsedMs)
                .DefaultIfEmpty(0)
                .Max();
            if (workerMax > 0)
            {
                var childSum = GetChildElapsedMsSum(node);
                return Math.Max(0, workerMax - childSum);
            }
            // Thread 0 only (coordinator) — exchange does negligible own work
            return 0;
        }

        var childElapsedSum = GetChildElapsedMsSum(node);
        return Math.Max(0, node.ActualElapsedMs - childElapsedSum);
    }

    private static bool IsExchangeOperator(PlanNode node) =>
        node.PhysicalOp == "Parallelism"
        || node.LogicalOp is "Gather Streams" or "Distribute Streams" or "Repartition Streams";

    private static long GetChildCpuMsSum(PlanNode node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            if (child.ActualCPUMs > 0)
                sum += child.ActualCPUMs;
            else
                sum += GetChildCpuMsSum(child); // skip through transparent operators
        }
        return sum;
    }

    private static long GetChildElapsedMsSum(PlanNode node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
            {
                // Exchange: take max of children (parallel branches)
                sum += child.Children
                    .Where(c => c.ActualElapsedMs > 0)
                    .Select(c => c.ActualElapsedMs)
                    .DefaultIfEmpty(0)
                    .Max();
            }
            else if (child.ActualElapsedMs > 0)
            {
                sum += child.ActualElapsedMs;
            }
            else
            {
                sum += GetChildElapsedMsSum(child); // skip through transparent operators
            }
        }
        return sum;
    }

    private void ShowWaitStats(List<WaitStatInfo> waits, bool isActualPlan)
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

        var sorted = waits.OrderByDescending(w => w.WaitTimeMs).ToList();
        var maxWait = sorted[0].WaitTimeMs;
        var totalWait = sorted.Sum(w => w.WaitTimeMs);

        // Update expander header with total
        WaitStatsHeader.Text = $"  Wait Stats \u2014 {totalWait:N0}ms total";

        // Build a single Grid for all rows so columns align
        // Name and duration auto-size; bar fills remaining space
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
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
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(durationText, i);
            Grid.SetColumn(durationText, 2);
            grid.Children.Add(durationText);
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

        // Efficiency thresholds: white >= 80%, yellow >= 60%, orange >= 40%, red < 40%
        static string EfficiencyColor(double pct) => pct >= 80 ? "#E4E6EB"
            : pct >= 60 ? "#FFD700" : pct >= 40 ? "#FFB347" : "#E57373";

        // Runtime stats (actual plans)
        if (statement.QueryTimeStats != null)
        {
            AddRow("Elapsed", $"{statement.QueryTimeStats.ElapsedTimeMs:N0}ms");
            AddRow("CPU", $"{statement.QueryTimeStats.CpuTimeMs:N0}ms");
            if (statement.QueryUdfCpuTimeMs > 0)
                AddRow("UDF CPU", $"{statement.QueryUdfCpuTimeMs:N0}ms");
            if (statement.QueryUdfElapsedTimeMs > 0)
                AddRow("UDF elapsed", $"{statement.QueryUdfElapsedTimeMs:N0}ms");
        }

        // Memory grant — color by utilization percentage
        if (statement.MemoryGrant != null)
        {
            var mg = statement.MemoryGrant;
            var grantPct = mg.GrantedMemoryKB > 0
                ? (double)mg.MaxUsedMemoryKB / mg.GrantedMemoryKB * 100 : 100;
            var grantColor = EfficiencyColor(grantPct);
            AddRow("Memory grant",
                $"{TextFormatter.FormatMemoryGrantKB(mg.GrantedMemoryKB)} granted, {TextFormatter.FormatMemoryGrantKB(mg.MaxUsedMemoryKB)} used ({grantPct:N0}%)",
                grantColor);
            if (mg.GrantWaitTimeMs > 0)
                AddRow("Grant wait", $"{mg.GrantWaitTimeMs:N0}ms", "#E57373");
        }

        // DOP + parallelism efficiency — color by efficiency
        if (statement.DegreeOfParallelism > 0)
        {
            var dopText = statement.DegreeOfParallelism.ToString();
            string? dopColor = null;
            if (statement.QueryTimeStats != null &&
                statement.QueryTimeStats.ElapsedTimeMs > 0 &&
                statement.QueryTimeStats.CpuTimeMs > 0 &&
                statement.DegreeOfParallelism > 1)
            {
                // Speedup ratio: CPU/elapsed = 1.0 means serial, = DOP means perfect parallelism
                var speedup = (double)statement.QueryTimeStats.CpuTimeMs / statement.QueryTimeStats.ElapsedTimeMs;
                var efficiency = Math.Min(100.0, (speedup - 1.0) / (statement.DegreeOfParallelism - 1.0) * 100.0);
                efficiency = Math.Max(0.0, efficiency);
                dopText += $" ({efficiency:N0}% efficient)";
                dopColor = EfficiencyColor(efficiency);
            }
            AddRow("DOP", dopText, dopColor);
        }
        else if (statement.NonParallelPlanReason != null)
            AddRow("Serial", statement.NonParallelPlanReason);

        // Thread stats — color by utilization
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

        // CE model
        if (statement.CardinalityEstimationModelVersion > 0)
            AddRow("CE model", statement.CardinalityEstimationModelVersion.ToString());

        // Compile stats (always available)
        if (statement.CompileTimeMs > 0)
            AddRow("Compile time", $"{statement.CompileTimeMs:N0}ms");
        if (statement.CachedPlanSizeKB > 0)
            AddRow("Cached plan size", $"{statement.CachedPlanSizeKB:N0} KB");

        // Optimization level
        if (!string.IsNullOrEmpty(statement.StatementOptmLevel))
            AddRow("Optimization", statement.StatementOptmLevel);
        if (!string.IsNullOrEmpty(statement.StatementOptmEarlyAbortReason))
            AddRow("Early abort", statement.StatementOptmEarlyAbortReason);

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

    #endregion

    #region Zoom

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetZoom(_zoomLevel + ZoomStep);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetZoom(_zoomLevel - ZoomStep);

    private void ZoomFit_Click(object? sender, RoutedEventArgs e)
    {
        if (PlanCanvas.Width <= 0 || PlanCanvas.Height <= 0) return;

        var viewWidth = PlanScrollViewer.Bounds.Width;
        var viewHeight = PlanScrollViewer.Bounds.Height;
        if (viewWidth <= 0 || viewHeight <= 0) return;

        var fitZoom = Math.Min(viewWidth / PlanCanvas.Width, viewHeight / PlanCanvas.Height);
        SetZoom(Math.Min(fitZoom, 1.0));
        PlanScrollViewer.Offset = new Avalonia.Vector(0, 0);
    }

    private void SetZoom(double level)
    {
        _zoomLevel = Math.Max(MinZoom, Math.Min(MaxZoom, level));
        _zoomTransform.ScaleX = _zoomLevel;
        _zoomTransform.ScaleY = _zoomLevel;
        ZoomLevelText.Text = $"{(int)(_zoomLevel * 100)}%";
    }

    private void PlanScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            SetZoom(_zoomLevel + (e.Delta.Y > 0 ? ZoomStep : -ZoomStep));
        }
    }

    private void PlanScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Don't intercept scrollbar interactions
        if (IsScrollBarAtPoint(e))
            return;

        var point = e.GetCurrentPoint(PlanScrollViewer);
        var isMiddle = point.Properties.IsMiddleButtonPressed;
        var isLeft = point.Properties.IsLeftButtonPressed;

        // Middle mouse always pans; left-click pans only on empty canvas (not on nodes)
        if (isMiddle || (isLeft && !IsNodeAtPoint(e)))
        {
            _isPanning = true;
            _panStart = point.Position;
            _panStartOffsetX = PlanScrollViewer.Offset.X;
            _panStartOffsetY = PlanScrollViewer.Offset.Y;
            PlanScrollViewer.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(PlanScrollViewer);
            e.Handled = true;
        }
    }

    private void PlanScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;

        var current = e.GetPosition(PlanScrollViewer);
        var dx = current.X - _panStart.X;
        var dy = current.Y - _panStart.Y;

        var newX = Math.Max(0, _panStartOffsetX - dx);
        var newY = Math.Max(0, _panStartOffsetY - dy);

        // Defer offset change so the ScrollViewer doesn't overwrite it during layout
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlanScrollViewer.Offset = new Vector(newX, newY);
        });

        e.Handled = true;
    }

    private void PlanScrollViewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        PlanScrollViewer.Cursor = Cursor.Default;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>Check if the pointer event originated from a node Border.</summary>
    private bool IsNodeAtPoint(PointerPressedEventArgs e)
    {
        // Walk up the visual tree from the source to see if we hit a node border
        var source = e.Source as Control;
        while (source != null && source != PlanCanvas)
        {
            if (source is Border b && _nodeBorderMap.ContainsKey(b))
                return true;
            source = source.Parent as Control;
        }
        return false;
    }

    /// <summary>Check if the pointer event originated from a ScrollBar.</summary>
    private bool IsScrollBarAtPoint(PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && source != PlanScrollViewer)
        {
            if (source is ScrollBar)
                return true;
            source = source.Parent as Control;
        }
        return false;
    }

    #endregion

    #region Save & Statement Selection

    private async void SavePlan_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || string.IsNullOrEmpty(_currentPlan.RawXml)) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Plan",
            DefaultExtension = "sqlplan",
            SuggestedFileName = $"plan_{DateTime.Now:yyyyMMdd_HHmmss}.sqlplan",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("SQL Plan Files") { Patterns = new[] { "*.sqlplan" } },
                new FilePickerFileType("XML Files") { Patterns = new[] { "*.xml" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (file != null)
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_currentPlan.RawXml);
        }
    }

    #endregion

    #region Statements Panel

    private void PopulateStatementsGrid(List<PlanStatement> statements)
    {
        StatementsHeader.Text = $"Statements ({statements.Count})";

        var hasActualTimes = statements.Any(s => s.QueryTimeStats != null &&
            (s.QueryTimeStats.CpuTimeMs > 0 || s.QueryTimeStats.ElapsedTimeMs > 0));
        var hasUdf = statements.Any(s => s.QueryUdfElapsedTimeMs > 0);

        // Build columns
        StatementsGrid.Columns.Clear();

        StatementsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "#",
            Binding = new Avalonia.Data.Binding("Index"),
            Width = new DataGridLength(40),
            IsReadOnly = true
        });

        var queryTemplate = new FuncDataTemplate<StatementRow>((row, _) =>
        {
            if (row == null) return new TextBlock();
            var tb = new TextBlock
            {
                Text = row.QueryText,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 80,
                FontSize = 11,
                Margin = new Thickness(4, 2)
            };
            ToolTip.SetTip(tb, new TextBlock
            {
                Text = row.FullQueryText,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 600,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11
            });
            return tb;
        }, supportsRecycling: false);

        StatementsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Query",
            CellTemplate = queryTemplate,
            Width = new DataGridLength(250),
            IsReadOnly = true
        });

        if (hasActualTimes)
        {
            StatementsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "CPU",
                Binding = new Avalonia.Data.Binding("CpuDisplay"),
                Width = new DataGridLength(70),
                IsReadOnly = true,
                CustomSortComparer = new LongComparer(r => r.CpuMs)
            });
            StatementsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Elapsed",
                Binding = new Avalonia.Data.Binding("ElapsedDisplay"),
                Width = new DataGridLength(70),
                IsReadOnly = true,
                CustomSortComparer = new LongComparer(r => r.ElapsedMs)
            });
        }

        if (hasUdf)
        {
            StatementsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "UDF",
                Binding = new Avalonia.Data.Binding("UdfDisplay"),
                Width = new DataGridLength(70),
                IsReadOnly = true,
                CustomSortComparer = new LongComparer(r => r.UdfMs)
            });
        }

        if (!hasActualTimes)
        {
            StatementsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Est. Cost",
                Binding = new Avalonia.Data.Binding("CostDisplay"),
                Width = new DataGridLength(80),
                IsReadOnly = true,
                CustomSortComparer = new DoubleComparer(r => r.EstCost)
            });
        }

        StatementsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Critical",
            Binding = new Avalonia.Data.Binding("Critical"),
            Width = new DataGridLength(60),
            IsReadOnly = true
        });

        StatementsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Warnings",
            Binding = new Avalonia.Data.Binding("Warnings"),
            Width = new DataGridLength(70),
            IsReadOnly = true
        });

        // Build rows
        var rows = new List<StatementRow>();
        for (int i = 0; i < statements.Count; i++)
        {
            var stmt = statements[i];
            var allWarnings = stmt.PlanWarnings.ToList();
            if (stmt.RootNode != null)
                CollectNodeWarnings(stmt.RootNode, allWarnings);

            var fullText = stmt.StatementText;
            if (string.IsNullOrWhiteSpace(fullText))
                fullText = $"Statement {i + 1}";
            var displayText = fullText.Length > 120 ? fullText[..120] + "..." : fullText;

            rows.Add(new StatementRow
            {
                Index = i + 1,
                QueryText = displayText,
                FullQueryText = fullText,
                CpuMs = stmt.QueryTimeStats?.CpuTimeMs ?? 0,
                ElapsedMs = stmt.QueryTimeStats?.ElapsedTimeMs ?? 0,
                UdfMs = stmt.QueryUdfElapsedTimeMs,
                EstCost = stmt.StatementSubTreeCost,
                Critical = allWarnings.Count(w => w.Severity == PlanWarningSeverity.Critical),
                Warnings = allWarnings.Count(w => w.Severity == PlanWarningSeverity.Warning),
                Statement = stmt
            });
        }

        StatementsGrid.ItemsSource = rows;
    }

    private void StatementsGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (StatementsGrid.SelectedItem is StatementRow row)
            RenderStatement(row.Statement);
    }

    private async void CopyStatementText_Click(object? sender, RoutedEventArgs e)
    {
        if (StatementsGrid.SelectedItem is not StatementRow row) return;
        var text = row.Statement.StatementText;
        if (string.IsNullOrEmpty(text)) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
            await topLevel.Clipboard.SetTextAsync(text);
    }

    private static void CollectNodeWarnings(PlanNode node, List<PlanWarning> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectNodeWarnings(child, warnings);
    }

    private void ToggleStatements_Click(object? sender, RoutedEventArgs e)
    {
        if (StatementsPanel.IsVisible)
            CloseStatementsPanel();
        else
            ShowStatementsPanel();
    }

    private void CloseStatements_Click(object? sender, RoutedEventArgs e)
    {
        CloseStatementsPanel();
    }

    private void ShowStatementsPanel()
    {
        _statementsColumn.Width = new GridLength(450);
        _statementsSplitterColumn.Width = new GridLength(5);
        StatementsSplitter.IsVisible = true;
        StatementsPanel.IsVisible = true;
        StatementsButton.IsVisible = true;
        StatementsButtonSeparator.IsVisible = true;
    }

    private void CloseStatementsPanel()
    {
        StatementsPanel.IsVisible = false;
        StatementsSplitter.IsVisible = false;
        _statementsColumn.Width = new GridLength(0);
        _statementsSplitterColumn.Width = new GridLength(0);
    }

    #endregion

    #region Helpers

    private IBrush FindBrushResource(string key)
    {
        if (this.TryFindResource(key, out var resource) && resource is IBrush brush)
            return brush;

        // Fallback brushes in case resources are not found
        return key switch
        {
            "BackgroundLightBrush" => new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E)),
            "BorderBrush" => new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x45)),
            "ForegroundBrush" => new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB)),
            "ForegroundMutedBrush" => TooltipFgBrush,
            _ => Brushes.White
        };
    }

    #endregion
}

/// <summary>Sort DataGrid column by a long property on StatementRow.</summary>
public class LongComparer : System.Collections.IComparer
{
    private readonly Func<StatementRow, long> _selector;
    public LongComparer(Func<StatementRow, long> selector) => _selector = selector;
    public int Compare(object? x, object? y)
    {
        if (x is StatementRow a && y is StatementRow b)
            return _selector(a).CompareTo(_selector(b));
        return 0;
    }
}

/// <summary>Sort DataGrid column by a double property on StatementRow.</summary>
public class DoubleComparer : System.Collections.IComparer
{
    private readonly Func<StatementRow, double> _selector;
    public DoubleComparer(Func<StatementRow, double> selector) => _selector = selector;
    public int Compare(object? x, object? y)
    {
        if (x is StatementRow a && y is StatementRow b)
            return _selector(a).CompareTo(_selector(b));
        return 0;
    }
}
