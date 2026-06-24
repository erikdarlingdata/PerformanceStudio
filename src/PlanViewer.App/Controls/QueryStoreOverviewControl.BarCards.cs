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
        for (int i = 0; i < topDbs.Count && i < _palette.Length; i++)
            _dbColorMap[topDbs[i]] = _palette[i];

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
