using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using PlanViewer.Core.Output;

namespace PlanViewer.App.Services;

/// <summary>
/// The inside of a statement card: the statement, what was found in it, and the supporting
/// detail the report prints underneath — all read off the <see cref="AnalysisResult"/> rather
/// than off the text written from it.
/// </summary>
internal static partial class AdviceContentBuilder
{
    /// <summary>
    /// A warning together with where it was attached. The report prints statement warnings and
    /// operator warnings as two separate sections; a card shows them as one severity-ordered
    /// list, because "what is wrong with this statement" is one question, and the operator a
    /// finding came from is an attribute of the finding rather than a category of its own.
    /// </summary>
    private readonly record struct Finding(WarningResult Warning, string? Operator);

    private static List<Finding> CollectFindings(StatementResult stmt)
    {
        /* Same suppression the report applies: the generic "Memory Grant" warning is noise once a
           specific one has named the grant, and a card showing it while the pasted text does not
           would make the two views disagree about how many findings there are. */
        var hasDetailedMemoryGrant = stmt.Warnings.Any(w =>
            w.Type == "Excessive Memory Grant" || w.Type == "Large Memory Grant");

        var findings = stmt.Warnings
            .Where(w => !(w.Type == "Memory Grant" && hasDetailedMemoryGrant))
            .Select(w => new Finding(w, w.Operator))
            .ToList();

        if (stmt.OperatorTree != null)
            CollectOperatorFindings(stmt.OperatorTree, findings);

        return findings
            .OrderBy(f => SeverityRank(f.Warning.Severity))
            .ThenByDescending(f => f.Warning.MaxBenefitPercent ?? -1)
            .ThenBy(f => f.Warning.Type, StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectOperatorFindings(OperatorResult node, List<Finding> findings)
    {
        foreach (var w in node.Warnings)
            findings.Add(new Finding(w, w.Operator ?? node.PhysicalOp));
        foreach (var child in node.Children)
            CollectOperatorFindings(child, findings);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Critical" => 0,
        "Warning" => 1,
        _ => 2
    };

    private static SolidColorBrush SeverityBrush(string severity) => SeverityRank(severity) switch
    {
        0 => CriticalBrush,
        1 => WarningBrush,
        _ => AccentTokenBrush
    };

    // -----------------------------------------------------------------------------------
    // Card body
    // -----------------------------------------------------------------------------------

    private static Control StatementBody(AnalysisResult analysis, StatementResult stmt, List<Finding> findings)
    {
        var body = new StackPanel { Margin = new Avalonia.Thickness(4, 4, 8, 8) };

        if (!string.IsNullOrWhiteSpace(stmt.StatementText))
            body.Children.Add(SqlBlock(stmt.StatementText));

        var triage = CreateTriageSummaryCard(stmt, includeMemoryGrant: false);
        if (triage != null)
            body.Children.Add(triage);

        AddDetailLines(body, stmt, analysis);

        if (findings.Count > 0)
        {
            body.Children.Add(SectionLabel(Plural(findings.Count, "finding")));
            foreach (var finding in findings)
                body.Children.Add(FindingRow(finding));
        }

        AddMissingIndexes(body, stmt);
        AddExpensiveOperators(body, stmt);
        AddWaitStats(body, stmt);
        AddParameters(body, stmt);

        return body;
    }

    /// <summary>
    /// The statement, on a recessed ground so it reads as the code it is. The colouring is the
    /// pane's existing SQL highlighter, and the lines are merged into one selectable block the
    /// same way the text view merges them (#503) — a query is something people drag across.
    /// </summary>
    private static Border SqlBlock(string statementText)
    {
        var lines = new StackPanel();
        var sql = new BodyTextAccumulator();
        foreach (var line in statementText.Replace("\r\n", "\n").Split('\n'))
            sql.AddLine(BuildSqlHighlightedLine(line));
        sql.Flush(lines);

        return new Border
        {
            Background = ChipGroundBrush,
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(8, 6),
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
            Child = lines
        };
    }

    /// <summary>
    /// The two facts with nowhere better to live: why the statement went serial, and the grant
    /// when the triage band did not already take it.
    /// </summary>
    private static void AddDetailLines(StackPanel body, StatementResult stmt, AnalysisResult analysis)
    {
        if (stmt.NonParallelReason != null)
            body.Children.Add(DetailLine("Serial reason: ", stmt.NonParallelReason));

        if (stmt.MemoryGrant is { GrantedKB: > 0 } grant)
        {
            var usedPct = (double)grant.MaxUsedKB / grant.GrantedKB * 100;
            var context = analysis.ServerContext?.MaxServerMemoryMB > 0
                ? $", {grant.GrantedKB / 1024.0 / analysis.ServerContext.MaxServerMemoryMB * 100:N1}% of max server memory"
                : "";
            /* The grant in full, coloured by the same utilisation verdict the triage band gives
               it — which is why the band is told to leave it alone: one grant, said once. */
            body.Children.Add(DetailLine(
                "Memory grant: ",
                $"{TextFormatter.FormatMemoryGrantKB(grant.GrantedKB)} granted, "
                + $"{TextFormatter.FormatMemoryGrantKB(grant.MaxUsedKB)} used ({usedPct:N0}% utilized{context})",
                MemoryGrantBrush(usedPct)));
        }
    }

    private static SelectableTextBlock DetailLine(string label, string value, IBrush? valueBrush = null)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 2, 0, 0)
        };
        tb.Inlines!.Add(new Run(label) { Foreground = QuietTextBrush });
        tb.Inlines.Add(new Run(value) { Foreground = valueBrush ?? ValueBrush });
        return tb;
    }

    private static SelectableTextBlock SectionLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = LabelBrush,
            Margin = new Avalonia.Thickness(0, 10, 0, 0)
        };

    // -----------------------------------------------------------------------------------
    // Findings
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// One finding: severity chip, what it is, what it says, and the fix when the rule has one.
    /// No icons — the chip already carries the severity in colour and in a word, and a glyph in
    /// front of it would be a third encoding of the same fact.
    /// </summary>
    private static Border FindingRow(Finding finding)
    {
        var w = finding.Warning;
        var brush = SeverityBrush(w.Severity);

        var head = new WrapPanel();
        head.Children.Add(Chip(w.Severity, brush));
        head.Children.Add(new SelectableTextBlock
        {
            Text = w.Type,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 8, 4)
        });

        var tags = new List<string>();
        if (finding.Operator != null)
            tags.Add(w.NodeId.HasValue ? $"{finding.Operator} (Node {w.NodeId})" : finding.Operator);
        if (w.Source == nameof(PlanViewer.Core.Models.PlanWarningSource.SqlServer))
            tags.Add("SQL Server");
        if (w.IsLegacy)
            tags.Add("legacy");
        if (w.MaxBenefitPercent.HasValue)
            tags.Add($"up to {FormatBenefit(w.MaxBenefitPercent.Value)}% benefit");
        if (tags.Count > 0)
        {
            head.Children.Add(new SelectableTextBlock
            {
                Text = string.Join("  ·  ", tags),
                FontSize = 11,
                Foreground = QuietTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 4)
            });
        }

        var panel = new StackPanel();
        panel.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(w.Message))
            panel.Children.Add(MessageBlock(w.Message));

        if (!string.IsNullOrEmpty(w.ActionableFix))
        {
            var fix = new SelectableTextBlock
            {
                FontFamily = MonoFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 3, 0, 0)
            };
            fix.Inlines!.Add(new Run("Fix: ") { Foreground = AccentTokenBrush, FontWeight = FontWeight.SemiBold });
            AppendMessageParts(fix.Inlines, w.ActionableFix);
            panel.Children.Add(fix);
        }

        return new Border
        {
            BorderBrush = brush,
            BorderThickness = new Avalonia.Thickness(2, 0, 0, 0),
            Padding = new Avalonia.Thickness(8, 2, 0, 2),
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
            Child = panel
        };
    }

    /// <summary>
    /// A warning message, with the same per-line styling the text view gives it — predicates and
    /// index DDL in code colour, bullet stats indented. The model holds real newlines here; only
    /// the text view has to escape them to survive being split back into lines.
    /// </summary>
    private static SelectableTextBlock MessageBlock(string message)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 2, 0, 0)
        };
        AppendMessageParts(tb.Inlines!, message);
        return tb;
    }

    private static void AppendMessageParts(InlineCollection inlines, string message)
    {
        var parts = message.Replace("\r\n", "\n").Split('\n');
        var emitted = false;
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;
            AppendMessagePart(inlines, part, isFirstPart: !emitted, prefix: emitted ? "\n" : "");
            emitted = true;
        }
    }

    private static string FormatBenefit(double percent) =>
        percent >= 100 ? percent.ToString("N0") : percent.ToString("N1");

    // -----------------------------------------------------------------------------------
    // Supporting sections
    // -----------------------------------------------------------------------------------

    private static void AddMissingIndexes(StackPanel body, StatementResult stmt)
    {
        if (stmt.MissingIndexes.Count == 0)
            return;

        body.Children.Add(SectionLabel("Missing indexes"));
        foreach (var mi in stmt.MissingIndexes)
        {
            body.Children.Add(CreateMissingIndexImpactLine($"{mi.Table} (impact: {mi.Impact:F0}%)"));
            body.Children.Add(new SelectableTextBlock
            {
                Text = mi.CreateStatement,
                FontFamily = MonoFont,
                FontSize = 12,
                Foreground = CodeBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(12, 1, 0, 4)
            });
        }
    }

    private static void AddExpensiveOperators(StackPanel body, StatementResult stmt)
    {
        var lines = ExpensiveOperatorLines(stmt);
        if (lines.Count == 0)
            return;

        body.Children.Add(SectionLabel("Expensive operators"));
        foreach (var (op, timing, stats) in lines)
            body.Children.Add(CreateOperatorGroup(op, timing, stats));
    }

    private static void AddWaitStats(StackPanel body, StatementResult stmt)
    {
        if (stmt.WaitStats.Count == 0)
            return;

        var benefits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var wb in stmt.WaitBenefits)
            benefits[wb.WaitType] = wb.MaxBenefitPercent;

        var ordered = stmt.WaitStats.OrderByDescending(w => w.WaitTimeMs).ToList();
        double maxMs = ordered.Count > 0 ? ordered[0].WaitTimeMs : 0;

        body.Children.Add(SectionLabel("Wait stats"));
        foreach (var w in ordered)
        {
            /* The benefit tag rides alongside the value rather than inside it: the bar is scaled
               from the value string, and the text view's tagged lines lose their bars for exactly
               that reason. */
            var tag = benefits.TryGetValue(w.WaitType, out var pct)
                ? $" (up to {FormatBenefit(pct)}% benefit)"
                : null;
            body.Children.Add(CreateWaitStatLine(w.WaitType, $"{w.WaitTimeMs:N0}ms", maxMs, tag));
        }
    }

    private static void AddParameters(StackPanel body, StatementResult stmt)
    {
        if (stmt.Parameters.Count == 0)
            return;

        body.Children.Add(SectionLabel("Parameters"));
        foreach (var p in stmt.Parameters)
        {
            var tb = new SelectableTextBlock
            {
                FontFamily = MonoFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(12, 1, 0, 1)
            };
            tb.Inlines!.Add(new Run(p.Name) { Foreground = LabelBrush });
            tb.Inlines.Add(new Run(" " + p.DataType) { Foreground = QuietTextBrush });
            tb.Inlines.Add(new Run(" = " + (p.CompiledValue ?? "?")) { Foreground = CodeBrush });
            if (p.SniffingIssue)
                tb.Inlines.Add(new Run("  [SNIFFING]") { Foreground = CriticalBrush, FontWeight = FontWeight.SemiBold });
            body.Children.Add(tb);
        }
    }

    // -----------------------------------------------------------------------------------
    // Expensive operators: own-time attribution
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The top five operators by own work, as the three strings the pane's operator renderer
    /// takes — deliberately the same three lines <see cref="TextFormatter"/> writes for this
    /// statement, character for character.
    ///
    /// <para>The attribution below mirrors TextFormatter's, which owns it for the text view and
    /// keeps it private. Rather than trust two copies to stay in step,
    /// <c>AdviceCardsTests.TheCardsOperatorLinesAreTheReportsOperatorLines</c> reads the report
    /// and asserts these strings appear in it, so a change to one copy fails loudly instead of
    /// showing two different top-five lists in the same window.</para>
    /// </summary>
    internal static List<(string Operator, string? Timing, string? Stats)> ExpensiveOperatorLines(
        StatementResult stmt)
    {
        var lines = new List<(string, string?, string?)>();
        if (stmt.OperatorTree == null)
            return lines;

        var timings = new List<(OperatorResult Node, long OwnCpuMs, long OwnElapsedMs)>();
        CollectNodeTimings(stmt.OperatorTree, timings);

        var top = timings
            .Where(t => t.OwnCpuMs > 0 || t.OwnElapsedMs > 0)
            .OrderByDescending(t => Math.Max(t.OwnCpuMs, t.OwnElapsedMs))
            .Take(5)
            .ToList();

        var totalCpu = stmt.QueryTime?.CpuTimeMs > 0 ? stmt.QueryTime.CpuTimeMs : 0;
        var totalElapsed = stmt.QueryTime?.ElapsedTimeMs ?? 0;

        foreach (var (n, ownCpu, ownElapsed) in top)
        {
            var label = n.ObjectName != null ? $"{n.PhysicalOp} ({n.ObjectName})" : n.PhysicalOp;

            var timeParts = new List<string>();
            if (ownCpu > 0)
            {
                var pct = totalCpu > 0 ? $" ({ownCpu * 100.0 / totalCpu:N0}%)" : "";
                timeParts.Add($"{ownCpu:N0}ms CPU{pct}");
            }
            if (ownElapsed > 0)
            {
                var pct = totalElapsed > 0 ? $" ({ownElapsed * 100.0 / totalElapsed:N0}%)" : "";
                timeParts.Add($"{ownElapsed:N0}ms elapsed{pct}");
            }

            var details = new List<string>();
            if (n.ActualRows > 0)
                details.Add($"{n.ActualRows:N0} rows");
            if (n.ActualLogicalReads > 0)
                details.Add($"{n.ActualLogicalReads:N0} logical reads");
            if (n.ActualPhysicalReads > 0)
                details.Add($"{n.ActualPhysicalReads:N0} physical reads");

            lines.Add((
                $"  {label} (Node {n.NodeId}):",
                timeParts.Count > 0 ? string.Join(", ", timeParts) : null,
                details.Count > 0 ? string.Join(", ", details) : null));
        }

        return lines;
    }

    private static void CollectNodeTimings(
        OperatorResult node,
        List<(OperatorResult Node, long OwnCpuMs, long OwnElapsedMs)> timings)
    {
        // Exchanges do negligible own work and carry misleading elapsed times.
        if (node.PhysicalOp != "Parallelism")
        {
            var mode = node.ActualExecutionMode ?? node.ExecutionMode;

            long ownCpu = 0;
            if (node.ActualCpuMs.HasValue)
                ownCpu = mode == "Batch"
                    ? node.ActualCpuMs.Value
                    : Math.Max(0, node.ActualCpuMs.Value - ChildCpuSum(node));

            long ownElapsed = 0;
            if (node.ActualElapsedMs.HasValue)
                ownElapsed = mode == "Batch"
                    ? node.ActualElapsedMs.Value
                    : Math.Max(0, node.ActualElapsedMs.Value - ChildElapsedSum(node));

            /* With CPU data present, elapsed-without-CPU is a cumulative timing artifact rather
               than work this operator did. Without it (an estimated plan) elapsed is all there
               is. */
            if (node.ActualCpuMs.HasValue)
            {
                if (ownCpu > 0)
                    timings.Add((node, ownCpu, ownElapsed));
            }
            else if (ownElapsed > 0)
            {
                timings.Add((node, 0, ownElapsed));
            }
        }

        foreach (var child in node.Children)
            CollectNodeTimings(child, timings);
    }

    private static long ChildCpuSum(OperatorResult node)
    {
        long sum = 0;
        foreach (var child in node.Children)
            sum += child.ActualCpuMs ?? ChildCpuSum(child);
        return sum;
    }

    private static long ChildElapsedSum(OperatorResult node)
    {
        long sum = 0;
        foreach (var child in node.Children)
        {
            if (child.PhysicalOp == "Parallelism" && child.Children.Count > 0)
                sum += child.Children
                    .Where(c => c.ActualElapsedMs.HasValue)
                    .Select(c => c.ActualElapsedMs!.Value)
                    .DefaultIfEmpty(0)
                    .Max();
            else if (child.ActualElapsedMs.HasValue)
                sum += child.ActualElapsedMs.Value;
            else
                sum += ChildElapsedSum(child);
        }
        return sum;
    }
}
