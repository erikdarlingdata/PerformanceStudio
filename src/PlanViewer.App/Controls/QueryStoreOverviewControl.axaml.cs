using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly bool _supportsWaitStats;
    private CancellationTokenSource? _cts;

    private List<DatabaseQueryStoreState> _states = new();
    private List<DatabaseMetrics> _metrics = new();
    private List<DatabaseTimeSlice> _timeSlices = new();
    private List<DatabaseWaitAmountTimeSlice> _waitSlices = new();
    private List<string> _activeDbs = new();

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

    public class DrillDownEventArgs(string database, DateTime startUtc, DateTime endUtc) : EventArgs
    {
        public string Database { get; } = database;
        public DateTime StartUtc { get; } = startUtc;
        public DateTime EndUtc { get; } = endUtc;
    }

    public event EventHandler<DrillDownEventArgs>? DrillDownRequested;

    public QueryStoreOverviewControl(ServerConnection serverConnection,
        ICredentialService credentialService, int maxDop = 8, int topN = 4, bool supportsWaitStats = true)
    {
        _serverConnection = serverConnection;
        _credentialService = credentialService;
        _masterConnectionString = serverConnection.GetConnectionString(credentialService, "master");
        _maxDop = maxDop;
        _topN = topN;
        _supportsWaitStats = supportsWaitStats;
        _slicerEndUtc = DateTime.UtcNow;
        _slicerStartUtc = _slicerEndUtc.AddHours(-24);

        InitializeComponent();

        this.SizeChanged += (_, _) =>
        {
            DrawDonut();
            DrawWaitStatsChart();
        };

        OverviewTimeSlicer.RangeChanged += OnSlicerRangeChanged;
    }

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        await Dispatcher.UIThread.InvokeAsync(() => LoadingBar.IsIndeterminate = true);

        try
        {
            // Phase 1: Get states
            _states = await QueryStoreOverviewService.FetchAllStatesAsync(
                _masterConnectionString, _maxDop, ct);

            await Dispatcher.UIThread.InvokeAsync(DrawDonut);

            // Phase 2: Get time slices for active databases (cache the list)
            _activeDbs = _states
                .Where(s => s.State != QueryStoreState.Off)
                .Select(s => s.DatabaseName).ToList();

            if (_activeDbs.Count == 0) return;

            _timeSlices = await QueryStoreOverviewService.FetchAllTimeSlicesAsync(
                _masterConnectionString, _activeDbs, _daysBack, _maxDop, ct);

            // Consolidate time slices across databases into QueryStoreTimeSlice for the slicer
            var consolidated = _timeSlices
                .GroupBy(s => s.IntervalStartUtc)
                .Select(g => new QueryStoreTimeSlice
                {
                    IntervalStartUtc = g.Key,
                    TotalCpu = g.Sum(x => x.TotalCpu),
                    TotalDuration = g.Sum(x => x.TotalDuration),
                    TotalReads = g.Sum(x => x.TotalReads),
                    TotalWrites = g.Sum(x => x.TotalWrites),
                    TotalPhysicalReads = g.Sum(x => x.TotalPhysicalReads),
                    TotalMemory = g.Sum(x => x.TotalMemory),
                    TotalExecutions = g.Sum(x => x.TotalExecutions),
                })
                .OrderBy(x => x.IntervalStartUtc)
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
                OverviewTimeSlicer.LoadData(consolidated, "cpu"));

            // Phase 3: Metrics and wait stats for selected time range
            await RefreshMetricsAndWaitStatsAsync(ct);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadingBar.IsIndeterminate = false);
        }
    }

    private async Task RefreshMetricsAndWaitStatsAsync(CancellationToken ct)
    {
        if (_activeDbs.Count == 0) return;

        await Dispatcher.UIThread.InvokeAsync(() => LoadingBar.IsIndeterminate = true);
        try
        {
            _metrics = await QueryStoreOverviewService.FetchAllMetricsAsync(
                _masterConnectionString, _activeDbs, _slicerStartUtc, _slicerEndUtc, _maxDop, ct);

            if (_supportsWaitStats)
            {
                var (slices, errors) = await QueryStoreOverviewService.FetchAllWaitStatsWithErrorsAsync(
                    _masterConnectionString, _activeDbs, _slicerStartUtc, _slicerEndUtc, _maxDop, ct);
                _waitSlices = slices;

                if (errors.Count > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        ShowWaitStatsErrors(errors));
                }
            }
            else
            {
                _waitSlices.Clear();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DrawBarCards();
                DrawWaitStatsChart();
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadingBar.IsIndeterminate = false);
        }
    }

    private void ShowWaitStatsErrors(List<(string Database, string Error)> errors)
    {
        var msg = string.Join("\n", errors.Select(e => $"[{e.Database}] {e.Error}"));
        var window = new Avalonia.Controls.Window
        {
            Title = "Wait Stats Errors",
            Width = 600,
            Height = 300,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = msg,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(10),
                    Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
                    FontSize = 12,
                }
            }
        };
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Avalonia.Controls.Window owner)
            window.ShowDialog(owner);
        else
            window.Show();
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

        var segmentInfos = new List<(int count, string label, QueryStoreState state, Color color)>();
        if (rwCount > 0) segmentInfos.Add((rwCount, "Read Write", QueryStoreState.ReadWrite, ReadWriteColor));
        if (roCount > 0) segmentInfos.Add((roCount, "Read Only", QueryStoreState.ReadOnly, ReadOnlyColor));
        if (offCount > 0) segmentInfos.Add((offCount, "OFF", QueryStoreState.Off, OffColor));

        double startAngle = -Math.PI / 2;
        foreach (var (count, label, state, color) in segmentInfos)
        {
            var fraction = (double)count / total;
            var sweepAngle = fraction * 2 * Math.PI;
            var path = CreateArcPath(cx, cy, radius, innerRadius, startAngle, startAngle + sweepAngle, color);

            // Tooltip with details
            var pct = fraction * 100;
            var tipText = $"{label}: {count} database{(count != 1 ? "s" : "")} ({pct:F0}%)";
            ToolTip.SetTip(path, tipText);
            ToolTip.SetShowDelay(path, 200);

            // Click → popup with database list
            var capturedState = state;
            path.Cursor = new Cursor(StandardCursorType.Hand);
            path.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ShowDonutPopup(capturedState);
            };

            DonutCanvas.Children.Add(path);

            // Percentage label on the arc midpoint
            if (fraction > 0.05) // only show label if segment is large enough
            {
                var midAngle = startAngle + sweepAngle / 2;
                var labelR = (radius + innerRadius) / 2;
                var lx = cx + labelR * Math.Cos(midAngle);
                var ly = cy + labelR * Math.Sin(midAngle);
                var pctLabel = new TextBlock
                {
                    Text = $"{pct:F0}%",
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                };
                pctLabel.Measure(Size.Infinity);
                Canvas.SetLeft(pctLabel, lx - pctLabel.DesiredSize.Width / 2);
                Canvas.SetTop(pctLabel, ly - pctLabel.DesiredSize.Height / 2);
                DonutCanvas.Children.Add(pctLabel);
            }

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

    private void ShowDonutPopup(QueryStoreState state)
    {
        var dbs = _states.Where(s => s.State == state).OrderBy(s => s.DatabaseName).ToList();
        var stateLabel = state switch
        {
            QueryStoreState.ReadWrite => "Read Write",
            QueryStoreState.ReadOnly => "Read Only",
            _ => "OFF"
        };

        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        stack.Children.Add(new TextBlock
        {
            Text = $"{stateLabel} ({dbs.Count})",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Margin = new Thickness(0, 0, 0, 4)
        });

        foreach (var db in dbs)
        {
            stack.Children.Add(new TextBlock
            {
                Text = db.DatabaseName,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            });
        }

        var popup = new Popup
        {
            PlacementTarget = DonutCanvas,
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = this.FindResource("BackgroundLightBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#22252D")),
                BorderBrush = this.FindResource("BorderBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#3A3D45")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                MinWidth = 180,
                MaxHeight = 300,
                Child = new ScrollViewer { Content = stack }
            }
        };

        // Add popup to the visual tree temporarily
        if (DonutCanvas.Parent is Panel parentPanel)
        {
            parentPanel.Children.Add(popup);
            popup.IsOpen = true;
            popup.Closed += (_, _) => parentPanel.Children.Remove(popup);
        }
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

    // ── Time Slicer (delegates to TimeRangeSlicerControl) ──────────────────

    private async void OnSlicerRangeChanged(object? sender, TimeRangeChangedEventArgs e)
    {
        _slicerStartUtc = e.StartUtc;
        _slicerEndUtc = e.EndUtc;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try
        {
            await RefreshMetricsAndWaitStatsAsync(_cts.Token);
        }
        catch (OperationCanceledException) { }
        catch { /* swallow errors from refresh */ }
    }

    // ── Wait Stats Chart (stacked by database) ──────────────────────────────

    private void DrawWaitStatsChart()
    {
        WaitStatsCanvas.Children.Clear();
        if (_waitSlices.Count == 0)
        {
            var msg = new TextBlock
            {
                Text = _supportsWaitStats ? "No wait stats data" : "Wait stats not supported (SQL 2017+ required)",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#888888")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            Canvas.SetLeft(msg, 10);
            Canvas.SetTop(msg, 20);
            WaitStatsCanvas.Children.Add(msg);
            return;
        }

        var w = WaitStatsBorder.Bounds.Width;
        var h = WaitStatsBorder.Bounds.Height;
        if (w < 10 || h < 10) return;

        const double paddingTop = 4;
        const double paddingBottom = 16;
        var chartH = h - paddingTop - paddingBottom;
        if (chartH <= 0) return;

        // Consolidate: sum ALL wait ratios per database per hour (ignore wait category)
        var dbHourData = _waitSlices
            .GroupBy(s => new { s.DatabaseName, s.IntervalStartUtc })
            .Select(g => (Db: g.Key.DatabaseName, Hour: g.Key.IntervalStartUtc, Total: g.Sum(x => x.WaitRatio)))
            .ToList();

        if (dbHourData.Count == 0) return;

        // Build complete hourly timeline
        var allHours = dbHourData.Select(x => x.Hour).Distinct().OrderBy(x => x).ToList();
        var n = allHours.Count;
        if (n == 0) return;

        // Use the same top-N databases + Others as the bar cards
        var topDbs = _dbColorMap.Keys.ToList();

        // Compute per-hour totals for each database group
        var hourLookup = dbHourData
            .GroupBy(x => x.Hour)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Find max stacked total for Y scaling
        double maxTotal = 0;
        double totalWaitSum = 0;
        int bucketsWithData = 0;
        foreach (var hour in allHours)
        {
            if (!hourLookup.TryGetValue(hour, out var items)) continue;
            var total = items.Sum(x => x.Total);
            if (total > maxTotal) maxTotal = total;
            totalWaitSum += total;
            bucketsWithData++;
        }
        if (maxTotal <= 0) maxTotal = 1;

        var stepX = w / n;
        var barGap = Math.Min(2.0, Math.Max(0.5, stepX * 0.1));

        for (int i = 0; i < n; i++)
        {
            var hour = allHours[i];
            if (!hourLookup.TryGetValue(hour, out var items)) continue;

            // Aggregate per database
            var dbTotals = new Dictionary<string, double>();
            double othersTotal = 0;
            foreach (var item in items)
            {
                if (topDbs.Contains(item.Db))
                    dbTotals[item.Db] = dbTotals.GetValueOrDefault(item.Db) + item.Total;
                else
                    othersTotal += item.Total;
            }

            double y = paddingTop + chartH; // bottom
            var x = i * stepX;

            // Draw stacked bars: top databases first, then others
            foreach (var db in topDbs)
            {
                var val = dbTotals.GetValueOrDefault(db);
                if (val <= 0) continue;
                var segH = (val / maxTotal) * chartH;
                y -= segH;

                var color = _dbColorMap.GetValueOrDefault(db, OthersColor);
                var rect = new Rectangle
                {
                    Width = Math.Max(1, stepX - barGap),
                    Height = Math.Max(0.5, segH),
                    Fill = new SolidColorBrush(color),
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                WaitStatsCanvas.Children.Add(rect);

                ToolTip.SetTip(rect, $"{db}: {WaitRatioFormatter.Format(val)}");
                ToolTip.SetShowDelay(rect, 200);
            }

            if (othersTotal > 0)
            {
                var segH = (othersTotal / maxTotal) * chartH;
                y -= segH;
                var rect = new Rectangle
                {
                    Width = Math.Max(1, stepX - barGap),
                    Height = Math.Max(0.5, segH),
                    Fill = new SolidColorBrush(OthersColor),
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                WaitStatsCanvas.Children.Add(rect);
                ToolTip.SetTip(rect, $"Others: {WaitRatioFormatter.Format(othersTotal)}");
                ToolTip.SetShowDelay(rect, 200);
            }
        }

        // X-axis labels at day boundaries
        var labelBrush = new SolidColorBrush(Color.Parse("#E4E6EB"));
        for (int i = 0; i < n; i++)
        {
            if (allHours[i].Hour == 0)
            {
                var xDay = i * stepX;
                WaitStatsCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(xDay, paddingTop),
                    EndPoint = new Point(xDay, paddingTop + chartH),
                    Stroke = labelBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = [4, 4],
                    Opacity = 0.3,
                });
                var tb = new TextBlock
                {
                    Text = TimeDisplayHelper.FormatForDisplay(allHours[i], "MM/dd"),
                    FontSize = 8,
                    Foreground = labelBrush,
                };
                Canvas.SetLeft(tb, xDay + 2);
                Canvas.SetTop(tb, h - paddingBottom + 1);
                WaitStatsCanvas.Children.Add(tb);
            }
        }

        // ── Horizontal dashed average line ─────────────────────────────────
        if (bucketsWithData > 0)
        {
            var avgWait = totalWaitSum / bucketsWithData;
            if (avgWait > 0 && avgWait <= maxTotal)
            {
                var avgY = paddingTop + chartH - (avgWait / maxTotal) * chartH;
                var dashBrush = new SolidColorBrush(Color.Parse("#E4E6EB"));
                var avgLine = new Line
                {
                    StartPoint = new Point(0, avgY),
                    EndPoint = new Point(w, avgY),
                    Stroke = dashBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = [6, 3],
                    Opacity = 0.7,
                };
                WaitStatsCanvas.Children.Add(avgLine);

                var avgLabel = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#B0D0D0D0")),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1),
                    Child = new TextBlock
                    {
                        Text = $"avg:{WaitRatioFormatter.Format(avgWait)}",
                        FontSize = 10,
                        Foreground = Brushes.Black,
                    },
                };
                Canvas.SetLeft(avgLabel, 2);
                Canvas.SetTop(avgLabel, Math.Max(0, avgY - 16));
                WaitStatsCanvas.Children.Add(avgLabel);
            }
        }
    }

    // ── Bar Chart Cards ─────────────────────────────────────────────────────

    /// <summary>Unified color map: same database → same color across all cards.</summary>
    private Dictionary<string, Color> _dbColorMap = new();

    private void DrawBarCards()
    {
        // Build a single color map based on top-N by total CPU (union across all databases)
        _dbColorMap.Clear();
        var ranked = _metrics
            .OrderByDescending(m => m.TotalCpu)
            .Select(m => m.DatabaseName)
            .ToList();
        var topDbs = ranked.Take(_topN).ToList();
        for (int i = 0; i < topDbs.Count && i < Palette.Length; i++)
            _dbColorMap[topDbs[i]] = Palette[i];

        DrawMetricRow(TotalMetricsGrid, isTotal: true, topDbs);
        DrawMetricRow(AvgMetricsGrid, isTotal: false, topDbs);
    }

    private void DrawMetricRow(Grid grid, bool isTotal, List<string> topDbs)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();

        var metricNames = isTotal
            ? new[] { "Total CPU", "Total Duration", "Executions", "Total Reads", "Total Writes", "Total Physical Reads", "Total Memory" }
            : new[] { "Avg CPU", "Avg Duration", "Executions", "Avg Reads", "Avg Writes", "Avg Physical Reads", "Avg Memory" };

        for (int i = 0; i < metricNames.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        for (int mi = 0; mi < metricNames.Length; mi++)
        {
            var card = CreateBarCard(metricNames[mi], mi, isTotal, topDbs, _dbColorMap);
            Grid.SetColumn(card, mi);
            card.Margin = new Thickness(mi == 0 ? 0 : 5, 0, mi == metricNames.Length - 1 ? 0 : 5, 0);
            grid.Children.Add(card);
        }
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
                menuItem.Click += (_, _) => DrillDownRequested?.Invoke(this, new DrillDownEventArgs(dbName, _slicerStartUtc, _slicerEndUtc));
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
