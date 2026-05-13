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
        ToggleRowExpansion(row);
    }

    private void ResultsGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual v) return;
        if (v.FindAncestorOfType<Button>() != null) return;
        if (v.FindAncestorOfType<DataGridRow>() == null) return;
        if (ResultsGrid.SelectedItem is not QueryStoreRow row) return;
        if (!row.HasChildren) return;
        ToggleRowExpansion(row);
    }

    private void ToggleRowExpansion(QueryStoreRow row)
    {
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

    private void AddExpandedChildren(QueryStoreRow parent)
    {
        foreach (var child in parent.Children)
        {
            _filteredRows.Add(child);
            if (child.IsExpanded)
                AddExpandedChildren(child);
        }
    }
}
