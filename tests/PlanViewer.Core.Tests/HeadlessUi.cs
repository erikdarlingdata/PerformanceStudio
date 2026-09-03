using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A headless Avalonia session, so UI code can be tested without a display.
///
/// <para><b>Why this is hand-rolled rather than Avalonia.Headless.XUnit.</b> That package exists and
/// would be less code, but at 11.3.20 it depends on <c>xunit.core 2.4.0</c> — xunit v2 — and this
/// suite runs on xunit.v3. Putting two xunit frameworks in one test project to get an attribute is a
/// worse trade than owning fifteen lines. <see cref="HeadlessUnitTestSession"/> is runner-agnostic
/// and is what that package wraps anyway.</para>
///
/// <para><b>The real App, not a stub.</b> A bare Application looked tidier but does not load the
/// application XAML, and MainWindow's toolbars resolve styles from it — FindResource("AppButton")
/// throws without them, which surfaces as an unrelated-looking "Failed to open" dialog. App.Initialize
/// loads those resources, and its OnFrameworkInitializationCompleted only creates a window under a
/// classic desktop lifetime, which a headless session is not, so nothing is spawned behind the
/// tests.</para>
///
/// <para><b>One session for the whole assembly.</b> The session is created on first use and never
/// disposed: the process is about to exit, and tearing an Avalonia session down while another test
/// class may still be queued is a good way to reintroduce the kind of test-host wedge #441 was
/// about. The session itself runs at <c>PerTest</c> isolation, which is not the same thing as one
/// Application: each <see cref="Dispatch"/> enters a fresh <c>AvaloniaLocator</c> scope, builds a
/// fresh <c>Application</c> in it, and tears both down afterwards. What is shared assembly-wide is
/// the dispatcher thread and the loop feeding it — which is exactly enough to be poisoned once and
/// stay poisoned, as #474 was.</para>
/// </summary>
internal static class HeadlessUi
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(PlanViewer.App.App)),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Where this run's redirected settings live, for the tests that pin the redirection.
    /// </summary>
    internal static string SettingsRedirectRoot { get; private set; } = string.Empty;

    /// <summary>
    /// Flips the process into test-host mode before anything else in this assembly runs.
    ///
    /// <para><b>Why booting the real App needs this at all (#451).</b> The session above
    /// constructs the actual <see cref="PlanViewer.App.App"/>, so its real startup side
    /// effects ran inside the test host on every local <c>dotnet test</c>: the .sqlplan
    /// registry association was rewritten to point at the test runner, tests restored and
    /// then destroyed the developer's saved open-tab list, and fixture paths evicted real
    /// Recent Plans entries — all confirmed live. <see cref="AppRuntimeMode.IsTestHost"/>
    /// is the one seam the app consults to keep the process-external effects (file
    /// association, pipe server, update check, MCP server) from launching here.</para>
    ///
    /// <para><b>Why a module initializer rather than harness setup.</b> It has to run
    /// before the first App boot AND before any test touches
    /// <see cref="AppSettingsService"/>, including ones that never go through this class.
    /// A module initializer is the only spot guaranteed to precede both.</para>
    ///
    /// <para><b>Why the settings directory is per RUN, not per test.</b> Tests like
    /// RestoreQueryTabsTests deliberately exercise save/load continuity across windows
    /// within a run; tests that need clean state already reset it themselves. A second
    /// <c>dotnet test</c> gets a fresh directory, which is what keeps runs from leaking
    /// into each other. The abandoned directories are a few hundred bytes each and left
    /// to OS temp cleanup — sweeping siblings here could race a concurrent run.</para>
    /// </summary>
    [ModuleInitializer]
    internal static void EnterTestHostMode()
    {
        AppRuntimeMode.IsTestHost = true;

        SettingsRedirectRoot = Directory.CreateTempSubdirectory("PlanViewer.Core.Tests-").FullName;
        AppSettingsService.RedirectStorageForTestHost(SettingsRedirectRoot);
    }

    /// <summary>
    /// Runs <paramref name="body"/> on the Avalonia UI thread and rethrows anything it threw, so an
    /// assertion failure inside surfaces as a test failure rather than a swallowed task.
    /// </summary>
    internal static void Run(Action body)
    {
        var failure = Dispatch(body);
        var sessionBroken = Dispatch(EnsureSessionSurvived);

        /* The body's own failure wins. A test that both failed its assertion and left the session
           unusable is still described best by the assertion it failed. */
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (sessionBroken is not null)
        {
            ExceptionDispatchInfo.Capture(sessionBroken).Throw();
        }
    }

    /// <summary>
    /// Runs one body on the UI thread and hands back whatever it threw rather than throwing here,
    /// so <see cref="Run"/> can decide which of two failures to report.
    ///
    /// <para><b>Why the queue is drained before returning (#474).</b> Avalonia's per-dispatch
    /// teardown disposes the session's <c>FontManager</c> and only then calls
    /// <c>Dispatcher.ResetForUnitTests</c>, which executes whatever is still queued. A window whose
    /// content is involved enough to leave a deferred render pass behind — a
    /// <c>PlanViewerControl</c> reliably does, a TextBlock does not — therefore renders text against
    /// a font manager that has just been disposed, throws <c>KeyNotFoundException</c> for
    /// <c>fonts:SystemFonts</c>, and that exception escapes the teardown delegate before it reaches
    /// <c>scope.Dispose()</c>. The locator scope is then never popped: every later dispatch nests
    /// inside the leaked one, resolves the disposed font manager through its parent chain, and dies
    /// constructing any <see cref="Window"/> at all. The guilty test passes, because the throw
    /// happens after its result has been recorded.</para>
    ///
    /// <para>Draining here leaves the teardown nothing to run, which is the whole fix. It is done
    /// even when the body failed, because a failing test is no less capable of poisoning the
    /// session than a passing one.</para>
    /// </summary>
    private static Exception? Dispatch(Action body)
    {
        Exception? failure = null;

        Session.Value.Dispatch(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                Dispatcher.UIThread.RunJobs();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            return Task.CompletedTask;
        }, default).GetAwaiter().GetResult();

        return failure;
    }

    /// <summary>
    /// Checks that the session a test just used is still usable by the next one, so a test that
    /// breaks it fails saying so instead of leaving a trail of unrelated red.
    ///
    /// <para>Constructing a <see cref="Window"/> is the check because it is the symptom: a window
    /// builds a compositor, which asks the font manager for a typeface before it does anything
    /// else. Nothing is shown and nothing is laid out, so this costs one object.</para>
    /// </summary>
    private static void EnsureSessionSurvived()
    {
        try
        {
            _ = new Window();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "This test left the shared Avalonia session unusable — a bare Window can no longer " +
                "be constructed, so every UI test that runs after it will fail too, on something " +
                "that is not their fault. See #474.",
                ex);
        }
    }
}
