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
            // The populated branch hangs the previous statement's totals on this tooltip;
            // without clearing it, an empty statement still answers hover with stale numbers.
            ToolTip.SetTip(WaitStatsHeader, null);
            WaitStatsEmpty.Text = isActualPlan
                ? "No wait stats recorded"
                : "No wait stats (estimated plan)";
            WaitStatsEmpty.IsVisible = true;
            SetInsightQuiet(WaitStatsHeader, WaitStatsAccent, true);
            return;
        }

        WaitStatsEmpty.IsVisible = false;
        SetInsightQuiet(WaitStatsHeader, WaitStatsAccent, false);

        // Build benefit lookup
        var benefitLookup = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var wb in benefits)
            benefitLookup[wb.WaitType] = wb.MaxBenefitPercent;

        var sorted = waits.OrderByDescending(w => w.WaitTimeMs).ToList();
        var maxWait = sorted[0].WaitTimeMs;
        var totalWait = sorted.Sum(w => w.WaitTimeMs);

        // The header ellipsizes in a narrow card, so the total it carries goes on a tooltip too.
        WaitStatsHeader.Text = $"Wait Stats \u2014 {totalWait:N0}ms total";
        ToolTip.SetTip(WaitStatsHeader, $"{totalWait:N0} ms of waits across {sorted.Count} wait types");

        /* One Grid for all rows so the columns align (round-1 finding V4: rows clipped mid-word and
           the panel grew a sideways scrollbar to show the rest).

           The wait type and the duration are both star columns; the bar and the trailing
           "up to N%" are Auto. That split matters at the strip's 280px minimum, where there is
           roughly 244px of usable width and the four pieces want closer to 290. Star columns take
           whatever is left after the Auto ones and shrink to zero if they must, so the two text
           columns ellipsize (each with its full value on a tooltip) and nothing ever overflows the
           card. Make the wait type Auto instead and it overflows a narrow card with horizontal
           scrolling switched off, which is the original bug wearing a different hat.

           Star-sizing the name also left-aligns every bar into a column, which is what makes them
           comparable at a glance. */
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*,Auto")
        };
        for (int i = 0; i < sorted.Count; i++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var durationBrush = FindBrushResource("ForegroundBrush");
        var benefitBrush = FindBrushResource("ForegroundMutedBrush");

        for (int i = 0; i < sorted.Count; i++)
        {
            var w = sorted[i];
            var barFraction = maxWait > 0 ? (double)w.WaitTimeMs / maxWait : 0;
            var category = GetWaitCategory(w.WaitType);
            var categoryBrush = FindBrushResource(GetWaitCategoryBrushKey(category));

            // Wait type name, colored by category
            var nameText = new TextBlock
            {
                Text = w.WaitType,
                FontSize = 12,
                Foreground = categoryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(nameText, $"{w.WaitType} \u2014 {category} wait");
            Grid.SetRow(nameText, i);
            Grid.SetColumn(nameText, 0);
            grid.Children.Add(nameText);

            // Bar: the category color at low opacity, a compact proportional indicator
            var colorBar = new Border
            {
                Width = Math.Max(4, barFraction * 60),
                Height = 14,
                Background = categoryBrush,
                Opacity = 0.38,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 8, 2)
            };
            Grid.SetRow(colorBar, i);
            Grid.SetColumn(colorBar, 1);
            grid.Children.Add(colorBar);

            // Duration text: the flexible column, so this is what gives when the strip is narrow
            var durationText = new TextBlock
            {
                Text = $"{w.WaitTimeMs:N0}ms ({w.WaitCount:N0} waits)",
                FontSize = 12,
                Foreground = durationBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 8, 2),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(durationText, $"{w.WaitTimeMs:N0} ms across {w.WaitCount:N0} waits");
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
                    Foreground = benefitBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2),
                    Background = Brushes.Transparent
                };
                ToolTip.SetTip(benefitText,
                    $"Up to {benefitPct:N0}% of this statement's runtime could be recovered by removing {w.WaitType} waits");
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

    /// <summary>
    /// The theme token each wait category is drawn in — the same identity palette the Query Store
    /// charts use, so a wait type is one colour everywhere in the app.
    /// </summary>
    /// <remarks>
    /// <para>These used to be the status triad: Lock drawn in the error brush, Network in the
    /// success brush, I/O in the warning brush. Two things wrong with that. It judged — a lock wait
    /// is not an error and a network wait is not a success, and the wait palette's own rule is that
    /// these classify rather than judge. And it coupled a wait category to a token that means
    /// something else entirely, so repainting "error" would have quietly repainted "Lock".</para>
    ///
    /// <para>It also put red and green on two categories in a tool for DBAs, which is the one pair
    /// a red-green colour deficiency cannot separate. The identity palette spaces its categories by
    /// lightness within each hue family for exactly that reason.</para>
    /// </remarks>
    private static string GetWaitCategoryBrushKey(string category)
    {
        return category switch
        {
            "CPU" => "WaitCategory.CPU",
            "I/O" => "WaitCategory.Buffer IO",
            "Lock" => "WaitCategory.Lock",
            "Memory" => "WaitCategory.Memory",
            "Network" => "WaitCategory.Network IO",
            _ => "WaitCategory.Others"
        };
    }
}
