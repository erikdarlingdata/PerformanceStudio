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
