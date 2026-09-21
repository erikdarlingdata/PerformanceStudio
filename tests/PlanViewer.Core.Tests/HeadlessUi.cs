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
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A headless Avalonia session, so UI code can be tested without a display.
///
/// <para><b>Why this is hand-rolled rather than Avalonia.Headless.XUnit.</b> The original objection
/// has expired and the conclusion has not. That package used to depend on <c>xunit.core 2.4.0</c> —
/// xunit v2 — against a suite running xunit.v3, so adopting it meant two xunit frameworks in one
/// project. At 12.1.2 it asks for <c>xunit.v3.extensibility.core 3.2.2</c>, which unifies upward
/// against this project's own 4.0.1, so it is now merely possible. It is still the wrong trade:
/// <see cref="HeadlessUnitTestSession"/> is runner-agnostic and is what that package wraps anyway,
/// while <c>[AvaloniaFact]</c> offers neither of the two behaviours this class exists for — the #474
/// queue drain and the <see cref="EnsureSessionSurvived"/> canary, which is the only reason a
/// session-poisoning test fails with its own name on it. Rewriting every entry point to lose both
/// would be a migration dressed as a simplification.</para>
///
/// <para><b>Inter ships here and is never used.</b> Avalonia.Headless 12.1.2 pulls
/// <c>Avalonia.Fonts.Inter</c> and <c>Avalonia.HarfBuzz</c> in transitively, so the Inter assembly
/// sits in the test output directory. Nothing registers it: <c>.WithInterFont()</c> lives on
/// <c>Program.BuildAvaloniaApp</c>, and the session below boots <c>App</c>, which has no such
/// method, so the builder never runs it. Text in this suite is measured against headless's own
/// embedded font, not Inter — see the metrics paragraph below.</para>
///
/// <para><b>What text measures, and why the layout numbers in this suite moved.</b> Stated once
/// here because several files pin measured widths and heights, and a number repeated with its
/// reason in every file is a number that goes stale in some of them. Headless 11 bound a stub text
/// shaper that gave every character a flat 10 DIP advance at any font size, over synthetic font
/// metrics that worked out to a line height of 0.8 em. Headless 12 drops the stub and shapes for
/// real through HarfBuzz against its own embedded BareMinimum font, which has no glyph for
/// ordinary text and so measures every character at one em. Both numbers therefore changed, on
/// different axes: a string is now exactly <c>FontSize</c> DIP per character, so widths scale by
/// <c>FontSize / 10</c> — unchanged at font size 10, 1.1x at the toolbar's 11, 1.4x at Fluent's
/// default 14 — while a line is 1.0898 em tall (measured: 11, 12, 14 and 16 DIP at font sizes 10,
/// 11, 12 and 14), which is 1.36x the old height at every size rather than a function of it.
/// Padding, margins and fixed sizes did not move at all, so text-driven measurements grew and
/// everything else stayed put. Thresholds elsewhere in the suite carry their new numbers and point
/// back here rather than re-deriving this.</para>
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

        // The other settings store: MCP port and proxy configuration, which Settings >
        // Integrations writes. Redirected for the same reason as the line above.
        SettingsFile.RedirectForTestHost(SettingsRedirectRoot);

        // And the third store. Proxy passwords live in the OS credential manager, not in either
        // JSON file, so redirecting those two still left a test able to read — or delete — the
        // developer's real saved credential.
        CredentialServiceFactory.UseInMemoryForTestHost();
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
    /// a font manager that has just been disposed, and throws <c>KeyNotFoundException</c> for
    /// <c>fonts:SystemFonts</c>. That ordering is unchanged in 12.1.2 — the dispose and the reset are
    /// still consecutive lines of <c>EnsureIsolatedApplication</c>'s teardown — so draining here,
    /// which leaves the teardown nothing to run, is still the only thing that prevents the throw.
    /// It is done even when the body failed, because a failing test is no less capable of poisoning
    /// the session than a passing one.</para>
    ///
    /// <para><b>What 12 fixed, and why the drain is not now redundant.</b> Under 11.3.22 the
    /// escaping exception also skipped <c>scope.Dispose()</c>, so the locator scope was never
    /// popped: every later dispatch nested inside the leaked one, resolved the disposed font manager
    /// through its parent chain, and died constructing any <see cref="Window"/> at all — while the
    /// guilty test passed, because the throw happened after its result had been recorded. 12 moved
    /// the scope disposal into a <c>finally</c> and routes the teardown failure into the dispatch's
    /// task. So the blast radius is now one test instead of every test after it, and the failure
    /// lands on the test that caused it. That makes #474 survivable, not absent. Deleting the drain
    /// would trade a prevented failure for a reported one.</para>
    /// </summary>
    private static Exception? Dispatch(Action body)
    {
        Exception? failure = null;

        var dispatch = Session.Value.Dispatch(() =>
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
        }, default);

        /* A teardown failure arrives here rather than in either catch above: 12 reports it through
           the dispatch's own task, which is awaited outside the delegate. Measured under 12.1.2,
           not assumed — a queued job that throws during teardown comes out of GetResult(), and when
           the body had failed too, the teardown exception is the one the caller sees, silently
           replacing the assertion message. That inverts the rule Run documents, so catch it and let
           the body's failure keep precedence. */
        try
        {
            dispatch.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

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
