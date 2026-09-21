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

    // Display helpers. The duration ladder this grid used to carry privately is now
    // MetricFormatter's, so the panels and tooltips scale the same numbers the same way.
    public string CpuDisplay => MetricFormatter.FormatDuration(CpuMs);
    public string ElapsedDisplay => MetricFormatter.FormatDuration(ElapsedMs);
    public string UdfDisplay => UdfMs > 0 ? MetricFormatter.FormatDuration(UdfMs) : "";
    public string CostDisplay => EstCost > 0 ? MetricFormatter.FormatCost(EstCost) : "";
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

    /* Minimap state. The default is a corner overlay, not a window: at the old 400x400 it
       covered roughly a quarter of the canvas on a laptop and read as something you had to
       dismiss to carry on working, which defeats a navigation aid. Resize still reaches 500
       for anyone who wants the old size, and the chosen size is static so it survives being
       reopened on another plan. */
    private static double _minimapWidth = 220;
    private static double _minimapHeight = 220;
    private const double MinimapMinSize = 160;
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

    /// <summary>
    /// Pans the plan toolbar when it is wider than the window — the same contract as the
    /// session toolbar's row: wheel up scrolls left, a horizontal wheel wins when the device
    /// sends both, and a fully visible toolbar leaves the wheel to whatever is under it.
    /// </summary>
    private void OnPlanToolbarWheel(object? sender, PointerWheelEventArgs e)
    {
        var max = Math.Max(0, PlanToolbarScroll.Extent.Width - PlanToolbarScroll.Viewport.Width);
        if (max <= 0)
            return;

        var delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        if (delta == 0)
            return;

        PlanToolbarScroll.Offset = new Avalonia.Vector(
            Math.Clamp(PlanToolbarScroll.Offset.X - (delta * 48), 0, max),
            PlanToolbarScroll.Offset.Y);
        e.Handled = true;
    }

    /// <summary>
    /// The toolbar's overflow: which trailing commands have moved into the chevron menu because
    /// the row is wider than the window, and the menu they moved into.
    /// </summary>
    public ToolbarOverflow Overflow { get; }

    public PlanViewerControl()
    {
        InitializeComponent();

        /* Icon adoption for the XAML-declared toolbar buttons. Set here rather than in the
           XAML because a bare PathIcon does not inherit the button's foreground (its stock
           theme sets one); AppIcons.MakeContent installs the corrected theme. */
        PlanConnectButton.Content = AppIcons.MakeContent(AppIcons.Connect, "Connect");
        SavePlanButton.Content = AppIcons.MakeContent(AppIcons.Save, "Save .sqlplan");
        StatementsButton.Content = AppIcons.MakeContent(AppIcons.Statements, "Statements");
        PlanToolbarOverflowButton.Content = AppIcons.MakeIcon(AppIcons.More);

        /* The minimap toggle used to be the literal word "minimap" at 9px, which read as a label
           rather than a control. AppIcons.Minimap was drawn for this button and left unwired. */
        MinimapToggleButton.Content = AppIcons.MakeIcon(AppIcons.Minimap);

        /* Same contract as the session toolbar's overflow, in the order these leave the row:
           Statements first, then Save. Zoom, Fit and the zoom readout stay — they are what this
           toolbar is for, and Fit in particular is the recovery from a zoom that went wrong.

           Statements is the interesting one: its visibility already belongs to the plan (a plan
           with no statement list has no button), so the overflow observes that intent rather than
           overwriting it — see ToolbarOverflow. Its divider is registered with it for the same
           reason the session toolbar's group dividers are, and moves with it either way. */
        var collapsible = new List<ToolbarOverflow.Item>
        {
            new(StatementsButton, AppIcons.Statements, "Statements", StatementsButtonSeparator),
            new(SavePlanButton, AppIcons.Save, "Save .sqlplan", SavePlanSeparator)
        };

        Overflow = ToolbarOverflow.Attach(
            PlanToolbarScroll, PlanToolbarOverflowButton, new MenuFlyout(), collapsible);

        // Same wheel contract as the session toolbar's scrolling row (see OnPlanToolbarWheel).
        PlanToolbarScroll.AddHandler(PointerWheelChangedEvent, OnPlanToolbarWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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
        // Same text the Copy Query Text menu entry produces (#467) — Ctrl+C is that entry's
        // unlabelled twin, and handing the two of them different statements is its own bug report.
        Helpers.DataGridBehaviors.AttachCopyGuard(StatementsGrid,
            item => item is StatementRow row ? RunnableStatementText(row.Statement) : null);

        /* Wire minimap resize grip (defined in AXAML, not in canvas).

           PointerCaptureLost matters as much as PointerReleased. Capture can go away without a
           release — alt-tab mid-drag, a touch cancel, another control taking it — and the "am I
           dragging?" flag is the only thing the move handlers check. Left set, a later plain
           hover over the grip resizes the panel against a start point from minutes ago. */
        MinimapResizeGrip.PointerPressed += MinimapResizeGrip_PointerPressed;
        MinimapResizeGrip.PointerMoved += MinimapResizeGrip_PointerMoved;
        MinimapResizeGrip.PointerReleased += MinimapResizeGrip_PointerReleased;
        MinimapResizeGrip.PointerCaptureLost += (_, _) => _minimapResizing = false;

        // Wire minimap canvas interaction handlers once
        MinimapCanvas.PointerPressed += MinimapCanvas_PointerPressed;
        MinimapCanvas.PointerMoved += MinimapCanvas_PointerMoved;
        MinimapCanvas.PointerReleased += MinimapCanvas_PointerReleased;
        MinimapCanvas.PointerCaptureLost += (_, _) => _minimapDragging = false;
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

    /// <summary>
    /// Whether this viewer is living as a sub-tab inside a query session, rather than as a
    /// top-level tab of its own.
    ///
    /// <para>Hosted, it drops the connection half of its toolbar — Reconnect, the server label
    /// and the Database picker — because the session's toolbar is one row above showing the same
    /// connection, and its own picker was permanently disabled there anyway: a plan inside a
    /// session inherits <see cref="ConnectionString"/> from the session (see
    /// QuerySessionControl.AddPlanTab), and never populates a database list of its own. Two
    /// stacked toolbars, three of the controls duplicated, one of them dead.</para>
    ///
    /// <para>Everything plan-scoped stays: zoom, Fit, the zoom readout, Save .sqlplan and
    /// Statements. And schema lookups keep working, since they read ConnectionString rather than
    /// the controls.</para>
    /// </summary>
    public bool HostedInSession
    {
        get => _hostedInSession;
        set
        {
            _hostedInSession = value;
            PlanConnectionControls.IsVisible = !value;
        }
    }

    private bool _hostedInSession;

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
    /// Update the connection UI to reflect an active connection (used when connection is
    /// inherited). Label and button only — the session-hosted viewers that call this hide the
    /// whole connection toolbar, so there is no picker to feed. A standalone tab with a live
    /// toolbar wants <see cref="AdoptConnection"/> instead.
    /// </summary>
    public void SetConnectionStatus(string serverName, string? database)
    {
        PlanServerLabel.Text = serverName;
        PlanServerLabel.Foreground = FindBrushResource("SuccessBrush");
        PlanConnectButton.Content = AppIcons.MakeContent(AppIcons.Connect, "Reconnect");
        if (database != null)
            _planSelectedDatabase = database;
    }

    /// <summary>
    /// Takes over a connection the ConnectionDialog just validated, for a standalone tab whose
    /// toolbar is visible: paints the status AND fills, enables and pre-selects the database
    /// picker, with <see cref="_planConnection"/> set so changing the picker actually switches
    /// <see cref="ConnectionString"/>. Status alone left a green label over a disabled, empty
    /// picker — the same lie the connect handlers used to tell (#540 follow-up).
    ///
    /// <para>Call <see cref="SetConnectionServices"/> first: the picker's SelectionChanged
    /// rebuilds the connection string through the credential service.</para>
    /// </summary>
    public void AdoptConnection(ServerConnection connection, string? database,
        IReadOnlyList<string> databases)
    {
        _planConnection = connection;
        SetConnectionStatus(connection.ServerName, database);

        PlanDatabaseBox.ItemsSource = databases;
        PlanDatabaseBox.IsEnabled = true;
        SelectPlanDatabase();
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

        PlanAnalysisPipeline.AnalyzeParsed(_currentPlan, ConfigLoader.Load(), _serverMetadata);

        /* #456 gave the analysis pipeline and Human/Robot Advice (ResultMapper) the shared
           PlanStatements.EnumerateAll traversal, which descends into stored procedure and UDF
           bodies. The grid and the MCP registration below kept walking batch.Statements, so an
           EXEC <procedure> plan showed one grid row — the EXEC itself — and registered near-zero
           counts, while the advice discussed dozens of warnings the UI could neither display nor
           navigate to. Same traversal here so what the grid shows, what the session reports, and
           what the advice says are the same plan. */
        var everyStatement = PlanStatements.EnumerateAllWithContainer(_currentPlan).ToList();

        /* Only statements with a root node can render on the canvas. The same filter has always
           applied to the outer batch (where the parser's synthetic statement roots mean it
           rarely excludes anything); it now applies across the whole traversal. */
        var allStatements = everyStatement
            .Where(e => e.Statement.RootNode != null)
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
        _allStatements = allStatements.Select(e => e.Statement).ToList();
        PopulateStatementsGrid(allStatements);
        ShowStatementsPanel();
        StatementsGrid.SelectedIndex = 0;

        /* Register with MCP session manager for AI tool access. Counts run over EVERY statement,
           renderable or not, because that is what the advice an MCP client reads was built from:
           statement-level PlanWarnings plus all node warnings, proc/UDF bodies included.
           StatementCount likewise matches the analysis output's total_statements rather than the
           grid's renderable subset. */
        int warningCount = 0, criticalCount = 0;
        foreach (var entry in everyStatement)
        {
            var s = entry.Statement;
            warningCount += s.PlanWarnings.Count;
            criticalCount += s.PlanWarnings.Count(w => w.Severity == PlanWarningSeverity.Critical);
            if (s.RootNode != null)
                CountNodeWarnings(s.RootNode, ref warningCount, ref criticalCount);
        }

        const string sessionSource = "file";
        PlanSessionManager.Instance.Register(new PlanViewer.Core.Models.PlanSession
        {
            SessionId = _mcpSessionId,
            Label = label,
            Source = sessionSource,
            Plan = _currentPlan,
            QueryText = queryText,
            StatementCount = everyStatement.Count,
            HasActualStats = everyStatement.Any(e => e.Statement.QueryTimeStats != null),
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
        // Button and divider both belong to the overflow — see ShowStatementsPanel.
        Overflow.SetAvailable(StatementsButton, false);
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

        // Pass the current database so a reconnect comes back to it rather than master.
        var dialog = new ConnectionDialog(_planCredentialService, _planConnectionStore, _planSelectedDatabase);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not Window parentWindow) return;

        var result = await dialog.ShowDialog<bool?>(parentWindow);
        if (result != true || dialog.ResultConnection == null) return;

        _planConnection = dialog.ResultConnection;
        _planSelectedDatabase = dialog.ResultDatabase;
        ConnectionString = _planConnection.GetConnectionString(_planCredentialService, _planSelectedDatabase);

        PlanServerLabel.Text = _planConnection.ServerName;
        PlanServerLabel.Foreground = FindBrushResource("SuccessBrush");
        PlanConnectButton.Content = AppIcons.MakeContent(AppIcons.Connect, "Reconnect");

        /* The dialog only closes with true after it opened this connection and enumerated
           these databases — through the database the user named, which is the one some
           logins (Azure SQL DB, JIT access) can open when master is off limits. Asking
           again here through a second, hardcoded-master connection was a wasted round trip
           whose swallowed failure left a green toolbar over a dead database picker. Same
           hand-over QuerySessionControl's connect block takes. */
        PlanDatabaseBox.ItemsSource = dialog.ResultDatabases;
        PlanDatabaseBox.IsEnabled = true;
        SelectPlanDatabase();
    }

    /// <summary>
    /// Points the picker at <see cref="_planSelectedDatabase"/> when the list holds it. The
    /// selection this raises recomputes the same ConnectionString the caller already set, which
    /// is idempotent on purpose — the handler is the one place the string is derived.
    /// </summary>
    private void SelectPlanDatabase()
    {
        if (_planSelectedDatabase == null) return;

        for (int i = 0; i < PlanDatabaseBox.Items.Count; i++)
        {
            if (PlanDatabaseBox.Items[i]?.ToString() == _planSelectedDatabase)
            {
                PlanDatabaseBox.SelectedIndex = i;
                break;
            }
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
