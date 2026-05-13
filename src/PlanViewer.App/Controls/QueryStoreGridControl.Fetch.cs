using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class QueryStoreGridControl : UserControl
{
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
        GridEmptyMessage.IsVisible = false;
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
        GridEmptyMessage.IsVisible = false;

        if (grouped.IntermediateRows.Count == 0)
        {
            if (_groupByMode == QueryStoreGroupBy.Module)
            {
                GridEmptyMessageText.Text = "No module found in the selected period";
                GridEmptyMessage.IsVisible = true;
            }
            else
            {
                StatusText.Text = "No Query Store data found for the selected range.";
            }
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

        UpdateStatusText();
        UpdateBarRatios();

        // Fetch per-plan wait stats for leaf rows, then consolidate upward
        if (_waitStatsSupported && _waitStatsEnabled && _slicerStartUtc.HasValue && _slicerEndUtc.HasValue)
            _ = FetchGroupedWaitStatsAsync(_slicerStartUtc.Value, _slicerEndUtc.Value, ct);
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
            ExecutionTypeDesc = row.ExecutionTypeDesc,
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
            ExecutionTypeDesc = rows.FirstOrDefault()?.ExecutionTypeDesc ?? "",
        };
    }
}
