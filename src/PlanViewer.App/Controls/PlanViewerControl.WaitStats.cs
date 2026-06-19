using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class PlanViewerControl : UserControl
{
    private void ShowWaitStats(List<WaitStatInfo> waits, List<WaitBenefit> benefits, bool isActualPlan)
    {
        WaitStatsContent.Children.Clear();

        if (waits.Count == 0)
        {
            WaitStatsHeader.Text = "Wait Stats";
            WaitStatsEmpty.Text = isActualPlan
                ? "No wait stats recorded"
                : "No wait stats (estimated plan)";
            WaitStatsEmpty.IsVisible = true;
            return;
        }

        WaitStatsEmpty.IsVisible = false;

        // Build benefit lookup
        var benefitLookup = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var wb in benefits)
            benefitLookup[wb.WaitType] = wb.MaxBenefitPercent;

        var sorted = waits.OrderByDescending(w => w.WaitTimeMs).ToList();
        var maxWait = sorted[0].WaitTimeMs;
        var totalWait = sorted.Sum(w => w.WaitTimeMs);

        // Update expander header with total
        WaitStatsHeader.Text = $"  Wait Stats \u2014 {totalWait:N0}ms total";

        // Build a single Grid for all rows so columns align
        // Name, bar, duration, and benefit columns
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };
        for (int i = 0; i < sorted.Count; i++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int i = 0; i < sorted.Count; i++)
        {
            var w = sorted[i];
            var barFraction = maxWait > 0 ? (double)w.WaitTimeMs / maxWait : 0;
            var color = GetWaitCategoryColor(GetWaitCategory(w.WaitType));

            // Wait type name — colored by category
            var nameText = new TextBlock
            {
                Text = w.WaitType,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(color)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2)
            };
            Grid.SetRow(nameText, i);
            Grid.SetColumn(nameText, 0);
            grid.Children.Add(nameText);

            // Bar — semi-transparent category color, compact proportional indicator
            var barColor = Color.Parse(color);
            var colorBar = new Border
            {
                Width = Math.Max(4, barFraction * 60),
                Height = 14,
                Background = new SolidColorBrush(Color.FromArgb(0x60, barColor.R, barColor.G, barColor.B)),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 8, 2)
            };
            Grid.SetRow(colorBar, i);
            Grid.SetColumn(colorBar, 1);
            grid.Children.Add(colorBar);

            // Duration text
            var durationText = new TextBlock
            {
                Text = $"{w.WaitTimeMs:N0}ms ({w.WaitCount:N0} waits)",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 8, 2)
            };
            Grid.SetRow(durationText, i);
            Grid.SetColumn(durationText, 2);
            grid.Children.Add(durationText);

            // Benefit % (if available)
            if (benefitLookup.TryGetValue(w.WaitType, out var benefitPct) && benefitPct > 0)
            {
                var benefitText = new TextBlock
                {
                    Text = $"up to {benefitPct:N0}%",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#8b949e")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                Grid.SetRow(benefitText, i);
                Grid.SetColumn(benefitText, 3);
                grid.Children.Add(benefitText);
            }
        }

        WaitStatsContent.Children.Add(grid);

    }

    private static string GetWaitCategory(string waitType)
    {
        if (waitType.StartsWith("SOS_SCHEDULER_YIELD") ||
            waitType.StartsWith("CXPACKET") ||
            waitType.StartsWith("CXCONSUMER") ||
            waitType.StartsWith("CXSYNC_PORT") ||
            waitType.StartsWith("CXSYNC_CONSUMER"))
            return "CPU";

        if (waitType.StartsWith("PAGEIOLATCH") ||
            waitType.StartsWith("WRITELOG") ||
            waitType.StartsWith("IO_COMPLETION") ||
            waitType.StartsWith("ASYNC_IO_COMPLETION"))
            return "I/O";

        if (waitType.StartsWith("LCK_M_"))
            return "Lock";

        if (waitType == "RESOURCE_SEMAPHORE" || waitType == "CMEMTHREAD")
            return "Memory";

        if (waitType == "ASYNC_NETWORK_IO")
            return "Network";

        return "Other";
    }

    private static string GetWaitCategoryColor(string category)
    {
        return category switch
        {
            "CPU" => "#4FA3FF",
            "I/O" => "#FFB347",
            "Lock" => "#E57373",
            "Memory" => "#9B59B6",
            "Network" => "#2ECC71",
            _ => "#6BB5FF"
        };
    }
}
