using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    private readonly List<(ScottPlot.Plottables.Scatter Scatter, string Label)> _scatters = new();

    // Hover tooltip
    private readonly Popup _tooltip;
    private readonly TextBlock _tooltipText;

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
        ["memory"]           = "TotalCpuMs", // no total memory metric in history, fallback
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
        UpdateRangeToggleButton();

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

        RefreshButton.IsEnabled = false;
        StatusText.Text = "Loading...";

        try
        {
            if (_useFullHistory)
            {
                _historyData = await QueryStoreService.FetchHistoryByHashAsync(
                    _connectionString, _queryHash, _maxHoursBack, ct);
            }
            else if (_slicerStartUtc.HasValue && _slicerEndUtc.HasValue)
            {
                _historyData = await QueryStoreService.FetchHistoryByHashAsync(
                    _connectionString, _queryHash, ct: ct,
                    startUtc: _slicerStartUtc.Value, endUtc: _slicerEndUtc.Value);
            }
            else
            {
                _historyData = await QueryStoreService.FetchHistoryByHashAsync(
                    _connectionString, _queryHash, _maxHoursBack, ct);
            }

            HistoryDataGrid.ItemsSource = _historyData;

            if (_historyData.Count > 0)
            {
                var planCount = _historyData.Select(r => r.PlanId).Distinct().Count();
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
            RefreshButton.IsEnabled = true;
        }
    }

    private void UpdateChart()
    {
        HistoryChart.Plot.Clear();
        _scatters.Clear();

        if (_historyData.Count == 0)
        {
            HistoryChart.Refresh();
            return;
        }

        var selected = MetricSelector.SelectedItem as ComboBoxItem;
        var tag = selected?.Tag?.ToString() ?? "AvgCpuMs";
        var label = selected?.Content?.ToString() ?? "Avg CPU (ms)";

        var planGroups = _historyData
            .GroupBy(r => r.PlanId)
            .OrderBy(g => g.Key)
            .ToList();

        int colorIndex = 0;
        foreach (var group in planGroups)
        {
            var ordered = group.OrderBy(r => r.IntervalStartUtc).ToList();
            var xs = ordered.Select(r => TimeDisplayHelper.ConvertForDisplay(r.IntervalStartUtc).ToOADate()).ToArray();
            var ys = ordered.Select(r => GetMetricValue(r, tag)).ToArray();

            var scatter = HistoryChart.Plot.Add.Scatter(xs, ys);
            scatter.Color = PlanColors[colorIndex % PlanColors.Length];
            scatter.LegendText = $"Plan {group.Key}";
            scatter.LineWidth = 2;
            scatter.MarkerSize = ordered.Count <= 2 ? 8 : 4;

            _scatters.Add((scatter, $"Plan {group.Key}"));
            colorIndex++;
        }

        // Add average line
        var allValues = _historyData.Select(r => GetMetricValue(r, tag)).ToArray();
        if (allValues.Length > 0)
        {
            var avg = allValues.Average();
            var hLine = HistoryChart.Plot.Add.HorizontalLine(avg);
            hLine.Color = ScottPlot.Color.FromHex("#FFD54F");
            hLine.LineWidth = 1.5f;
            hLine.LinePattern = LinePattern.Dashed;
            hLine.LegendText = $"avg: {avg:N2} {label}";
        }

        // Show legend when multiple plans exist
        HistoryChart.Plot.ShowLegend(planGroups.Count > 1 || allValues.Length > 0
            ? Alignment.UpperRight
            : Alignment.UpperRight);

        HistoryChart.Plot.Axes.DateTimeTicksBottom();
        HistoryChart.Plot.YLabel(label);
        ApplyDarkTheme();
        HistoryChart.Refresh();
    }

    private void OnChartPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_scatters.Count == 0) { _tooltip.IsOpen = false; return; }

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

            foreach (var (scatter, chartLabel) in _scatters)
            {
                var nearest = scatter.Data.GetNearest(mouseCoords, HistoryChart.Plot.LastRender);
                if (!nearest.IsReal) continue;

                var nearestPixel = HistoryChart.Plot.GetPixel(
                    new ScottPlot.Coordinates(nearest.X, nearest.Y));
                double dx = Math.Abs(nearestPixel.X - pixel.X);
                double dy = Math.Abs(nearestPixel.Y - pixel.Y);

                if (dx < 80 && dy < bestDist)
                {
                    bestDist = dy;
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
        HistoryChart.Plot.Legend.BackgroundColor = fig;
        HistoryChart.Plot.Legend.FontColor = ScottPlot.Color.FromHex("#E4E6EB");
        HistoryChart.Plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#3A3D45");
    }

    private void UpdateRangeToggleButton()
    {
        if (_useFullHistory)
        {
            RangeToggleButton.Content = "Full History";
        }
        else
        {
            RangeToggleButton.Content = "Range Period";
        }
    }

    private async void RangeToggle_Click(object? sender, RoutedEventArgs e)
    {
        _useFullHistory = !_useFullHistory;
        UpdateRangeToggleButton();
        await LoadHistoryAsync();
    }

    private void MetricSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsVisible && _historyData.Count > 0)
            UpdateChart();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        await LoadHistoryAsync();
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
