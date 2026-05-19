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
}
