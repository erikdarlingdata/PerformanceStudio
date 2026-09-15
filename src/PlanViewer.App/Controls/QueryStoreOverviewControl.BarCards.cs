using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;

namespace PlanViewer.App.Controls;

public partial class QueryStoreOverviewControl : UserControl
{
    // ── Metric cards ────────────────────────────────────────────────────────
    //
    // What was here: fourteen cards — seven Totals over seven Avgs — each a stack of coloured
    // pills whose only encoding was length, with the database name printed inside the pill and cut
    // to twelve characters ("StackOverflo...", "ha..."), and no value anywhere except a tooltip.
    // The pills also lied: each one was laid out as `value/max` against `1 - value/max` star
    // columns of the CARD, so a card whose second database was 92% of the first drew two bars that
    // looked the same length, and nothing on the card said otherwise.
    //
    // What is here now is small multiples. One row of cards, a Total/Avg switch above them, and
    // inside each card one horizontal bar per database sorted longest-first, every bar sitting in
    // a full-width track so the zero-anchored scale is visible, every bar carrying its own value
    // in real units. Colour still comes from the user's MultiQsTopDbColors and is stable across
    // every card, which is what makes the row of cards readable as one picture — so the legend is
    // now drawn once, above them, instead of being re-stated inside all fourteen.

    /// <summary>Unified color map: same database → same color across all cards.</summary>
    private Dictionary<string, Color> _dbColorMap = new();

    /// <summary>
    /// The units a metric arrives in, which decides how its value label is written.
    /// </summary>
    internal enum MetricUnit
    {
        /// <summary>Milliseconds, off the service's <c>/ 1000.0</c> of a microsecond column.</summary>
        Milliseconds,
        /// <summary>A plain count — executions, or logical/physical pages.</summary>
        Count,
        /// <summary>Megabytes, off the service's <c>* 8.0 / 1024.0</c> of an 8KB page count.</summary>
        Megabytes
    }

    /// <summary>
    /// One card. Executions is deliberately the same in both modes: a count of executions has no
    /// per-execution average, and printing "Avg Executions" over the identical number was one of
    /// the things that made the duplicated row look like it carried twice the information.
    /// </summary>
    private readonly record struct MetricCardSpec(string TotalTitle, string AvgTitle, MetricUnit Unit);

    private static readonly MetricCardSpec[] MetricCards =
    [
        new("Total CPU", "Avg CPU", MetricUnit.Milliseconds),
        new("Total Duration", "Avg Duration", MetricUnit.Milliseconds),
        new("Executions", "Executions", MetricUnit.Count),
        new("Total Reads", "Avg Reads", MetricUnit.Count),
        new("Total Writes", "Avg Writes", MetricUnit.Count),
        new("Total Physical Reads", "Avg Physical Reads", MetricUnit.Count),
        new("Total Memory", "Avg Memory", MetricUnit.Megabytes),
    ];

    /// <summary>The aggregate row every database outside the top N is folded into.</summary>
    internal const string OthersLabel = "Others";

    /// <summary>
    /// The one card that is a count of executions rather than a measurement per execution, so it is
    /// the one the Total/Avg switch leaves alone.
    /// </summary>
    private const int ExecutionsMetricIndex = 2;

    /// <summary>One bar: a database (or the Others aggregate) and what it measured.</summary>
    internal readonly record struct OverviewBarRow(string Database, double Value, bool IsOthers);

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

        var hasOthers = _metrics.Count > topDbs.Count;

        DrawLegend(topDbs, hasOthers);

        MetricsGrid.Children.Clear();
        MetricsGrid.ColumnDefinitions.Clear();

        for (int i = 0; i < MetricCards.Length; i++)
            MetricsGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        for (int mi = 0; mi < MetricCards.Length; mi++)
        {
            var rows = BuildBarRows(_metrics, topDbs, mi, _showAverages);
            var card = CreateMetricCard(mi, rows);
            Grid.SetColumn(card, mi);
            card.Margin = new Thickness(mi == 0 ? 0 : 4, 0, mi == MetricCards.Length - 1 ? 0 : 4, 0);
            MetricsGrid.Children.Add(card);
        }
    }

    /// <summary>
    /// The bars one card shows, longest first.
    ///
    /// <para>Every database outside the top N is summed into a single Others row, and Others sorts
    /// on its own value like any other row rather than being pinned to the bottom — it is a real
    /// quantity, and on a server with one busy database and forty quiet ones it can legitimately
    /// be the longest bar on the card. Ties break on the database name so a redraw of unchanged
    /// data cannot reshuffle the card.</para>
    ///
    /// <para>The Others row is present whenever any database falls outside the top N, even when it
    /// sums to zero for this particular metric. Dropping it on the cards where it happens to be
    /// zero would give neighbouring cards different row counts, and the whole point of drawing
    /// seven cards side by side is that they can be compared row for row.</para>
    ///
    /// <para><b>In Avg mode Others is re-averaged, not added up.</b> The average of forty databases
    /// is not the sum of their forty averages — adding rates together produces a number that is not
    /// a rate at all. Forty quiet databases each averaging 10ms would report an Others bar of 400ms,
    /// outrank every real database on the card, become the scale everything else is measured
    /// against, and squash the real bars to slivers. So the tail keeps its totals and its execution
    /// count and divides at the end, exactly as <see cref="DatabaseMetrics"/> derives its own
    /// averages. Executions is the exception in both directions: it has no per-execution average, so
    /// it stays a plain sum whichever mode is showing.</para>
    /// </summary>
    internal static List<OverviewBarRow> BuildBarRows(
        IReadOnlyList<DatabaseMetrics> metrics,
        IReadOnlyCollection<string> topDbs,
        int metricIndex,
        bool showAverages)
    {
        var rows = new List<OverviewBarRow>();
        double othersTotal = 0;
        long othersExecutions = 0;
        var anyOthers = false;

        foreach (var m in metrics)
        {
            if (topDbs.Contains(m.DatabaseName))
            {
                rows.Add(new OverviewBarRow(
                    m.DatabaseName, GetMetricValue(m, metricIndex, showAverages), IsOthers: false));
            }
            else
            {
                othersTotal += GetMetricValue(m, metricIndex, showAverages: false);
                othersExecutions += m.TotalExecutions;
                anyOthers = true;
            }
        }

        if (anyOthers)
        {
            var othersValue = showAverages && metricIndex != ExecutionsMetricIndex
                ? (othersExecutions > 0 ? othersTotal / othersExecutions : 0)
                : othersTotal;

            rows.Add(new OverviewBarRow(OthersLabel, othersValue, IsOthers: true));
        }

        return rows
            .OrderByDescending(r => r.Value)
            .ThenBy(r => r.Database, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The colour key for the whole row of cards, drawn once. Top databases in palette order, then
    /// Others in the muted colour it is drawn in everywhere else.
    /// </summary>
    private void DrawLegend(IReadOnlyList<string> topDbs, bool hasOthers)
    {
        DatabaseLegend.Children.Clear();

        foreach (var db in topDbs)
            DatabaseLegend.Children.Add(BuildLegendEntry(db, _dbColorMap.GetValueOrDefault(db, OthersColor)));

        if (hasOthers)
            DatabaseLegend.Children.Add(BuildLegendEntry(OthersLabel, OthersColor));
    }

    private static Control BuildLegendEntry(string database, Color color)
    {
        var swatch = new Border
        {
            Classes = { "legendSwatch" },
            Background = new SolidColorBrush(color)
        };

        var label = new TextBlock { Text = database, Classes = { "legendLabel" } };

        /* Transparent, not null: Avalonia hit-tests what a control actually drew, so a bare panel
           answers the pointer only where its children's glyphs are and the tooltip would go dead in
           the strip above and below a 9px swatch. */
        var entry = new Border
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 14, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { swatch, label }
            }
        };

        ToolTip.SetTip(entry, database);
        return entry;
    }

    private Border CreateMetricCard(int metricIndex, IReadOnlyList<OverviewBarRow> rows)
    {
        var spec = MetricCards[metricIndex];
        var isQuiet = rows.Count == 0 || rows.All(r => r.Value <= 0);

        var header = new TextBlock
        {
            Text = _showAverages ? spec.AvgTitle : spec.TotalTitle,
            Classes = { "cardHeader" }
        };
        header.Classes.Set("empty", isQuiet);

        var content = new StackPanel { Children = { header } };

        if (isQuiet)
        {
            content.Children.Add(new TextBlock
            {
                Text = "No activity in range",
                Classes = { "cardEmpty" }
            });

            return new Border { Classes = { "metricCard" }, Child = content };
        }

        // Every bar in a card is measured against the same zero-anchored scale — the card's own
        // largest value. That is the encoding the old pills claimed to have and did not.
        var scaleMax = rows.Max(r => r.Value);
        if (scaleMax <= 0) scaleMax = 1;

        foreach (var row in rows)
            content.Children.Add(BuildBarRow(row, scaleMax, spec.Unit));

        return new Border { Classes = { "metricCard" }, Child = content };
    }

    /// <summary>
    /// One bar: its name and its value on a line together, and the bar itself on the full width of
    /// the card underneath them.
    ///
    /// <para><b>Why the label is above the bar rather than beside it.</b> Beside it was the first
    /// shape this took, and measured at MainWindow's default 1280 it does not work: seven cards
    /// share the row, which leaves each one about 156px of content, and a name column and a value
    /// column — both Auto, both winning their width against a star-sized track — took 70px and 60px
    /// of it. The bar got fourteen pixels. A fourteen-pixel bar encodes nothing, which is the exact
    /// complaint this card was rebuilt to answer, so the labels moved to their own line and the bar
    /// took the whole width. It costs a line of height per row and buys an order of magnitude of
    /// the only thing on the card that is a picture.</para>
    /// </summary>
    private Border BuildBarRow(OverviewBarRow row, double scaleMax, MetricUnit unit)
    {
        var name = new TextBlock { Text = row.Database, Classes = { "barName" } };
        Grid.SetColumn(name, 0);

        var value = new TextBlock { Text = FormatMetric(row.Value, unit), Classes = { "barValue" } };
        Grid.SetColumn(value, 1);

        var labels = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { name, value }
        };

        /* The fill is proportioned by two star columns rather than a measured width, because the
           card is star-sized itself and has no width to measure until it is arranged. Clamped
           because a star length must not go negative on a rounding wobble. */
        var fraction = Math.Clamp(row.Value / scaleMax, 0, 1);
        var proportion = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(fraction, GridUnitType.Star),
                new ColumnDefinition(1 - fraction, GridUnitType.Star)
            }
        };

        var color = row.IsOthers ? OthersColor : _dbColorMap.GetValueOrDefault(row.Database, OthersColor);
        var fill = new Border
        {
            Classes = { "barFill" },
            Background = new SolidColorBrush(color)
        };
        Grid.SetColumn(fill, 0);
        proportion.Children.Add(fill);

        var track = new Border { Classes = { "barTrack" }, Child = proportion };

        /* Transparent, not null: Avalonia hit-tests what a control drew, so a container with no
           background answers the pointer only over its children's glyphs. The tooltip carries the
           unabbreviated figure, so stepping a big count up to "1.2B" in the label never costs the
           reader the number itself, and the full database name survives an ellipsized one. */
        var container = new Border
        {
            Background = Brushes.Transparent,
            Child = new StackPanel { Children = { labels, track } }
        };

        ToolTip.SetTip(container, $"{row.Database}: {FormatMetricExact(row.Value, unit)}");
        ToolTip.SetShowDelay(container, 200);

        /* Drill-down hangs off the whole row, not off the coloured fill it used to hang off: on the
           database that measured one percent of the leader, the fill is a sliver and the
           right-click target was a sliver with it. */
        if (!row.IsOthers)
        {
            var database = row.Database;
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "Drill Down to DB Query Store" };
            item.Click += (_, _) => DrillDownRequested?.Invoke(
                this, new DrillDownEventArgs(database, _slicerStartUtc, _slicerEndUtc));
            menu.Items.Add(item);
            container.ContextMenu = menu;
        }

        return container;
    }

    /// <summary>
    /// A bar's value label, in the units the service handed over, and capped in WIDTH as well as
    /// scaled in value.
    ///
    /// <para>The label shares its row with the bar, and the bar is what the card is for. A 30-day
    /// Total Reads is routinely eleven or twelve digits, and "123,456,789,012" spends about 72px of
    /// a card that has roughly 160 to give at the app's default 1280-wide window — with a long
    /// database name on the other side that leaves the bar almost nothing. Above a hundred thousand
    /// the label steps up a unit instead, which bounds it at six characters, and the exact figure
    /// moves to the tooltip where it costs no width at all. Below that nothing changes: small
    /// numbers stay exactly what they are.</para>
    /// </summary>
    internal static string FormatMetric(double value, MetricUnit unit) => unit switch
    {
        MetricUnit.Milliseconds => FormatMilliseconds(value),
        MetricUnit.Megabytes => FormatMegabytes(value),
        _ => FormatCount(value)
    };

    /// <summary>
    /// The same value with nothing abbreviated away, for the tooltip. Durations are already on a
    /// scaled ladder that loses nothing a reader wants, so they come through unchanged.
    /// </summary>
    internal static string FormatMetricExact(double value, MetricUnit unit) => unit switch
    {
        MetricUnit.Milliseconds => FormatMilliseconds(value),
        MetricUnit.Megabytes => FormatMagnitude(value) + " MB",
        _ => FormatMagnitude(value)
    };

    private static string FormatCount(double value)
    {
        if (value < 100_000) return FormatMagnitude(value);
        if (value < 1_000_000) return (value / 1_000).ToString("N0") + "K";
        if (value < 1_000_000_000) return (value / 1_000_000).ToString("0.#") + "M";
        if (value < 1_000_000_000_000) return (value / 1_000_000_000).ToString("0.#") + "B";
        return (value / 1_000_000_000_000).ToString("0.#") + "T";
    }

    /// <summary>
    /// Megabytes, because that is what the service's <c>* 8.0 / 1024.0</c> of a page count produces.
    /// A busy instance's Total Memory over a month runs to millions of them, so this climbs to GB
    /// and TB rather than printing seven digits of megabyte.
    /// </summary>
    private static string FormatMegabytes(double mb)
    {
        if (mb < 1024) return FormatMagnitude(mb) + " MB";
        if (mb < 1024 * 1024) return (mb / 1024).ToString("0.#") + " GB";
        return (mb / (1024.0 * 1024.0)).ToString("0.#") + " TB";
    }

    /// <summary>
    /// <see cref="MetricFormatter.FormatDuration"/> for anything a millisecond or longer, so these
    /// labels climb the same ms → s → m ladder as the statements grid.
    ///
    /// <para>Below a millisecond it has to do its own thing: that ladder starts at whole
    /// milliseconds, and an average CPU of 0.4ms — an ordinary number on a healthy OLTP database —
    /// would round to "0ms" and report a query that ran as a query that did not.</para>
    /// </summary>
    private static string FormatMilliseconds(double ms)
    {
        if (ms <= 0) return "0ms";
        if (ms < 0.01) return "<0.01ms";
        if (ms < 1) return ms.ToString("0.##") + "ms";
        return MetricFormatter.FormatDuration((long)Math.Round(ms));
    }

    /// <summary>
    /// A count or a megabyte figure with enough decimals to stay a number, and no more.
    ///
    /// <para>A whole number prints whole however small it is: these cards carry execution counts
    /// and page counts, and "4.0 executions" is not a thing. Decimals are for the fractions only an
    /// average produces, where they are the entire value — "0" is the wrong answer for a database
    /// averaging 0.3 physical reads an execution, and so is "0.0".</para>
    /// </summary>
    private static string FormatMagnitude(double value)
    {
        if (value <= 0) return "0";
        if (value == Math.Floor(value)) return value.ToString("N0");
        if (value < 0.01) return "<0.01";
        if (value < 1) return value.ToString("N2");
        if (value < 10) return value.ToString("N1");
        return value.ToString("N0");
    }

    private static double GetMetricValue(DatabaseMetrics m, int metricIndex, bool showAverages)
    {
        if (!showAverages)
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
