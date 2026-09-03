using PlanViewer.App;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #451: the headless harness boots the REAL App, and before these seams existed the real
/// startup side effects ran inside the test host — every local <c>dotnet test</c> rewrote
/// HKCU's .sqlplan association to point at the test runner, restored and then destroyed the
/// developer's saved open-tab list, evicted real Recent Plans entries with fixture paths,
/// held the app's single named-pipe slot, and hit GitHub with an update check per
/// constructed window. All confirmed live on a dev machine.
///
/// These tests pin the two seams that stop that — the settings redirection and the
/// <see cref="AppRuntimeMode.IsTestHost"/> gate — so the next person's MainWindow test
/// cannot silently reach back into the machine.
/// </summary>
public class TestHostIsolationTests
{
    /// <summary>
    /// The guard against a test wiping real user state: with the harness active, the
    /// effective settings path is under the run-scoped temp root, never the real profile,
    /// and a save lands there.
    ///
    /// <para>Runs on the UI thread even though it shows nothing, because every other
    /// mutation of the shared cached <see cref="AppSettings"/> happens there — serializing
    /// it off-thread could race a seeding test's list mutation mid-write.</para>
    /// </summary>
    [Fact]
    public void SettingsReadsAndWritesLandInTheRunScopedTempDirectory()
    {
        HeadlessUi.Run(() =>
        {
            var realProfileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PerformanceStudio");

            Assert.NotEqual(string.Empty, HeadlessUi.SettingsRedirectRoot);
            Assert.StartsWith(
                HeadlessUi.SettingsRedirectRoot,
                AppSettingsService.SettingsFilePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(
                AppSettingsService.SettingsFilePath.StartsWith(realProfileDir, StringComparison.OrdinalIgnoreCase),
                "the test host must never read or write the real appsettings.json");

            /* Save(Load()) rather than Save(new AppSettings()): Save caches the instance it
               is given, and swapping in a fresh one would yank state out from under a test
               that had just seeded the shared cached instance. This is the write that used
               to land in the developer's real profile. */
            AppSettingsService.Save(AppSettingsService.Load());
            Assert.True(
                File.Exists(AppSettingsService.SettingsFilePath),
                "a save under the harness must land in the redirected file");
        });
    }

    /// <summary>
    /// A MainWindow built under the harness launches none of the process-external startup
    /// services. The flags are set by the launch methods themselves, not by the gate, so
    /// this fails honestly if a future change starts one of them through a new path.
    /// </summary>
    [Fact]
    public void AWindowBuiltUnderTheHarnessLaunchesNoStartupServices()
    {
        HeadlessUi.Run(() =>
        {
            // Pins the ordering the whole design leans on: the module initializer flipped
            // the gate before the first App (and this window) booted.
            Assert.True(AppRuntimeMode.IsTestHost);

            var window = new MainWindow();

            Assert.False(
                window.PipeServerStarted,
                "the pipe server would hold the machine-wide SQLPerformanceStudio_OpenFile slot and never release it");
            Assert.False(
                window.StartupUpdateCheckStarted,
                "the update check would hit GitHub once per constructed window");
            Assert.False(
                window.McpServerStartAttempted,
                "starting MCP would read the user's real ~/.planview settings and could bind a real port");
        });
    }
}
