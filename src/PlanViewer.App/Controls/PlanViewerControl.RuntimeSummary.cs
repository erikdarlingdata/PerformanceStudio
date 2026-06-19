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
    private void ShowRuntimeSummary(PlanStatement statement)
    {
        RuntimeSummaryContent.Children.Clear();

        var labelColor = "#E4E6EB";
        var valueColor = "#E4E6EB";

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        int rowIndex = 0;

        void AddRow(string label, string value, string? color = null)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(labelColor)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 8, 1)
            };
            Grid.SetRow(labelText, rowIndex);
            Grid.SetColumn(labelText, 0);
            grid.Children.Add(labelText);

            var valueText = new TextBlock
            {
                Text = value,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(color ?? valueColor)),
                Margin = new Thickness(0, 1, 0, 1)
            };
            Grid.SetRow(valueText, rowIndex);
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);

            rowIndex++;
        }

        // Efficiency thresholds: white >= 40%, orange >= 20%, red < 20%.
        // Loosened per Joe's feedback (#215 C1): for memory grants, moderate
        // utilization (e.g. 60%) is fine — operators can spill near their max,
        // so we shouldn't flag anything above a real over-grant threshold.
        static string EfficiencyColor(double pct) => pct >= 40 ? "#E4E6EB"
            : pct >= 20 ? "#FFB347" : "#E57373";

        // Memory grant color tiers (#215 C1 + E8 + E9): over-used grant (red),
        // any operator spilled (orange), otherwise tier by utilization.
        static string MemoryGrantColor(double pctUsed, bool hasSpill)
        {
            if (pctUsed > 100) return "#E57373";
            if (hasSpill) return "#FFB347";
            if (pctUsed >= 40) return "#E4E6EB";
            if (pctUsed >= 20) return "#FFB347";
            return "#E57373";
        }

        // E7: rename the panel title for estimated plans
        var isEstimated = statement.QueryTimeStats == null;
        RuntimeSummaryTitle.Text = isEstimated ? "Predicted Runtime" : "Runtime Summary";

        var hasSpillInTree = statement.RootNode != null && HasSpillInPlanTree(statement.RootNode);

        // E11: order — Elapsed → CPU:Elapsed → DOP → CPU → Compile → Memory → Used → Optimization → CE Model → Cost.
        // Extra Avalonia-only rows (threads, UDF, cached plan size) kept near their logical neighbors.

        if (statement.QueryTimeStats != null)
        {
            AddRow("Elapsed", $"{statement.QueryTimeStats.ElapsedTimeMs:N0}ms");
            if (statement.QueryTimeStats.ElapsedTimeMs > 0)
            {
                long externalWaitMs = 0;
                foreach (var w in statement.WaitStats)
                    if (BenefitScorer.IsExternalWait(w.WaitType))
                        externalWaitMs += w.WaitTimeMs;
                var effectiveCpu = Math.Max(0L, statement.QueryTimeStats.CpuTimeMs - externalWaitMs);
                var ratio = (double)effectiveCpu / statement.QueryTimeStats.ElapsedTimeMs;
                AddRow("CPU:Elapsed", ratio.ToString("N2"));
            }
        }

        // DOP + parallelism efficiency
        if (statement.DegreeOfParallelism > 0)
        {
            var dopText = statement.DegreeOfParallelism.ToString();
            string? dopColor = null;
            if (statement.QueryTimeStats != null &&
                statement.QueryTimeStats.ElapsedTimeMs > 0 &&
                statement.QueryTimeStats.CpuTimeMs > 0 &&
                statement.DegreeOfParallelism > 1)
            {
                long externalWaitMs = 0;
                foreach (var w in statement.WaitStats)
                    if (BenefitScorer.IsExternalWait(w.WaitType))
                        externalWaitMs += w.WaitTimeMs;
                var effectiveCpu = Math.Max(0, statement.QueryTimeStats.CpuTimeMs - externalWaitMs);
                var speedup = (double)effectiveCpu / statement.QueryTimeStats.ElapsedTimeMs;
                var efficiency = Math.Min(100.0, (speedup - 1.0) / (statement.DegreeOfParallelism - 1.0) * 100.0);
                efficiency = Math.Max(0.0, efficiency);
                dopText += $" ({efficiency:N0}% efficient)";
                dopColor = EfficiencyColor(efficiency);
            }
            AddRow("DOP", dopText, dopColor);
        }
        else if (statement.NonParallelPlanReason != null)
            AddRow("Serial", statement.NonParallelPlanReason);

        if (statement.QueryTimeStats != null)
        {
            AddRow("CPU", $"{statement.QueryTimeStats.CpuTimeMs:N0}ms");
            if (statement.QueryUdfCpuTimeMs > 0)
                AddRow("UDF CPU", $"{statement.QueryUdfCpuTimeMs:N0}ms");
            if (statement.QueryUdfElapsedTimeMs > 0)
                AddRow("UDF elapsed", $"{statement.QueryUdfElapsedTimeMs:N0}ms");
        }

        // Compile stats (category B plan-level property)
        if (statement.CompileTimeMs > 0)
            AddRow("Compile", $"{statement.CompileTimeMs:N0}ms");
        if (statement.CachedPlanSizeKB > 0)
            AddRow("Cached plan size", $"{statement.CachedPlanSizeKB:N0} KB");

        // Memory grant — color per new tiers, spill indicator if any operator spilled
        if (statement.MemoryGrant != null)
        {
            var mg = statement.MemoryGrant;
            var grantPct = mg.GrantedMemoryKB > 0
                ? (double)mg.MaxUsedMemoryKB / mg.GrantedMemoryKB * 100 : 100;
            var grantColor = MemoryGrantColor(grantPct, hasSpillInTree);
            var spillTag = hasSpillInTree ? " ⚠ spill" : "";
            AddRow("Memory grant",
                $"{TextFormatter.FormatMemoryGrantKB(mg.GrantedMemoryKB)} granted, {TextFormatter.FormatMemoryGrantKB(mg.MaxUsedMemoryKB)} used ({grantPct:N0}%){spillTag}",
                grantColor);
            if (mg.GrantWaitTimeMs > 0)
                AddRow("Grant wait", $"{mg.GrantWaitTimeMs:N0}ms", "#E57373");
        }

        // Thread stats
        if (statement.ThreadStats != null)
        {
            var ts = statement.ThreadStats;
            AddRow("Branches", ts.Branches.ToString());
            var totalReserved = ts.Reservations.Sum(r => r.ReservedThreads);
            if (totalReserved > 0)
            {
                var threadPct = (double)ts.UsedThreads / totalReserved * 100;
                var threadColor = EfficiencyColor(threadPct);
                var threadText = ts.UsedThreads == totalReserved
                    ? $"{ts.UsedThreads} used ({totalReserved} reserved)"
                    : $"{ts.UsedThreads} used of {totalReserved} reserved ({totalReserved - ts.UsedThreads} inactive)";
                AddRow("Threads", threadText, threadColor);
            }
            else
            {
                AddRow("Threads", $"{ts.UsedThreads} used");
            }
        }

        // Optimization + CE model
        if (!string.IsNullOrEmpty(statement.StatementOptmLevel))
            AddRow("Optimization", statement.StatementOptmLevel);
        if (!string.IsNullOrEmpty(statement.StatementOptmEarlyAbortReason))
            AddRow("Early abort", statement.StatementOptmEarlyAbortReason);
        if (statement.CardinalityEstimationModelVersion > 0)
            AddRow("CE model", statement.CardinalityEstimationModelVersion.ToString());

        if (grid.Children.Count > 0)
        {
            RuntimeSummaryContent.Children.Add(grid);
            RuntimeSummaryEmpty.IsVisible = false;
        }
        else
        {
            RuntimeSummaryEmpty.IsVisible = true;
        }
        ShowServerContext();
    }

    private void ShowServerContext()
    {
        ServerContextContent.Children.Clear();
        if (_serverMetadata == null)
        {
            ServerContextEmpty.IsVisible = true;
            ServerContextBorder.IsVisible = true;
            return;
        }

        ServerContextEmpty.IsVisible = false;

        var m = _serverMetadata;
        var fgColor = "#E4E6EB";

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        int rowIndex = 0;

        void AddRow(string label, string value)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var lb = new TextBlock
            {
                Text = label, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(fgColor)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 8, 1)
            };
            Grid.SetRow(lb, rowIndex);
            Grid.SetColumn(lb, 0);
            grid.Children.Add(lb);

            var vb = new TextBlock
            {
                Text = value, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse(fgColor)),
                Margin = new Thickness(0, 1, 0, 1)
            };
            Grid.SetRow(vb, rowIndex);
            Grid.SetColumn(vb, 1);
            grid.Children.Add(vb);
            rowIndex++;
        }

        // Server name + edition
        var edition = m.Edition;
        if (edition != null)
        {
            var idx = edition.IndexOf(" (64-bit)");
            if (idx > 0) edition = edition[..idx];
        }
        var serverLine = m.ServerName ?? "Unknown";
        if (edition != null) serverLine += $" ({edition})";
        if (m.ProductVersion != null) serverLine += $", {m.ProductVersion}";
        AddRow("Server", serverLine);

        // Hardware
        if (m.CpuCount > 0)
            AddRow("Hardware", $"{m.CpuCount} CPUs, {m.PhysicalMemoryMB:N0} MB RAM");

        // Instance settings
        AddRow("MAXDOP", m.MaxDop.ToString());
        AddRow("Cost threshold", m.CostThresholdForParallelism.ToString());
        AddRow("Max memory", $"{m.MaxServerMemoryMB:N0} MB");

        // Database
        if (m.Database != null)
            AddRow("Database", $"{m.Database.Name} (compat {m.Database.CompatibilityLevel})");

        ServerContextContent.Children.Add(grid);
        ServerContextBorder.IsVisible = true;
    }

}
