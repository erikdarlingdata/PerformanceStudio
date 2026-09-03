using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #490: session restore knew only what a clean close wrote down. The list was written once,
/// in OnClosed, so a crash, a task kill, or an OS "shut down anyway" restored zero tabs; and
/// the writer walked MainTabControl alone, so a file detached into its own window was never
/// written down at all. Membership changes now persist continuously (debounced), and detached
/// windows are part of the collected set.
///
/// <para>Every assertion here reads the settings FILE, not the in-memory list — the whole
/// point of continuous persistence is what is on disk when the process dies without warning,
/// so the disk is what gets checked. The file is the redirected per-run one (#451), which is
/// what makes asserting real writes safe. The debounce is driven through
/// <see cref="MainWindow.FlushPendingSessionPersistForTests"/> rather than waited out: the
/// harness shares one dispatcher across the suite, so a real one-second timer would tick
/// during some unrelated later test — see RequestSessionPersist for the full story.</para>
///
/// <para>Tests that leave real paths in the persisted list blank it on the way out
/// (<see cref="ResetPersistedState"/>, same hygiene as UpdateRestartGuardTests), because the
/// next MainWindow constructed anywhere in the run restores whatever it finds there.</para>
/// </summary>
public class SessionPersistenceTests
{
    [Fact]
    public void OpeningATabIsPersistedWithoutClosingTheApp()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS persisted_live;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                /* The open armed the debounced writer through the tab watcher; the seam
                   stands in for the timer's tick. Before #490 nothing short of closing the
                   app wrote this down. */
                window.FlushPendingSessionPersistForTests();

                Assert.Contains(path, PersistedOpenTabs());
            }
            finally
            {
                ResetPersistedState();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ClosingATabRemovesItFromThePersistedList()
    {
        HeadlessUi.Run(() =>
        {
            var stays = TempSql("SELECT 1 AS stays;");
            var goes = TempSql("SELECT 2 AS goes;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(stays);
                window.LoadSqlFile(goes);
                window.FlushPendingSessionPersistForTests();
                Assert.Contains(goes, PersistedOpenTabs());

                /* Through the real close button, not Items.Remove, so the whole path is the
                   one a user takes. The session is clean, so no prompt holds the close. */
                CloseButton(QueryTab(window, goes)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                window.FlushPendingSessionPersistForTests();

                var persisted = PersistedOpenTabs();
                Assert.DoesNotContain(goes, persisted);
                Assert.Contains(stays, persisted);
            }
            finally
            {
                ResetPersistedState();
                File.Delete(stays);
                File.Delete(goes);
            }
        });
    }

    [Fact]
    public void ADetachedQueryStaysOnThePersistedListAndRedockKeepsIt()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS detached;");
            Window? detached = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                detached = window.DetachTabToWindow(QueryTab(window, path))!;
                window.FlushPendingSessionPersistForTests();

                /* The first gap #490 names: off the tab strip is not out of the app. Before
                   this, detaching a file was indistinguishable from closing it as far as the
                   next session was concerned. */
                Assert.Contains(path, PersistedOpenTabs());

                RedockButton(detached).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                window.FlushPendingSessionPersistForTests();

                /* And exactly once — redock has to move the entry back to the docked half of
                   the collection, not leave a stale twin on the detached half. */
                Assert.Single(PersistedOpenTabs(), p => p == path);
            }
            finally
            {
                PutAway(detached);
                ResetPersistedState();
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The register #473 built for unsaved-changes prompts is query-sessions-only, because
    /// only an edit can be lost. Persistence needs every FILE-backed detached window, and a
    /// detached plan is exactly the read-only case that register ignores — which is why #490
    /// keeps its own register instead of borrowing that one.
    /// </summary>
    [Fact]
    public void ADetachedPlanIsPersistedToo()
    {
        HeadlessUi.Run(() =>
        {
            var path = Path.Combine(System.AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan");
            Window? detached = null;
            try
            {
                var window = new MainWindow();
                window.LoadPlanFile(path);

                var planTab = window.MainTabControl.Items.OfType<TabItem>()
                    .Last(t => t.Content is DockPanel);
                detached = window.DetachTabToWindow(planTab)!;
                window.FlushPendingSessionPersistForTests();

                Assert.Contains(path, PersistedOpenTabs());
            }
            finally
            {
                PutAway(detached);
                ResetPersistedState();
            }
        });
    }

    [Fact]
    public void ClosingADetachedWindowTakesItsFileOffThePersistedList()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS closed_detached;");
            Window? detached = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                detached = window.DetachTabToWindow(QueryTab(window, path))!;
                window.FlushPendingSessionPersistForTests();
                Assert.Contains(path, PersistedOpenTabs());

                /* A detached window closing changes nothing on the tab strip, so this is the
                   one leave-the-app path the tab watcher cannot see — the trigger rides on the
                   detached register instead. Clean session, so the close guard waves it
                   through on the first pass. */
                detached.Close();
                Dispatcher.UIThread.RunJobs();
                window.FlushPendingSessionPersistForTests();

                Assert.DoesNotContain(path, PersistedOpenTabs());
            }
            finally
            {
                PutAway(detached);
                ResetPersistedState();
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// A scratch buffer joins the list the moment it becomes a file. Tab membership never
    /// changes across a save, so this is the trigger that lives at SaveQueryToPath rather
    /// than on the watcher.
    /// </summary>
    [Fact]
    public void AScratchGainingAFileJoinsThePersistedList()
    {
        HeadlessUi.Run(() =>
        {
            var savedAs = Path.Combine(Path.GetTempPath(), $"saved_{Path.GetRandomFileName()}.sql");
            try
            {
                var window = new MainWindow();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var tab = window.MainTabControl.Items.OfType<TabItem>()
                    .Last(t => t.Content is QuerySessionControl);
                var session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 1 AS first_save;";

                window.FlushPendingSessionPersistForTests();
                Assert.DoesNotContain(savedAs, PersistedOpenTabs());

                Assert.True(window.SaveQueryToPath(tab, session, savedAs));
                window.FlushPendingSessionPersistForTests();

                Assert.Contains(savedAs, PersistedOpenTabs());
            }
            finally
            {
                ResetPersistedState();
                if (File.Exists(savedAs))
                    File.Delete(savedAs);
            }
        });
    }

    [Fact]
    public void RestoreWritesTheRestoredTabsBackImmediately()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS restored_and_kept;");
            try
            {
                Seed(path);
                var window = new MainWindow();

                /* No flush call here, deliberately: RestoreOpenPlans flushes synchronously at
                   its own end, so that a crash a moment after startup still finds the session
                   on disk instead of an empty list waiting out a debounce. Reading the file
                   straight after the constructor IS the assertion. */
                Assert.Contains(path, PersistedOpenTabs());
            }
            finally
            {
                ResetPersistedState();
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The crash-loop invariant. Restore clears the saved list before the first open, and only
    /// a file that actually opened re-enters it — so a file that fails during restore has, by
    /// construction, not re-added itself. A file that CRASHES the process mid-load is the same
    /// case with a bigger bang: it dies before its own re-add, so the next start does not retry
    /// it. (A test cannot crash its own process to prove that literally; failing to open walks
    /// the identical path — cleared first, never re-added.)
    /// </summary>
    [Fact]
    public void AFileThatFailsToOpenDuringRestoreStaysOffThePersistedList()
    {
        HeadlessUi.Run(() =>
        {
            var good = TempSql("SELECT 1 AS survivor;");
            var poison = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sqlplan");
            /* Exists (so restore attempts it) but is not a plan, so the open fails and no tab
               is created. The startup error dialog it raises is ownerless and left behind, the
               same way ShowErrorBeforeVisibleTests leaves theirs. */
            File.WriteAllText(poison, "<NotAPlan/>");
            try
            {
                Seed(good, poison);
                var window = new MainWindow();

                var persisted = PersistedOpenTabs();
                Assert.Contains(good, persisted);
                Assert.DoesNotContain(poison, persisted);
            }
            finally
            {
                ResetPersistedState();
                File.Delete(good);
                File.Delete(poison);
            }
        });
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    /// <summary>
    /// What is actually on disk, read back through the same serializer the app writes with.
    /// The redirected file (#451) makes this safe to hit for real.
    /// </summary>
    private static List<string> PersistedOpenTabs()
    {
        var json = File.ReadAllText(AppSettingsService.SettingsFilePath);
        return JsonSerializer.Deserialize<AppSettings>(json)!.OpenTabs;
    }

    /// <summary>
    /// Stages paths where the next MainWindow will look for the previous session's tabs —
    /// same mechanism as RestoreQueryTabsTests.Seed: Load returns the process-wide cached
    /// instance, which is the very object the window reads.
    /// </summary>
    private static void Seed(params string[] paths)
    {
        var settings = AppSettingsService.Load();
        settings.OpenTabs.Clear();
        settings.OpenTabs.AddRange(paths);
    }

    /// <summary>
    /// Leaves nothing persisted for the next test's MainWindow to restore. Cache and file
    /// both, since restore reads the former and these tests assert the latter.
    /// </summary>
    private static void ResetPersistedState()
    {
        var settings = AppSettingsService.Load();
        settings.OpenTabs.Clear();
        AppSettingsService.Save(settings);
    }

    /// <summary>
    /// Same job as DetachedUnsavedChangesTests.PutAway: a leaked window outlives its test and
    /// poisons the shared session (#474), so closure lives in a finally. Sessions here are
    /// clean, so no prompt ever holds the close.
    /// </summary>
    private static void PutAway(Window? detached)
    {
        if (detached == null)
            return;

        foreach (var prompt in detached.OwnedWindows.ToList())
            prompt.Close();

        detached.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static TabItem QueryTab(MainWindow window, string path) =>
        window.MainTabControl.Items.OfType<TabItem>()
            .Single(t => t.Content is QuerySessionControl s && s.SourceFilePath == path);

    private static Button CloseButton(TabItem tab) =>
        ((StackPanel)tab.Header!).Children.OfType<Button>().Single();

    private static Button RedockButton(Window detached) =>
        ((DockPanel)detached.Content!).Children.OfType<StackPanel>().Single()
            .Children.OfType<Button>().Single();

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }
}
