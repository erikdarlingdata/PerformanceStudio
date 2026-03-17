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
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Data.SqlClient;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
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

    public event EventHandler<List<QueryStorePlan>>? PlansSelected;
    public event EventHandler<string>? DatabaseChanged;

    public string Database => _database;

    public QueryStoreGridControl(ServerConnection serverConnection, ICredentialService credentialService,
        string initialDatabase, List<string> databases)
    {
        _serverConnection = serverConnection;
        _credentialService = credentialService;
        _database = initialDatabase;
        _connectionString = serverConnection.GetConnectionString(credentialService, initialDatabase);
        InitializeComponent();
        ResultsGrid.ItemsSource = _filteredRows;
        EnsureFilterPopup();
        SetupColumnHeaders();
        PopulateDatabaseBox(databases, initialDatabase);
    }

    private void PopulateDatabaseBox(List<string> databases, string selectedDatabase)
    {
        QsDatabaseBox.ItemsSource = databases;
        QsDatabaseBox.SelectedItem = selectedDatabase;
    }

    private async void QsDatabase_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (QsDatabaseBox.SelectedItem is not string db || db == _database) return;

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

    private async void Fetch_Click(object? sender, RoutedEventArgs e)
    {
        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        var topN = (int)(TopNBox.Value ?? 25);
        var hoursBack = (int)(HoursBackBox.Value ?? 24);
        var orderBy = (OrderByBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cpu";
        var filter = BuildSearchFilter();

        FetchButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        StatusText.Text = "Fetching...";
        _rows.Clear();
        _filteredRows.Clear();

        try
        {
            var plans = await QueryStoreService.FetchTopPlansAsync(
                _connectionString, topN, orderBy, hoursBack, filter, ct);

            if (plans.Count == 0)
            {
                StatusText.Text = "No Query Store data found.";
                return;
            }

            foreach (var plan in plans)
                _rows.Add(new QueryStoreRow(plan));

            ApplyFilters();
            LoadButton.IsEnabled = true;
            SelectToggleButton.Content = "Select None";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message;
        }
        finally
        {
            FetchButton.IsEnabled = true;
        }
    }

    private QueryStoreFilter? BuildSearchFilter()
    {
        var searchType = (SearchTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var searchValue = SearchValueBox.Text?.Trim();

        if (string.IsNullOrEmpty(searchType) || string.IsNullOrEmpty(searchValue))
            return null;

        var filter = new QueryStoreFilter();

        switch (searchType)
        {
            case "query-id" when long.TryParse(searchValue, out var qid):
                filter.QueryId = qid;
                break;
            case "plan-id" when long.TryParse(searchValue, out var pid):
                filter.PlanId = pid;
                break;
            case "query-hash":
                filter.QueryHash = searchValue;
                break;
            case "plan-hash":
                filter.QueryPlanHash = searchValue;
                break;
            case "module":
                // Default to dbo schema if no schema specified, following sp_QuickieStore pattern
                filter.ModuleName = searchValue.Contains('.') ? searchValue : $"dbo.{searchValue}";
                break;
            default:
                return null;
        }

        return filter;
    }

    private void SearchValue_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            Fetch_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        SearchTypeBox.SelectedIndex = 0;
        SearchValueBox.Text = "";
    }

    private void SelectToggle_Click(object? sender, RoutedEventArgs e)
    {
        var allSelected = _filteredRows.Count > 0 && _filteredRows.All(r => r.IsSelected);
        foreach (var row in _filteredRows)
            row.IsSelected = !allSelected;
        SelectToggleButton.Content = allSelected ? "Select All" : "Select None";
    }

    private void LoadSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _filteredRows.Where(r => r.IsSelected).Select(r => r.Plan).ToList();
        if (selected.Count > 0)
            PlansSelected?.Invoke(this, selected);
    }

    private void LoadHighlightedPlan_Click(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is QueryStoreRow row)
            PlansSelected?.Invoke(this, new List<QueryStorePlan> { row.Plan });
    }

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

    private void SetupColumnHeaders()
    {
        var cols = ResultsGrid.Columns;
        SetColumnFilterButton(cols[1],  "QueryId",        "Query ID");
        SetColumnFilterButton(cols[2],  "PlanId",         "Plan ID");
        SetColumnFilterButton(cols[3],  "QueryHash",      "Query Hash");
        SetColumnFilterButton(cols[4],  "PlanHash",       "Plan Hash");
        SetColumnFilterButton(cols[5],  "ModuleName",     "Module");
        SetColumnFilterButton(cols[6],  "LastExecuted",   "Last Executed (Local)");
        SetColumnFilterButton(cols[7],  "Executions",     "Executions");
        SetColumnFilterButton(cols[8],  "TotalCpu",       "Total CPU (ms)");
        SetColumnFilterButton(cols[9],  "AvgCpu",         "Avg CPU (ms)");
        SetColumnFilterButton(cols[10], "TotalDuration",  "Total Duration (ms)");
        SetColumnFilterButton(cols[11], "AvgDuration",    "Avg Duration (ms)");
        SetColumnFilterButton(cols[12], "TotalReads",     "Total Reads");
        SetColumnFilterButton(cols[13], "AvgReads",       "Avg Reads");
        SetColumnFilterButton(cols[14], "TotalWrites",    "Total Writes");
        SetColumnFilterButton(cols[15], "AvgWrites",      "Avg Writes");
        SetColumnFilterButton(cols[16], "TotalPhysReads", "Total Physical Reads");
        SetColumnFilterButton(cols[17], "AvgPhysReads",   "Avg Physical Reads");
        SetColumnFilterButton(cols[18], "TotalMemory",    "Total Memory (MB)");
        SetColumnFilterButton(cols[19], "AvgMemory",      "Avg Memory (MB)");
        SetColumnFilterButton(cols[20], "QueryText",      "Query Text");
    }

    private void SetColumnFilterButton(DataGridColumn col, string columnId, string label)
    {
        var icon = new TextBlock
        {
            Text = "▽",
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        var btn = new Button
        {
            Content = icon,
            Tag = columnId,
            Width = 16,
            Height = 16,
            Padding = new Avalonia.Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        btn.Click += ColumnFilter_Click;
        ToolTip.SetTip(btn, "Click to filter");

        var text = new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(4, 0, 0, 0),
        };

        var header = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        header.Children.Add(btn);
        header.Children.Add(text);
        col.Header = header;
    }

    private void EnsureFilterPopup()
    {
        if (_filterPopup != null) return;
        _filterPopupContent = new ColumnFilterPopup();
        _filterPopup = new Popup
        {
            Child = _filterPopupContent,
            IsLightDismissEnabled = true,
            Placement = PlacementMode.Bottom,
        };
        // Add to visual tree so DynamicResources resolve inside the popup
        ((Grid)Content!).Children.Add(_filterPopup);
        _filterPopupContent.FilterApplied += OnFilterApplied;
        _filterPopupContent.FilterCleared += OnFilterCleared;
    }

    private void ColumnFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string columnId) return;
        EnsureFilterPopup();
        _activeFilters.TryGetValue(columnId, out var existing);
        _filterPopupContent!.Initialize(columnId, existing);
        _filterPopup!.PlacementTarget = button;
        _filterPopup.IsOpen = true;
    }

    private void OnFilterApplied(object? sender, FilterAppliedEventArgs e)
    {
        _filterPopup!.IsOpen = false;
        if (e.FilterState.IsActive)
            _activeFilters[e.FilterState.ColumnName] = e.FilterState;
        else
            _activeFilters.Remove(e.FilterState.ColumnName);
        ApplyFilters();
        UpdateFilterButtonStyles();
    }

    private void OnFilterCleared(object? sender, EventArgs e)
    {
        _filterPopup!.IsOpen = false;
    }

    private void UpdateFilterButtonStyles()
    {
        foreach (var col in ResultsGrid.Columns)
        {
            if (col.Header is not StackPanel sp) continue;
            var btn = sp.Children.OfType<Button>().FirstOrDefault();
            if (btn?.Tag is not string colId) continue;
            if (btn.Content is not TextBlock tb) continue;

            bool hasFilter = _activeFilters.TryGetValue(colId, out var f) && f.IsActive;
            tb.Text = hasFilter ? "▼" : "▽";
            if (hasFilter)
                tb.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            else
                tb.ClearValue(TextBlock.ForegroundProperty);

            ToolTip.SetTip(btn, hasFilter
                ? $"Filter: {f!.DisplayText} (click to modify)"
                : "Click to filter");
        }
    }

    private void ApplyFilters()
    {
        _filteredRows.Clear();
        foreach (var row in _rows)
        {
            if (RowMatchesAllFilters(row))
                _filteredRows.Add(row);
        }
        UpdateStatusText();
    }

    private bool RowMatchesAllFilters(QueryStoreRow row)
    {
        foreach (var (colId, state) in _activeFilters)
        {
            if (!state.IsActive) continue;
            if (TextAccessors.TryGetValue(colId, out var textAcc))
            {
                if (!MatchText(textAcc(row), state.Operator, state.Value)) return false;
            }
            else if (NumericAccessors.TryGetValue(colId, out var numAcc))
            {
                var isTextOp = state.Operator is FilterOperator.Contains or FilterOperator.StartsWith
                               or FilterOperator.EndsWith or FilterOperator.IsEmpty or FilterOperator.IsNotEmpty;
                if (isTextOp)
                {
                    if (!MatchText(numAcc(row).ToString("G"), state.Operator, state.Value)) return false;
                }
                else
                {
                    if (!double.TryParse(state.Value, out var numVal)) continue;
                    if (!MatchNumeric(numAcc(row), state.Operator, numVal)) return false;
                }
            }
        }
        return true;
    }

    private static bool MatchText(string data, FilterOperator op, string val) => op switch
    {
        FilterOperator.Contains   => data.Contains(val, StringComparison.OrdinalIgnoreCase),
        FilterOperator.Equals     => data.Equals(val, StringComparison.OrdinalIgnoreCase),
        FilterOperator.NotEquals  => !data.Equals(val, StringComparison.OrdinalIgnoreCase),
        FilterOperator.StartsWith => data.StartsWith(val, StringComparison.OrdinalIgnoreCase),
        FilterOperator.EndsWith   => data.EndsWith(val, StringComparison.OrdinalIgnoreCase),
        FilterOperator.IsEmpty    => string.IsNullOrEmpty(data),
        FilterOperator.IsNotEmpty => !string.IsNullOrEmpty(data),
        _                         => true,
    };

    private static bool MatchNumeric(double data, FilterOperator op, double val) => op switch
    {
        FilterOperator.Equals            => Math.Abs(data - val) < 1e-9,
        FilterOperator.NotEquals         => Math.Abs(data - val) >= 1e-9,
        FilterOperator.GreaterThan       => data > val,
        FilterOperator.GreaterThanOrEqual => data >= val,
        FilterOperator.LessThan          => data < val,
        FilterOperator.LessThanOrEqual   => data <= val,
        _                                => true,
    };

    private void UpdateStatusText()
    {
        if (_rows.Count == 0) return;
        StatusText.Text = _filteredRows.Count == _rows.Count
            ? $"{_rows.Count} plans"
            : $"{_filteredRows.Count} / {_rows.Count} plans (filtered)";
    }
}

public class QueryStoreRow : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public QueryStoreRow(QueryStorePlan plan)
    {
        Plan = plan;
    }

    public QueryStorePlan Plan { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public long QueryId => Plan.QueryId;
    public long PlanId => Plan.PlanId;
    public string QueryHash => Plan.QueryHash;
    public string QueryPlanHash => Plan.QueryPlanHash;
    public string ModuleName => Plan.ModuleName;

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

    public string LastExecutedLocal => Plan.LastExecutedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string QueryPreview => Plan.QueryText.Length > 80
        ? Plan.QueryText[..80].Replace("\n", " ").Replace("\r", "") + "..."
        : Plan.QueryText.Replace("\n", " ").Replace("\r", "");
    public string FullQueryText => Plan.QueryText;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
