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
    private string? _waitHighlightCategory;
    private const int AutoSelectTopN = 1; // number of rows auto-selected after each fetch
    private bool _waitStatsSupported;  // false until version + capture mode confirmed
    private bool _waitStatsEnabled = true;
    private bool _waitPercentMode;
    private QueryStoreGroupBy _groupByMode = QueryStoreGroupBy.None;
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
        _slicerDaysBack = AppSettingsService.Load().QueryStoreSlicerDays;
        InitializeComponent();
        ResultsGrid.ItemsSource = _filteredRows;
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
            // Load slicer data, preserving the current selection if one exists.
            // Without this, LoadData defaults to last 24h and the user's range is lost.
            await LoadTimeSlicerDataAsync(orderBy, ct, _slicerStartUtc, _slicerEndUtc);
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
        GridLoadingOverlay.IsVisible = true;
        GridLoadingText.Text = "Fetching plans...";
        _rows.Clear();
        _filteredRows.Clear();
        _groupedRootRows.Clear();

        // Start global + ribbon wait stats early (they don't depend on plan results)
        if (_waitStatsSupported && _waitStatsEnabled && _slicerStartUtc.HasValue && _slicerEndUtc.HasValue)
            _ = FetchGlobalWaitStatsOnlyAsync(_slicerStartUtc.Value, _slicerEndUtc.Value, ct);

        try
        {
            if (_groupByMode == QueryStoreGroupBy.None)
            {
                await FetchFlatPlansAsync(topN, orderBy, filter, ct);
            }
            else
            {
                await FetchGroupedPlansAsync(topN, orderBy, filter, ct);
            }
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
            GridLoadingOverlay.IsVisible = false;
            FetchButton.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task FetchFlatPlansAsync(
        int topN, string orderBy, QueryStoreFilter? filter, CancellationToken ct)
    {
        var plans = await QueryStoreService.FetchTopPlansAsync(
            _connectionString, topN, orderBy, filter: filter, ct: ct,
            startUtc: _slicerStartUtc, endUtc: _slicerEndUtc);

        GridLoadingOverlay.IsVisible = false;

        if (plans.Count == 0)
        {
            StatusText.Text = "No Query Store data found for the selected range.";
            return;
        }

        foreach (var plan in plans)
            _rows.Add(new QueryStoreRow(plan));

        ApplyFilters();
        LoadButton.IsEnabled = true;
        SelectToggleButton.Content = "Select All";

        // Fetch per-plan wait stats after grid is populated (needs plan IDs)
        if (_waitStatsSupported && _waitStatsEnabled && _slicerStartUtc.HasValue && _slicerEndUtc.HasValue)
            _ = FetchPerPlanWaitStatsAsync(_slicerStartUtc.Value, _slicerEndUtc.Value, ct);
    }

    private async System.Threading.Tasks.Task FetchGroupedPlansAsync(
        int topN, string orderBy, QueryStoreFilter? filter, CancellationToken ct)
    {
        QueryStoreGroupedResult grouped;
        if (_groupByMode == QueryStoreGroupBy.QueryHash)
        {
            grouped = await QueryStoreService.FetchGroupedByQueryHashAsync(
                _connectionString, topN, orderBy, filter, ct,
                _slicerStartUtc, _slicerEndUtc);
        }
        else // Module
        {
            grouped = await QueryStoreService.FetchGroupedByModuleAsync(
                _connectionString, topN, orderBy, filter, ct,
                _slicerStartUtc, _slicerEndUtc);
        }

        GridLoadingOverlay.IsVisible = false;

        if (grouped.IntermediateRows.Count == 0)
        {
            StatusText.Text = "No Query Store data found for the selected range.";
            return;
        }

        var rootRows = BuildGroupedRows(grouped);

        // Sort root rows by consolidated metric descending
        var metricAccessor = GetMetricAccessor(orderBy);
        rootRows = rootRows.OrderByDescending(r => metricAccessor(r)).ToList();
        _groupedRootRows = rootRows;

        // Flatten to _rows (all levels) and show only top-level in _filteredRows
        foreach (var root in rootRows)
        {
            _rows.Add(root);
            foreach (var mid in root.Children)
            {
                _rows.Add(mid);
                foreach (var leaf in mid.Children)
                    _rows.Add(leaf);
            }
        }

        // Show only root-level rows initially (collapsed)
        _filteredRows.Clear();
        foreach (var root in rootRows)
            _filteredRows.Add(root);

        LoadButton.IsEnabled = true;
        SelectToggleButton.Content = "Select All";

        // Auto-expand the first root row to the deepest level
        if (rootRows.Count > 0)
        {
            var first = rootRows[0];
            ExpandRowRecursive(first);
        }

        UpdateStatusText();
        UpdateBarRatios();

        // Fetch per-plan wait stats for leaf rows, then consolidate upward
        if (_waitStatsSupported && _waitStatsEnabled && _slicerStartUtc.HasValue && _slicerEndUtc.HasValue)
            _ = FetchGroupedWaitStatsAsync(_slicerStartUtc.Value, _slicerEndUtc.Value, ct);
    }

    /// <summary>
    /// Recursively expands a row and all its children, inserting them into _filteredRows.
    /// </summary>
    private void ExpandRowRecursive(QueryStoreRow row)
    {
        if (!row.HasChildren) return;
        row.IsExpanded = true;

        var idx = _filteredRows.IndexOf(row);
        if (idx < 0) return;

        var insertAt = idx + 1;
        foreach (var child in row.Children)
        {
            _filteredRows.Insert(insertAt, child);
            insertAt++;
        }

        // Recurse into each child that has children
        foreach (var child in row.Children)
            ExpandRowRecursive(child);
    }

    /// <summary>
    /// Fetches per-plan wait stats for all real plan IDs found in the grouped hierarchy,
    /// assigns them to leaf rows, then consolidates upward to intermediate and root rows.
    /// </summary>
    private async System.Threading.Tasks.Task FetchGroupedWaitStatsAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        try
        {
            // Collect all real plan IDs from rows that have a real PlanId
            var allPlanIds = _rows
                .Where(r => r.PlanId > 0)
                .Select(r => r.PlanId)
                .Distinct()
                .ToList();

            if (allPlanIds.Count == 0) return;

            var planWaits = await QueryStoreService.FetchPlanWaitStatsAsync(
                _connectionString, startUtc, endUtc, allPlanIds, ct);
            if (ct.IsCancellationRequested) return;

            // Build lookup: plan_id → list of WaitCategoryTotal
            var byPlan = planWaits
                .GroupBy(x => x.PlanId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Wait).ToList());

            // 1. Assign raw waits + profiles to rows with a real PlanId
            foreach (var row in _rows)
            {
                if (row.PlanId > 0 && byPlan.TryGetValue(row.PlanId, out var waits))
                {
                    row.RawWaitCategories = waits;
                    row.WaitProfile = QueryStoreService.BuildWaitProfile(waits);
                }
            }

            // 2. Consolidate upward through the hierarchy
            foreach (var root in _groupedRootRows)
                ConsolidateWaitProfileUpward(root);

            UpdateWaitBarMode();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <summary>
    /// Recursively consolidates wait profiles from children into their parent.
    /// For each parent: merges all children's RawWaitCategories by summing WaitRatio
    /// per category, then builds a new WaitProfile from the merged totals.
    /// </summary>
    private static void ConsolidateWaitProfileUpward(QueryStoreRow parent)
    {
        if (parent.Children.Count == 0) return;

        // Recurse first so children are consolidated before we merge them
        foreach (var child in parent.Children)
            ConsolidateWaitProfileUpward(child);

        // Merge all children's raw wait categories by summing WaitRatio per category
        var merged = parent.Children
            .SelectMany(c => c.RawWaitCategories)
            .GroupBy(w => new { w.WaitCategory, w.WaitCategoryDesc })
            .Select(g => new WaitCategoryTotal
            {
                WaitCategory = g.Key.WaitCategory,
                WaitCategoryDesc = g.Key.WaitCategoryDesc,
                WaitRatio = g.Sum(w => w.WaitRatio),
            })
            .ToList();

        if (merged.Count > 0)
        {
            parent.RawWaitCategories = merged;
            parent.WaitProfile = QueryStoreService.BuildWaitProfile(merged);
        }
    }

    /// <summary>Maps an orderBy metric string to a Func that extracts the sort value from a QueryStoreRow.</summary>
    private static Func<QueryStoreRow, double> GetMetricAccessor(string orderBy) => orderBy.ToLowerInvariant() switch
    {
        "cpu"              => r => r.TotalCpuSort,
        "avg-cpu"          => r => r.AvgCpuSort,
        "duration"         => r => r.TotalDurSort,
        "avg-duration"     => r => r.AvgDurSort,
        "reads"            => r => r.TotalReadsSort,
        "avg-reads"        => r => r.AvgReadsSort,
        "writes"           => r => r.TotalWritesSort,
        "avg-writes"       => r => r.AvgWritesSort,
        "physical-reads"   => r => r.TotalPhysReadsSort,
        "avg-physical-reads" => r => r.AvgPhysReadsSort,
        "memory"           => r => r.TotalMemSort,
        "avg-memory"       => r => r.AvgMemSort,
        "executions"       => r => r.ExecsSort,
        _                  => r => r.TotalCpuSort,
    };

    private List<QueryStoreRow> BuildGroupedRows(QueryStoreGroupedResult grouped)
    {
        var roots = new List<QueryStoreRow>();
        var metricAccessor = GetMetricAccessor(_lastFetchedOrderBy);

        if (_groupByMode == QueryStoreGroupBy.QueryHash)
        {
            // Level 0: QueryHash groups
            var queryHashGroups = grouped.IntermediateRows
                .GroupBy(r => r.QueryHash)
                .ToList();

            foreach (var qhGroup in queryHashGroups)
            {
                var qhKey = qhGroup.Key;
                var intermediateRows = qhGroup.ToList();

                // Build level-1 children (PlanHash)
                var midChildren = new List<QueryStoreRow>();
                foreach (var mid in intermediateRows)
                {
                    // Build level-2 children (QueryId/PlanId)
                    var leafChildren = new List<QueryStoreRow>();
                    var leaves = grouped.LeafRows
                        .Where(l => l.QueryHash == mid.QueryHash && l.QueryPlanHash == mid.QueryPlanHash)
                        .ToList();
                    foreach (var leaf in leaves)
                    {
                        var leafPlan = GroupedRowToPlan(leaf);
                        leafChildren.Add(new QueryStoreRow(leafPlan, 2,
                            $"Q:{leaf.QueryId} P:{leaf.PlanId}{(leaf.IsTopRepresentative ? " ★" : "")}", new List<QueryStoreRow>()));
                    }

                    // Sort leaf children by metric descending
                    leafChildren = leafChildren.OrderByDescending(r => metricAccessor(r)).ToList();

                    var midPlan = GroupedRowToPlan(mid);
                    // Populate QueryText from the top representative leaf for this plan hash
                    var topLeafForMid = leaves.FirstOrDefault(l => l.IsTopRepresentative) ?? leaves.FirstOrDefault();
                    if (topLeafForMid != null && !string.IsNullOrEmpty(topLeafForMid.QueryText))
                        midPlan.QueryText = topLeafForMid.QueryText;
                    midChildren.Add(new QueryStoreRow(midPlan, 1, mid.QueryPlanHash, leafChildren));
                }

                // Sort mid children by metric descending
                midChildren = midChildren.OrderByDescending(r => metricAccessor(r)).ToList();

                // Aggregate metrics at QueryHash level
                var aggPlan = AggregateGroupedRows(intermediateRows, qhKey, intermediateRows.FirstOrDefault()?.ModuleName ?? "");
                // Populate QueryText from the top representative leaf across all leaves in this query hash group
                var topLeafForRoot = grouped.LeafRows
                    .Where(l => l.QueryHash == qhKey && l.IsTopRepresentative && !string.IsNullOrEmpty(l.QueryText))
                    .FirstOrDefault()
                    ?? grouped.LeafRows.FirstOrDefault(l => l.QueryHash == qhKey && !string.IsNullOrEmpty(l.QueryText));
                if (topLeafForRoot != null)
                    aggPlan.QueryText = topLeafForRoot.QueryText;
                roots.Add(new QueryStoreRow(aggPlan, 0, qhKey, midChildren));
            }
        }
        else // Module
        {
            // Level 0: Module groups
            var moduleGroups = grouped.IntermediateRows
                .GroupBy(r => r.ModuleName)
                .ToList();

            foreach (var modGroup in moduleGroups)
            {
                var modKey = modGroup.Key;
                var intermediateRows = modGroup.ToList();

                // Build level-1 children (QueryHash)
                var midChildren = new List<QueryStoreRow>();
                foreach (var mid in intermediateRows)
                {
                    // Build level-2 children (QueryId/PlanId)
                    var leafChildren = new List<QueryStoreRow>();
                    var leaves = grouped.LeafRows
                        .Where(l => l.ModuleName == mid.ModuleName && l.QueryHash == mid.QueryHash)
                        .ToList();
                    foreach (var leaf in leaves)
                    {
                        var leafPlan = GroupedRowToPlan(leaf);
                        leafChildren.Add(new QueryStoreRow(leafPlan, 2,
                            $"Q:{leaf.QueryId} P:{leaf.PlanId}{(leaf.IsTopRepresentative ? " ★" : "")}", new List<QueryStoreRow>()));
                    }

                    // Sort leaf children by metric descending
                    leafChildren = leafChildren.OrderByDescending(r => metricAccessor(r)).ToList();

                    var midPlan = GroupedRowToPlan(mid);
                    // Populate QueryText from the top representative leaf for this query hash
                    var topLeafForMid = leaves.FirstOrDefault(l => l.IsTopRepresentative) ?? leaves.FirstOrDefault();
                    if (topLeafForMid != null && !string.IsNullOrEmpty(topLeafForMid.QueryText))
                        midPlan.QueryText = topLeafForMid.QueryText;
                    midChildren.Add(new QueryStoreRow(midPlan, 1, mid.QueryHash, leafChildren));
                }

                // Sort mid children by metric descending
                midChildren = midChildren.OrderByDescending(r => metricAccessor(r)).ToList();

                // Aggregate metrics at Module level
                var aggPlan = AggregateGroupedRows(intermediateRows, "", modKey);
                // Populate QueryText from the top representative leaf across all leaves in this module group
                var topLeafForRoot = grouped.LeafRows
                    .Where(l => l.ModuleName == modKey && l.IsTopRepresentative && !string.IsNullOrEmpty(l.QueryText))
                    .FirstOrDefault()
                    ?? grouped.LeafRows.FirstOrDefault(l => l.ModuleName == modKey && !string.IsNullOrEmpty(l.QueryText));
                if (topLeafForRoot != null)
                    aggPlan.QueryText = topLeafForRoot.QueryText;
                roots.Add(new QueryStoreRow(aggPlan, 0, modKey, midChildren));
            }
        }

        return roots;
    }

    private static QueryStorePlan GroupedRowToPlan(QueryStoreGroupedPlanRow row)
    {
        var totalExecs = row.CountExecutions > 0 ? row.CountExecutions : 1;
        return new QueryStorePlan
        {
            QueryId = row.QueryId,
            PlanId = row.PlanId,
            QueryHash = row.QueryHash,
            QueryPlanHash = row.QueryPlanHash,
            ModuleName = row.ModuleName,
            QueryText = row.QueryText,
            PlanXml = row.PlanXml,
            CountExecutions = row.CountExecutions,
            TotalCpuTimeUs = row.TotalCpuTimeUs,
            TotalDurationUs = row.TotalDurationUs,
            TotalLogicalIoReads = row.TotalLogicalIoReads,
            TotalLogicalIoWrites = row.TotalLogicalIoWrites,
            TotalPhysicalIoReads = row.TotalPhysicalIoReads,
            TotalMemoryGrantPages = row.TotalMemoryGrantPages,
            AvgCpuTimeUs = (double)row.TotalCpuTimeUs / totalExecs,
            AvgDurationUs = (double)row.TotalDurationUs / totalExecs,
            AvgLogicalIoReads = (double)row.TotalLogicalIoReads / totalExecs,
            AvgLogicalIoWrites = (double)row.TotalLogicalIoWrites / totalExecs,
            AvgPhysicalIoReads = (double)row.TotalPhysicalIoReads / totalExecs,
            AvgMemoryGrantPages = (double)row.TotalMemoryGrantPages / totalExecs,
            LastExecutedUtc = row.LastExecutedUtc,
        };
    }

    private static QueryStorePlan AggregateGroupedRows(List<QueryStoreGroupedPlanRow> rows, string queryHash, string moduleName)
    {
        var totalExecs = rows.Sum(r => r.CountExecutions);
        var safeExecs = totalExecs > 0 ? totalExecs : 1;
        var totalCpu = rows.Sum(r => r.TotalCpuTimeUs);
        var totalDur = rows.Sum(r => r.TotalDurationUs);
        var totalReads = rows.Sum(r => r.TotalLogicalIoReads);
        var totalWrites = rows.Sum(r => r.TotalLogicalIoWrites);
        var totalPhysReads = rows.Sum(r => r.TotalPhysicalIoReads);
        var totalMem = rows.Sum(r => r.TotalMemoryGrantPages);
        var lastExec = rows.Max(r => r.LastExecutedUtc);

        return new QueryStorePlan
        {
            QueryHash = queryHash,
            ModuleName = moduleName,
            CountExecutions = totalExecs,
            TotalCpuTimeUs = totalCpu,
            TotalDurationUs = totalDur,
            TotalLogicalIoReads = totalReads,
            TotalLogicalIoWrites = totalWrites,
            TotalPhysicalIoReads = totalPhysReads,
            TotalMemoryGrantPages = totalMem,
            AvgCpuTimeUs = (double)totalCpu / safeExecs,
            AvgDurationUs = (double)totalDur / safeExecs,
            AvgLogicalIoReads = (double)totalReads / safeExecs,
            AvgLogicalIoWrites = (double)totalWrites / safeExecs,
            AvgPhysicalIoReads = (double)totalPhysReads / safeExecs,
            AvgMemoryGrantPages = (double)totalMem / safeExecs,
            LastExecutedUtc = lastExec,
        };
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

    private int[]? _savedColumnDisplayIndices;

    private void GroupBy_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialOrderByLoaded) return;
        var tag = (GroupByBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        var newMode = tag switch
        {
            "query-hash" => QueryStoreGroupBy.QueryHash,
            "module" => QueryStoreGroupBy.Module,
            _ => QueryStoreGroupBy.None,
        };
        if (newMode == _groupByMode) return;
        _groupByMode = newMode;

        // Show/hide the expand column (first column in the grid)
        ResultsGrid.Columns[0].IsVisible = _groupByMode != QueryStoreGroupBy.None;

        // Reorder columns: move the group key column right after expand+checkbox
        ReorderColumnsForGroupBy();

        // Re-fetch with new grouping
        Fetch_Click(null, new RoutedEventArgs());
    }

    private void ReorderColumnsForGroupBy()
    {
        var cols = ResultsGrid.Columns;

        if (_groupByMode == QueryStoreGroupBy.None)
        {
            // Restore original column order
            if (_savedColumnDisplayIndices != null)
            {
                for (int i = 0; i < cols.Count && i < _savedColumnDisplayIndices.Length; i++)
                    cols[i].DisplayIndex = _savedColumnDisplayIndices[i];
                _savedColumnDisplayIndices = null;
            }
            // Reset header colors
            ApplyGroupByHeaderColors();
            return;
        }

        // Save original order if not yet saved
        _savedColumnDisplayIndices ??= cols.Select(c => c.DisplayIndex).ToArray();

        // Column definition indices (AXAML order):
        //   0=Expand, 1=Checkbox, 2=QueryId, 3=PlanId, 4=QueryHash, 5=PlanHash, 6=Module
        if (_groupByMode == QueryStoreGroupBy.QueryHash)
        {
            // Order: Expand, Checkbox, QueryHash, PlanHash, QueryId, PlanId, ...
            cols[4].DisplayIndex = 2;  // QueryHash → 2
            cols[5].DisplayIndex = 3;  // PlanHash → 3
            cols[2].DisplayIndex = 4;  // QueryId → 4
            cols[3].DisplayIndex = 5;  // PlanId → 5
        }
        else // Module
        {
            // Order: Expand, Checkbox, Module, QueryHash, QueryId, PlanId, ...
            cols[6].DisplayIndex = 2;  // Module → 2
            cols[4].DisplayIndex = 3;  // QueryHash → 3
            cols[2].DisplayIndex = 4;  // QueryId → 4
            cols[3].DisplayIndex = 5;  // PlanId → 5
        }

        // Apply golden header colors for expandable columns
        ApplyGroupByHeaderColors();
    }

    /// <summary>
    /// Applies golden foreground to column headers that represent expandable/collapsible
    /// grouping levels in the current GroupBy mode, and resets others.
    /// </summary>
    private void ApplyGroupByHeaderColors()
    {
        // Column definition indices: 4=QueryHash, 5=PlanHash, 6=Module
        var goldenCols = _groupByMode switch
        {
            QueryStoreGroupBy.QueryHash => new HashSet<int> { 4, 5 },   // QueryHash + PlanHash
            QueryStoreGroupBy.Module    => new HashSet<int> { 6, 4 },   // Module + QueryHash
            _                           => new HashSet<int>(),
        };

        var goldenBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)); // Gold

        for (int i = 0; i < ResultsGrid.Columns.Count; i++)
        {
            var col = ResultsGrid.Columns[i];
            if (col.Header is not StackPanel sp) continue;
            var label = sp.Children.OfType<TextBlock>().LastOrDefault();
            if (label == null) continue;

            if (goldenCols.Contains(i))
                label.Foreground = goldenBrush;
            else
                label.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void ExpandRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not QueryStoreRow row) return;
        if (!row.HasChildren) return;

        row.IsExpanded = !row.IsExpanded;

        if (row.IsExpanded)
        {
            // Insert children after this row in _filteredRows
            var idx = _filteredRows.IndexOf(row);
            if (idx < 0) return;
            var insertAt = idx + 1;
            foreach (var child in row.Children)
            {
                _filteredRows.Insert(insertAt, child);
                insertAt++;
            }

            // Scroll the first child into view so the expansion is visible
            if (row.Children.Count > 0)
                ResultsGrid.ScrollIntoView(row.Children[0], null);
        }
        else
        {
            // Remove children (and their expanded children) recursively
            CollapseRowChildren(row);
        }

        UpdateStatusText();
        UpdateBarRatios();
    }

    private void CollapseRowChildren(QueryStoreRow parent)
    {
        foreach (var child in parent.Children)
        {
            if (child.IsExpanded)
            {
                child.IsExpanded = false;
                CollapseRowChildren(child);
            }
            _filteredRows.Remove(child);
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

    private async System.Threading.Tasks.Task LoadTimeSlicerDataAsync(
        string metric, CancellationToken ct,
        DateTime? preserveStart = null, DateTime? preserveEnd = null)
    {
        try
        {
            var sliceData = await QueryStoreService.FetchTimeSliceDataAsync(
                _connectionString, metric, _slicerDaysBack, ct);
            if (ct.IsCancellationRequested) return;
            if (sliceData.Count > 0)
                TimeRangeSlicer.LoadData(sliceData, metric, preserveStart, preserveEnd);
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

    // ── Wait stats ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches global bar + ribbon wait stats (independent of grid plan IDs).
    /// Shows loading indicator on the wait stats panel.
    /// </summary>
    private async System.Threading.Tasks.Task FetchGlobalWaitStatsOnlyAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        WaitStatsProfile.SetLoading(true);
        try
        {
            // Global (bar)
            var globalWaits = await QueryStoreService.FetchGlobalWaitStatsAsync(
                _connectionString, startUtc, endUtc, ct);
            if (ct.IsCancellationRequested) { return; }
            var globalProfile = QueryStoreService.BuildWaitProfile(globalWaits);
            WaitStatsProfile.SetBarProfile(globalProfile);

            // Global (ribbon) — fetched lazily, data ready for toggle
            var ribbonData = await QueryStoreService.FetchGlobalWaitStatsRibbonAsync(
                _connectionString, startUtc, endUtc, ct);
            if (ct.IsCancellationRequested) { return; }
            WaitStatsProfile.SetRibbonData(ribbonData);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            WaitStatsProfile.SetLoading(false);
        }
    }

    /// <summary>
    /// Fetches per-plan wait stats for the plan IDs currently in the grid.
    /// </summary>
    private async System.Threading.Tasks.Task FetchPerPlanWaitStatsAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        try
        {
            var visiblePlanIds = _rows.Select(r => r.PlanId).ToList();
            var planWaits = await QueryStoreService.FetchPlanWaitStatsAsync(
                _connectionString, startUtc, endUtc, visiblePlanIds, ct);
            if (ct.IsCancellationRequested) { return; }

            var byPlan = planWaits
                .GroupBy(x => x.PlanId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Wait).ToList());

            foreach (var row in _rows)
            {
                if (byPlan.TryGetValue(row.PlanId, out var waits))
                    row.WaitProfile = QueryStoreService.BuildWaitProfile(waits);
                else
                    row.WaitProfile = null;
            }
            UpdateWaitBarMode();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <summary>
    /// Full wait stats fetch (global + ribbon + per-plan). Used when re-expanding the wait stats panel.
    /// </summary>
    private async System.Threading.Tasks.Task FetchWaitStatsAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        await FetchGlobalWaitStatsOnlyAsync(startUtc, endUtc, ct);
        if (_groupByMode != QueryStoreGroupBy.None)
            await FetchGroupedWaitStatsAsync(startUtc, endUtc, ct);
        else
            await FetchPerPlanWaitStatsAsync(startUtc, endUtc, ct);
    }

    private void OnWaitCategoryClicked(object? sender, string category)
    {
        // Toggle highlight: click same category again → clear
        if (_waitHighlightCategory == category)
            _waitHighlightCategory = null;
        else
            _waitHighlightCategory = category;

        ApplyWaitHighlight();
    }

    private void OnWaitCategoryDoubleClicked(object? sender, string category)
    {
        _waitHighlightCategory = category;
        ApplyWaitHighlight();

        // Sort grid by this category's wait ratio (descending)
        var sorted = _filteredRows
            .OrderByDescending(r =>
                r.WaitProfile?.Segments
                    .Where(s => s.Category == category)
                    .Sum(s => s.WaitRatio) ?? 0)
            .ToList();

        _filteredRows.Clear();
        foreach (var row in sorted)
            _filteredRows.Add(row);

        // Clear column sort indicators since we're using custom sort
        _sortedColumnTag = null;
        UpdateSortIndicators(null);
        ReapplyTopNSelection();
        UpdateBarRatios();
    }

    private void ApplyWaitHighlight()
    {
        WaitStatsProfile.SetHighlight(_waitHighlightCategory);
        foreach (var row in _rows)
            row.WaitHighlightCategory = _waitHighlightCategory;
    }

    private void OnWaitStatsCollapsedChanged(object? sender, bool collapsed)
    {
        _waitStatsEnabled = !collapsed;

        var waitProfileCol = ResultsGrid.Columns
            .FirstOrDefault(c => c.SortMemberPath == "WaitGrandTotalSort");
        if (waitProfileCol != null)
            waitProfileCol.IsVisible = !collapsed;

        if (!collapsed && _waitStatsSupported && _slicerStartUtc.HasValue && _slicerEndUtc.HasValue)
        {
            // Re-fetch wait stats when expanding — reuse the shared CTS
            var ct = _fetchCts?.Token ?? CancellationToken.None;
            _ = FetchWaitStatsAsync(_slicerStartUtc.Value, _slicerEndUtc.Value, ct);
        }
    }

    private void WaitStatsChevron_Click(object? sender, RoutedEventArgs e)
    {
        if (WaitStatsProfile.IsCollapsed)
        {
            WaitStatsProfile.Expand();
            WaitStatsChevronButton.Content = "»";
            SlicerRow.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
            SlicerRow.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            WaitStatsProfile.Collapse();
            WaitStatsChevronButton.Content = "«";
            SlicerRow.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SlicerRow.ColumnDefinitions[2].Width = new GridLength(0);
        }
    }

    private void WaitModeToggle_Click(object? sender, RoutedEventArgs e)
    {
        _waitPercentMode = !_waitPercentMode;
        if (sender is Button btn)
            btn.Content = _waitPercentMode ? "%" : "v";
        UpdateWaitBarMode();
    }

    private void UpdateWaitBarMode()
    {
        var maxGrand = _filteredRows.Count > 0
            ? _filteredRows.Max(r => r.WaitProfile?.GrandTotalRatio ?? 0)
            : 1.0;
        if (maxGrand <= 0) maxGrand = 1.0;
        foreach (var row in _filteredRows)
        {
            row.WaitPercentMode = _waitPercentMode;
            row.WaitMaxGrandTotal = maxGrand;
        }
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
        List<QueryStorePlan> selected;
        if (_groupByMode != QueryStoreGroupBy.None)
        {
            // In grouped mode, expand selected grouped rows to their leaf plans
            selected = _filteredRows
                .Where(r => r.IsSelected)
                .SelectMany(r => r.HasChildren ? CollectLeafPlans(r) : (r.PlanId > 0 && r.QueryId > 0 ? [r.Plan] : []))
                .ToList();
        }
        else
        {
            selected = _filteredRows.Where(r => r.IsSelected).Select(r => r.Plan).ToList();
        }
        if (selected.Count > 0)
            PlansSelected?.Invoke(this, selected);
    }

    private void LoadHighlightedPlan_Click(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not QueryStoreRow row) return;

        // In grouped mode, load all descendant leaf plans with real IDs
        if (_groupByMode != QueryStoreGroupBy.None && row.HasChildren)
        {
            var leafPlans = CollectLeafPlans(row);
            if (leafPlans.Count > 0)
                PlansSelected?.Invoke(this, leafPlans);
        }
        else if (row.PlanId > 0 && row.QueryId > 0)
        {
            PlansSelected?.Invoke(this, new List<QueryStorePlan> { row.Plan });
        }
    }

    /// <summary>
    /// Recursively collects all leaf-level plans (PlanId > 0 and QueryId > 0) from a grouped row and its descendants.
    /// </summary>
    private static List<QueryStorePlan> CollectLeafPlans(QueryStoreRow row)
    {
        var plans = new List<QueryStorePlan>();
        if (row.Children.Count == 0)
        {
            if (row.PlanId > 0 && row.QueryId > 0)
                plans.Add(row.Plan);
        }
        else
        {
            foreach (var child in row.Children)
                plans.AddRange(CollectLeafPlans(child));
        }
        return plans;
    }

    private async void ViewHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not QueryStoreRow row) return;
        if (string.IsNullOrEmpty(row.QueryHash)) return;

        var metricTag = QueryStoreHistoryWindow.MapOrderByToMetricTag(_lastFetchedOrderBy);

        var window = new QueryStoreHistoryWindow(
            _connectionString,
            row.QueryHash,
            row.FullQueryText,
            _database,
            initialMetricTag: metricTag,
            slicerStartUtc: _slicerStartUtc,
            slicerEndUtc: _slicerEndUtc,
            slicerDaysBack: _slicerDaysBack);

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
        // cols[0] = Expand column, cols[1] = Checkbox
        SetColumnFilterButton(cols[2],  "QueryId",        "Query ID");
        SetColumnFilterButton(cols[3],  "PlanId",         "Plan ID");
        SetColumnFilterButton(cols[4],  "QueryHash",      "Query Hash");
        SetColumnFilterButton(cols[5],  "PlanHash",       "Plan Hash");
        SetColumnFilterButton(cols[6],  "ModuleName",     "Module");
        // cols[7] = WaitProfile (no filter button)
        SetColumnFilterButton(cols[8],  "LastExecuted",   "Last Executed (Local)");
        SetColumnFilterButton(cols[9],  "Executions",     "Executions");
        SetColumnFilterButton(cols[10], "TotalCpu",       "Total CPU (ms)");
        SetColumnFilterButton(cols[11], "AvgCpu",         "Avg CPU (ms)");
        SetColumnFilterButton(cols[12], "TotalDuration",  "Total Duration (ms)");
        SetColumnFilterButton(cols[13], "AvgDuration",    "Avg Duration (ms)");
        SetColumnFilterButton(cols[14], "TotalReads",     "Total Reads");
        SetColumnFilterButton(cols[15], "AvgReads",       "Avg Reads");
        SetColumnFilterButton(cols[16], "TotalWrites",    "Total Writes");
        SetColumnFilterButton(cols[17], "AvgWrites",      "Avg Writes");
        SetColumnFilterButton(cols[18], "TotalPhysReads", "Total Physical Reads");
        SetColumnFilterButton(cols[19], "AvgPhysReads",   "Avg Physical Reads");
        SetColumnFilterButton(cols[20], "TotalMemory",    "Total Memory (MB)");
        SetColumnFilterButton(cols[21], "AvgMemory",      "Avg Memory (MB)");
        SetColumnFilterButton(cols[22], "QueryText",      "Query Text");
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
        if (_groupByMode != QueryStoreGroupBy.None)
        {
            var rootCount = _groupedRootRows.Count;
            var visibleRoots = _filteredRows.Count(r => r.IndentLevel == 0);
            StatusText.Text = visibleRoots == rootCount
                ? $"{rootCount} groups ({_rows.Count} total rows)"
                : $"{visibleRoots} / {rootCount} groups (filtered)";
        }
        else
        {
            StatusText.Text = _filteredRows.Count == _rows.Count
                ? $"{_rows.Count} plans"
                : $"{_filteredRows.Count} / {_rows.Count} plans (filtered)";
        }
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

    private void ReapplyTopNSelection()
    {
        if (_filteredRows.Count == 0) return;
        foreach (var r in _rows) r.IsSelected = false;
        foreach (var r in _filteredRows.Take(AutoSelectTopN)) r.IsSelected = true;
    }

    private void ApplySortAndFilters()
    {
        if (_groupByMode != QueryStoreGroupBy.None)
        {
            ApplySortAndFiltersGrouped();
            return;
        }

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

        ReapplyTopNSelection();
        UpdateStatusText();
        UpdateBarRatios();
    }

    private void ApplySortAndFiltersGrouped()
    {
        // In grouped mode, sort/filter only root rows and rebuild the visible list
        IEnumerable<QueryStoreRow> source = _groupedRootRows.Where(RowMatchesAllFilters);

        if (_sortedColumnTag != null)
        {
            source = _sortAscending
                ? source.OrderBy(r => GetSortKey(_sortedColumnTag, r))
                : source.OrderByDescending(r => GetSortKey(_sortedColumnTag, r));
        }

        _filteredRows.Clear();
        foreach (var root in source)
        {
            _filteredRows.Add(root);
            if (root.IsExpanded)
                AddExpandedChildren(root);
        }

        UpdateStatusText();
        UpdateBarRatios();
    }

    private void AddExpandedChildren(QueryStoreRow parent)
    {
        foreach (var child in parent.Children)
        {
            _filteredRows.Add(child);
            if (child.IsExpanded)
                AddExpandedChildren(child);
        }
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

        UpdateWaitBarMode();
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
            "WaitGrandTotalSort" => r.WaitGrandTotalSort,
            _                    => r.LastExecutedLocal,
        };
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
