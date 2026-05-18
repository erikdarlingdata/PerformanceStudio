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

    private void ViewHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not QueryStoreRow row) return;
        if (string.IsNullOrEmpty(row.QueryHash)) return;

        var metricTag = QueryStoreHistoryWindow.MapOrderByToMetricTag(_lastFetchedOrderBy);

        var control = new QueryStoreHistoryControl(
            _connectionString,
            row.QueryHash,
            row.FullQueryText,
            _database,
            initialMetricTag: metricTag,
            slicerStartUtc: _slicerStartUtc,
            slicerEndUtc: _slicerEndUtc,
            slicerDaysBack: _slicerDaysBack);

        var shortHash = row.QueryHash.Length > 8 ? row.QueryHash[..8] + "…" : row.QueryHash;

        // Walk up the visual tree to find the parent QuerySessionControl
        var session = this.FindAncestorOfType<QuerySessionControl>();
        if (session != null)
        {
            session.AddHistorySubTab($"History: {shortHash}", control);
        }
        else
        {
            // Fallback: open as standalone window
            var window = new QueryStoreHistoryWindow(
                _connectionString,
                row.QueryHash,
                row.FullQueryText,
                _database,
                initialMetricTag: metricTag,
                slicerStartUtc: _slicerStartUtc,
                slicerEndUtc: _slicerEndUtc,
                slicerDaysBack: _slicerDaysBack);
            window.Show();
        }
    }

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
}
