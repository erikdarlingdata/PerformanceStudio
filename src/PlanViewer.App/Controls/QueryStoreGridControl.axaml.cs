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
        _slicerDaysBack = AppSettingsService.Load().QueryStoreSlicerDays;
        InitializeComponent();
        ResultsGrid.ItemsSource = _filteredRows;
        EnsureFilterPopup();
        SetupColumnHeaders();
        PopulateDatabaseBox(databases, initialDatabase);
        TimeRangeSlicer.RangeChanged += OnTimeRangeChanged;
        TimeRangeSlicer.IsExpanded = true;

        // Auto-fetch with default settings on connect
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Fetch_Click(null, new RoutedEventArgs());
            _initialOrderByLoaded = true;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
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

    private async void Fetch_Click(object? sender, RoutedEventArgs e)
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        var orderBy = (OrderByBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cpu";
        _lastFetchedOrderBy = orderBy;

        FetchButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        StatusText.Text = "Loading time slicer...";
        _rows.Clear();
        _filteredRows.Clear();

        try
        {
            // Load slicer data first — LoadData sets a default 24h selection and
            // fires RangeChanged which triggers FetchPlansForRangeAsync.
            await LoadTimeSlicerDataAsync(orderBy, ct);
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

    private async System.Threading.Tasks.Task FetchPlansForRangeAsync()
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        var topN = (int)(TopNBox.Value ?? 25);
        var orderBy = _lastFetchedOrderBy;
        var filter = BuildSearchFilter();

        FetchButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        StatusText.Text = "Fetching plans...";
        _rows.Clear();
        _filteredRows.Clear();

        try
        {
            var plans = await QueryStoreService.FetchTopPlansAsync(
                _connectionString, topN, orderBy, ct: ct,
                startUtc: _slicerStartUtc, endUtc: _slicerEndUtc);

            if (plans.Count == 0)
            {
                StatusText.Text = "No Query Store data found for the selected range.";
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
            case "query-id":
                StatusText.Text = "Invalid Query ID";
                return null;
            case "plan-id" when long.TryParse(searchValue, out var pid):
                filter.PlanId = pid;
                break;
            case "plan-id":
                StatusText.Text = "Invalid Plan ID";
                return null;
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

    private async void OrderBy_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialOrderByLoaded) return;
        var newOrderBy = (OrderByBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cpu";
        if (newOrderBy == _lastFetchedOrderBy) return;

        _lastFetchedOrderBy = newOrderBy;

        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        // Capture the current slicer selection so it survives the reload
        var selStart = TimeRangeSlicer.SelectionStart;
        var selEnd = TimeRangeSlicer.SelectionEnd;

        FetchButton.IsEnabled = false;
        StatusText.Text = "Refreshing metric...";

        try
        {
            var sliceData = await QueryStoreService.FetchTimeSliceDataAsync(
                _connectionString, newOrderBy, _slicerDaysBack, ct);
            if (ct.IsCancellationRequested) return;

            if (sliceData.Count > 0)
            {
                // Suppress the implicit RangeChanged fetch — we will refresh the grid explicitly below
                _suppressRangeChanged = true;
                try { TimeRangeSlicer.LoadData(sliceData, newOrderBy, selStart, selEnd); }
                finally { _suppressRangeChanged = false; }

                // Explicitly refresh the grid with the new metric and current time range
                await FetchPlansForRangeAsync();
            }
            else
            {
                StatusText.Text = "No time-slicer data available.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message;
        }
        finally
        {
            FetchButton.IsEnabled = true;
        }
    }

    private void TimeDisplay_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var tag = (TimeDisplayBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (tag == null) return;
        TimeDisplayHelper.Current = tag switch
        {
            "Utc" => TimeDisplayMode.Utc,
            "Server" => TimeDisplayMode.Server,
            _ => TimeDisplayMode.Local
        };
        // Refresh grid display
        if (_filteredRows.Count > 0)
        {
            foreach (var row in _filteredRows)
                row.NotifyTimeDisplayChanged();
            ResultsGrid.ItemsSource = null;
            ResultsGrid.ItemsSource = _filteredRows;
        }
        // Refresh slicer labels
        TimeRangeSlicer.Redraw();
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        SearchTypeBox.SelectedIndex = 0;
        SearchValueBox.Text = "";
    }

    private async System.Threading.Tasks.Task LoadTimeSlicerDataAsync(string metric, CancellationToken ct)
    {
        try
        {
            var sliceData = await QueryStoreService.FetchTimeSliceDataAsync(
                _connectionString, metric, _slicerDaysBack, ct);
            if (ct.IsCancellationRequested) return;
            if (sliceData.Count > 0)
                TimeRangeSlicer.LoadData(sliceData, metric);
            else
                StatusText.Text = "No time-slicer data available.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            StatusText.Text = $"Slicer: {(ex.Message.Length > 60 ? ex.Message[..60] + "..." : ex.Message)}";
        }
    }

    private async void OnTimeRangeChanged(object? sender, TimeRangeChangedEventArgs e)
    {
        _slicerStartUtc = e.StartUtc;
        _slicerEndUtc = e.EndUtc;
        if (_suppressRangeChanged) return;
        await FetchPlansForRangeAsync();
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

    private async void ViewHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not QueryStoreRow row) return;

        var window = new QueryStoreHistoryWindow(
            _connectionString,
            row.QueryId,
            row.FullQueryText,
            _database);

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel is Window parentWindow)
            await window.ShowDialog(parentWindow);
        else
            window.Show();
    }

    // ── Context menu ────────────────────────────────────────────────────────

    private void ContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var row = ResultsGrid.SelectedItem as QueryStoreRow;
        var hasRow = row != null;

        ViewHistoryItem.IsEnabled = hasRow;
        CopyQueryIdItem.IsEnabled = hasRow;
        CopyPlanIdItem.IsEnabled = hasRow;
        CopyQueryHashItem.IsEnabled = hasRow && !string.IsNullOrEmpty(row!.QueryHash);
        CopyPlanHashItem.IsEnabled = hasRow && !string.IsNullOrEmpty(row!.QueryPlanHash);
        CopyModuleItem.IsEnabled = hasRow && !string.IsNullOrEmpty(row!.ModuleName);
        CopyQueryTextItem.IsEnabled = hasRow;
        CopyRowItem.IsEnabled = hasRow;

        // Wire click handlers (clear first to avoid stacking)
        CopyQueryIdItem.Click -= CopyMenuItem_Click;
        CopyPlanIdItem.Click -= CopyMenuItem_Click;
        CopyQueryHashItem.Click -= CopyMenuItem_Click;
        CopyPlanHashItem.Click -= CopyMenuItem_Click;
        CopyModuleItem.Click -= CopyMenuItem_Click;
        CopyQueryTextItem.Click -= CopyMenuItem_Click;
        CopyRowItem.Click -= CopyMenuItem_Click;

        if (!hasRow) return;

        CopyQueryIdItem.Tag = row!.QueryId.ToString();
        CopyPlanIdItem.Tag = row.PlanId.ToString();
        CopyQueryHashItem.Tag = row.QueryHash;
        CopyPlanHashItem.Tag = row.QueryPlanHash;
        CopyModuleItem.Tag = row.ModuleName;
        CopyQueryTextItem.Tag = row.FullQueryText;
        CopyRowItem.Tag = $"{row.QueryId}\t{row.PlanId}\t{row.QueryHash}\t{row.QueryPlanHash}\t{row.ModuleName}\t{row.LastExecutedLocal}\t{row.ExecsDisplay}\t{row.TotalCpuDisplay}\t{row.AvgCpuDisplay}\t{row.TotalDurDisplay}\t{row.AvgDurDisplay}\t{row.TotalReadsDisplay}\t{row.AvgReadsDisplay}\t{row.TotalWritesDisplay}\t{row.AvgWritesDisplay}\t{row.TotalPhysReadsDisplay}\t{row.AvgPhysReadsDisplay}\t{row.TotalMemDisplay}\t{row.AvgMemDisplay}\t{row.FullQueryText}";

        CopyQueryIdItem.Click += CopyMenuItem_Click;
        CopyPlanIdItem.Click += CopyMenuItem_Click;
        CopyQueryHashItem.Click += CopyMenuItem_Click;
        CopyPlanHashItem.Click += CopyMenuItem_Click;
        CopyModuleItem.Click += CopyMenuItem_Click;
        CopyQueryTextItem.Click += CopyMenuItem_Click;
        CopyRowItem.Click += CopyMenuItem_Click;
    }

    private async void CopyMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string text)
            await SetClipboardTextAsync(text);
    }

    private async System.Threading.Tasks.Task SetClipboardTextAsync(string text)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
            await topLevel.Clipboard.SetTextAsync(text);
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
        ApplySortAndFilters();
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
        ApplySortAndFilters();
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

    private void ResultsGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;

        var colTag = e.Column.Tag as string ?? e.Column.SortMemberPath;
        if (colTag == null) return;

        // Toggle: first click on a new column → descending; second click → ascending; third → clear
        if (_sortedColumnTag == colTag)
        {
            if (!_sortAscending)
                _sortAscending = true;   // descending → ascending
            else
            {
                // ascending → clear sort
                _sortedColumnTag = null;
                foreach (var col in ResultsGrid.Columns)
                    col.Tag = col.Tag; // no-op, just reset indicator below
                UpdateSortIndicators(null);
                ApplySortAndFilters();
                return;
            }
        }
        else
        {
            _sortedColumnTag = colTag;
            _sortAscending = false;      // first click → descending
        }

        UpdateSortIndicators(e.Column);
        ApplySortAndFilters();
    }

    private void UpdateSortIndicators(DataGridColumn? activeColumn)
    {
        foreach (var col in ResultsGrid.Columns)
        {
            if (col.Header is not StackPanel sp) continue;
            var label = sp.Children.OfType<TextBlock>().LastOrDefault();
            if (label == null) continue;

            if (col == activeColumn)
                label.Text = _sortAscending ? $"{GetColumnLabel(sp)} ▲" : $"{GetColumnLabel(sp)} ▼";
            else
                label.Text = GetColumnLabel(sp);
        }
    }

    private static string GetColumnLabel(StackPanel header)
    {
        var tb = header.Children.OfType<TextBlock>().LastOrDefault();
        if (tb == null) return string.Empty;
        // Strip any existing sort indicator
        return tb.Text?.TrimEnd(' ', '▲', '▼') ?? string.Empty;
    }

    private void ApplySortAndFilters()
    {
        IEnumerable<QueryStoreRow> source = _rows.Where(RowMatchesAllFilters);

        if (_sortedColumnTag != null)
        {
            source = _sortAscending
                ? source.OrderBy(r => GetSortKey(_sortedColumnTag, r))
                : source.OrderByDescending(r => GetSortKey(_sortedColumnTag, r));
        }

        _filteredRows.Clear();
        foreach (var row in source)
            _filteredRows.Add(row);

        UpdateStatusText();
        UpdateBarRatios();
    }

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

    private void UpdateBarRatios()
    {
        if (_filteredRows.Count == 0) return;

        var sortedColumnId = _sortedColumnTag != null &&
                             SortTagToColumnId.TryGetValue(_sortedColumnTag, out var sid) ? sid : null;

        foreach (var (columnId, accessor) in BarColumns)
        {
            var max = _filteredRows.Max(r => accessor(r));
            var isSorted = columnId == sortedColumnId;
            foreach (var row in _filteredRows)
            {
                var ratio = max > 0 ? accessor(row) / max : 0.0;
                row.SetBar(columnId, ratio, isSorted);
            }
        }
    }

    private static IComparable GetSortKey(string columnTag, QueryStoreRow r) =>
        columnTag switch
        {
            // Columns with no SortMemberPath: Avalonia uses the binding property name as key
            "QueryId"            => (IComparable)r.QueryId,
            "PlanId"             => r.PlanId,
            "QueryHash"          => r.QueryHash,
            "QueryPlanHash"      => r.QueryPlanHash,
            "ModuleName"         => r.ModuleName,
            "LastExecutedLocal"  => r.LastExecutedLocal,
            // Columns with explicit SortMemberPath
            "ExecsSort"          => r.ExecsSort,
            "TotalCpuSort"       => r.TotalCpuSort,
            "AvgCpuSort"         => r.AvgCpuSort,
            "TotalDurSort"       => r.TotalDurSort,
            "AvgDurSort"         => r.AvgDurSort,
            "TotalReadsSort"     => r.TotalReadsSort,
            "AvgReadsSort"       => r.AvgReadsSort,
            "TotalWritesSort"    => r.TotalWritesSort,
            "AvgWritesSort"      => r.AvgWritesSort,
            "TotalPhysReadsSort" => r.TotalPhysReadsSort,
            "AvgPhysReadsSort"   => r.AvgPhysReadsSort,
            "TotalMemSort"       => r.TotalMemSort,
            "AvgMemSort"         => r.AvgMemSort,
            _                    => r.LastExecutedLocal,
        };
}

public class QueryStoreRow : INotifyPropertyChanged
{
    private bool _isSelected = true;

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
