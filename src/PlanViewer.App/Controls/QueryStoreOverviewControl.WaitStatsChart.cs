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
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class QueryStoreOverviewControl : UserControl
{
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
                Foreground = this.FindResource("ForegroundMutedBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#B0B6C0")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            Canvas.SetLeft(msg, 10);
            Canvas.SetTop(msg, 20);
            WaitStatsCanvas.Children.Add(msg);
            return;
        }

        if (_dbColorMap.Count == 0) return; // not yet initialized by DrawBarCards

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
            .Select(g => (Db: g.Key.DatabaseName, Hour: g.Key.IntervalStartUtc, Total: g.Sum(x => x.WaitAmountHours)))
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

}
