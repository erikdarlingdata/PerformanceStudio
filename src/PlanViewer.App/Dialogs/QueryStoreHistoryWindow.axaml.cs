using System;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;
using ScottPlot;

namespace PlanViewer.App.Dialogs;

public partial class QueryStoreHistoryWindow : Window
{
    private readonly string _connectionString;
    private readonly string _queryHash;
    private readonly string _database;
    private readonly string _queryText;
    private readonly DateTime? _slicerStartUtc;
    private readonly DateTime? _slicerEndUtc;
    private readonly int _maxHoursBack;
    private bool _useFullHistory;
    private CancellationTokenSource? _fetchCts;
    private List<QueryStoreHistoryRow> _historyData = new();
    private readonly List<(ScottPlot.Plottables.Scatter Scatter, string Label, string PlanHash)> _scatters = new();

    // Hover tooltip
    private readonly Popup _tooltip;
    private readonly TextBlock _tooltipText;

    // Box selection state
    private bool _isDragging;
    private Point _dragStartPoint;
    private ScottPlot.Plottables.Rectangle? _selectionRect;
    private readonly HashSet<int> _selectedRowIndices = new();

    // Highlight markers for selected dots
    private readonly List<ScottPlot.Plottables.Scatter> _highlightMarkers = new();

    // Color mapping: plan hash -> color
    private readonly Dictionary<string, ScottPlot.Color> _planHashColorMap = new();

    // Legend state
    private bool _legendExpanded;
    private bool _suppressGridSelectionEvent;

    // Legend highlight: which plan hash is currently highlighted (null = none)
    private string? _highlightedPlanHash;
    private ScottPlot.Plottables.HorizontalLine? _avgLine;

    // Active button highlight brush
    private static readonly SolidColorBrush ActiveButtonBg = new(Avalonia.Media.Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly SolidColorBrush ActiveButtonFg = new(Avalonia.Media.Color.FromRgb(0x11, 0x12, 0x17));
    private static readonly SolidColorBrush InactiveButtonFg = new(Avalonia.Media.Color.FromRgb(0x9D, 0xA5, 0xB4));

    private static readonly ScottPlot.Color[] PlanColors =
    {
        ScottPlot.Color.FromHex("#4FC3F7"),
        ScottPlot.Color.FromHex("#FF7043"),
        ScottPlot.Color.FromHex("#66BB6A"),
        ScottPlot.Color.FromHex("#AB47BC"),
        ScottPlot.Color.FromHex("#FFA726"),
        ScottPlot.Color.FromHex("#26C6DA"),
        ScottPlot.Color.FromHex("#F06292"),
        ScottPlot.Color.FromHex("#A1887F"),
    };

    // Map grid orderBy tags to history metric tags
    private static readonly Dictionary<string, string> OrderByToMetricTag = new()
    {
        ["cpu"]              = "TotalCpuMs",
        ["avg-cpu"]          = "AvgCpuMs",
        ["duration"]         = "TotalDurationMs",
        ["avg-duration"]     = "AvgDurationMs",
        ["reads"]            = "TotalLogicalReads",
        ["avg-reads"]        = "AvgLogicalReads",
        ["writes"]           = "TotalLogicalWrites",
        ["avg-writes"]       = "AvgLogicalWrites",
        ["physical-reads"]   = "TotalPhysicalReads",
        ["avg-physical-reads"] = "AvgPhysicalReads",
        ["memory"]           = "TotalCpuMs",
        ["avg-memory"]       = "AvgMemoryMb",
        ["executions"]       = "CountExecutions",
    };

    public QueryStoreHistoryWindow(string connectionString, string queryHash,
        string queryText, string database,
        string initialMetricTag = "AvgCpuMs",
        DateTime? slicerStartUtc = null, DateTime? slicerEndUtc = null,
        int slicerDaysBack = 30)
    {
        _connectionString = connectionString;
        _queryHash = queryHash;
        _database = database;
        _queryText = queryText;
        _slicerStartUtc = slicerStartUtc;
        _slicerEndUtc = slicerEndUtc;
        _maxHoursBack = slicerDaysBack * 24;
        InitializeComponent();

        QueryIdentifierText.Text = $"Query Store History: {queryHash} in [{database}]";
        QueryTextBox.Text = queryText;

        // Select initial metric in the combo box
        var metricTag = initialMetricTag;
        foreach (ComboBoxItem item in MetricSelector.Items)
        {
            if (item.Tag?.ToString() == metricTag)
            {
                MetricSelector.SelectedItem = item;
                break;
            }
        }

        // Default to range period mode when slicer range is available
        _useFullHistory = !(_slicerStartUtc.HasValue && _slicerEndUtc.HasValue);
        UpdateRangeButtons();

        // Build hover tooltip
        _tooltipText = new TextBlock
        {
            Foreground = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)),
            FontSize = 13
        };
        _tooltip = new Popup
        {
            PlacementTarget = HistoryChart,
            Placement = PlacementMode.Pointer,
            IsHitTestVisible = false,
            IsLightDismissEnabled = false,
            Child = new Border
            {
                Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x33, 0x33, 0x33)),
                BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 4, 8, 4),
                Child = _tooltipText
            }
        };
        ((Grid)Content!).Children.Add(_tooltip);

        HistoryChart.PointerMoved += OnChartPointerMoved;
        HistoryChart.PointerExited += (_, _) => _tooltip.IsOpen = false;
        HistoryChart.PointerPressed += OnChartPointerPressed;
        HistoryChart.PointerReleased += OnChartPointerReleased;

        // Disable ScottPlot's built-in left-click-drag pan so our box selection works
        HistoryChart.UserInputProcessor.LeftClickDragPan(enable: false);

        Opened += async (_, _) => await LoadHistoryAsync();
    }

    /// <summary>
    /// Maps a grid orderBy tag (e.g. "cpu", "avg-duration") to the history metric tag.
    /// </summary>
    public static string MapOrderByToMetricTag(string orderBy)
    {
        return OrderByToMetricTag.TryGetValue(orderBy?.ToLowerInvariant() ?? "", out var tag)
            ? tag
            : "AvgCpuMs";
    }

    private async System.Threading.Tasks.Task LoadHistoryAsync()
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        StatusText.Text = "Loading...";

        try
        {
            if (_useFullHistory)
            {
                _historyData = await QueryStoreService.FetchAggregateHistoryAsync(
                    _connectionString, _queryHash, _maxHoursBack, ct);
            }
            else if (_slicerStartUtc.HasValue && _slicerEndUtc.HasValue)
            {
                _historyData = await QueryStoreService.FetchAggregateHistoryAsync(
                    _connectionString, _queryHash, ct: ct,
                    startUtc: _slicerStartUtc.Value, endUtc: _slicerEndUtc.Value);
            }
            else
            {
                _historyData = await QueryStoreService.FetchAggregateHistoryAsync(
                    _connectionString, _queryHash, _maxHoursBack, ct);
            }

            BuildColorMap();
            HistoryDataGrid.ItemsSource = _historyData;
            ApplyColorIndicators();

            if (_historyData.Count > 0)
            {
                var planCount = _historyData.Select(r => r.QueryPlanHash).Distinct().Count();
                var totalExec = _historyData.Sum(r => r.CountExecutions);
                var first = TimeDisplayHelper.ConvertForDisplay(_historyData.Min(r => r.IntervalStartUtc));
                var last = TimeDisplayHelper.ConvertForDisplay(_historyData.Max(r => r.IntervalStartUtc));
                StatusText.Text = $"{_historyData.Count} intervals, {planCount} plan(s), " +
                                  $"{totalExec:N0} total executions | " +
                                  $"{first:MM/dd HH:mm} to {last:MM/dd HH:mm}";
            }
            else
            {
                StatusText.Text = "No history data found for this query.";
            }

            UpdateChart();
            PopulateLegendPanel();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message;
        }
    }

    private void BuildColorMap()
    {
        _planHashColorMap.Clear();
        var hashes = _historyData.Select(r => r.QueryPlanHash).Distinct().OrderBy(h => h).ToList();
        for (int i = 0; i < hashes.Count; i++)
            _planHashColorMap[hashes[i]] = PlanColors[i % PlanColors.Length];
    }

    private void ApplyColorIndicators()
    {
        HistoryDataGrid.LoadingRow -= OnDataGridLoadingRow;
        HistoryDataGrid.LoadingRow += OnDataGridLoadingRow;
    }

    private void OnDataGridLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is QueryStoreHistoryRow row &&
            _planHashColorMap.TryGetValue(row.QueryPlanHash, out var color))
        {
            var avColor = Avalonia.Media.Color.FromRgb(color.R, color.G, color.B);
            var brush = new SolidColorBrush(avColor);
            e.Row.Tag = brush;

            // Try to apply immediately (works for recycled rows whose visual tree already exists)
            if (TryApplyColorIndicator(e.Row, brush))
                return;
        }

        // Visual tree not ready yet (first load) — defer to Loaded
        e.Row.Loaded -= OnRowLoaded;
        e.Row.Loaded += OnRowLoaded;
    }

    private void OnRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not DataGridRow dgRow) return;
        dgRow.Loaded -= OnRowLoaded;

        if (dgRow.Tag is SolidColorBrush brush)
            TryApplyColorIndicator(dgRow, brush);
    }

    private bool TryApplyColorIndicator(DataGridRow dgRow, SolidColorBrush brush)
    {
        var presenter = FindVisualChild<DataGridCellsPresenter>(dgRow);
        if (presenter == null) return false;

        var cell = presenter.Children.OfType<DataGridCell>().FirstOrDefault();
        if (cell == null) return false;

        var border = FindVisualChild<Border>(cell, "ColorIndicator");
        if (border == null) return false;

        border.Background = brush;
        return true;
    }

    private static T? FindVisualChild<T>(Avalonia.Visual parent, string? name = null) where T : Avalonia.Visual
    {
        if (parent is T t && (name == null || (t is Control c && c.Name == name)))
            return t;

        var children = parent.GetVisualChildren();
        foreach (var child in children)
        {
            if (child is Avalonia.Visual vc)
            {
                var found = FindVisualChild<T>(vc, name);
                if (found != null) return found;
            }
        }
        return null;
    }

    // ── Legend ────────────────────────────────────────────────────────────

    private void PopulateLegendPanel()
    {
        LegendItemsPanel.Children.Clear();
        foreach (var (hash, color) in _planHashColorMap.OrderBy(kv => kv.Key))
        {
            var avColor = Avalonia.Media.Color.FromRgb(color.R, color.G, color.B);
            var item = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Tag = hash,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            item.Children.Add(new Border
            {
                Width = 12, Height = 12,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(avColor),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            item.Children.Add(new TextBlock
            {
                Text = hash,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0xE0, 0xE0))
            });
            item.PointerPressed += OnLegendItemClicked;
            LegendItemsPanel.Children.Add(item);
        }
    }

    private void OnLegendItemClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not StackPanel panel || panel.Tag is not string planHash) return;

        // Toggle: click again to deselect
        if (_highlightedPlanHash == planHash)
            _highlightedPlanHash = null;
        else
            _highlightedPlanHash = planHash;

        ApplyPlanHighlight();
        UpdateLegendVisuals();
    }

    private void UpdateLegendVisuals()
    {
        foreach (var child in LegendItemsPanel.Children)
        {
            if (child is not StackPanel panel || panel.Tag is not string hash) continue;
            var isActive = _highlightedPlanHash == null || _highlightedPlanHash == hash;
            panel.Opacity = isActive ? 1.0 : 0.4;
        }
    }

    private void ApplyPlanHighlight()
    {
        var tag = (MetricSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AvgCpuMs";

        foreach (var (scatter, _, planHash) in _scatters)
        {
            if (_highlightedPlanHash == null)
            {
                // No highlight: restore normal appearance
                var color = _planHashColorMap.GetValueOrDefault(planHash, PlanColors[0]);
                scatter.Color = color.WithAlpha(140);
                scatter.LineWidth = 2;
                scatter.MarkerSize = 8;
            }
            else if (planHash == _highlightedPlanHash)
            {
                // Highlighted plan: emphasized
                var color = _planHashColorMap.GetValueOrDefault(planHash, PlanColors[0]);
                scatter.Color = color.WithAlpha(220);
                scatter.LineWidth = 4;
                scatter.MarkerSize = 10;
            }
            else
            {
                // Other plans: dimmed
                var color = _planHashColorMap.GetValueOrDefault(planHash, PlanColors[0]);
                scatter.Color = color.WithAlpha(40);
                scatter.LineWidth = 1;
                scatter.MarkerSize = 5;
            }
        }

        // Recompute average line based on highlighted plan or all data
        if (_avgLine != null)
        {
            var relevantRows = _highlightedPlanHash != null
                ? _historyData.Where(r => r.QueryPlanHash == _highlightedPlanHash).ToList()
                : _historyData;

            if (relevantRows.Count > 0)
            {
                var avg = relevantRows.Select(r => GetMetricValue(r, tag)).Average();
                _avgLine.Y = avg;
                _avgLine.Text = $"avg: {avg:N0}";
                _avgLine.IsVisible = true;
            }
            else
            {
                _avgLine.IsVisible = false;
            }
        }

        HistoryChart.Refresh();
    }

    private void LegendToggle_Click(object? sender, RoutedEventArgs e)
    {
        _legendExpanded = !_legendExpanded;
        LegendPanel.IsVisible = _legendExpanded;
        LegendArrow.Text = _legendExpanded ? "\u25b2" : "\u25bc";
    }

    // ── Chart ────────────────────────────────────────────────────────────

    private void UpdateChart()
    {
        HistoryChart.Plot.Clear();
        _scatters.Clear();
        _selectionRect = null;
        _highlightMarkers.Clear();
        _avgLine = null;
        _highlightedPlanHash = null;

        if (_historyData.Count == 0)
        {
            HistoryChart.Refresh();
            return;
        }

        var selected = MetricSelector.SelectedItem as ComboBoxItem;
        var tag = selected?.Tag?.ToString() ?? "AvgCpuMs";
        var label = selected?.Content?.ToString() ?? "Avg CPU (ms)";

        var planGroups = _historyData
            .GroupBy(r => r.QueryPlanHash)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in planGroups)
        {
            var planHash = group.Key;
            var color = _planHashColorMap.GetValueOrDefault(planHash, PlanColors[0]);

            var ordered = group.OrderBy(r => r.IntervalStartUtc).ToList();
            var xs = ordered.Select(r => TimeDisplayHelper.ConvertForDisplay(r.IntervalStartUtc).ToOADate()).ToArray();
            var ys = ordered.Select(r => GetMetricValue(r, tag)).ToArray();

            var scatter = HistoryChart.Plot.Add.Scatter(xs, ys);
            scatter.Color = color.WithAlpha(140);
            scatter.LegendText = "";
            scatter.LineWidth = 2;
            scatter.MarkerSize = 8;
            scatter.MarkerShape = MarkerShape.FilledCircle;
            scatter.MarkerLineColor = ScottPlot.Color.FromHex("#AAAAAA");
            scatter.MarkerLineWidth = 1f;

            _scatters.Add((scatter, planHash.Length > 10 ? planHash[..10] : planHash, planHash));
        }

        // Add average line with label positioned just above the line
        var allValues = _historyData.Select(r => GetMetricValue(r, tag)).ToArray();
        if (allValues.Length > 0)
        {
            var avg = allValues.Average();
            _avgLine = HistoryChart.Plot.Add.HorizontalLine(avg);
            _avgLine.Color = ScottPlot.Color.FromHex("#FFD54F").WithAlpha(150);
            _avgLine.LineWidth = 2f;
            _avgLine.LinePattern = LinePattern.DenselyDashed;
            _avgLine.Text = $"avg: {avg:N0}";
            _avgLine.LabelFontColor = ScottPlot.Color.FromHex("#9DA5B4");
            _avgLine.LabelFontSize = 11;
            _avgLine.LabelBackgroundColor = ScottPlot.Color.FromHex("#333333").WithAlpha(270);
            _avgLine.LabelOppositeAxis = false;
            _avgLine.LabelRotation = 0;
            _avgLine.LabelAlignment = Alignment.LowerLeft;
            _avgLine.LabelOffsetX = 38;
            _avgLine.LabelOffsetY = -8;
        }

        // Y-axis always includes 0 as origin
        HistoryChart.Plot.Axes.AutoScale();
        var yLimits = HistoryChart.Plot.Axes.GetLimits();
        HistoryChart.Plot.Axes.SetLimitsY(0, yLimits.Top * 1.1);

        // Disable ScottPlot's built-in legend — we use our custom overlay
        HistoryChart.Plot.HideLegend();

        // Smart X-axis labels
        ConfigureSmartXAxis();

        HistoryChart.Plot.YLabel(label);
        ApplyDarkTheme();
        HistoryChart.Refresh();
    }

    private void ConfigureSmartXAxis()
    {
        if (_historyData.Count == 0) return;

        var minTime = _historyData.Min(r => r.IntervalStartUtc);
        var maxTime = _historyData.Max(r => r.IntervalStartUtc);
        var span = maxTime - minTime;

        HistoryChart.Plot.Axes.DateTimeTicksBottom();

        if (span.TotalHours <= 48)
        {
            HistoryChart.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Color.FromHex("#9DA5B4");
            HistoryChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
            {
                LabelFormatter = dt => dt.ToString("HH:mm\nMM/dd")
            };
        }
        else if (span.TotalDays <= 14)
        {
            HistoryChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
            {
                LabelFormatter = dt => dt.ToString("HH:mm\nMM/dd")
            };
        }
        else
        {
            HistoryChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
            {
                LabelFormatter = dt => dt.ToString("MM/dd\nyyyy")
            };
        }
    }

    // ── Dot highlighting on chart ────────────────────────────────────────

    private void ClearHighlightMarkers()
    {
        foreach (var m in _highlightMarkers)
            HistoryChart.Plot.Remove(m);
        _highlightMarkers.Clear();
    }

    private void HighlightDotsOnChart(HashSet<int> rowIndices)
    {
        ClearHighlightMarkers();
        if (rowIndices.Count == 0)
        {
            HistoryChart.Refresh();
            return;
        }

        var tag = (MetricSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AvgCpuMs";

        // Group selected rows by plan hash for coloring
        var groups = rowIndices
            .Where(i => i >= 0 && i < _historyData.Count)
            .Select(i => _historyData[i])
            .GroupBy(r => r.QueryPlanHash);

        foreach (var group in groups)
        {
            var color = _planHashColorMap.GetValueOrDefault(group.Key, PlanColors[0]);
            var xs = group.Select(r => TimeDisplayHelper.ConvertForDisplay(r.IntervalStartUtc).ToOADate()).ToArray();
            var ys = group.Select(r => GetMetricValue(r, tag)).ToArray();

            // Bigger filled dot with white border for emphasis
            var highlight = HistoryChart.Plot.Add.Scatter(xs, ys);
            highlight.LineWidth = 0;
            highlight.MarkerSize = 14;
            highlight.MarkerShape = MarkerShape.FilledCircle;
            highlight.Color = color;
            highlight.MarkerLineColor = ScottPlot.Colors.White;
            highlight.MarkerLineWidth = 2.5f;

            _highlightMarkers.Add(highlight);
        }

        HistoryChart.Refresh();
    }

    // ── Box selection ────────────────────────────────────────────────────

    private void OnChartPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(HistoryChart).Properties.IsLeftButtonPressed) return;

        _isDragging = true;
        _dragStartPoint = e.GetPosition(HistoryChart);

        // Remove old selection rect
        if (_selectionRect != null)
        {
            HistoryChart.Plot.Remove(_selectionRect);
            _selectionRect = null;
        }

        e.Handled = true;
    }

    private void OnChartPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;

        // Remove the drag preview rect
        if (_selectionRect != null)
        {
            HistoryChart.Plot.Remove(_selectionRect);
            _selectionRect = null;
        }

        var endPoint = e.GetPosition(HistoryChart);
        var startCoords = PixelToCoordinates(_dragStartPoint);
        var endCoords = PixelToCoordinates(endPoint);

        // Determine if this was a click (small drag) or a box selection
        var dx = Math.Abs(endPoint.X - _dragStartPoint.X);
        var dy = Math.Abs(endPoint.Y - _dragStartPoint.Y);

        if (dx < 5 && dy < 5)
        {
            HandleSingleClickSelection(endPoint);
        }
        else
        {
            HandleBoxSelection(startCoords, endCoords);
        }

        e.Handled = true;
    }

    private ScottPlot.Coordinates PixelToCoordinates(Point pos)
    {
        var scaling = HistoryChart.Bounds.Width > 0
            ? (float)(HistoryChart.Plot.RenderManager.LastRender.FigureRect.Width / HistoryChart.Bounds.Width)
            : 1f;
        var pixel = new ScottPlot.Pixel((float)(pos.X * scaling), (float)(pos.Y * scaling));
        return HistoryChart.Plot.GetCoordinates(pixel);
    }

    private void HandleSingleClickSelection(Point clickPoint)
    {
        if (_scatters.Count == 0) return;

        var scaling = HistoryChart.Bounds.Width > 0
            ? (float)(HistoryChart.Plot.RenderManager.LastRender.FigureRect.Width / HistoryChart.Bounds.Width)
            : 1f;
        var pixel = new ScottPlot.Pixel((float)(clickPoint.X * scaling), (float)(clickPoint.Y * scaling));
        var mouseCoords = HistoryChart.Plot.GetCoordinates(pixel);

        double bestDist = double.MaxValue;
        ScottPlot.DataPoint bestPoint = default;
        string bestPlanHash = "";
        bool found = false;

        foreach (var (scatter, _, planHash) in _scatters)
        {
            var nearest = scatter.Data.GetNearest(mouseCoords, HistoryChart.Plot.LastRender);
            if (!nearest.IsReal) continue;

            var nearestPixel = HistoryChart.Plot.GetPixel(
                new ScottPlot.Coordinates(nearest.X, nearest.Y));
            var d = Math.Sqrt(Math.Pow(nearestPixel.X - pixel.X, 2) + Math.Pow(nearestPixel.Y - pixel.Y, 2));

            if (d < 30 && d < bestDist)
            {
                bestDist = d;
                bestPoint = nearest;
                bestPlanHash = planHash;
                found = true;
            }
        }

        _selectedRowIndices.Clear();

        if (found)
        {
            var clickedTime = DateTime.FromOADate(bestPoint.X);
            for (int i = 0; i < _historyData.Count; i++)
            {
                var row = _historyData[i];
                var displayTime = TimeDisplayHelper.ConvertForDisplay(row.IntervalStartUtc);
                if (row.QueryPlanHash == bestPlanHash &&
                    Math.Abs((displayTime - clickedTime).TotalMinutes) < 1)
                {
                    _selectedRowIndices.Add(i);
                }
            }
        }

        HighlightDotsOnChart(_selectedRowIndices);
        HighlightGridRows();
    }

    private void HandleBoxSelection(ScottPlot.Coordinates start, ScottPlot.Coordinates end)
    {
        var x1 = Math.Min(start.X, end.X);
        var x2 = Math.Max(start.X, end.X);
        var y1 = Math.Min(start.Y, end.Y);
        var y2 = Math.Max(start.Y, end.Y);

        // Find all data points inside the box
        var tag = (MetricSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AvgCpuMs";
        _selectedRowIndices.Clear();

        for (int i = 0; i < _historyData.Count; i++)
        {
            var row = _historyData[i];
            var xVal = TimeDisplayHelper.ConvertForDisplay(row.IntervalStartUtc).ToOADate();
            var yVal = GetMetricValue(row, tag);

            if (xVal >= x1 && xVal <= x2 && yVal >= y1 && yVal <= y2)
                _selectedRowIndices.Add(i);
        }

        HighlightDotsOnChart(_selectedRowIndices);
        HighlightGridRows();
    }

    private void HighlightGridRows()
    {
        // Scroll to first selected row if any
        if (_selectedRowIndices.Count > 0)
        {
            var firstIdx = _selectedRowIndices.Min();
            if (firstIdx < _historyData.Count)
                HistoryDataGrid.ScrollIntoView(_historyData[firstIdx], null);
        }

        HistoryDataGrid.LoadingRow -= OnHighlightLoadingRow;
        HistoryDataGrid.LoadingRow += OnHighlightLoadingRow;

        // Force grid to re-render rows
        _suppressGridSelectionEvent = true;
        var source = _historyData;
        HistoryDataGrid.ItemsSource = null;
        HistoryDataGrid.ItemsSource = source;
        _suppressGridSelectionEvent = false;
    }

    private void OnHighlightLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        var idx = e.Row.GetIndex();
        if (_selectedRowIndices.Contains(idx))
        {
            e.Row.Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 79, 195, 247));
        }
        else
        {
            e.Row.Background = Brushes.Transparent;
        }
    }

    // ── Grid row click → chart highlight ─────────────────────────────────

    private void HistoryDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressGridSelectionEvent) return;

        _selectedRowIndices.Clear();
        if (HistoryDataGrid.SelectedItems != null)
        {
            foreach (var item in HistoryDataGrid.SelectedItems)
            {
                if (item is QueryStoreHistoryRow row)
                {
                    var idx = _historyData.IndexOf(row);
                    if (idx >= 0)
                        _selectedRowIndices.Add(idx);
                }
            }
        }

        HighlightDotsOnChart(_selectedRowIndices);
    }

    // ── Hover tooltip ────────────────────────────────────────────────────

    private void OnChartPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_scatters.Count == 0) { _tooltip.IsOpen = false; return; }

        // If dragging, update selection rectangle preview
        if (_isDragging)
        {
            var currentPoint = e.GetPosition(HistoryChart);
            var startCoords = PixelToCoordinates(_dragStartPoint);
            var currentCoords = PixelToCoordinates(currentPoint);

            if (_selectionRect != null)
                HistoryChart.Plot.Remove(_selectionRect);

            var x1 = Math.Min(startCoords.X, currentCoords.X);
            var x2 = Math.Max(startCoords.X, currentCoords.X);
            var y1 = Math.Min(startCoords.Y, currentCoords.Y);
            var y2 = Math.Max(startCoords.Y, currentCoords.Y);

            _selectionRect = HistoryChart.Plot.Add.Rectangle(x1, x2, y1, y2);
            _selectionRect.FillColor = ScottPlot.Color.FromHex("#4FC3F7").WithAlpha(30);
            _selectionRect.LineColor = ScottPlot.Color.FromHex("#4FC3F7").WithAlpha(120);
            _selectionRect.LineWidth = 1;
            HistoryChart.Refresh();

            _tooltip.IsOpen = false;
            return;
        }

        try
        {
            var pos = e.GetPosition(HistoryChart);
            var scaling = HistoryChart.Bounds.Width > 0
                ? (float)(HistoryChart.Plot.RenderManager.LastRender.FigureRect.Width / HistoryChart.Bounds.Width)
                : 1f;
            var pixel = new ScottPlot.Pixel((float)(pos.X * scaling), (float)(pos.Y * scaling));
            var mouseCoords = HistoryChart.Plot.GetCoordinates(pixel);

            double bestDist = double.MaxValue;
            ScottPlot.DataPoint bestPoint = default;
            string bestLabel = "";
            bool found = false;

            foreach (var (scatter, chartLabel, _) in _scatters)
            {
                var nearest = scatter.Data.GetNearest(mouseCoords, HistoryChart.Plot.LastRender);
                if (!nearest.IsReal) continue;

                var nearestPixel = HistoryChart.Plot.GetPixel(
                    new ScottPlot.Coordinates(nearest.X, nearest.Y));
                double ddx = Math.Abs(nearestPixel.X - pixel.X);
                double ddy = Math.Abs(nearestPixel.Y - pixel.Y);

                if (ddx < 80 && ddy < bestDist)
                {
                    bestDist = ddy;
                    bestPoint = nearest;
                    bestLabel = chartLabel;
                    found = true;
                }
            }

            if (found)
            {
                var time = DateTime.FromOADate(bestPoint.X);
                var metricLabel = (MetricSelector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                _tooltipText.Text = $"{bestLabel}\n{metricLabel}: {bestPoint.Y:N2}\n{time:MM/dd HH:mm}";
                _tooltip.IsOpen = true;
            }
            else
            {
                _tooltip.IsOpen = false;
            }
        }
        catch
        {
            _tooltip.IsOpen = false;
        }
    }

    private static double GetMetricValue(QueryStoreHistoryRow row, string tag) => tag switch
    {
        "AvgCpuMs"           => row.AvgCpuMs,
        "AvgDurationMs"      => row.AvgDurationMs,
        "AvgLogicalReads"    => row.AvgLogicalReads,
        "AvgLogicalWrites"   => row.AvgLogicalWrites,
        "AvgPhysicalReads"   => row.AvgPhysicalReads,
        "AvgMemoryMb"        => row.AvgMemoryMb,
        "AvgRowcount"        => row.AvgRowcount,
        "TotalCpuMs"         => row.TotalCpuMs,
        "TotalDurationMs"    => row.TotalDurationMs,
        "TotalLogicalReads"  => row.TotalLogicalReads,
        "TotalLogicalWrites" => row.TotalLogicalWrites,
        "TotalPhysicalReads" => row.TotalPhysicalReads,
        "CountExecutions"    => row.CountExecutions,
        _                    => row.AvgCpuMs,
    };

    private void ApplyDarkTheme()
    {
        var fig = ScottPlot.Color.FromHex("#22252b");
        var data = ScottPlot.Color.FromHex("#111217");
        var text = ScottPlot.Color.FromHex("#9DA5B4");
        var grid = ScottPlot.Colors.White.WithAlpha(40);

        HistoryChart.Plot.FigureBackground.Color = fig;
        HistoryChart.Plot.DataBackground.Color = data;
        HistoryChart.Plot.Axes.Color(text);
        HistoryChart.Plot.Grid.MajorLineColor = grid;
        HistoryChart.Plot.Axes.Bottom.TickLabelStyle.ForeColor = text;
        HistoryChart.Plot.Axes.Left.TickLabelStyle.ForeColor = text;
    }

    private void UpdateRangeButtons()
    {
        if (_useFullHistory)
        {
            FullHistoryButton.Background = ActiveButtonBg;
            FullHistoryButton.Foreground = ActiveButtonFg;
            RangePeriodButton.Background = Brushes.Transparent;
            RangePeriodButton.Foreground = InactiveButtonFg;
        }
        else
        {
            RangePeriodButton.Background = ActiveButtonBg;
            RangePeriodButton.Foreground = ActiveButtonFg;
            FullHistoryButton.Background = Brushes.Transparent;
            FullHistoryButton.Foreground = InactiveButtonFg;
        }
    }

    private async void RangePeriod_Click(object? sender, RoutedEventArgs e)
    {
        if (!_useFullHistory) return;
        _useFullHistory = false;
        UpdateRangeButtons();
        await LoadHistoryAsync();
    }

    private async void FullHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_useFullHistory) return;
        _useFullHistory = true;
        UpdateRangeButtons();
        await LoadHistoryAsync();
    }

    private void MetricSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsVisible && _historyData.Count > 0)
            UpdateChart();
    }

    private async void CopyQuery_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_queryText)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(_queryText);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
