using Avalonia.Controls;
using Avalonia.Media;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #448: a failed query reported an error that was cut off, three separate times over — truncated to
/// 100 characters in code, then clipped by a label with no wrapping, inside a panel fixed at 300px.
///
/// These are the first tests in this suite to construct real Avalonia controls. That is the point:
/// #447 and #448 were both genuine bugs that no test could reach, because every test here worked on
/// Core models and the defects were in the UI. See <see cref="HeadlessUi"/> for why the session is
/// hand-rolled.
/// </summary>
public class ExecutionFailureDisplayTests
{
    /// <summary>A real SQL error, longer than the old ceiling. Msg 208 with a long object name.</summary>
    private const string LongSqlError =
        "Invalid object name 'dbo.ThisTableNameIsDeliberatelyVeryLongIndeedSoThatTheResultingErrorMessageComfortablyExceedsOneHundredCharacters'.";

    [Fact]
    public void TheWholeErrorIsShown()
    {
        HeadlessUi.Run(() =>
        {
            var (panel, label, progress, cancel) = BuildLoadingPanel();

            QuerySessionControl.ShowExecutionFailure(panel, label, progress, cancel, LongSqlError);

            Assert.Equal(LongSqlError, label.Text);
            Assert.True(LongSqlError.Length > 100, "the fixture must exceed the ceiling it is pinning");
            Assert.DoesNotContain("...", label.Text!, System.StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Showing the whole string is not enough on its own — without wrapping it is clipped by the
    /// panel instead of by the substring, which looks identical to the user. Both halves of #448.
    /// </summary>
    [Fact]
    public void TheErrorWrapsInsteadOfBeingClipped()
    {
        HeadlessUi.Run(() =>
        {
            var (panel, label, progress, cancel) = BuildLoadingPanel();

            QuerySessionControl.ShowExecutionFailure(panel, label, progress, cancel, LongSqlError);

            Assert.Equal(TextWrapping.Wrap, label.TextWrapping);
        });
    }

    /// <summary>
    /// The panel is sized for a spinner and a Cancel button. An error needs room, but bounded room —
    /// MaxWidth rather than Width, so a short error stays compact and a long one does not run the
    /// full width of the window.
    /// </summary>
    [Fact]
    public void ThePanelStopsBeingSpinnerSizedOnFailure()
    {
        HeadlessUi.Run(() =>
        {
            var (panel, label, progress, cancel) = BuildLoadingPanel();
            Assert.Equal(300, panel.Width);

            QuerySessionControl.ShowExecutionFailure(panel, label, progress, cancel, LongSqlError);

            Assert.True(double.IsNaN(panel.Width), "a fixed width would still clip the message");
            Assert.True(panel.MaxWidth is > 300 and < double.PositiveInfinity,
                "unbounded would let a long error run the width of the window");
        });
    }

    /// <summary>The spinner and Cancel button belong to a running query, not a failed one.</summary>
    [Fact]
    public void TheProgressAffordancesGoAway()
    {
        HeadlessUi.Run(() =>
        {
            var (panel, label, progress, cancel) = BuildLoadingPanel();

            QuerySessionControl.ShowExecutionFailure(panel, label, progress, cancel, LongSqlError);

            Assert.False(progress.IsVisible);
            Assert.False(cancel.IsVisible);
        });
    }

    /// <summary>
    /// A SQL error is the string in this app a user most needs to paste somewhere else, and the
    /// label it lands in used to be a plain TextBlock.
    /// </summary>
    [Fact]
    public void TheErrorCanBeSelected()
    {
        HeadlessUi.Run(() =>
        {
            var (_, label, _, _) = BuildLoadingPanel();
            Assert.IsAssignableFrom<SelectableTextBlock>(label);
        });
    }

    /// <summary>Mirrors how QuerySessionControl builds the loading tab, including the 300px width.</summary>
    private static (StackPanel Panel, SelectableTextBlock Label, ProgressBar Progress, Button Cancel) BuildLoadingPanel()
    {
        var label = new SelectableTextBlock { Text = "Capturing actual plan...", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { IsIndeterminate = true, IsVisible = true };
        var cancel = new Button { Content = "Cancel", IsVisible = true };
        var panel = new StackPanel { Width = 300, Children = { progress, label, cancel } };
        return (panel, label, progress, cancel);
    }
}
