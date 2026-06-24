using System;
using System.Collections.Generic;
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
using AvaloniaEdit.TextMate;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Dialogs;
using PlanViewer.Core.Interfaces;
using PlanViewer.App.Helpers;
using PlanViewer.App.Services;
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
    private static readonly SolidColorBrush MinimapExpensiveNodeBgBrush = new(Color.FromArgb(0x60, 0xE5, 0x73, 0x73));

    // Link accuracy coloring brushes (Dark theme)
    private static readonly SolidColorBrush LinkFluoBlueBrush = new(Color.FromRgb(0x00, 0xE5, 0xFF));
    private static readonly SolidColorBrush LinkLightBlueBrush = new(Color.FromRgb(0x64, 0xB5, 0xF6));
    private static readonly SolidColorBrush LinkBlueBrush = new(Color.FromRgb(0x42, 0x8B, 0xCA));
    private static readonly SolidColorBrush LinkLightOrangeBrush = new(Color.FromRgb(0xFF, 0xB7, 0x4D));
    private static readonly SolidColorBrush LinkFluoOrangeBrush = new(Color.FromRgb(0xFF, 0x8C, 0x00));
    private static readonly SolidColorBrush LinkFluoRedBrush = new(Color.FromRgb(0xFF, 0x17, 0x44));


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

    // Minimap state
    private static double _minimapWidth = 400;
    private static double _minimapHeight = 400;
    private const double MinimapMinSize = 200;
    private const double MinimapMaxSize = 500;
    private bool _minimapDragging;
    private Border? _minimapViewportBox;
    private bool _minimapResizing;
    private Point _minimapResizeStart;
    private double _minimapResizeStartW;
    private double _minimapResizeStartH;
    private readonly Dictionary<Border, PlanNode> _minimapNodeMap = new();
    private Border? _minimapSelectedNode;
    private PlanNode? _selectedNode;

    public PlanViewerControl()
    {
        InitializeComponent();
        // Use Tunnel routing so Ctrl+wheel zoom fires before ScrollViewer consumes the event
        PlanScrollViewer.AddHandler(PointerWheelChangedEvent, PlanScrollViewer_PointerWheelChanged, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Use Tunnel routing so pan handlers fire before ScrollViewer consumes the events
        PlanScrollViewer.AddHandler(PointerPressedEvent, PlanScrollViewer_PointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PlanScrollViewer.AddHandler(PointerMovedEvent, PlanScrollViewer_PointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PlanScrollViewer.AddHandler(PointerReleasedEvent, PlanScrollViewer_PointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PlanScrollViewer.ScrollChanged += (_, _) => UpdateMinimapViewportBox();

        // Resolve ColumnDefinitions from the named 5-column layout Grid.
        // (x:Name works on Grid but not on ColumnDefinition, so we index into the definitions.)
        //   [0]=Statements(0), [1]=StmtSplitter(0), [2]=Canvas(*), [3]=PropsSplitter(0), [4]=Props(0)
        _statementsColumn = PlanGrid.ColumnDefinitions[0];
        _statementsSplitterColumn = PlanGrid.ColumnDefinitions[1];
        _splitterColumn = PlanGrid.ColumnDefinitions[3];
        _propertiesColumn = PlanGrid.ColumnDefinitions[4];

        // ScaleTransform is the LayoutTransform of the wrapper around PlanCanvas
        var layoutTransform = this.FindControl<Avalonia.Controls.LayoutTransformControl>("PlanLayoutTransform")!;
        _zoomTransform = (ScaleTransform)layoutTransform.LayoutTransform!;

        Helpers.DataGridBehaviors.Attach(StatementsGrid);

        // Wire minimap resize grip (defined in AXAML, not in canvas)
        MinimapResizeGrip.PointerPressed += MinimapResizeGrip_PointerPressed;
        MinimapResizeGrip.PointerMoved += MinimapResizeGrip_PointerMoved;
        MinimapResizeGrip.PointerReleased += MinimapResizeGrip_PointerReleased;

        // Wire minimap canvas interaction handlers once
        MinimapCanvas.PointerPressed += MinimapCanvas_PointerPressed;
        MinimapCanvas.PointerMoved += MinimapCanvas_PointerMoved;
        MinimapCanvas.PointerReleased += MinimapCanvas_PointerReleased;
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
    /// Reason the most recent <see cref="LoadPlan"/> failed (blank XML, parse error,
    /// or no renderable statements), or null when it succeeded. Lets callers surface
    /// why a plan didn't load instead of silently showing the empty state.
    /// </summary>
    public string? LastLoadError { get; private set; }

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

    /// <summary>
    /// Connection string for schema lookups. Set when the plan was loaded from a connected session.
    /// </summary>
    public string? ConnectionString { get; set; }

    // Connection state for plans that connect via the toolbar
    private ServerConnection? _planConnection;
    private ICredentialService? _planCredentialService;
    private ConnectionStore? _planConnectionStore;
    private string? _planSelectedDatabase;

    /// <summary>
    /// Provide credential service and connection store so the plan viewer can show a connection dialog.
    /// </summary>
    public void SetConnectionServices(ICredentialService credentialService, ConnectionStore connectionStore)
    {
        _planCredentialService = credentialService;
        _planConnectionStore = connectionStore;
    }

    /// <summary>
    /// Update the connection UI to reflect an active connection (used when connection is inherited).
    /// </summary>
    public void SetConnectionStatus(string serverName, string? database)
    {
        PlanServerLabel.Text = serverName;
        PlanServerLabel.Foreground = Brushes.LimeGreen;
        PlanConnectButton.Content = "Reconnect";
        if (database != null)
            _planSelectedDatabase = database;
    }

    // Events for MainWindow to wire up advice/repro actions
    public event EventHandler? HumanAdviceRequested;
    public event EventHandler? RobotAdviceRequested;
    public event EventHandler? CopyReproRequested;
    public event EventHandler<string>? OpenInEditorRequested;

    /// <summary>
    /// Navigates to a specific plan node by ID: selects it, zooms to show it,
    /// and scrolls to center it in the viewport.
    /// </summary>
    public void NavigateToNode(int nodeId)
    {
        // Find the Border for this node
        Border? targetBorder = null;
        PlanNode? targetNode = null;
        foreach (var (border, node) in _nodeBorderMap)
        {
            if (node.NodeId == nodeId)
            {
                targetBorder = border;
                targetNode = node;
                break;
            }
        }

        if (targetBorder == null || targetNode == null)
            return;

        // Activate the parent window so the plan viewer becomes visible
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window parentWindow)
            parentWindow.Activate();

        // Select the node (highlights it and shows properties)
        SelectNode(targetBorder, targetNode);

        // Ensure zoom level makes the node comfortably visible
        var viewWidth = PlanScrollViewer.Bounds.Width;
        var viewHeight = PlanScrollViewer.Bounds.Height;
        if (viewWidth <= 0 || viewHeight <= 0)
            return;

        // If the node is too small at the current zoom, zoom in so it's ~1/3 of the viewport
        var nodeW = PlanLayoutEngine.NodeWidth;
        var nodeH = PlanLayoutEngine.GetNodeHeight(targetNode);
        var minVisibleZoom = Math.Min(viewWidth / (nodeW * 4), viewHeight / (nodeH * 4));
        if (_zoomLevel < minVisibleZoom)
            SetZoom(Math.Min(minVisibleZoom, 1.0));

        // Scroll to center the node in the viewport
        var centerX = (targetNode.X + nodeW / 2) * _zoomLevel - viewWidth / 2;
        var centerY = (targetNode.Y + nodeH / 2) * _zoomLevel - viewHeight / 2;
        centerX = Math.Max(0, centerX);
        centerY = Math.Max(0, centerY);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlanScrollViewer.Offset = new Vector(centerX, centerY);
        });
    }

    /// <summary>
    /// Parses and renders a plan. Returns true when a plan was rendered; false when the
    /// XML was blank, failed to parse, or contained no renderable statements (in which case
    /// the empty state explains why and <see cref="LastLoadError"/> holds the reason).
    /// </summary>
    public bool LoadPlan(string planXml, string label, string? queryText = null)
    {
        _label = label;
        _queryText = queryText;
        LastLoadError = null;

        // Query text stored for copy/repro but no longer shown in a
        // separate expander — it's already visible in the Statements grid.

        // A Query Store row can have a NULL/empty query_plan; don't treat that
        // (or a parse failure) as a silent "No Plan Loaded".
        if (string.IsNullOrWhiteSpace(planXml))
        {
            LastLoadError = "The plan is empty — this source has no stored query plan XML.";
            ShowEmptyState("Couldn't Load Plan", LastLoadError);
            return false;
        }

        _currentPlan = ShowPlanParser.Parse(planXml);

        // ShowPlanParser never throws; it records failures in ParseError and returns an
        // empty plan. Surface that instead of rendering a blank "No Plan Loaded" panel.
        if (!string.IsNullOrEmpty(_currentPlan.ParseError))
        {
            LastLoadError = _currentPlan.ParseError;
            ShowEmptyState("Couldn't Load Plan", $"Parse error: {_currentPlan.ParseError}");
            return false;
        }

        PlanAnalyzer.Analyze(_currentPlan, ConfigLoader.Load(), _serverMetadata);
        BenefitScorer.Score(_currentPlan);

        var allStatements = _currentPlan.Batches
            .SelectMany(b => b.Statements)
            .Where(s => s.RootNode != null)
            .ToList();

        if (allStatements.Count == 0)
        {
            LastLoadError = "The plan parsed but contains no statements to display.";
            ShowEmptyState("No Plan Loaded", null);
            return false;
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

        return true;
    }

    /// <summary>
    /// Shows the empty-state panel with a title and, optionally, an error detail line.
    /// When <paramref name="error"/> is null the normal "open a file" hint is shown instead.
    /// </summary>
    private void ShowEmptyState(string title, string? error)
    {
        EmptyStateTitle.Text = title;
        if (string.IsNullOrEmpty(error))
        {
            EmptyStateError.IsVisible = false;
            EmptyStateHint.IsVisible = true;
        }
        else
        {
            EmptyStateError.Text = error;
            EmptyStateError.IsVisible = true;
            EmptyStateHint.IsVisible = false;
        }
        EmptyState.IsVisible = true;
        PlanScrollViewer.IsVisible = false;
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
        _selectedNode = null;
        LastLoadError = null;
        ShowEmptyState("No Plan Loaded", null);
        InsightsPanel.IsVisible = false;
        CostText.Text = "";
        CloseStatementsPanel();
        StatementsButton.IsVisible = false;
        StatementsButtonSeparator.IsVisible = false;
        ClosePropertiesPanel();
        CloseMinimapPanel();
    }


    #region Minimap


    private static readonly Color[] MinimapBranchColors =
    {
        Color.FromArgb(0x30, 0x4F, 0xA3, 0xFF), // blue
        Color.FromArgb(0x30, 0x7B, 0xCF, 0x7B), // green
        Color.FromArgb(0x30, 0xFF, 0xB3, 0x47), // orange
        Color.FromArgb(0x30, 0xE5, 0x73, 0x73), // red
        Color.FromArgb(0x30, 0xCF, 0x7B, 0xCF), // purple
        Color.FromArgb(0x30, 0x7B, 0xCF, 0xCF), // teal
        Color.FromArgb(0x30, 0xFF, 0xE0, 0x4F), // yellow
        Color.FromArgb(0x30, 0xFF, 0x7B, 0xA5), // pink
    };


    // Cached per render cycle in RenderMinimap() to avoid per-node brush creation
    private IBrush _minimapNodeBorderBrushCache = Brushes.Gray;


    #endregion


    #region Plan Viewer Connection

    private async void PlanConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_planCredentialService == null || _planConnectionStore == null) return;

        var dialog = new ConnectionDialog(_planCredentialService, _planConnectionStore);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window parentWindow) return;

        var result = await dialog.ShowDialog<bool?>(parentWindow);
        if (result != true || dialog.ResultConnection == null) return;

        _planConnection = dialog.ResultConnection;
        _planSelectedDatabase = dialog.ResultDatabase;
        ConnectionString = _planConnection.GetConnectionString(_planCredentialService, _planSelectedDatabase);

        PlanServerLabel.Text = _planConnection.ServerName;
        PlanServerLabel.Foreground = Brushes.LimeGreen;
        PlanConnectButton.Content = "Reconnect";

        // Populate database dropdown
        try
        {
            var connStr = _planConnection.GetConnectionString(_planCredentialService, "master");
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            var databases = new List<string>();
            using var cmd = new SqlCommand(
                "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databases.Add(reader.GetString(0));

            PlanDatabaseBox.ItemsSource = databases;
            PlanDatabaseBox.IsEnabled = true;

            if (_planSelectedDatabase != null)
            {
                for (int i = 0; i < PlanDatabaseBox.Items.Count; i++)
                {
                    if (PlanDatabaseBox.Items[i]?.ToString() == _planSelectedDatabase)
                    {
                        PlanDatabaseBox.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
        catch
        {
            PlanDatabaseBox.IsEnabled = false;
        }
    }

    private void PlanDatabase_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_planConnection == null || _planCredentialService == null || PlanDatabaseBox.SelectedItem == null) return;

        _planSelectedDatabase = PlanDatabaseBox.SelectedItem.ToString();
        ConnectionString = _planConnection.GetConnectionString(_planCredentialService, _planSelectedDatabase);
    }

    #endregion

    #region Schema Lookup


    // --- Formatters (same logic as QuerySessionControl) ---


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
