using Avalonia.Controls;
using Avalonia.Controls.Documents;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #503: the advice pane put every line in its own <see cref="SelectableTextBlock"/>, and Avalonia
/// gives each one its own selection with nothing coordinating a drag between them. So a selection
/// could never be longer than a single line — the reporter could grab a one-line query but not
/// Server Context, Parameters, or Missing indexes, which are several lines each. Copying everything
/// into Notepad was the only way to get a piece of it.
///
/// <para>These assert on selection rather than on the control tree, because "one block per section"
/// is the mechanism and "a drag covers more than one line" is the thing that was broken.</para>
/// </summary>
public class AdviceSelectionTests
{
    private const string ServerContext =
        "=== Server Context ===\n"
        + "SQL Server: 16.0.1000\n"
        + "Edition: Developer\n"
        + "Cores: 8\n"
        + "Max server memory: 32768 MB\n";

    [Fact]
    public void ASectionsBodyIsOneSelectionCoveringEveryLineInIt()
    {
        HeadlessUi.Run(() =>
        {
            var body = BodyBlocks(ServerContext).Single();

            body.SelectAll();

            // The whole point: one drag reaches the first value and the last one.
            Assert.Contains("16.0.1000", body.SelectedText);
            Assert.Contains("Developer", body.SelectedText);
            Assert.Contains("32768 MB", body.SelectedText);
        });
    }

    [Fact]
    public void ABlankLineInsideASectionDoesNotSplitTheSelection()
    {
        HeadlessUi.Run(() =>
        {
            // A blank line is a paragraph break in the text, not a section break, so a drag has to
            // run straight through it.
            var body = BodyBlocks("=== Server Context ===\nEdition: Developer\n\nCores: 8\n").Single();

            body.SelectAll();

            Assert.Contains("Developer", body.SelectedText);
            Assert.Contains("Cores", body.SelectedText);
        });
    }

    [Fact]
    public void AMultiLineQueryIsOneSelection()
    {
        HeadlessUi.Run(() =>
        {
            var body = BodyBlocks(
                "=== Statement 1 ===\nSELECT p.Id\nFROM dbo.Posts AS p\nWHERE p.Score > 10;\n").Single();

            body.SelectAll();

            Assert.Contains("SELECT", body.SelectedText);
            Assert.Contains("FROM", body.SelectedText);
            Assert.Contains("WHERE", body.SelectedText);
        });
    }

    /// <summary>
    /// Warning blocks are Bordered cards, not text runs, so they cannot join a text block. They still
    /// break the body either side of them — that is the ceiling on what merging can do here, and it
    /// is worth pinning so a later change does not quietly assume the whole pane is one selection.
    /// </summary>
    [Fact]
    public void AWarningCardStillStandsOnItsOwnAndSplitsTheBodyEitherSide()
    {
        HeadlessUi.Run(() =>
        {
            // Deliberately not a "Statement" header: that sets isStatementText, and every line after
            // it takes the SQL path without ever reaching the severity checks.
            var panel = Build(
                "=== Warnings ===\nEdition: Developer\n[Critical] Something is wrong\nCores: 8\n");

            Assert.Single(panel.Children.OfType<Border>());
            Assert.Equal(2, BodyBlocks(panel).Count);
        });
    }

    /// <summary>
    /// A helper that builds a line expresses its indent as a Margin, and a Margin is precisely what
    /// is lost when the runs move into a shared block. The first cut of this dropped the SQL indent
    /// that way, so the indent is pinned here rather than trusted.
    /// </summary>
    [Fact]
    public void StatementSqlKeepsItsIndentAfterMerging()
    {
        HeadlessUi.Run(() =>
        {
            var body = BodyBlocks("=== Statement 1 ===\nSELECT p.Id\nFROM dbo.Posts AS p\n").Single();

            body.SelectAll();

            // 8px of Margin becomes a leading space at the pane's monospace body size.
            Assert.StartsWith(" SELECT", body.SelectedText);
        });
    }

    [Fact]
    public void TheSectionHeaderIsStillItsOwnBlock()
    {
        HeadlessUi.Run(() =>
        {
            var panel = Build(ServerContext);

            // Header block + one body block. The header keeps its own size and weight, which a Run
            // inside the body could carry but which would let a drag start mid-title.
            Assert.Equal(2, panel.Children.OfType<SelectableTextBlock>().Count());
            Assert.Equal("Server Context", FirstBlock(panel).Text);
        });
    }

    /// <summary>
    /// Merging lines put LineBreaks between them, and a LineBreak occupies a position in the
    /// laid-out text while exposing no Text of its own. The Node-link hit test walked Runs only, so
    /// every offset past the first line slid backwards and a "Node N" on a later line resolved to
    /// the wrong run. Checks the arithmetic directly rather than through a laid-out click, which
    /// would be testing the font.
    /// </summary>
    [Fact]
    public void NodeLinkHitTestingSurvivesTheLineBreaksBetweenMergedLines()
    {
        HeadlessUi.Run(() =>
        {
            var link = new Run("Node 42")
            {
                TextDecorations = Avalonia.Media.TextDecorations.Underline
            };
            var block = new SelectableTextBlock();
            block.Inlines!.Add(new Run("first line, Node 1"));
            block.Inlines.Add(new LineBreak());
            block.Inlines.Add(new Run("second line, "));
            block.Inlines.Add(link);

            // The index the layout would report for the link's own text.
            var index = block.Inlines.Text!.IndexOf("Node 42", System.StringComparison.Ordinal);
            Assert.True(index > 0, "fixture should place the link after the break");

            Assert.Same(link, AdviceContentBuilder.RunAtCharIndex(block, index));
            Assert.Same(link, AdviceContentBuilder.RunAtCharIndex(block, index + 3));

            // And the run before the break still resolves to itself.
            Assert.Equal("first line, Node 1", AdviceContentBuilder.RunAtCharIndex(block, 0)!.Text);
        });
    }

    /// <summary>
    /// Two runs can carry the same text — the same node referenced on two lines. Locating each run by
    /// searching forward from the previous one has to land on the second occurrence for the second
    /// run, not re-find the first.
    /// </summary>
    [Fact]
    public void RepeatedRunTextResolvesToTheRightOccurrence()
    {
        HeadlessUi.Run(() =>
        {
            var first = new Run("Node 4");
            var second = new Run("Node 4");
            var block = new SelectableTextBlock();
            block.Inlines!.Add(first);
            block.Inlines.Add(new LineBreak());
            block.Inlines.Add(second);

            var secondIndex = block.Inlines.Text!.LastIndexOf("Node 4", System.StringComparison.Ordinal);

            Assert.Same(first, AdviceContentBuilder.RunAtCharIndex(block, 0));
            Assert.Same(second, AdviceContentBuilder.RunAtCharIndex(block, secondIndex));
        });
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static StackPanel Build(string content)
    {
        var panel = AdviceContentBuilder.Build(content);

        // Selection needs a laid-out control attached to a TopLevel.
        var window = new Window { Content = panel, Width = 1000, Height = 700 };
        window.Show();
        window.UpdateLayout();

        return panel;
    }

    private static SelectableTextBlock FirstBlock(StackPanel panel) =>
        panel.Children.OfType<SelectableTextBlock>().First();

    /// <summary>The body blocks: every selectable block except the section header.</summary>
    private static System.Collections.Generic.List<SelectableTextBlock> BodyBlocks(StackPanel panel) =>
        panel.Children.OfType<SelectableTextBlock>().Skip(1).ToList();

    private static System.Collections.Generic.List<SelectableTextBlock> BodyBlocks(string content) =>
        BodyBlocks(Build(content));
}
