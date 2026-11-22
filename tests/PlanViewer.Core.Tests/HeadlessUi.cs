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
/// default 14 — while a line is 1.0898 em tall, rounded up to whole DIPs, which measures as desired
/// heights of 11, 12, 14 and 16 at font sizes 10, 11, 12 and 14. That is 1.36x the old line height
/// at every size, a flat ratio rather than something that scales with the size.</para>
///
/// <para>The height figure is worth pinning down because the plausible wrong answer is 1.25x. That
/// would be the em box alone; 1.36x is the em box plus the font's line gap. The font manager
/// reports this face as ascent 819, descent 205, line gap 92 over an em of 1024 — the OS/2
/// typographic values, not the hhea pair (782 and 0) that would have given 0.854 em and a 7%
/// change. So when a height assertion moves, 1.36x is the tell; if a height moves by something
/// else, the cause is not this.</para>
///
/// <para>Padding, margins and fixed sizes did not move at all, so text-driven measurements grew and
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
/// <para><b>One session, one Application, for the whole assembly.</b> The session is started and
/// its <c>Application</c> built from <see cref="EnterTestHostMode"/>, before anything else in this
/// assembly runs, and neither is ever disposed: the process is about to exit, and tearing an
/// Avalonia session down while another test class may still be queued is a good way to reintroduce
/// the kind of test-host wedge #441 was about. The session runs at <c>PerAssembly</c> isolation,
/// so every <see cref="Dispatch"/> runs against the same <c>Application</c>, the same
/// <c>AvaloniaLocator</c> scope and the same dispatcher thread — <see cref="ClaimUiThread"/> says
/// why that level and what it costs. What is shared is therefore enough to be poisoned once and
/// stay poisoned, as #474 was, which is what <see cref="EnsureSessionSurvived"/> watches for.</para>
/// </summary>
internal static class HeadlessUi
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(
                typeof(PlanViewer.App.App), AvaloniaTestIsolationLevel.PerAssembly),
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

        // The saved server list (connections.json). Redirected for the same reason: its path
        // used to be a plain static field under the real profile, so a test that saved a
        // connection would have rewritten the developer's own list.
        ConnectionStore.RedirectForTestHost(SettingsRedirectRoot);

        // And the credential store. Proxy passwords live in the OS credential manager, not in
        // any of the JSON files above, so redirecting those still left a test able to read — or
        // delete — the developer's real saved credential.
        CredentialServiceFactory.UseInMemoryForTestHost();

        ClaimUiThread();
    }

    /// <summary>
    /// Builds the run's one <c>Application</c> on the session thread, which makes that thread
    /// Avalonia's UI thread, while this is still the only code in the process that touches Avalonia.
    ///
    /// <para><b>The race this closes (#544, AvaloniaUI/Avalonia#22021).</b> Avalonia hands the
    /// UI-thread dispatcher to whichever thread constructs a <c>Dispatcher</c> first while
    /// <c>Dispatcher.s_uiThread</c> is null, and constructing any <c>AvaloniaObject</c> on a thread
    /// that has no dispatcher constructs one. Under <c>PerTest</c> isolation,
    /// <c>EnsureIsolatedApplication</c> nulls that field with <c>Dispatcher.ResetBeforeUnitTests()</c>
    /// at the start of every dispatch, and the session thread claims it back only part-way through
    /// <c>AppBuilder.SetupUnsafe()</c>, when the headless platform's <c>Compositor</c> reads
    /// <c>Dispatcher.UIThread</c>. An xunit worker that builds a brush or a control in a plain test
    /// during those milliseconds, or a timer or continuation an earlier test left behind, becomes the
    /// UI thread instead, and the session thread's <c>DefaultRenderLoop.Add</c> fails
    /// <c>VerifyAccess</c> against it. The window reopens on every dispatch, and 12.1.5 leaves
    /// every line of it unchanged.</para>
    ///
    /// <para><b>Why PerAssembly, and why from the module initializer.</b>
    /// <c>EnsureSharedApplication</c> never resets the dispatcher, so the field is claimed exactly
    /// once per process, by whichever thread touches Avalonia first — and never taken back, which
    /// is why upstream reports a lost first dispatch under PerAssembly as poisoning the process: a
    /// worker that constructed a control before the first UI test holds the UI thread for good.
    /// Claimed here there is nothing to lose to: xunit has not started a test, no test class's type
    /// initializer has run, and the session thread is the first thread in the process to construct
    /// a <c>Dispatcher</c>. The claim goes through <see cref="Dispatch"/> so that a failure is
    /// reported with its own stack rather than wrapped by the module loader.</para>
    ///
    /// <para><b>What changes for the tests.</b> One <c>Application</c>, one locator scope and one
    /// <c>FontManager</c> for the run instead of a fresh set per dispatch: resources read through
    /// <c>Application.Current</c> are the real App's and stay put, and a window a test leaves open
    /// stays in memory until the process exits. Nothing in this suite swaps App-level state between
    /// tests, which is what full-suite runs at this isolation level confirm.</para>
    /// </summary>
    private static void ClaimUiThread()
    {
        var failure = Dispatch(static () => { });

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The headless Avalonia session could not build the test Application, so no UI test " +
                "in this assembly can run. See #544.",
                failure);
        }
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
    /// <para><b>Why the queue is drained before returning (#474).</b> A window whose content is
    /// involved enough to leave a deferred render pass behind — a <c>PlanViewerControl</c> reliably
    /// does, a TextBlock does not — leaves it on the dispatcher's queue when the body returns.
    /// Avalonia's shared session runs that queue itself once the delegate is done, and reports
    /// anything it throws through the dispatch's task, where the catch below takes it behind the
    /// body's own failure, under the name of the test that queued it. Draining inside the delegate
    /// runs the same jobs one step earlier with the same attribution and precedence. It duplicates
    /// the session's own teardown drain on purpose, so that a #474-class failure is reported from
    /// this class's catch whatever the session's teardown does with it. It is done even when the
    /// body failed, because a failing test is no less capable of leaving work behind than a passing
    /// one.</para>
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

        /* A failure in the session's own work around the delegate — building the Application on the
           first dispatch, or the RunJobs its teardown performs — arrives here rather than in either
           catch above, through the dispatch's task. When the body had failed too, the body's failure
           is the one the caller sees: Run documents that precedence, and a teardown exception that
           replaced an assertion message would invert it. */
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
