using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace PlanViewer.App.Helpers;

/// <summary>
/// What a tab shows while a query runs to capture its plan: a progress bar, a status line and a
/// Cancel button. If the run fails, the status line is where the error is reported.
///
/// <para>Every capture path builds it here: the query session's Execute and Get Actual Plan, and
/// Run Repro on a plan opened from a file. Those used to be three hand-copied panels. #448 fixed the
/// two in the query session and the third kept its old label, so a SQL error from Run Repro on a
/// plan file was still cut off at the edge of the panel (#625). With one builder, a fix to how a
/// failure reads reaches every path.</para>
/// </summary>
internal sealed class CaptureProgressPanel
{
    /* The colours the theme holds today, as literals, so a key missing from the dictionary renders
       something sane rather than nothing. The same values QuerySessionControl falls back to. */
    private static readonly IBrush FallbackForeground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB));
    private static readonly IBrush FallbackBackground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x23));
    private static readonly IBrush FallbackError = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));

    /* Looked up when the panel is built, off the host, which is in the tree. The label is not in
       the tree while its tab is in the background, and a failure can land then: a lookup off the
       label at that moment finds nothing and falls back to the literal. */
    private readonly IBrush _errorBrush;

    /// <summary>The tab content. Fills the tab, and takes Escape as a cancel of this run.</summary>
    public Grid Root { get; }

    /// <summary>The centred column that holds the progress bar, the status line and Cancel.</summary>
    public StackPanel Panel { get; }

    public ProgressBar ProgressBar { get; }

    /// <summary>
    /// The run's status, and its error if it fails. Selectable, because a SQL error is the one
    /// string in this app a user most needs to copy somewhere else. Wrapping, because unwrapped it
    /// was clipped by the panel (#448).
    /// </summary>
    public SelectableTextBlock StatusLabel { get; }

    public Button CancelButton { get; }

    /// <summary>
    /// Whether the run behind this panel is still going: true from construction until
    /// <see cref="ShowOutcome"/>, the one call every ending without a plan goes through (a failure,
    /// "no plan returned", a cancel the owner reports on the panel). A run that produces its plan
    /// never gets there, because its owner swaps the panel out of the tab instead.
    ///
    /// <para>#627: the window asks this before it detaches a tab, because a run that finishes into a
    /// tab nobody is looking at is a plan nobody can see. A cancel that has been requested but not yet
    /// acted on still reads as running, which is right: the owner is about to close the tab, and the
    /// tab has to still be in the strip when it does.</para>
    /// </summary>
    public bool IsRunning { get; private set; } = true;

    /// <summary>
    /// The panel that <paramref name="content"/> belongs to, or null when the content is something
    /// else. A tab holds only the <see cref="Root"/> grid, so the window has no other way back to the
    /// panel it was built around; <see cref="Root"/> carries the panel in its Tag for that.
    /// </summary>
    public static CaptureProgressPanel? From(Control? content) => content?.Tag as CaptureProgressPanel;

    /// <param name="resources">Where the theme brushes and the AppButton theme are looked up.</param>
    /// <param name="status">What the status line says when the run starts.</param>
    /// <param name="run">The run that the Cancel button and Escape cancel.</param>
    public CaptureProgressPanel(IResourceHost resources, string status, CancellationTokenSource run)
    {
        _errorBrush = Token(resources, "ErrorBrush", FallbackError);

        ProgressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            Margin = new Thickness(0, 0, 0, 12)
        };
        // This panel lives in tab content the user can switch away from mid-capture.
        ProgressBarBehaviors.SetRestartOnReattach(ProgressBar, true);

        StatusLabel = new SelectableTextBlock
        {
            Text = status,
            FontSize = 14,
            Foreground = Token(resources, "ForegroundBrush", FallbackForeground),
            /* A hit-test surface. With no background, selectable text answers a press only on its
               glyphs, so a drag that starts beside a short line, or between two wrapped lines,
               selects nothing. That is where a drag across a wrapped error usually starts. */
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        CancelButton = new Button
        {
            Content = AppIcons.MakeContent(AppIcons.Stop, "Cancel"),
            Height = 32,
            Width = 120,
            Padding = new Thickness(16, 0),
            FontSize = 13,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = resources.FindResource("AppButton") as ControlTheme
        };
        CancelButton.Click += (_, _) => run.Cancel();

        Panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 300,
            Children = { ProgressBar, StatusLabel, CancelButton }
        };

        Root = new Grid
        {
            Background = Token(resources, "BackgroundBrush", FallbackBackground),
            Focusable = true,
            Children = { Panel },
            Tag = this // see From
        };
        /* This run's own source, not whatever run its host thinks is current. The handler outlives
           the run: a failed capture leaves this panel on screen, still focusable, and Escape pressed
           on it later must not reach past it and cancel a newer run. */
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { run.Cancel(); e.Handled = true; }
        };
    }

    /// <summary>
    /// The run is over and there is no plan to show: says why, and removes the progress bar and
    /// the Cancel button, which belong to a run that is still going.
    /// </summary>
    public void ShowOutcome(string text)
    {
        IsRunning = false;
        StatusLabel.Text = text;
        ProgressBar.IsVisible = false;
        CancelButton.IsVisible = false;
    }

    /// <summary>
    /// Reports a query failure, in full (#448).
    ///
    /// <para>It used to be cut to 100 characters with an ellipsis, in a panel fixed at 300px wide
    /// holding a non-wrapping label — three separate reasons the same message got clipped, and
    /// between them a SQL error was routinely unreadable. 100 characters does not even reach the end
    /// of "Msg 208, Level 16, State 1, Procedure X, Line N" before the sentence naming the actual
    /// problem starts.</para>
    ///
    /// <para>The panel is sized for a spinner and a Cancel button, which is why it is narrow; on
    /// failure it is re-sized for prose. MaxWidth rather than Width, so a short error stays compact
    /// and a long one is bounded at a readable measure instead of running the width of the window.</para>
    /// </summary>
    public void ShowFailure(string message)
    {
        Panel.Width = double.NaN;
        Panel.MaxWidth = 640;

        ShowOutcome(message);
        StatusLabel.Foreground = _errorBrush;
    }

    /// <summary>
    /// A theme brush by key, or <paramref name="fallback"/> when the key is not in the dictionary.
    /// </summary>
    private static IBrush Token(IResourceHost host, string key, IBrush fallback) =>
        host.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;
}
