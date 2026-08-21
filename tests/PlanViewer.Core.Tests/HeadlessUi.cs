using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;

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
/// <para><b>One session for the whole assembly.</b> Avalonia allows a single Application per
/// process, so this cannot be per-test or per-class. It is created on first use and never disposed:
/// the process is about to exit, and tearing an Avalonia session down while another test class may
/// still be queued is a good way to reintroduce the kind of test-host wedge #441 was about.</para>
/// </summary>
internal static class HeadlessUi
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(PlanViewer.App.App)),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Runs <paramref name="body"/> on the Avalonia UI thread and rethrows anything it threw, so an
    /// assertion failure inside surfaces as a test failure rather than a swallowed task.
    /// </summary>
    internal static void Run(Action body)
    {
        Session.Value.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, default).GetAwaiter().GetResult();
    }
}
