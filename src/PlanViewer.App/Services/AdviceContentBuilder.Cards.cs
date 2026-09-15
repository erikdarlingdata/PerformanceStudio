using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.Core.Output;

namespace PlanViewer.App.Services;

/// <summary>
/// The card view of an <see cref="AnalysisResult"/>: a header strip of stat chips over one card
/// per statement, in place of the monospace report the pane used to print.
///
/// <para>The report itself has not moved. <see cref="TextFormatter"/> still writes it from the same
/// model and the Copy button still hands out exactly those bytes — this file is a second view over
/// that model, not a transformation of its text, which is the only arrangement in which the two can
/// be changed independently without one quietly reformatting the other.</para>
/// </summary>
internal static partial class AdviceContentBuilder
{
    /* Every colour on the card path is a design token. The literals in AdviceContentBuilder.cs
       belong to the text renderer and are deliberately not reused here. */
    private static readonly SolidColorBrush CardSurfaceBrush = FromTheme("BackgroundLightBrush", "#22252D");
    private static readonly SolidColorBrush CardEdgeQuietBrush = FromTheme("BorderBrush", "#3A3D45");
    private static readonly SolidColorBrush ChipGroundBrush = FromTheme("BackgroundDarkBrush", "#15171C");
    private static readonly SolidColorBrush AccentTokenBrush = FromTheme("AccentBrush", "#2eaef1");
    private static readonly SolidColorBrush MissingIndexTokenBrush = FromTheme("InsightIndexBrush", "#FFB347");
    private static readonly SolidColorBrush QuietTextBrush = FromTheme("ForegroundMutedBrush", "#B0B6C0");

    /// <summary>
    /// Statement count up to which every card opens expanded. Past it the reader is scrolling a
    /// list, not reading a report, and wants the titles first.
    /// </summary>
    private const int ExpandedByDefaultCeiling = 3;

    internal static StackPanel BuildCards(AnalysisResult analysis, Action<int>? onNodeClick)
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(4, 0) };

        panel.Children.Add(BuildHeaderStrip(analysis));

        var expanded = analysis.Statements.Count <= ExpandedByDefaultCeiling;
        for (int i = 0; i < analysis.Statements.Count; i++)
            panel.Children.Add(BuildStatementCard(analysis, analysis.Statements[i], i + 1, expanded));

        if (onNodeClick != null)
            MakeNodeRefsClickable(panel, onNodeClick);

        MakeTextBlocksHitTestable(panel);

        return panel;
    }

    // -----------------------------------------------------------------------------------
    // Header strip
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Server line plus the summary as chips. The counts used to be four lines of "Label: value"
    /// that had to be read to be counted; as chips the critical count is the one red thing on the
    /// screen and lands before the reader has finished the server name.
    /// </summary>
    private static Border BuildHeaderStrip(AnalysisResult analysis)
    {
        var body = new StackPanel();

        var title = ServerTitle(analysis);
        if (title != null)
        {
            body.Children.Add(new SelectableTextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = LabelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 6)
            });
        }

        var context = ContextChips(analysis.ServerContext);
        if (context.Count > 0)
            body.Children.Add(ChipRow(context));

        var summary = analysis.Summary;
        var stats = new List<Border>
        {
            Chip(Plural(summary.TotalStatements, "statement"), AccentTokenBrush)
        };
        if (summary.CriticalWarnings > 0)
            stats.Add(Chip($"{summary.CriticalWarnings:N0} critical", CriticalBrush));
        var nonCritical = Math.Max(0, summary.TotalWarnings - summary.CriticalWarnings);
        if (nonCritical > 0)
            stats.Add(Chip(Plural(nonCritical, "warning"), WarningBrush));
        if (summary.MissingIndexes > 0)
            stats.Add(Chip(Plural(summary.MissingIndexes, "missing index", "missing indexes"), MissingIndexTokenBrush));
        if (summary.TotalWarnings == 0 && summary.MissingIndexes == 0)
            stats.Add(Chip("nothing flagged", QuietTextBrush));
        stats.Add(Chip(summary.HasActualStats ? "actual stats" : "estimated plan", QuietTextBrush));
        body.Children.Add(ChipRow(stats));

        /* The warning-type taxonomy. A flat list under "Warning types:" read as a second summary
           to work through; as tags it is a legend for the cards below it. */
        if (summary.WarningTypes.Count > 0)
            body.Children.Add(ChipRow(summary.WarningTypes.Select(t => Chip(t, QuietTextBrush)).ToList()));

        return new Border
        {
            Background = CardSurfaceBrush,
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(12, 10),
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            Child = body
        };
    }

    /// <summary>
    /// The one line naming what was analysed, connected or not. Null when the plan carries no
    /// server identity at all, so the strip opens on the counts rather than on a blank row.
    /// </summary>
    private static string? ServerTitle(AnalysisResult analysis)
    {
        var ctx = analysis.ServerContext;
        if (ctx == null)
            return analysis.SqlServerBuild != null ? $"SQL Server {analysis.SqlServerBuild}" : null;

        var name = string.IsNullOrWhiteSpace(ctx.ServerName) ? "SQL Server" : ctx.ServerName;
        var version = string.Join(" ", new[]
            {
                ctx.IsAzure ? "Azure SQL" : TrimBitness(ctx.Edition),
                ctx.ProductVersion,
                ctx.ProductLevel is null or "RTM" ? null : ctx.ProductLevel
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))!);

        return version.Length > 0 ? $"{name} — {version}" : name;
    }

    private static string? TrimBitness(string? edition)
    {
        if (edition == null)
            return null;
        var idx = edition.IndexOf(" (64-bit)", StringComparison.Ordinal);
        return idx > 0 ? edition[..idx] : edition;
    }

    /// <summary>
    /// The instance and database settings the report prints under Server Context. Chips rather
    /// than lines: every one of them is a short "name value" pair that a reader scans for an
    /// outlier, which is what a chip row is for.
    /// </summary>
    private static List<Border> ContextChips(ServerContextResult? ctx)
    {
        var chips = new List<Border>();
        if (ctx == null)
            return chips;

        if (ctx.CpuCount > 0)
            chips.Add(Chip($"{ctx.CpuCount:N0} CPUs, {ctx.PhysicalMemoryMB:N0} MB RAM", QuietTextBrush));
        chips.Add(Chip($"MAXDOP {ctx.MaxDop}", QuietTextBrush));
        chips.Add(Chip($"Cost threshold {ctx.CostThresholdForParallelism}", QuietTextBrush));
        chips.Add(Chip($"Max memory {ctx.MaxServerMemoryMB:N0} MB", QuietTextBrush));

        var db = ctx.Database;
        if (db == null)
            return chips;

        chips.Add(Chip($"{db.Name} (compat {db.CompatibilityLevel})", AccentTokenBrush));
        if (!string.IsNullOrWhiteSpace(db.CollationName))
            chips.Add(Chip(db.CollationName, QuietTextBrush));

        /* Same rule the report applies: only settings that deviate from a healthy default get
           named, so the row stays a list of things worth a second look. */
        if (db.SnapshotIsolationState > 0)
            chips.Add(Chip("Snapshot isolation ON", WarningBrush));
        if (db.ReadCommittedSnapshot)
            chips.Add(Chip("RCSI ON", WarningBrush));
        if (!db.AutoCreateStats)
            chips.Add(Chip("Auto create stats OFF", CriticalBrush));
        if (!db.AutoUpdateStats)
            chips.Add(Chip("Auto update stats OFF", CriticalBrush));
        if (db.AutoUpdateStatsAsync)
            chips.Add(Chip("Auto update stats async ON", WarningBrush));
        if (db.ParameterizationForced)
            chips.Add(Chip("Forced parameterization ON", WarningBrush));

        foreach (var sc in db.NonDefaultScopedConfigs)
            chips.Add(Chip($"{sc.Name} = {sc.Value}", WarningBrush));

        return chips;
    }

    // -----------------------------------------------------------------------------------
    // Statement cards
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// One statement, as a card on the plan-insights visual system: neutral surface, 3px accent
    /// edge carrying the verdict. Red edge when something critical is in there, amber when only
    /// warnings are, neutral when the statement is clean — so the shape of the plan's trouble is
    /// readable from the scrollbar without opening anything.
    /// </summary>
    private static Border BuildStatementCard(
        AnalysisResult analysis,
        StatementResult stmt,
        int ordinal,
        bool expandedByDefault)
    {
        var findings = CollectFindings(stmt);
        var edge = findings.Any(f => IsSeverity(f.Warning.Severity, "Critical")) ? CriticalBrush
            : findings.Any(f => IsSeverity(f.Warning.Severity, "Warning")) ? WarningBrush
            : CardEdgeQuietBrush;

        var expander = new Expander
        {
            IsExpanded = expandedByDefault,
            /* Fluent fills the header row with its own surface, which on a card ground reads as a
               second panel sitting inside the first. Only the resting fill is cleared — the
               pointer-over and pressed fills are separate keys and stay, so the header still
               answers the mouse. */
            Resources = { ["ExpanderHeaderBackground"] = Brushes.Transparent },
            Header = StatementHeader(stmt, ordinal, findings),
            Content = StatementBody(analysis, stmt, findings),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(0),
            Foreground = LabelBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var accent = new Border { Width = 3, Background = edge };
        DockPanel.SetDock(accent, Dock.Left);

        var layout = new DockPanel();
        layout.Children.Add(accent);
        layout.Children.Add(expander);

        return new Border
        {
            Background = CardSurfaceBrush,
            CornerRadius = new Avalonia.CornerRadius(6),
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            ClipToBounds = true,
            Child = layout
        };
    }

    /// <summary>
    /// The card title: which statement, and the numbers that say how big it is. The judgment
    /// calls — parallel efficiency, where the waits went, the grant it actually used — stay in
    /// the triage band inside, so the title never has to be read twice.
    /// </summary>
    private static Control StatementHeader(
        StatementResult stmt,
        int ordinal,
        List<Finding> findings)
    {
        var chips = new List<Border>();

        if (stmt.EstimatedCost > 0)
            chips.Add(Chip($"cost {MetricFormatter.FormatCost(stmt.EstimatedCost)}", QuietTextBrush));
        if (stmt.QueryTime is { ElapsedTimeMs: > 0 })
            chips.Add(Chip(MetricFormatter.FormatDuration(stmt.QueryTime.ElapsedTimeMs) + " elapsed", AccentTokenBrush));
        if (stmt.QueryTime is { CpuTimeMs: > 0 })
            chips.Add(Chip(MetricFormatter.FormatDuration(stmt.QueryTime.CpuTimeMs) + " CPU", QuietTextBrush));
        if (stmt.DegreeOfParallelism > 0)
            chips.Add(Chip($"DOP {stmt.DegreeOfParallelism}", QuietTextBrush));

        var critical = findings.Count(f => IsSeverity(f.Warning.Severity, "Critical"));
        var warning = findings.Count(f => IsSeverity(f.Warning.Severity, "Warning"));
        if (critical > 0)
            chips.Add(Chip($"{critical:N0} critical", CriticalBrush));
        if (warning > 0)
            chips.Add(Chip(Plural(warning, "warning"), WarningBrush));
        if (stmt.MissingIndexes.Count > 0)
            chips.Add(Chip(Plural(stmt.MissingIndexes.Count, "missing index", "missing indexes"), MissingIndexTokenBrush));

        var header = new StackPanel { Margin = new Avalonia.Thickness(4, 0, 0, 0) };
        header.Children.Add(new SelectableTextBlock
        {
            Text = $"Statement {ordinal}",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = LabelBrush,
            TextWrapping = TextWrapping.Wrap
        });
        if (chips.Count > 0)
            header.Children.Add(ChipRow(chips));

        /* A collapsed card has to say what it is holding. The first line of the statement is the
           only thing in here that identifies it to the person who wrote the query. */
        if (!string.IsNullOrWhiteSpace(stmt.StatementText))
        {
            header.Children.Add(new SelectableTextBlock
            {
                Text = FirstLine(stmt.StatementText),
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = QuietTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Avalonia.Thickness(0, 4, 0, 0)
            });
        }

        return header;
    }

    private static string FirstLine(string text)
    {
        var line = text.Replace('\r', '\n').Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        line = line.Trim();
        return line.Length <= 120 ? line : line[..120] + "…";
    }

    // -----------------------------------------------------------------------------------
    // Chips
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// A chip: the accent colour on a recessed ground, sized by its text. Selectable like every
    /// other piece of text in this pane — a count someone wants is a count they can copy.
    /// </summary>
    private static Border Chip(string text, SolidColorBrush accent) =>
        new()
        {
            Background = ChipGroundBrush,
            BorderBrush = accent,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(8, 1),
            Margin = new Avalonia.Thickness(0, 0, 6, 4),
            Child = new SelectableTextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = accent
            }
        };

    private static WrapPanel ChipRow(IReadOnlyList<Border> chips)
    {
        var row = new WrapPanel { Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        foreach (var chip in chips)
            row.Children.Add(chip);
        return row;
    }

    private static string Plural(int count, string singular, string? plural = null) =>
        $"{count:N0} {(count == 1 ? singular : plural ?? singular + "s")}";

    private static bool IsSeverity(string severity, string expected) =>
        severity.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
