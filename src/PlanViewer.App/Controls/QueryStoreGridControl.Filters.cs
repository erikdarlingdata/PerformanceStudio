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
    private void SearchType_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SearchValuePanel is null || ExecutionTypePanel is null)
            return;

        var tag = (SearchTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var isExecutionType = tag == "execution-type";
        SearchValuePanel.IsVisible = !isExecutionType;
        ExecutionTypePanel.IsVisible = isExecutionType;
    }

    private QueryStoreFilter? BuildSearchFilter()
    {
        var searchType = (SearchTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        if (string.IsNullOrEmpty(searchType))
            return null;

        if (searchType == "execution-type")
        {
            var tag = (ExecutionTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            // "any" tag (first item) means no filter
            if (string.IsNullOrEmpty(tag) || tag == "any")
                return null;
            // "Failed" bundles Aborted + Exception into an IN predicate
            if (tag == "Failed")
                return new QueryStoreFilter { ExecutionTypeDescs = ["Aborted", "Exception"] };
            return new QueryStoreFilter { ExecutionTypeDescs = [tag] };
        }

        var searchValue = SearchValueBox.Text?.Trim();
        if (string.IsNullOrEmpty(searchValue))
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

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        SearchTypeBox.SelectedIndex = 0;
        SearchValueBox.Text = "";
        // Resetting SearchTypeBox triggers SearchType_SelectionChanged which hides ExecutionTypePanel.
        ExecutionTypeBox.SelectedIndex = 0;
    }

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
}
