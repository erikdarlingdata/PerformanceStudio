using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using PlanViewer.App.Services;
using PlanViewer.Core.Output;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Advice for Humans is two views over one <see cref="AnalysisResult"/>: the cards on screen and
/// the plain-text report the Copy button hands out. The report is a frozen contract — people paste
/// it into tickets — so the first test here pins its bytes, and the rest assert that the card view
/// is built from the model rather than from that text.
/// </summary>
public class AdviceCardsTests
{
    private const string GoldenPlan = "key_lookup_plan.sqlplan";
    private const string GoldenFile = "advice-for-humans.key_lookup_plan.txt";

    /// <summary>
    /// The report, character for character, as it read before the pane became cards.
    ///
    /// <para>The Copy button hands out <see cref="TextFormatter"/>'s output untouched, so a
    /// reformatting anywhere between the model and that string would change what lands in someone
    /// else's ticket. Nothing in the rebuild was supposed to; this is what says so.</para>
    ///
    /// <para>Also formats the report a second time after building the cards. "Two views over one
    /// model" only holds while the card builder treats the model as read-only — an in-place sort
    /// for display would reorder the text view too, quietly, for whoever copies afterwards.</para>
    /// </summary>
    [Fact]
    public void TheCopiedReportStillReadsExactlyAsItDid()
    {
        var result = Analyze(GoldenPlan);

        var before = InInvariantCulture(() => TextFormatter.Format(result));

        HeadlessUi.Run(() => AdviceContentBuilder.Build(before, result));

        var after = InInvariantCulture(() => TextFormatter.Format(result));
        Assert.Equal(Normalize(before), Normalize(after));

        var goldenPath = GoldenPath(GoldenFile);
        if (!File.Exists(goldenPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, before, new UTF8Encoding(false));
            Assert.Fail($"No golden report yet. Wrote one to {goldenPath} — read it, then commit it.");
        }

        // Line endings are the platform's, and this repo normalises them in the index; the
        // contract is the content.
        Assert.Equal(Normalize(File.ReadAllText(goldenPath)), Normalize(before));
    }

    /// <summary>
    /// The card path never looks at the text. Handed a report that says nothing at all, it still
    /// builds the statement out of the model — which is the whole point of the split, and the thing
    /// that stops a change to one view reformatting the other.
    /// </summary>
    [Fact]
    public void TheCardsComeFromTheModelNotFromTheReportText()
    {
        var result = Analyze(GoldenPlan);

        HeadlessUi.Run(() =>
        {
            var panel = Show(AdviceContentBuilder.Build("this text is not the model", result));

            Assert.Contains(
                panel.GetLogicalDescendants().OfType<SelectableTextBlock>(),
                b => b.Text == "Statement 1");
            Assert.Contains("Eggs McLaren", AllText(panel));
            Assert.DoesNotContain("this text is not the model", AllText(panel));
        });
    }

    /// <summary>
    /// With no model — Advice for Robots, whose content is raw JSON — the pane still renders the
    /// text line by line. The card path is an addition, not a replacement.
    /// </summary>
    [Fact]
    public void WithNoModelThePaneStillRendersTheTextItWasGiven()
    {
        HeadlessUi.Run(() =>
        {
            var panel = Show(AdviceContentBuilder.Build("=== Summary ===\nStatements: 1\n"));

            Assert.Contains("Statements: 1", AllText(panel));
        });
    }

    /// <summary>
    /// The expensive-operator block the cards render is the expensive-operator block the report
    /// prints — same operators, same order, same numbers.
    ///
    /// <para>The own-time attribution behind it exists twice: <see cref="TextFormatter"/> keeps its
    /// copy private for the text view, and the card builder has its own. Two copies of an algorithm
    /// drift; this asserts the strings one produces appear verbatim in what the other wrote, so the
    /// day they disagree is the day a test fails rather than the day a window shows two different
    /// top-five lists.</para>
    /// </summary>
    [Fact]
    public void TheCardsOperatorLinesAreTheReportsOperatorLines()
    {
        var result = Analyze("excellent-parallel-spill.sqlplan");
        var stmt = result.Statements[0];

        var report = InInvariantCulture(() => TextFormatter.Format(result));
        var block = InInvariantCulture(() =>
        {
            var sb = new StringBuilder();
            foreach (var (op, timing, stats) in AdviceContentBuilder.ExpensiveOperatorLines(stmt))
            {
                sb.Append(op).Append('\n');
                if (timing != null) sb.Append("    ").Append(timing).Append('\n');
                if (stats != null) sb.Append("    ").Append(stats).Append('\n');
            }
            return sb.ToString();
        });

        Assert.NotEmpty(block);
        Assert.Contains(block, Normalize(report));
    }

    /// <summary>
    /// The 3px edge is the card's verdict, and it has to be readable from the scrollbar: red when
    /// anything critical is inside, amber when only warnings are, and the neutral border colour
    /// when the statement is clean. A card that shouts when it has nothing to say is the failure
    /// mode the plan-insights strip was already fixed for.
    /// </summary>
    [Fact]
    public void AStatementCardsEdgeCarriesItsWorstFinding()
    {
        HeadlessUi.Run(() =>
        {
            Assert.Equal(Token("ErrorBrush"), EdgeColour(Warned("Critical", "Warning")));
            Assert.Equal(Token("WarningBrush"), EdgeColour(Warned("Warning", "Info")));
            Assert.Equal(Token("BorderBrush"), EdgeColour(Warned()));
        });
    }

    /// <summary>
    /// A warning hanging off an operator counts towards the edge exactly as a statement warning
    /// does. The report keeps them in two sections, which is why the first cut of the edge only
    /// looked at one of them and left a plan whose only critical finding was on a Key Lookup —
    /// this fixture — reading as clean.
    /// </summary>
    [Fact]
    public void AnOperatorsCriticalFindingReachesTheCardEdge()
    {
        var result = Analyze(GoldenPlan);

        HeadlessUi.Run(() =>
        {
            var panel = Show(AdviceContentBuilder.Build("", result));

            Assert.Equal(Token("ErrorBrush"), EdgeColour(panel));
            Assert.Contains("Key Lookup", AllText(panel));
        });
    }

    /// <summary>
    /// Up to three statements the reader is reading a report, and every card opens. Past that they
    /// are scrolling a list and want the titles first.
    /// </summary>
    [Fact]
    public void CardsOpenExpandedUntilThereAreTooManyToRead()
    {
        HeadlessUi.Run(() =>
        {
            Assert.All(Expanders(WithStatements(3)), e => Assert.True(e.IsExpanded));
            Assert.All(Expanders(WithStatements(4)), e => Assert.False(e.IsExpanded));
        });
    }

    /// <summary>
    /// The summary counts, as chips. Checked through the pane's text rather than by counting
    /// Borders: what matters is that a reader sees "1 critical", not how many containers it took.
    /// </summary>
    [Fact]
    public void TheHeaderStripCarriesTheSummaryAsChips()
    {
        var result = Analyze(GoldenPlan);

        HeadlessUi.Run(() =>
        {
            var text = AllText(Show(AdviceContentBuilder.Build("", result)));

            Assert.Contains("SQL Server 16.0.4222.2", text);
            Assert.Contains("1 statement", text);
            Assert.Contains("1 critical", text);
            Assert.Contains("actual stats", text);
            // The warning-type taxonomy, now a tag rather than a list to work through.
            Assert.Contains("Key Lookup", text);
        });
    }

    /// <summary>
    /// The statement keeps the pane's SQL colouring inside the card — the same highlighter, not a
    /// second one — and stays one selectable block, so a drag still takes the whole query (#503).
    /// </summary>
    [Fact]
    public void TheStatementKeepsItsSyntaxColouringAndItsSelection()
    {
        var result = Analyze(GoldenPlan);

        HeadlessUi.Run(() =>
        {
            var panel = Show(AdviceContentBuilder.Build("", result));

            var sql = panel.GetLogicalDescendants().OfType<SelectableTextBlock>()
                .First(b => b.Inlines?.Text?.Contains("Eggs McLaren") == true);

            sql.SelectAll();
            Assert.Contains("SELECT", sql.SelectedText);
            Assert.Contains("WHERE", sql.SelectedText);

            var runs = sql.Inlines!.OfType<Run>().ToList();
            var keyword = runs.First(r => r.Text == "SELECT");
            var identifier = runs.First(r => r.Text == "Eggs");
            Assert.NotEqual(keyword.Foreground, identifier.Foreground);
        });
    }

    /// <summary>
    /// Every text block in the card pane takes a press anywhere in its rectangle, the Expander
    /// headers included (#503 follow-up).
    ///
    /// <para>A block with no background is only hit-testable where its glyphs rendered, so a press
    /// in the padding starts no selection at all. The card view nests blocks in shapes the old
    /// hand-rolled walk did not know — a Border around an Expander, an Expander's header — which is
    /// why that walk is now a logical-tree one. Asserted over the logical tree rather than by
    /// mirroring the production traversal: a mirror shares its blind spots.</para>
    /// </summary>
    [Fact]
    public void EveryTextBlockInACardTakesAPressAnywhereInItsRectangle()
    {
        var result = Analyze("excellent-parallel-spill.sqlplan");

        HeadlessUi.Run(() =>
        {
            var panel = Show(AdviceContentBuilder.Build("", result));

            var blocks = panel.GetLogicalDescendants().OfType<SelectableTextBlock>().ToList();
            Assert.True(blocks.Count >= 20, $"fixture should fill a card, got {blocks.Count}");
            Assert.All(blocks, b => Assert.NotNull(b.Background));

            // The header lives on the Expander, not in its content — the shape the old walk missed.
            var header = Assert.IsAssignableFrom<Control>(Expanders(panel).First().Header);
            Assert.All(
                header.GetSelfAndLogicalDescendants().OfType<SelectableTextBlock>(),
                b => Assert.NotNull(b.Background));
        });
    }

    /// <summary>
    /// The card is one surface. Fluent fills an Expander's header row with a surface of its own,
    /// which on a card ground reads as a second panel inside the first, so the resting fill is
    /// cleared through the theme's own key. Asserted against the realised header rather than the
    /// override, because the thing that breaks is Avalonia renaming the key — at which point the
    /// override silently does nothing and only the header shows it.
    /// </summary>
    [Fact]
    public void TheCardHeaderDoesNotPaintASecondSurfaceOverTheCard()
    {
        HeadlessUi.Run(() =>
        {
            var expander = Expanders(WithStatements(1)).Single();

            var header = expander.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .First();

            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(header.Background).Color);
        });
    }

    /// <summary>
    /// "Node N" inside a card is a link, the same as it was in the report. The wiring runs over the
    /// finished pane, so nesting findings inside cards is exactly where it would have been lost.
    /// </summary>
    [Fact]
    public void NodeReferencesInsideACardAreStillLinks()
    {
        var result = Analyze(GoldenPlan);

        HeadlessUi.Run(() =>
        {
            var panel = Show(AdviceContentBuilder.Build("", result, _ => { }));

            var links = panel.GetLogicalDescendants().OfType<SelectableTextBlock>()
                .SelectMany(b => b.Inlines is { } inlines ? inlines.OfType<Run>() : Enumerable.Empty<Run>())
                .Where(r => r.TextDecorations == TextDecorations.Underline)
                .ToList();

            Assert.Contains(links, r => r.Text != null && r.Text.StartsWith("Node "));
        });
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static AnalysisResult Analyze(string planFile) =>
        ResultMapper.Map(PlanTestHelper.LoadAndAnalyze(planFile), planFile);

    /// <summary>A statement carrying one finding per severity named.</summary>
    private static StackPanel Warned(params string[] severities)
    {
        var stmt = new StatementResult { StatementText = "SELECT 1;" };
        foreach (var severity in severities)
            stmt.Warnings.Add(new WarningResult
            {
                Severity = severity,
                Type = severity + " finding",
                Message = "something"
            });

        var result = new AnalysisResult { Summary = new AnalysisSummary { TotalStatements = 1 } };
        result.Statements.Add(stmt);
        return Show(AdviceContentBuilder.Build("", result));
    }

    private static StackPanel WithStatements(int count)
    {
        var result = new AnalysisResult { Summary = new AnalysisSummary { TotalStatements = count } };
        for (int i = 0; i < count; i++)
            result.Statements.Add(new StatementResult { StatementText = $"SELECT {i};" });
        return Show(AdviceContentBuilder.Build("", result));
    }

    /// <summary>The 3px accent edge is the only fixed-width Border the card view builds.</summary>
    private static Color EdgeColour(StackPanel panel)
    {
        var edge = panel.GetLogicalDescendants().OfType<Border>().First(b => b.Width == 3);
        return Assert.IsAssignableFrom<ISolidColorBrush>(edge.Background).Color;
    }

    private static List<Expander> Expanders(StackPanel panel) =>
        panel.GetLogicalDescendants().OfType<Expander>().ToList();

    private static Color Token(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var value), $"no token {key}");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static string AllText(StackPanel panel) =>
        string.Join("\n", panel.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .Select(b => b.Inlines?.Text ?? b.Text ?? ""));

    /// <summary>Selection and layout need a laid-out control attached to a TopLevel.</summary>
    private static StackPanel Show(StackPanel panel)
    {
        var window = new Window { Content = panel, Width = 1000, Height = 700 };
        window.Show();
        window.UpdateLayout();
        return panel;
    }

    /// <summary>
    /// The report's numbers are formatted against the current culture, so the golden has to be
    /// compared against a fixed one or it pins the machine that wrote it.
    /// </summary>
    private static T InInvariantCulture<T>(Func<T> format)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return format();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string GoldenPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlanViewer.Core.Tests.csproj")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Golden", name);
    }
}
