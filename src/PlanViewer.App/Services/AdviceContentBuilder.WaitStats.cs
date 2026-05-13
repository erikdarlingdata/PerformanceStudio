using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Services;

internal static partial class AdviceContentBuilder
{
    private static StackPanel CreateWaitStatLine(string waitName, string waitValue, double maxWaitMs)
    {
        var wrapper = new StackPanel
        {
            Margin = new Avalonia.Thickness(12, 1, 0, 1)
        };

        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var waitBrush = GetWaitCategoryBrush(waitName);
        tb.Inlines!.Add(new Run(waitName) { Foreground = waitBrush });
        tb.Inlines.Add(new Run(": " + waitValue) { Foreground = ValueBrush });

        // Inline description label for the wait type
        var label = PlanAnalyzer.GetWaitLabel(waitName);
        if (!string.IsNullOrEmpty(label))
            tb.Inlines.Add(new Run("  " + label) { Foreground = MutedBrush, FontSize = 11 });

        wrapper.Children.Add(tb);

        // Proportional bar scaled to max wait in group
        var ms = ParseWaitMs(waitValue);
        if (ms > 0 && maxWaitMs > 0)
        {
            var barWidth = MaxBarWidth * (ms / maxWaitMs);
            wrapper.Children.Add(new Border
            {
                Width = Math.Max(2, barWidth),
                Height = 4,
                Background = waitBrush,
                CornerRadius = new Avalonia.CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Avalonia.Thickness(0, 0, 0, 2)
            });
        }

        return wrapper;
    }

    /// <summary>
    /// Renders a missing index impact line like "dbo.Posts (impact: 95%)" with
    /// the table name in value color and the impact colored by severity.
    /// </summary>
    private static SelectableTextBlock CreateMissingIndexImpactLine(string trimmed)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(12, 2, 0, 0)
        };

        var impactStart = trimmed.IndexOf("(impact:");
        var tableName = trimmed[..impactStart].TrimEnd();
        var impactPart = trimmed[impactStart..];

        // Parse the percentage to pick a color
        var pctStr = impactPart.Replace("(impact:", "").Replace("%)", "").Trim();
        var impactBrush = MutedBrush;
        if (double.TryParse(pctStr, out var pct))
        {
            impactBrush = pct >= 70 ? CriticalBrush : (pct >= 40 ? WarningBrush : InfoBrush);
        }

        tb.Inlines!.Add(new Run(tableName + " ") { Foreground = ValueBrush });
        tb.Inlines.Add(new Run(impactPart) { Foreground = impactBrush, FontWeight = FontWeight.SemiBold });

        return tb;
    }

    /// <summary>
    /// Parses a wait stat value like "1,234ms" into a double.
    /// </summary>
    private static double ParseWaitMs(string waitValue)
    {
        var numStr = waitValue.Replace("ms", "").Replace(",", "").Trim();
        return double.TryParse(numStr, out var val) ? val : 0;
    }

    private static SolidColorBrush GetWaitCategoryBrush(string waitType)
    {
        // CPU-related
        if (waitType.StartsWith("SOS_SCHEDULER") || waitType.StartsWith("CXPACKET") ||
            waitType.StartsWith("CXCONSUMER") || waitType == "THREADPOOL" ||
            waitType.StartsWith("EXECSYNC"))
            return new SolidColorBrush(Color.Parse("#FFB347")); // orange

        // I/O-related
        if (waitType.StartsWith("PAGEIOLATCH") || waitType.StartsWith("WRITELOG") ||
            waitType.StartsWith("IO_COMPLETION") || waitType.StartsWith("ASYNC_IO"))
            return new SolidColorBrush(Color.Parse("#E57373")); // red

        // Lock/blocking
        if (waitType.StartsWith("LCK_") || waitType.StartsWith("LOCK"))
            return new SolidColorBrush(Color.Parse("#E57373")); // red

        // Memory
        if (waitType.StartsWith("RESOURCE_SEMAPHORE") || waitType.StartsWith("CMEMTHREAD"))
            return new SolidColorBrush(Color.Parse("#C792EA")); // purple

        // Network
        if (waitType.StartsWith("ASYNC_NETWORK"))
            return new SolidColorBrush(Color.Parse("#6BB5FF")); // blue

        return LabelBrush; // default muted
    }

    /// <summary>
    /// Creates a per-statement triage summary card showing key findings at a glance.
    /// </summary>
    private static Border? CreateTriageSummaryCard(StatementResult stmt)
    {
        var items = new List<(string text, SolidColorBrush brush)>();

        // Parallel efficiency
        var dop = stmt.DegreeOfParallelism;
        if (dop > 1 && stmt.QueryTime != null && stmt.QueryTime.ElapsedTimeMs > 0)
        {
            var cpuMs = (double)stmt.QueryTime.CpuTimeMs;
            var elapsedMs = (double)stmt.QueryTime.ElapsedTimeMs;
            // efficiency = (cpu/elapsed - 1) / (dop - 1) * 100, clamped 0-100
            var ratio = cpuMs / elapsedMs;
            var efficiency = (ratio - 1.0) / (dop - 1.0) * 100.0;
            efficiency = Math.Clamp(efficiency, 0, 100);
            var effBrush = efficiency < 50 ? CriticalBrush : (efficiency < 75 ? WarningBrush : InfoBrush);
            items.Add(($"\u26A0 {efficiency:F0}% parallel efficiency (DOP {dop})", effBrush));
        }

        // Memory grant — color by utilization efficiency
        if (stmt.MemoryGrant != null && stmt.MemoryGrant.GrantedKB > 0)
        {
            var grantedMB = stmt.MemoryGrant.GrantedKB / 1024.0;
            var usedPct = stmt.MemoryGrant.MaxUsedKB > 0
                ? (double)stmt.MemoryGrant.MaxUsedKB / stmt.MemoryGrant.GrantedKB * 100.0
                : 0.0;
            // Red: <10% used (massive waste), Amber: <50%, Blue: <80%, Green-ish (info): >=80%
            var memBrush = usedPct < 10 ? CriticalBrush
                         : usedPct < 50 ? WarningBrush
                         : InfoBrush;
            items.Add(($"Memory grant: {grantedMB:F1} MB ({usedPct:F0}% used)", memBrush));
        }

        // Wait profile classification
        if (stmt.WaitStats.Count > 0)
        {
            var totalMs = stmt.WaitStats.Sum(w => w.WaitTimeMs);
            if (totalMs > 0)
            {
                long ioMs = 0, cpuMs = 0, parallelMs = 0, lockMs = 0;
                foreach (var w in stmt.WaitStats)
                {
                    var wt = w.WaitType.ToUpperInvariant();
                    if (wt.StartsWith("PAGEIOLATCH") || wt.Contains("IO_COMPLETION"))
                        ioMs += w.WaitTimeMs;
                    else if (wt == "SOS_SCHEDULER_YIELD")
                        cpuMs += w.WaitTimeMs;
                    else if (wt.StartsWith("CX"))
                        parallelMs += w.WaitTimeMs;
                    else if (wt.StartsWith("LCK_"))
                        lockMs += w.WaitTimeMs;
                }

                // Pick the dominant category (>= 30% of total)
                var categories = new List<(string label, long ms)>();
                if (ioMs * 100 / totalMs >= 30) categories.Add(("I/O", ioMs));
                if (cpuMs * 100 / totalMs >= 30) categories.Add(("CPU", cpuMs));
                if (parallelMs * 100 / totalMs >= 30) categories.Add(("parallelism", parallelMs));
                if (lockMs * 100 / totalMs >= 30) categories.Add(("lock contention", lockMs));

                if (categories.Count > 0)
                {
                    var label = string.Join(" + ", categories.Select(c => c.label));
                    items.Add(($"{label} bound ({totalMs:N0}ms total wait time)", InfoBrush));
                }
            }
        }

        // Warning counts by severity
        var criticalCount = stmt.Warnings.Count(w =>
            w.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase));
        var warningCount = stmt.Warnings.Count(w =>
            w.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        if (criticalCount > 0 || warningCount > 0)
        {
            var parts = new List<string>();
            if (criticalCount > 0)
                parts.Add($"{criticalCount} critical");
            if (warningCount > 0)
                parts.Add($"{warningCount} warning{(warningCount != 1 ? "s" : "")}");
            var countBrush = criticalCount > 0 ? CriticalBrush : WarningBrush;
            items.Add((string.Join(", ", parts), countBrush));
        }

        // Missing indexes
        if (stmt.MissingIndexes.Count > 0)
        {
            items.Add(($"{stmt.MissingIndexes.Count} missing index suggestion{(stmt.MissingIndexes.Count != 1 ? "s" : "")}", InfoBrush));
        }

        // Spill warnings
        var spillCount = stmt.Warnings.Count(w =>
            w.Type.Contains("Spill", StringComparison.OrdinalIgnoreCase));
        if (spillCount > 0)
        {
            items.Add(($"{spillCount} spill warning{(spillCount != 1 ? "s" : "")}", CriticalBrush));
        }

        if (items.Count == 0)
            return null;

        var cardPanel = new StackPanel
        {
            Margin = new Avalonia.Thickness(4)
        };

        for (int idx = 0; idx < items.Count; idx++)
        {
            var (text, brush) = items[idx];
            var isHeadline = idx == 0;
            cardPanel.Children.Add(new SelectableTextBlock
            {
                Text = text,
                FontFamily = MonoFont,
                FontSize = isHeadline ? 13 : 12,
                FontWeight = isHeadline ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = brush,
                Margin = new Avalonia.Thickness(4, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new Border
        {
            Background = CardBackgroundBrush,
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(8, 4, 8, 4),
            Margin = new Avalonia.Thickness(0, 4, 0, 6),
            Child = cardPanel
        };
    }
}
