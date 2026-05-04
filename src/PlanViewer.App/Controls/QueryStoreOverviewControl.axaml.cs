using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class QueryStoreOverviewControl : UserControl
{
    private readonly ServerConnection _serverConnection;
    private readonly ICredentialService _credentialService;
    private readonly string _masterConnectionString;
    private readonly int _maxDop;
    private readonly int _topN;
    private CancellationTokenSource? _cts;

    private List<DatabaseQueryStoreState> _states = new();
    private List<DatabaseMetrics> _metrics = new();
    private List<DatabaseTimeSlice> _timeSlices = new();
    private List<DatabaseWaitCategoryTimeSlice> _waitSlices = new();

    private DateTime _slicerStartUtc;
    private DateTime _slicerEndUtc;
    private int _daysBack = 30;

    // Color palette for databases (minimizes color dispersion)
    private static readonly Color[] Palette = new[]
    {
        Color.Parse("#2EAEF1"), // blue
        Color.Parse("#F2994A"), // orange
        Color.Parse("#27AE60"), // green
        Color.Parse("#9B51E0"), // purple
        Color.Parse("#EB5757"), // red
        Color.Parse("#F2C94C"), // yellow
        Color.Parse("#56CCF2"), // light blue
        Color.Parse("#BB6BD9"), // violet
    };

    private static readonly Color OthersColor = Color.Parse("#555555");

    // Donut colors
    private static readonly Color ReadWriteColor = Color.Parse("#2EAEF1");  // light blue
    private static readonly Color ReadOnlyColor = Color.Parse("#1A5276");   // dark blue
    private static readonly Color OffColor = Color.Parse("#666666");        // grey

    public event EventHandler<string>? DrillDownRequested;

    public QueryStoreOverviewControl(ServerConnection serverConnection,
        ICredentialService credentialService, int maxDop = 8, int topN = 2)
    {
        _serverConnection = serverConnection;
        _credentialService = credentialService;
        _masterConnectionString = serverConnection.GetConnectionString(credentialService, "master");
        _maxDop = maxDop;
        _topN = topN;
        _slicerEndUtc = DateTime.UtcNow;
        _slicerStartUtc = _slicerEndUtc.AddHours(-24);

        InitializeComponent();

        this.SizeChanged += (_, _) =>
        {
            DrawDonut();
            DrawSlicer();
            DrawWaitStats();
        };
    }

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Phase 1: Get states
        _states = await QueryStoreOverviewService.FetchAllStatesAsync(
            _masterConnectionString, _maxDop, ct);

        await Dispatcher.UIThread.InvokeAsync(DrawDonut);

        // Phase 2: Get time slices for active databases
        var activeDbs = _states
            .Where(s => s.State != QueryStoreState.Off)
            .Select(s => s.DatabaseName).ToList();

        if (activeDbs.Count == 0) return;

        _timeSlices = await QueryStoreOverviewService.FetchAllTimeSlicesAsync(
            _masterConnectionString, activeDbs, _daysBack, _maxDop, ct);

        await Dispatcher.UIThread.InvokeAsync(DrawSlicer);

        // Phase 3: Metrics and wait stats for selected time range
        await RefreshMetricsAndWaitStatsAsync(ct);
    }

    private async Task RefreshMetricsAndWaitStatsAsync(CancellationToken ct)
    {
        var activeDbs = _states
            .Where(s => s.State != QueryStoreState.Off)
            .Select(s => s.DatabaseName).ToList();

        if (activeDbs.Count == 0) return;

        _metrics = await QueryStoreOverviewService.FetchAllMetricsAsync(
            _masterConnectionString, activeDbs, _slicerStartUtc, _slicerEndUtc, _maxDop, ct);

        _waitSlices = await QueryStoreOverviewService.FetchAllWaitStatsAsync(
            _masterConnectionString, activeDbs, _slicerStartUtc, _slicerEndUtc, _maxDop, ct);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DrawWaitStats();
            DrawBarCards();
        });
    }

    // ── Donut Chart ──────────────────────────────────────────────────────────

    private void DrawDonut()
    {
        DonutCanvas.Children.Clear();
        if (_states.Count == 0) return;

        var w = DonutCanvas.Bounds.Width;
        var h = DonutCanvas.Bounds.Height;
        if (w < 10 || h < 10) return;

        var cx = w / 2;
        var cy = h / 2;
        var radius = Math.Min(w, h) / 2 - 10;
        var innerRadius = radius * 0.6;

        int rwCount = _states.Count(s => s.State == QueryStoreState.ReadWrite);
        int roCount = _states.Count(s => s.State == QueryStoreState.ReadOnly);
        int offCount = _states.Count(s => s.State == QueryStoreState.Off);
        int total = _states.Count;
        int activeCount = rwCount + roCount;

        var segments = new List<(double fraction, Color color)>();
        if (rwCount > 0) segments.Add(((double)rwCount / total, ReadWriteColor));
        if (roCount > 0) segments.Add(((double)roCount / total, ReadOnlyColor));
        if (offCount > 0) segments.Add(((double)offCount / total, OffColor));

        double startAngle = -Math.PI / 2;
        foreach (var (fraction, color) in segments)
        {
            var sweepAngle = fraction * 2 * Math.PI;
            var path = CreateArcPath(cx, cy, radius, innerRadius, startAngle, startAngle + sweepAngle, color);
            DonutCanvas.Children.Add(path);
            startAngle += sweepAngle;
        }

        // Center text
        var centerText = new TextBlock
        {
            Text = $"{activeCount}/{total}",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        centerText.Measure(Size.Infinity);
        Canvas.SetLeft(centerText, cx - centerText.DesiredSize.Width / 2);
        Canvas.SetTop(centerText, cy - centerText.DesiredSize.Height / 2);
        DonutCanvas.Children.Add(centerText);

        // Legend below
        var legendY = cy + radius + 5;
        DrawLegendItem(DonutCanvas, 4, legendY, ReadWriteColor, $"RW: {rwCount}");
        DrawLegendItem(DonutCanvas, 4, legendY + 16, ReadOnlyColor, $"RO: {roCount}");
        DrawLegendItem(DonutCanvas, 4, legendY + 32, OffColor, $"OFF: {offCount}");
    }

    private void DrawLegendItem(Canvas canvas, double x, double y, Color color, string text)
    {
        if (y + 14 > canvas.Bounds.Height) return;
        var rect = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(color) };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB"))
        };
        Canvas.SetLeft(tb, x + 14);
        Canvas.SetTop(tb, y - 1);
        canvas.Children.Add(tb);
    }

    private Path CreateArcPath(double cx, double cy, double outerR, double innerR,
        double startAngle, double endAngle, Color fill)
    {
        var largeArc = (endAngle - startAngle) > Math.PI;
        var outerStart = new Point(cx + outerR * Math.Cos(startAngle), cy + outerR * Math.Sin(startAngle));
        var outerEnd = new Point(cx + outerR * Math.Cos(endAngle), cy + outerR * Math.Sin(endAngle));
        var innerStart = new Point(cx + innerR * Math.Cos(endAngle), cy + innerR * Math.Sin(endAngle));
        var innerEnd = new Point(cx + innerR * Math.Cos(startAngle), cy + innerR * Math.Sin(startAngle));

        var fig = new PathFigure { StartPoint = outerStart, IsClosed = true };
        fig.Segments!.Add(new ArcSegment
        {
            Point = outerEnd,
            Size = new Size(outerR, outerR),
            IsLargeArc = largeArc,
            SweepDirection = SweepDirection.Clockwise
        });
        fig.Segments.Add(new LineSegment { Point = innerStart });
        fig.Segments.Add(new ArcSegment
        {
            Point = innerEnd,
            Size = new Size(innerR, innerR),
            IsLargeArc = largeArc,
            SweepDirection = SweepDirection.CounterClockwise
        });

        var geo = new PathGeometry();
        geo.Figures!.Add(fig);
        return new Path { Data = geo, Fill = new SolidColorBrush(fill) };
    }

    // ── Time Slicer (consolidated) ──────────────────────────────────────────

    private void DrawSlicer()
    {
        SlicerCanvas.Children.Clear();
        if (_timeSlices.Count == 0) return;

        var w = SlicerCanvas.Bounds.Width;
        var h = SlicerCanvas.Bounds.Height;
        if (w < 10 || h < 10) return;

        // Consolidate across databases by hour
        var consolidated = _timeSlices
            .GroupBy(s => s.IntervalStartUtc)
            .Select(g => new { Hour = g.Key, Total = g.Sum(x => x.TotalCpu) })
            .OrderBy(x => x.Hour)
            .ToList();

        if (consolidated.Count == 0) return;

        var maxVal = consolidated.Max(c => c.Total);
        if (maxVal <= 0) maxVal = 1;

        var barW = w / consolidated.Count;
        var chartBrush = new SolidColorBrush(Color.Parse("#332EAEF1"));
        var lineBrush = new SolidColorBrush(Color.Parse("#2EAEF1"));

        for (int i = 0; i < consolidated.Count; i++)
        {
            var barH = (consolidated[i].Total / maxVal) * (h - 20);
            var rect = new Rectangle
            {
                Width = Math.Max(1, barW - 1),
                Height = barH,
                Fill = chartBrush,
            };
            Canvas.SetLeft(rect, i * barW);
            Canvas.SetTop(rect, h - barH - 10);
            SlicerCanvas.Children.Add(rect);
        }

        // Selection overlay for the current time range
        if (consolidated.Count > 0)
        {
            var minTime = consolidated.First().Hour;
            var maxTime = consolidated.Last().Hour;
            var totalSpan = (maxTime - minTime).TotalHours;
            if (totalSpan > 0)
            {
                var startNorm = Math.Max(0, (_slicerStartUtc - minTime).TotalHours / totalSpan);
                var endNorm = Math.Min(1, (_slicerEndUtc - minTime).TotalHours / totalSpan);
                var selRect = new Rectangle
                {
                    Width = (endNorm - startNorm) * w,
                    Height = h,
                    Fill = new SolidColorBrush(Color.Parse("#442EAEF1")),
                };
                Canvas.SetLeft(selRect, startNorm * w);
                Canvas.SetTop(selRect, 0);
                SlicerCanvas.Children.Add(selRect);
            }
        }

        // Label
        var label = new TextBlock
        {
            Text = "Consolidated CPU (all DBs)",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB"))
        };
        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, 2);
        SlicerCanvas.Children.Add(label);
    }

    // ── Wait Stats (consolidated) ───────────────────────────────────────────

    private void DrawWaitStats()
    {
        WaitStatsCanvas.Children.Clear();
        if (_waitSlices.Count == 0) return;

        var w = WaitStatsCanvas.Bounds.Width;
        var h = WaitStatsCanvas.Bounds.Height;
        if (w < 10 || h < 10) return;

        // Consolidate: sum wait ratios across databases by hour+category
        var consolidated = _waitSlices
            .GroupBy(s => new { s.IntervalStartUtc, s.WaitCategoryDesc })
            .Select(g => new WaitCategoryTimeSlice
            {
                IntervalStartUtc = g.Key.IntervalStartUtc,
                WaitCategoryDesc = g.Key.WaitCategoryDesc,
                WaitCategory = g.First().WaitCategory,
                WaitRatio = g.Sum(x => x.WaitRatio)
            })
            .OrderBy(x => x.IntervalStartUtc)
            .ToList();

        // Use the existing ribbon painting logic (simplified)
        var hours = consolidated.Select(x => x.IntervalStartUtc).Distinct().OrderBy(x => x).ToList();
        var categories = consolidated.GroupBy(x => x.WaitCategoryDesc)
            .OrderByDescending(g => g.Sum(x => x.WaitRatio))
            .Select(g => g.Key).Take(5).ToList();

        if (hours.Count == 0) return;

        var maxPerHour = hours.Select(hr =>
            consolidated.Where(c => c.IntervalStartUtc == hr).Sum(c => c.WaitRatio)).Max();
        if (maxPerHour <= 0) maxPerHour = 1;

        var barW = w / hours.Count;
        var waitColors = new[] { "#EB5757", "#F2994A", "#F2C94C", "#27AE60", "#2EAEF1" };

        for (int hi = 0; hi < hours.Count; hi++)
        {
            double yOffset = 0;
            var hourData = consolidated.Where(c => c.IntervalStartUtc == hours[hi]).ToList();
            for (int ci = 0; ci < categories.Count && ci < waitColors.Length; ci++)
            {
                var val = hourData.FirstOrDefault(c => c.WaitCategoryDesc == categories[ci])?.WaitRatio ?? 0;
                var barH = (val / maxPerHour) * (h - 20);
                var rect = new Rectangle
                {
                    Width = Math.Max(1, barW - 1),
                    Height = barH,
                    Fill = new SolidColorBrush(Color.Parse(waitColors[ci]))
                };
                Canvas.SetLeft(rect, hi * barW);
                Canvas.SetTop(rect, h - 10 - yOffset - barH);
                WaitStatsCanvas.Children.Add(rect);
                yOffset += barH;
            }
        }

        var label = new TextBlock
        {
            Text = "Wait Stats (all DBs)",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB"))
        };
        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, 2);
        WaitStatsCanvas.Children.Add(label);
    }

    // ── Bar Chart Cards ─────────────────────────────────────────────────────

    private void DrawBarCards()
    {
        DrawMetricRow(TotalMetricsGrid, isTotal: true);
        DrawMetricRow(AvgMetricsGrid, isTotal: false);
    }

    private void DrawMetricRow(Grid grid, bool isTotal)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();

        var metricNames = isTotal
            ? new[] { "Total CPU", "Total Duration", "Executions", "Total Reads", "Total Writes", "Total Physical Reads", "Total Memory" }
            : new[] { "Avg CPU", "Avg Duration", "Executions", "Avg Reads", "Avg Writes", "Avg Physical Reads", "Avg Memory" };

        for (int i = 0; i < metricNames.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        // Get top N databases + others
        var ranked = GetRankedDatabases(isTotal);
        var topDbs = ranked.Take(_topN).Select(r => r.db).ToList();
        var dbColors = new Dictionary<string, Color>();
        for (int i = 0; i < topDbs.Count && i < Palette.Length; i++)
            dbColors[topDbs[i]] = Palette[i];

        for (int mi = 0; mi < metricNames.Length; mi++)
        {
            var card = CreateBarCard(metricNames[mi], mi, isTotal, topDbs, dbColors);
            Grid.SetColumn(card, mi);
            card.Margin = new Thickness(mi == 0 ? 0 : 5, 0, mi == metricNames.Length - 1 ? 0 : 5, 0);
            grid.Children.Add(card);
        }
    }

    private List<(string db, double total)> GetRankedDatabases(bool isTotal)
    {
        if (_metrics.Count == 0) return new();
        // Rank by total CPU
        return _metrics
            .Select(m => (m.DatabaseName, isTotal ? m.TotalCpu : m.AvgCpu))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    private Border CreateBarCard(string title, int metricIndex, bool isTotal,
        List<string> topDbs, Dictionary<string, Color> dbColors)
    {
        var border = new Border
        {
            Background = this.FindResource("BackgroundLightBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#22252D")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            ClipToBounds = true
        };

        var stack = new StackPanel { Spacing = 4 };

        // Title
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        });

        // Build bar data
        var bars = new List<(string db, double value, Color color)>();
        double othersValue = 0;

        foreach (var m in _metrics)
        {
            var val = GetMetricValue(m, metricIndex, isTotal);
            if (topDbs.Contains(m.DatabaseName))
                bars.Add((m.DatabaseName, val, dbColors.GetValueOrDefault(m.DatabaseName, OthersColor)));
            else
                othersValue += val;
        }

        if (othersValue > 0)
            bars.Add(("Others", othersValue, OthersColor));

        var maxVal = bars.Count > 0 ? bars.Max(b => b.value) : 1;
        if (maxVal <= 0) maxVal = 1;

        foreach (var (db, value, color) in bars.OrderByDescending(b => b.value))
        {
            var barContainer = new Border
            {
                Height = 22,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(color),
                ClipToBounds = true,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Width = double.NaN,
                Margin = new Thickness(0, 1)
            };

            // Determine font color based on luminance
            var luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
            var fontColor = luminance > 128 ? Colors.Black : Colors.White;

            var barText = new TextBlock
            {
                Text = db.Length > 15 ? db[..12] + "..." : db,
                FontSize = 10,
                Foreground = new SolidColorBrush(fontColor),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            barContainer.Child = barText;
            ToolTip.SetTip(barContainer, $"{db}: {value:N0}");

            // Context menu for drill-down
            if (db != "Others")
            {
                var menu = new ContextMenu();
                var menuItem = new MenuItem { Header = "Drill Down to DB Query Store" };
                var dbName = db;
                menuItem.Click += (_, _) => DrillDownRequested?.Invoke(this, dbName);
                menu.Items.Add(menuItem);
                barContainer.ContextMenu = menu;
            }

            // Scale width by ratio via a wrapping grid
            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(value / maxVal, GridUnitType.Star)));
            barGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - value / maxVal, GridUnitType.Star)));
            Grid.SetColumn(barContainer, 0);
            barGrid.Children.Add(barContainer);

            stack.Children.Add(barGrid);
        }

        border.Child = stack;
        return border;
    }

    private static double GetMetricValue(DatabaseMetrics m, int metricIndex, bool isTotal)
    {
        if (isTotal)
        {
            return metricIndex switch
            {
                0 => m.TotalCpu,
                1 => m.TotalDuration,
                2 => m.TotalExecutions,
                3 => m.TotalReads,
                4 => m.TotalWrites,
                5 => m.TotalPhysicalReads,
                6 => m.TotalMemory,
                _ => 0
            };
        }
        return metricIndex switch
        {
            0 => m.AvgCpu,
            1 => m.AvgDuration,
            2 => m.TotalExecutions, // Executions is the same
            3 => m.AvgReads,
            4 => m.AvgWrites,
            5 => m.AvgPhysicalReads,
            6 => m.AvgMemory,
            _ => 0
        };
    }
}
