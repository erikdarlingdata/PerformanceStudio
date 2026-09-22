using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PlanViewer.App.Helpers;

/// <summary>
/// Restarts an indeterminate <see cref="ProgressBar"/>'s animation when it re-enters the
/// visual tree.
///
/// <para>Avalonia 12 completes a style-applied animation when its visual detaches — a handler
/// its changelog does not document, sitting outside the <c>PlaybackBehavior</c> pause guard —
/// and re-attaching re-applies the style without resurrecting the finished animation. Switch
/// tabs while a fetch is running and its loading bar comes back frozen mid-track, indistinguishable
/// from a hang. Avalonia 11 had no detach handling at all, so the bar just kept animating; this
/// restores that behavior at the seven bars whose hosts can detach mid-run.</para>
///
/// <para>The restart works by deactivating and reactivating the <c>:indeterminate</c> style:
/// flipping <see cref="ProgressBar.IsIndeterminate"/> off and, one dispatcher hop later, back on
/// makes the style system spawn a fresh animation instance. The hop is load-bearing — both flips
/// in the same pass coalesce into no style change at all.</para>
///
/// <para>Verified against the live symptom, not assumed: before this, five window captures
/// spanning a full animation cycle after a tab round-trip were pixel-identical in the bar's
/// region; with it, the sweep resumes. The headless suite cannot see any of this (animations
/// need frames), which is why the proof lives in a driven run rather than a test.</para>
/// </summary>
public static class ProgressBarBehaviors
{
    /// <summary>
    /// Set true on any indeterminate bar whose host can detach while it runs — tab content,
    /// overlays inside documents, detachable panels.
    /// </summary>
    public static readonly AttachedProperty<bool> RestartOnReattachProperty =
        AvaloniaProperty.RegisterAttached<ProgressBar, bool>(
            "RestartOnReattach", typeof(ProgressBarBehaviors));

    public static void SetRestartOnReattach(ProgressBar bar, bool value) =>
        bar.SetValue(RestartOnReattachProperty, value);

    public static bool GetRestartOnReattach(ProgressBar bar) =>
        bar.GetValue(RestartOnReattachProperty);

    static ProgressBarBehaviors()
    {
        RestartOnReattachProperty.Changed.AddClassHandler<ProgressBar>((bar, e) =>
        {
            if (e.NewValue is true)
                bar.AttachedToVisualTree += Restart;
            else
                bar.AttachedToVisualTree -= Restart;
        });
    }

    private static void Restart(object? sender, VisualTreeAttachmentEventArgs e)
    {
        /* An idle bar (Overview's LoadingBar between loads) has nothing to restart, and
           flipping it would wrongly start one. */
        if (sender is not ProgressBar bar || !bar.IsIndeterminate)
            return;

        bar.IsIndeterminate = false;
        Dispatcher.UIThread.Post(() => bar.IsIndeterminate = true, DispatcherPriority.Loaded);
    }
}
