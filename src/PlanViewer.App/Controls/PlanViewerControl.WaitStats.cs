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

        WaitStatsHeader.Text = $"Wait Stats \u2014 {totalWait:N0}ms total";

        /* One Grid for all rows so the columns align. Only the duration column flexes (round-1
           finding V4): the wait type is capped and ellipsized, the bar is fixed, and the trailing
           "up to N%" sits in an Auto column so it is never the thing that gets clipped. The panel's
           horizontal scrolling is off, so squeezing the strip narrows the duration rather than
           growing a sideways scrollbar. */
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto")
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

            // Wait type name, colored by category. Capped so one long type cannot widen every row.
            var nameText = new TextBlock
            {
                Text = w.WaitType,
                FontSize = 12,
                Foreground = categoryBrush,
                MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2)
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
                Margin = new Thickness(0, 2, 8, 2)
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
                    Margin = new Thickness(0, 2, 0, 2)
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
    /// The theme token each wait category is drawn in.
    ///
    /// <para>I/O, Lock and Network map onto the semantic tokens that already mean the same thing
    /// elsewhere (warning, error, ok). CPU takes the Wait Stats panel's own accent. Memory has no
    /// semantic token of its own, so it borrows the one token left that stays distinct from the
    /// other five. "Other" is the unclassified bucket and gets the neutral muted foreground, which
    /// is what it always meant.</para>
    /// </summary>
    private static string GetWaitCategoryBrushKey(string category)
    {
        return category switch
        {
            "CPU" => "InsightWaitsBrush",
            "I/O" => "WarningBrush",
            "Lock" => "ErrorBrush",
            "Memory" => "InsightServerBrush",
            "Network" => "SuccessBrush",
            _ => "ForegroundMutedBrush"
        };
    }
}
