using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PlanViewer.App.Helpers;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #448: a failed query reported an error that was cut off, three separate times over — truncated to
/// 100 characters in code, then clipped by a label with no wrapping, inside a panel fixed at 300px.
///
/// <para>#625: the same panel had been copied three times, and #448 fixed two of the copies. The
/// third, behind Run Repro on a plan opened from a file, kept a plain one-line label, so its errors
/// were still cut off at the edge of the panel. Every capture path now builds
/// <see cref="CaptureProgressPanel"/>, so these tests build the panel the app shows, not a copy
/// of it that can drift the way the app's copies did.</para>
///
/// <para>These were the first tests in this suite to construct real Avalonia controls. That is the
/// point: #447 and #448 were both genuine bugs that no test could reach, because every test here
/// worked on Core models and the defects were in the UI. See <see cref="HeadlessUi"/> for why the
/// session is hand-rolled.</para>
/// </summary>
public class ExecutionFailureDisplayTests
{
    /// <summary>
    /// The #625 error and the one SQL Server sends after it when a batch cannot compile, then a
    /// long object name. Longer than the old 100-character ceiling, and far wider than the panel.
    /// </summary>
    private const string LongSqlError =
        "Must declare the table variable \"@Exceptions\". Statement(s) could not be prepared. " +
        "Invalid object name 'dbo.ThisTableNameIsDeliberatelyLongSoTheMessageRunsWellPastOneLine'.";

    [Fact]
    public void TheWholeErrorIsShown()
    {
        HeadlessUi.Run(() =>
        {
            var progress = BuildPanel();

            progress.ShowFailure(LongSqlError);

            Assert.Equal(LongSqlError, progress.StatusLabel.Text);
            Assert.True(LongSqlError.Length > 100, "the fixture must exceed the ceiling it is pinning");
            Assert.DoesNotContain("...", progress.StatusLabel.Text!, System.StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Showing the whole string is not enough on its own — without wrapping it is clipped by the
    /// panel instead of by the substring, which looks identical to the user. Both halves of #448,
    /// and the whole of #625: its label was built without wrapping, and a failure cannot fix that
    /// after the fact.
    /// </summary>
    [Fact]
    public void TheStatusLineIsBuiltToWrap()
    {
        HeadlessUi.Run(() =>
        {
            var progress = BuildPanel();

            Assert.Equal(TextWrapping.Wrap, progress.StatusLabel.TextWrapping);
        });
    }

    /// <summary>
    /// What the user sees, not the property that should produce it: laid out in a window, the
    /// error takes more than one line, and no line runs past the edge of the label.
    /// </summary>
    [Fact]
    public void TheErrorFitsInsideThePanel()
    {
        HeadlessUi.Run(() =>
        {
            var progress = BuildPanel();
            var window = new Window { Content = progress.Root, Width = 1200, Height = 800 };
            try
            {
                window.Show();
                progress.ShowFailure(LongSqlError);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var label = progress.StatusLabel;
                var lines = label.TextLayout.TextLines;

                Assert.True(lines.Count > 1, "an error this long has to wrap to fit the panel");
                Assert.All(lines, line => Assert.True(line.Width <= label.Bounds.Width + 0.5,
                    $"a {line.Width:F0}px line in a {label.Bounds.Width:F0}px label is cut off"));
                Assert.True(progress.Panel.Bounds.Width <= progress.Panel.MaxWidth);
            }
            finally
            {
                window.Close();
            }
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
            var progress = BuildPanel();
            Assert.Equal(300, progress.Panel.Width);

            progress.ShowFailure(LongSqlError);

            Assert.True(double.IsNaN(progress.Panel.Width), "a fixed width would still clip the message");
            Assert.True(progress.Panel.MaxWidth is > 300 and < double.PositiveInfinity,
                "unbounded would let a long error run the width of the window");
        });
    }

    /// <summary>The spinner and Cancel button belong to a running query, not a failed one.</summary>
    [Fact]
    public void TheProgressAffordancesGoAway()
    {
        HeadlessUi.Run(() =>
        {
            var progress = BuildPanel();

            progress.ShowFailure(LongSqlError);

            Assert.False(progress.ProgressBar.IsVisible);
            Assert.False(progress.CancelButton.IsVisible);
        });
    }

    /// <summary>
    /// A run that ends with no plan, or is cancelled, is over too, so the spinner and Cancel go.
    /// But it did not fail, so it is not painted as an error.
    /// </summary>
    [Fact]
    public void AnOutcomeEndsTheRunWithoutCallingItAnError()
    {
        HeadlessUi.Run(() =>
        {
            var progress = BuildPanel();
            var running = progress.StatusLabel.Foreground;

            progress.ShowOutcome("No actual plan returned (0.4s).");

            Assert.Equal("No actual plan returned (0.4s).", progress.StatusLabel.Text);
            Assert.False(progress.ProgressBar.IsVisible);
            Assert.False(progress.CancelButton.IsVisible);
            Assert.Same(running, progress.StatusLabel.Foreground);

            progress.ShowFailure(LongSqlError);

            Assert.NotSame(running, progress.StatusLabel.Foreground);
        });
    }

    /// <summary>
    /// A SQL error is the string in this app a user most needs to paste somewhere else, and the
    /// label it lands in used to be a plain TextBlock.
    ///
    /// <para>Selectable from its whitespace, too. With no background, a SelectableTextBlock
    /// hit-tests only its glyphs, so a drag that starts beside a short line or between two wrapped
    /// lines selects nothing. This headless session has no Skia, so a drag here would prove
    /// nothing; the background is the cause, so the background is what is pinned.</para>
    /// </summary>
    [Fact]
    public void TheErrorCanBeSelected()
    {
        HeadlessUi.Run(() =>
        {
            var progress = BuildPanel();
            Assert.IsAssignableFrom<SelectableTextBlock>(progress.StatusLabel);
            Assert.NotNull(progress.StatusLabel.Background);
        });
    }

    /// <summary>
    /// A failure can land while its tab is in the background, and a label in a background tab is
    /// out of the tree. A colour looked up off the label at that moment found nothing and fell back
    /// to a literal. The panel here is never attached to anything, which is that same state.
    /// </summary>
    [Fact]
    public void TheErrorTakesTheThemeColourWhileOutOfTheTree()
    {
        HeadlessUi.Run(() =>
        {
            var progress = BuildPanel();

            progress.ShowFailure(LongSqlError);

            Assert.True(Application.Current!.TryFindResource("ErrorBrush", out var themed));
            Assert.Same(themed, progress.StatusLabel.Foreground);
        });
    }

    /// <summary>The Cancel button and Escape both stop the run the panel was built for.</summary>
    [Fact]
    public void CancelAndEscapeStopTheRun()
    {
        HeadlessUi.Run(() =>
        {
            using var clicked = new CancellationTokenSource();
            BuildPanel(clicked).CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(clicked.IsCancellationRequested, "Cancel did not cancel the run");

            using var escaped = new CancellationTokenSource();
            BuildPanel(escaped).Root.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape
            });
            Assert.True(escaped.IsCancellationRequested, "Escape did not cancel the run");
        });
    }

    private static CaptureProgressPanel BuildPanel(CancellationTokenSource? run = null) =>
        new(Application.Current!, "Capturing actual plan...", run ?? new CancellationTokenSource());
}
