using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Dialogs;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The comparison window, as opposed to the comparison.
///
/// <para><b>What this is guarding.</b> Plan Comparison used to open the advice window and pour the
/// text report into it: "99.3% cheaper" and "1,386% slower" drawn in the same plain white, waits as
/// two stacked lists. These assert the two things that stops being true — the report text is not on
/// screen any more, and every delta wears the colour its direction earned.</para>
///
/// <para>Colours are checked against the theme's own brushes rather than hex, so a token changing
/// value in DarkTheme.axaml does not fail a test about whether the right token was chosen.</para>
/// </summary>
public class ComparisonWindowTests
{
    /// <summary>
    /// Mixed on purpose, so one comparison exercises every chip colour: reads and CPU improve,
    /// runtime and the memory grant regress, and the row estimate moves a long way without earning
    /// a verdict either way.
    /// </summary>
    private const string PlanA = "slow-multi-seek.sqlplan";
    private const string PlanB = "spill_plan.sqlplan";

    [Fact]
    public void EveryDeltaIsAChipInTheColourItsDirectionEarned()
    {
        HeadlessUi.Run(() =>
        {
            MainWindow? owner = null;
            try
            {
                owner = Open(PlanA, PlanB, out var comparison);
                var model = ComparisonFormatter.Build(Analyze(PlanA), Analyze(PlanB), PlanA, PlanB);

                var chips = ChipsByText(comparison);

                /* The unchanged rows are excluded because the window deliberately relabels them —
                   see APlanComparedAgainstItselfSaysNoChangeRatherThanCostlier, which is where that
                   substitution is pinned. Everything else must appear verbatim. */
                var deltas = model.Statements
                    .SelectMany(statement => statement.Metrics
                        .Where(metric => metric.DeltaLabel != null && metric.DeltaPercent != 0)
                        .Select(metric => (metric.DeltaLabel!, metric.Direction))
                        .Concat(statement.Waits
                            .Where(wait => wait.DeltaLabel != null && wait.DeltaPercent != 0)
                            .Select(wait => (wait.DeltaLabel!, wait.Direction))))
                    .ToList();

                Assert.NotEmpty(deltas);

                foreach (var (label, direction) in deltas)
                {
                    Assert.True(chips.TryGetValue(label, out var chip),
                        $"the window shows no chip for '{label}', which the comparison says changed");
                    Assert.Equal(ChipToken(owner, direction), chip.BorderBrush);
                }

                /* And the pair really does exercise all three, so a future fixture swap that made
                   every metric neutral would fail here rather than quietly testing nothing. */
                Assert.Contains(deltas, d => d.Item2 == ComparisonDirection.Better);
                Assert.Contains(deltas, d => d.Item2 == ComparisonDirection.Worse);
                Assert.Contains(deltas, d => d.Item2 == ComparisonDirection.Neutral);
            }
            finally
            {
                PutAway(owner);
            }
        });
    }

    [Fact]
    public void APlanComparedAgainstItselfShowsNoRedAndNoGreen()
    {
        /* The sharpest edge of the old rendering. The text report calls an unchanged metric
           "(0.0% costlier)", because equality falls through to the worse branch of a two-way
           choice, and a window that coloured from the wording would paint a plan compared against
           itself entirely red. */
        HeadlessUi.Run(() =>
        {
            MainWindow? owner = null;
            try
            {
                owner = Open(PlanA, PlanA, out var comparison);

                var chips = ChipsByText(comparison);
                Assert.NotEmpty(chips);
                Assert.All(chips, chip => Assert.Equal(
                    ChipToken(owner, ComparisonDirection.Neutral), chip.Value.BorderBrush));

                Assert.All(StatementCards(comparison), card => Assert.Equal(
                    CardToken(owner, ComparisonDirection.Neutral), card.BorderBrush));
            }
            finally
            {
                PutAway(owner);
            }
        });
    }

    [Fact]
    public void APlanComparedAgainstItselfSaysNoChangeRatherThanCostlier()
    {
        /* The one place the window deliberately does not reprint the report's wording. The report
           says "(0.0% costlier)" for a metric that did not move and keeps saying it; putting that
           word on a chip would be repeating a claim the model itself calls Neutral. */
        HeadlessUi.Run(() =>
        {
            MainWindow? owner = null;
            try
            {
                owner = Open(PlanA, PlanA, out var comparison);

                var texts = AllText(comparison);

                Assert.Contains("no change", texts);
                Assert.DoesNotContain(texts, text => text.Contains("costlier"));

                // And the report itself has not budged.
                Assert.Contains("0.0% costlier",
                    ComparisonFormatter.Compare(Analyze(PlanA), Analyze(PlanA), PlanA, PlanA));
            }
            finally
            {
                PutAway(owner);
            }
        });
    }

    [Fact]
    public void TwoPlansWithNothingToCompareSaySoRatherThanShowingAnEmptyWindow()
    {
        HeadlessUi.Run(() =>
        {
            MainWindow? owner = null;
            try
            {
                var empty = new AnalysisResult { PlanSource = "test" };

                owner = new MainWindow();
                owner.Show();
                ComparisonWindow.Show(owner, empty, empty, "before", "after");
                Dispatcher.UIThread.RunJobs();

                var comparison = Assert.Single(owner.OwnedWindows);

                Assert.Empty(StatementCards(comparison));
                Assert.Contains("No statements to compare.", AllText(comparison));
            }
            finally
            {
                PutAway(owner);
            }
        });
    }

    [Fact]
    public void AStatementCardWearsItsOwnVerdictOnItsEdge()
    {
        HeadlessUi.Run(() =>
        {
            MainWindow? owner = null;
            try
            {
                owner = Open(PlanA, PlanB, out var comparison);
                var model = ComparisonFormatter.Build(Analyze(PlanA), Analyze(PlanB), PlanA, PlanB);

                var cards = StatementCards(comparison);

                Assert.Equal(model.Statements.Count, cards.Count);
                Assert.Equal(
                    model.Statements.Select(statement => CardToken(owner, statement.NetDirection)),
                    cards.Select(card => card.BorderBrush));
            }
            finally
            {
                PutAway(owner);
            }
        });
    }

    [Fact]
    public void TheHeaderNamesBothPlansAndCarriesTheVerdict()
    {
        HeadlessUi.Run(() =>
        {
            MainWindow? owner = null;
            try
            {
                owner = Open(PlanA, PlanB, out var comparison);
                var model = ComparisonFormatter.Build(Analyze(PlanA), Analyze(PlanB), PlanA, PlanB);

                var texts = AllText(comparison);

                Assert.Contains(PlanA, texts);
                Assert.Contains(PlanB, texts);

                Assert.NotNull(model.Verdict);
                Assert.Contains(model.Verdict.Text, texts);

                Assert.Contains("Copy report", ButtonCaptions(comparison));
            }
            finally
            {
                PutAway(owner);
            }
        });
    }

    [Fact]
    public void TheTextReportIsNotWhatIsOnScreen()
    {
        HeadlessUi.Run(() =>
        {
            MainWindow? owner = null;
            try
            {
                owner = Open(PlanA, PlanB, out var comparison);

                var texts = AllText(comparison);

                Assert.DoesNotContain(texts, text => text.Contains("=== Plan Comparison ==="));
                Assert.DoesNotContain(texts, text => text.Contains("--- Statement 1 ---"));
                Assert.DoesNotContain(texts, text => text.Contains(" -> "));

                /* The report itself is still one click away and still byte-identical — the copy
                   path goes through the same Compare() the MCP tool calls. */
                Assert.Contains("=== Plan Comparison ===",
                    ComparisonFormatter.Compare(Analyze(PlanA), Analyze(PlanB), PlanA, PlanB));
            }
            finally
            {
                PutAway(owner);
            }
        });
    }

    // --- Driving the window --------------------------------------------------------------------

    private static MainWindow Open(string planA, string planB, out Window comparison)
    {
        var owner = new MainWindow();
        owner.Show(); // the comparison window is owned, and an owner has to be showable

        ComparisonWindow.Show(owner, Analyze(planA), Analyze(planB), planA, planB);
        Dispatcher.UIThread.RunJobs();

        comparison = Assert.Single(owner.OwnedWindows);
        return owner;
    }

    private static void PutAway(MainWindow? owner)
    {
        if (owner == null) return;

        foreach (var owned in owner.OwnedWindows.ToList())
            owned.Close();

        owner.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // --- Reading the tree ----------------------------------------------------------------------

    /// <summary>
    /// The delta chips, keyed by what they say. A chip is a rounded border wrapping one text block;
    /// the only other control of that shape is the boxed A/B letter in the header, which no delta
    /// label can collide with.
    /// </summary>
    private static Dictionary<string, Border> ChipsByText(Window comparison) =>
        Descendants(comparison)
            .OfType<Border>()
            .Where(border => border.CornerRadius.TopLeft == 9 && border.Child is TextBlock)
            .GroupBy(border => ((TextBlock)border.Child!).Text ?? "")
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    /// <summary>The statement cards: the only borders with a heavy left edge and a hairline elsewhere.</summary>
    private static List<Border> StatementCards(Window comparison) =>
        Descendants(comparison)
            .OfType<Border>()
            .Where(border => border.BorderThickness is { Left: 3, Top: 1, Right: 1, Bottom: 1 })
            .ToList();

    private static List<string> AllText(Window comparison) =>
        Descendants(comparison)
            .Select(control => control switch
            {
                SelectableTextBlock selectable => selectable.Text,
                TextBlock text => text.Text,
                _ => null
            })
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToList();

    private static List<string> ButtonCaptions(Window comparison) =>
        Descendants(comparison)
            .OfType<Button>()
            .Select(button => button.Content as string)
            .Where(caption => caption != null)
            .Select(caption => caption!)
            .ToList();

    /// <summary>
    /// The logical tree rather than the visual one: this window's content is built by hand and set
    /// straight onto the window, so it is all there before anything is templated or laid out.
    /// </summary>
    private static IEnumerable<Control> Descendants(Window comparison) =>
        comparison.GetLogicalDescendants().OfType<Control>();

    /// <summary>
    /// The chip's outline, which is also its text: muted rather than BorderBrush for Neutral,
    /// because a line colour at 1.4:1 on the card surface is a pill nobody can see.
    /// </summary>
    private static IBrush ChipToken(Window owner, ComparisonDirection direction) =>
        Token(owner, direction, neutral: "ForegroundMutedBrush");

    /// <summary>The card's accent edge, where BorderBrush is the right "no verdict".</summary>
    private static IBrush CardToken(Window owner, ComparisonDirection direction) =>
        Token(owner, direction, neutral: "BorderBrush");

    private static IBrush Token(Window owner, ComparisonDirection direction, string neutral) =>
        (IBrush)owner.FindResource(direction switch
        {
            ComparisonDirection.Better => "SuccessBrush",
            ComparisonDirection.Worse => "ErrorBrush",
            _ => neutral
        })!;

    private static AnalysisResult Analyze(string planFile) =>
        ResultMapper.Map(
            ShowPlanParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", planFile))
                .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"")),
            planFile);
}
