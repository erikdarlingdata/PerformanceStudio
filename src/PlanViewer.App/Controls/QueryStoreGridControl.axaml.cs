using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Services;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class QueryStoreGridControl : UserControl
{
    private readonly ServerConnection _serverConnection;
    private readonly ICredentialService _credentialService;
    private string _connectionString;
    private string _database;
    private CancellationTokenSource? _fetchCts;
    private ObservableCollection<QueryStoreRow> _rows = new();
    private ObservableCollection<QueryStoreRow> _filteredRows = new();
    private readonly Dictionary<string, ColumnFilterState> _activeFilters = new();
    private Popup? _filterPopup;
    private ColumnFilterPopup? _filterPopupContent;
    private string? _sortedColumnTag;
    private bool _sortAscending;
    private DateTime? _slicerStartUtc;
    private DateTime? _slicerEndUtc;
    private int _slicerDaysBack = 30;
    private string _lastFetchedOrderBy = "cpu";
    private bool _initialOrderByLoaded;
    private bool _suppressRangeChanged;
    private string? _waitHighlightCategory;
    private const int AutoSelectTopN = 1; // number of rows auto-selected after each fetch
    private bool _waitStatsSupported;  // false until version + capture mode confirmed
    private bool _waitStatsEnabled = true;
    private bool _waitPercentMode;
    private QueryStoreGroupBy _groupByMode = QueryStoreGroupBy.QueryHash;
    private List<QueryStoreRow> _groupedRootRows = new(); // top-level rows for grouped mode

    public event EventHandler<List<QueryStorePlan>>? PlansSelected;
    public event EventHandler<string>? DatabaseChanged;

    public string Database => _database;

    public QueryStoreGridControl(ServerConnection serverConnection, ICredentialService credentialService,
        string initialDatabase, List<string> databases, bool supportsWaitStats = false)
    {
        _serverConnection = serverConnection;
        _credentialService = credentialService;
        _database = initialDatabase;
        _connectionString = serverConnection.GetConnectionString(credentialService, initialDatabase);
        _waitStatsSupported = supportsWaitStats;

        var userSettings = AppSettingsService.Load();
        _slicerDaysBack = userSettings.QueryStoreSlicerDays;

        InitializeComponent();

        // Apply user defaults to UI controls
        TopNBox.Value = userSettings.QueryStoreTopLimit;
        SelectComboByTag(OrderByBox, userSettings.QueryStoreDefaultMetric);
        SelectComboByTag(GroupByBox, userSettings.QueryStoreDefaultGroupBy switch
        {
            "QueryHash" => "query-hash",
            "Module" => "module",
            "None" => "none",
            _ => "query-hash"
        });

        ResultsGrid.ItemsSource = _filteredRows;
        Helpers.DataGridBehaviors.Attach(ResultsGrid);
        EnsureFilterPopup();
        SetupColumnHeaders();
        PopulateDatabaseBox(databases, initialDatabase);
        TimeRangeSlicer.RangeChanged += OnTimeRangeChanged;

        WaitStatsProfile.CategoryClicked += OnWaitCategoryClicked;
        WaitStatsProfile.CategoryDoubleClicked += OnWaitCategoryDoubleClicked;
        WaitStatsProfile.CollapsedChanged += OnWaitStatsCollapsedChanged;

        if (!_waitStatsSupported)
        {
            // Hide wait stats panel and column when server doesn't support it
            WaitStatsProfile.Collapse();
            WaitStatsChevronButton.IsVisible = false;
            WaitStatsSplitter.IsVisible = false;
            SlicerRow.ColumnDefinitions[2].Width = new GridLength(0);
            var waitProfileCol = ResultsGrid.Columns
                .FirstOrDefault(c => c.SortMemberPath == "WaitGrandTotalSort");
            if (waitProfileCol != null)
                waitProfileCol.IsVisible = false;
        }

        // Auto-fetch with default settings on connect
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ReorderColumnsForGroupBy();
            Fetch_Click(null, new RoutedEventArgs());
            _initialOrderByLoaded = true;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Sets the initial slicer time range (e.g. from overview drill-down).
    /// Must be called before the control is loaded to take effect on the first fetch.
    /// </summary>
    public void SetInitialTimeRange(DateTime startUtc, DateTime endUtc)
    {
        _slicerStartUtc = startUtc;
        _slicerEndUtc = endUtc;
    }

    private void PopulateDatabaseBox(List<string> databases, string selectedDatabase)
    {
        QsDatabaseBox.ItemsSource = databases;
        QsDatabaseBox.SelectedItem = selectedDatabase;
    }

    private async void QsDatabase_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (QsDatabaseBox.SelectedItem is not string db || db == _database) return;

        _fetchCts?.Cancel();

        // Check if Query Store is enabled on the new database
        var newConnStr = _serverConnection.GetConnectionString(_credentialService, db);
        StatusText.Text = "Checking Query Store...";

        try
        {
            var (enabled, state) = await QueryStoreService.CheckEnabledAsync(newConnStr);
            if (!enabled)
            {
                StatusText.Text = $"Query Store not enabled on {db} ({state ?? "unknown"})";
                QsDatabaseBox.SelectedItem = _database; // revert
                return;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message.Length > 60 ? ex.Message[..60] + "..." : ex.Message;
            QsDatabaseBox.SelectedItem = _database; // revert
            return;
        }

        _database = db;
        _connectionString = newConnStr;
        _rows.Clear();
        _filteredRows.Clear();
        LoadButton.IsEnabled = false;
        StatusText.Text = "";
        DatabaseChanged?.Invoke(this, db);
    }


    private int[]? _savedColumnDisplayIndices;


    // ── Wait stats ─────────────────────────────────────────────────────────


    // ── Context menu ────────────────────────────────────────────────────────


    // ── Column filter infrastructure ───────────────────────────────────────

    private static readonly Dictionary<string, Func<QueryStoreRow, string>> TextAccessors = new()
    {
        ["QueryHash"]     = r => r.QueryHash,
        ["PlanHash"]      = r => r.QueryPlanHash,
        ["ModuleName"]    = r => r.ModuleName,
        ["LastExecuted"]  = r => r.LastExecutedLocal,
        ["QueryText"]     = r => r.FullQueryText,
    };

    private static readonly Dictionary<string, Func<QueryStoreRow, double>> NumericAccessors = new()
    {
        ["QueryId"]        = r => r.QueryId,
        ["PlanId"]         = r => r.PlanId,
        ["Executions"]     = r => r.ExecsSort,
        ["TotalCpu"]       = r => r.TotalCpuSort / 1000.0,       // µs → ms (matches display)
        ["AvgCpu"]         = r => r.AvgCpuSort / 1000.0,         // µs → ms
        ["TotalDuration"]  = r => r.TotalDurSort / 1000.0,       // µs → ms
        ["AvgDuration"]    = r => r.AvgDurSort / 1000.0,         // µs → ms
        ["TotalReads"]     = r => r.TotalReadsSort,
        ["AvgReads"]       = r => r.AvgReadsSort,
        ["TotalWrites"]    = r => r.TotalWritesSort,
        ["AvgWrites"]      = r => r.AvgWritesSort,
        ["TotalPhysReads"] = r => r.TotalPhysReadsSort,
        ["AvgPhysReads"]   = r => r.AvgPhysReadsSort,
        ["TotalMemory"]    = r => r.TotalMemSort * 8.0 / 1024.0, // pages → MB (matches display)
        ["AvgMemory"]      = r => r.AvgMemSort * 8.0 / 1024.0,   // pages → MB
    };


    // ── Bar chart ratio computation ────────────────────────────────────────

    // Maps a ColumnId (used in BarChartConfig) to the accessor that returns the raw sort value.
    private static readonly (string ColumnId, Func<QueryStoreRow, double> Accessor)[] BarColumns =
    [
        ("Executions",    r => r.ExecsSort),
        ("TotalCpu",      r => r.TotalCpuSort),
        ("AvgCpu",        r => r.AvgCpuSort),
        ("TotalDuration", r => r.TotalDurSort),
        ("AvgDuration",   r => r.AvgDurSort),
        ("TotalReads",    r => r.TotalReadsSort),
        ("AvgReads",      r => r.AvgReadsSort),
        ("TotalWrites",   r => r.TotalWritesSort),
        ("AvgWrites",     r => r.AvgWritesSort),
        ("TotalPhysReads",r => r.TotalPhysReadsSort),
        ("AvgPhysReads",  r => r.AvgPhysReadsSort),
        ("TotalMemory",   r => r.TotalMemSort),
        ("AvgMemory",     r => r.AvgMemSort),
    ];

    // Maps a SortMemberPath tag (used in the sort dictionary) → ColumnId
    private static readonly Dictionary<string, string> SortTagToColumnId = new()
    {
        ["ExecsSort"]          = "Executions",
        ["TotalCpuSort"]       = "TotalCpu",
        ["AvgCpuSort"]         = "AvgCpu",
        ["TotalDurSort"]       = "TotalDuration",
        ["AvgDurSort"]         = "AvgDuration",
        ["TotalReadsSort"]     = "TotalReads",
        ["AvgReadsSort"]       = "AvgReads",
        ["TotalWritesSort"]    = "TotalWrites",
        ["AvgWritesSort"]      = "AvgWrites",
        ["TotalPhysReadsSort"] = "TotalPhysReads",
        ["AvgPhysReadsSort"]   = "AvgPhysReads",
        ["TotalMemSort"]       = "TotalMemory",
        ["AvgMemSort"]         = "AvgMemory",
    };

    private static void SelectComboByTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        // Unknown tag — fall back to the first item so the combo is never empty
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }


}

public class QueryStoreRow : INotifyPropertyChanged
{
    private bool _isSelected = false;

    // Bar ratios [0..1] per column
    private double _execsRatio;
    private double _totalCpuRatio;
    private double _avgCpuRatio;
    private double _totalDurRatio;
    private double _avgDurRatio;
    private double _totalReadsRatio;
    private double _avgReadsRatio;
    private double _totalWritesRatio;
    private double _avgWritesRatio;
    private double _totalPhysReadsRatio;
    private double _avgPhysReadsRatio;
    private double _totalMemRatio;
    private double _avgMemRatio;

    // IsSortedColumn flags
    private bool _isSorted_Executions;
    private bool _isSorted_TotalCpu;
    private bool _isSorted_AvgCpu;
    private bool _isSorted_TotalDuration;
    private bool _isSorted_AvgDuration;
    private bool _isSorted_TotalReads;
    private bool _isSorted_AvgReads;
    private bool _isSorted_TotalWrites;
    private bool _isSorted_AvgWrites;
    private bool _isSorted_TotalPhysReads;
    private bool _isSorted_AvgPhysReads;
    private bool _isSorted_TotalMemory;
    private bool _isSorted_AvgMemory;

    // Wait stats
    private WaitProfile? _waitProfile;
    private string? _waitHighlightCategory;

    /// <summary>Raw wait category totals for this row. Used for upward consolidation in grouped mode.</summary>
    public List<WaitCategoryTotal> RawWaitCategories { get; set; } = new();

    // Hierarchy support
    private bool _isExpanded;
    private int _indentLevel;

    /// <summary>Standard constructor for flat (ungrouped) rows.</summary>
    public QueryStoreRow(QueryStorePlan plan)
    {
        Plan = plan;
    }

    /// <summary>Constructor for grouped parent/intermediate rows (aggregated, no single plan).</summary>
    public QueryStoreRow(QueryStorePlan syntheticPlan, int indentLevel, string groupLabel, List<QueryStoreRow> children)
    {
        Plan = syntheticPlan;
        _indentLevel = indentLevel;
        GroupLabel = groupLabel;
        Children = children;
    }

    public QueryStorePlan Plan { get; }

    // ── Hierarchy properties ───────────────────────────────────────────────

    /// <summary>Indentation level: 0 = top group, 1 = intermediate, 2 = leaf.</summary>
    public int IndentLevel
    {
        get => _indentLevel;
        set { _indentLevel = value; OnPropertyChanged(); }
    }

    /// <summary>Label shown for grouped rows (e.g. "0x1A2B3C" or "dbo.MyProc").</summary>
    public string GroupLabel { get; set; } = "";

    /// <summary>Direct children of this group row.</summary>
    public List<QueryStoreRow> Children { get; set; } = new();

    public bool HasChildren => Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandChevron)); }
    }

    public string ExpandChevron => HasChildren ? (IsExpanded ? "▾" : "▸") : "";

    /// <summary>Left margin that increases with indent level to visually show hierarchy.</summary>
    public Avalonia.Thickness IndentMargin => new(IndentLevel * 20, 0, 0, 0);

    /// <summary>Text shown next to the chevron: the group label for parent rows, or QueryId/PlanId for leaves.</summary>
    public string GroupDisplayText => !string.IsNullOrEmpty(GroupLabel) ? GroupLabel : "";

    /// <summary>Bold for top-level groups, normal for children.</summary>
    public Avalonia.Media.FontWeight GroupFontWeight => IndentLevel == 0 ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    // ── Bar ratio properties ───────────────────────────────────────────────
    public double ExecsRatio         { get => _execsRatio;          private set { _execsRatio = value;          OnPropertyChanged(); } }
    public double TotalCpuRatio      { get => _totalCpuRatio;       private set { _totalCpuRatio = value;       OnPropertyChanged(); } }
    public double AvgCpuRatio        { get => _avgCpuRatio;         private set { _avgCpuRatio = value;         OnPropertyChanged(); } }
    public double TotalDurRatio      { get => _totalDurRatio;       private set { _totalDurRatio = value;       OnPropertyChanged(); } }
    public double AvgDurRatio        { get => _avgDurRatio;         private set { _avgDurRatio = value;         OnPropertyChanged(); } }
    public double TotalReadsRatio    { get => _totalReadsRatio;     private set { _totalReadsRatio = value;     OnPropertyChanged(); } }
    public double AvgReadsRatio      { get => _avgReadsRatio;       private set { _avgReadsRatio = value;       OnPropertyChanged(); } }
    public double TotalWritesRatio   { get => _totalWritesRatio;    private set { _totalWritesRatio = value;    OnPropertyChanged(); } }
    public double AvgWritesRatio     { get => _avgWritesRatio;      private set { _avgWritesRatio = value;      OnPropertyChanged(); } }
    public double TotalPhysReadsRatio{ get => _totalPhysReadsRatio; private set { _totalPhysReadsRatio = value; OnPropertyChanged(); } }
    public double AvgPhysReadsRatio  { get => _avgPhysReadsRatio;   private set { _avgPhysReadsRatio = value;   OnPropertyChanged(); } }
    public double TotalMemRatio      { get => _totalMemRatio;       private set { _totalMemRatio = value;       OnPropertyChanged(); } }
    public double AvgMemRatio        { get => _avgMemRatio;         private set { _avgMemRatio = value;         OnPropertyChanged(); } }

    // ── IsSortedColumn properties ──────────────────────────────────────────
    public bool IsSortedColumn_Executions    { get => _isSorted_Executions;    private set { _isSorted_Executions = value;    OnPropertyChanged(); } }
    public bool IsSortedColumn_TotalCpu      { get => _isSorted_TotalCpu;      private set { _isSorted_TotalCpu = value;      OnPropertyChanged(); } }
    public bool IsSortedColumn_AvgCpu        { get => _isSorted_AvgCpu;        private set { _isSorted_AvgCpu = value;        OnPropertyChanged(); } }
    public bool IsSortedColumn_TotalDuration { get => _isSorted_TotalDuration; private set { _isSorted_TotalDuration = value; OnPropertyChanged(); } }
    public bool IsSortedColumn_AvgDuration   { get => _isSorted_AvgDuration;   private set { _isSorted_AvgDuration = value;   OnPropertyChanged(); } }
    public bool IsSortedColumn_TotalReads    { get => _isSorted_TotalReads;    private set { _isSorted_TotalReads = value;    OnPropertyChanged(); } }
    public bool IsSortedColumn_AvgReads      { get => _isSorted_AvgReads;      private set { _isSorted_AvgReads = value;      OnPropertyChanged(); } }
    public bool IsSortedColumn_TotalWrites   { get => _isSorted_TotalWrites;   private set { _isSorted_TotalWrites = value;   OnPropertyChanged(); } }
    public bool IsSortedColumn_AvgWrites     { get => _isSorted_AvgWrites;     private set { _isSorted_AvgWrites = value;     OnPropertyChanged(); } }
    public bool IsSortedColumn_TotalPhysReads{ get => _isSorted_TotalPhysReads;private set { _isSorted_TotalPhysReads = value;OnPropertyChanged(); } }
    public bool IsSortedColumn_AvgPhysReads  { get => _isSorted_AvgPhysReads;  private set { _isSorted_AvgPhysReads = value;  OnPropertyChanged(); } }
    public bool IsSortedColumn_TotalMemory   { get => _isSorted_TotalMemory;   private set { _isSorted_TotalMemory = value;   OnPropertyChanged(); } }
    public bool IsSortedColumn_AvgMemory     { get => _isSorted_AvgMemory;     private set { _isSorted_AvgMemory = value;     OnPropertyChanged(); } }

    /// <summary>Called by the grid after each sort/filter pass to update bar rendering.</summary>
    public void SetBar(string columnId, double ratio, bool isSorted)
    {
        switch (columnId)
        {
            case "Executions":     ExecsRatio = ratio;          IsSortedColumn_Executions = isSorted;     break;
            case "TotalCpu":       TotalCpuRatio = ratio;       IsSortedColumn_TotalCpu = isSorted;       break;
            case "AvgCpu":         AvgCpuRatio = ratio;         IsSortedColumn_AvgCpu = isSorted;         break;
            case "TotalDuration":  TotalDurRatio = ratio;       IsSortedColumn_TotalDuration = isSorted;  break;
            case "AvgDuration":    AvgDurRatio = ratio;         IsSortedColumn_AvgDuration = isSorted;    break;
            case "TotalReads":     TotalReadsRatio = ratio;     IsSortedColumn_TotalReads = isSorted;     break;
            case "AvgReads":       AvgReadsRatio = ratio;       IsSortedColumn_AvgReads = isSorted;       break;
            case "TotalWrites":    TotalWritesRatio = ratio;    IsSortedColumn_TotalWrites = isSorted;    break;
            case "AvgWrites":      AvgWritesRatio = ratio;      IsSortedColumn_AvgWrites = isSorted;      break;
            case "TotalPhysReads": TotalPhysReadsRatio = ratio; IsSortedColumn_TotalPhysReads = isSorted; break;
            case "AvgPhysReads":   AvgPhysReadsRatio = ratio;   IsSortedColumn_AvgPhysReads = isSorted;   break;
            case "TotalMemory":    TotalMemRatio = ratio;       IsSortedColumn_TotalMemory = isSorted;    break;
            case "AvgMemory":      AvgMemRatio = ratio;         IsSortedColumn_AvgMemory = isSorted;      break;
        }
    }

    // ── Wait profile ───────────────────────────────────────────────────────

    public WaitProfile? WaitProfile
    {
        get => _waitProfile;
        set { _waitProfile = value; OnPropertyChanged(); }
    }

    public string? WaitHighlightCategory
    {
        get => _waitHighlightCategory;
        set { _waitHighlightCategory = value; OnPropertyChanged(); }
    }

    private bool _waitPercentMode;
    private double _waitMaxGrandTotal = 1.0;

    public bool WaitPercentMode
    {
        get => _waitPercentMode;
        set { _waitPercentMode = value; OnPropertyChanged(); }
    }

    public double WaitMaxGrandTotal
    {
        get => _waitMaxGrandTotal;
        set { _waitMaxGrandTotal = value; OnPropertyChanged(); }
    }

    public double WaitGrandTotalSort => _waitProfile?.GrandTotalRatio ?? 0;

    public long QueryId => Plan.QueryId;
    public long PlanId => Plan.PlanId;
    public string QueryHash => Plan.QueryHash;
    public string QueryPlanHash => Plan.QueryPlanHash;
    public string ModuleName => Plan.ModuleName;
    public string ExecutionTypeDesc => Plan.ExecutionTypeDesc;

    public string ExecsDisplay => Plan.CountExecutions.ToString("N0");
    public string TotalCpuDisplay => (Plan.TotalCpuTimeUs / 1000.0).ToString("N0");
    public string AvgCpuDisplay => (Plan.AvgCpuTimeUs / 1000.0).ToString("N1");
    public string TotalDurDisplay => (Plan.TotalDurationUs / 1000.0).ToString("N0");
    public string AvgDurDisplay => (Plan.AvgDurationUs / 1000.0).ToString("N1");
    public string TotalReadsDisplay => Plan.TotalLogicalIoReads.ToString("N0");
    public string AvgReadsDisplay => Plan.AvgLogicalIoReads.ToString("N0");
    public string TotalWritesDisplay => Plan.TotalLogicalIoWrites.ToString("N0");
    public string AvgWritesDisplay => Plan.AvgLogicalIoWrites.ToString("N0");
    public string TotalPhysReadsDisplay => Plan.TotalPhysicalIoReads.ToString("N0");
    public string AvgPhysReadsDisplay => Plan.AvgPhysicalIoReads.ToString("N0");
    public string TotalMemDisplay => (Plan.TotalMemoryGrantPages * 8.0 / 1024.0).ToString("N1");
    public string AvgMemDisplay => (Plan.AvgMemoryGrantPages * 8.0 / 1024.0).ToString("N1");

    // Numeric sort properties (DataGrid SortMemberPath targets)
    public long ExecsSort => Plan.CountExecutions;
    public long TotalCpuSort => Plan.TotalCpuTimeUs;
    public double AvgCpuSort => Plan.AvgCpuTimeUs;
    public long TotalDurSort => Plan.TotalDurationUs;
    public double AvgDurSort => Plan.AvgDurationUs;
    public long TotalReadsSort => Plan.TotalLogicalIoReads;
    public double AvgReadsSort => Plan.AvgLogicalIoReads;
    public long TotalWritesSort => Plan.TotalLogicalIoWrites;
    public double AvgWritesSort => Plan.AvgLogicalIoWrites;
    public long TotalPhysReadsSort => Plan.TotalPhysicalIoReads;
    public double AvgPhysReadsSort => Plan.AvgPhysicalIoReads;
    public long TotalMemSort => Plan.TotalMemoryGrantPages;
    public double AvgMemSort => Plan.AvgMemoryGrantPages;

    public string LastExecutedLocal => TimeDisplayHelper.FormatForDisplay(Plan.LastExecutedUtc);

    public void NotifyTimeDisplayChanged() => OnPropertyChanged(nameof(LastExecutedLocal));

    public string QueryPreview => Plan.QueryText.Length > 80
        ? Plan.QueryText[..80].Replace("\n", " ").Replace("\r", "") + "..."
        : Plan.QueryText.Replace("\n", " ").Replace("\r", "");
    public string FullQueryText => Plan.QueryText;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
