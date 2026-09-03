using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The About window's Velopack "Restart Now" calls ApplyUpdatesAndRestart, which exits the
/// process without ever raising Closing — so the unsaved-changes walk (#462/#473) never ran
/// on that route and dirty edits were discarded without a question, and OnClosed's session
/// save never ran either, so the relaunched app restored nothing (RestoreOpenPlans had
/// already cleared the saved list at startup). The fix reuses the close path's walk,
/// <see cref="MainWindow.ConfirmAllUnsavedWorkAsync"/>, and persists the open tabs before
/// the restart.
///
/// <para>Same testing shape as UnsavedQueryChangesTests: nothing headless can click a
/// dialog's buttons, so what is pinned is the walk itself — a clean window answers yes
/// without raising anything, and a dirty one stops, asks, and takes a dismissal as no.</para>
/// </summary>
public class UpdateRestartGuardTests
{
    [Fact]
    public void ACleanWindowConfirmsImmediatelyWithoutRaisingAPrompt()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var task = window.ConfirmAllUnsavedWorkAsync();

                /* Nothing dirty means the walk has nobody to ask, so the task must finish on
                   the spot. A prompt would leave it pending forever headlessly — completing
                   at all is the proof that no dialog went up. */
                Assert.True(task.IsCompletedSuccessfully, "a clean window has nothing to ask about");
                Assert.True(task.Result);
                Assert.Empty(window.OwnedWindows);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ADirtyTabHoldsTheRestartAndADismissedPromptRefusesIt()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            MainWindow? window = null;
            QuerySessionControl? session = null;
            try
            {
                window = new MainWindow();
                window.Show(); // the prompt's ShowDialog needs a visible owner, headless or not
                window.LoadSqlFile(path);

                var tab = window.MainTabControl.Items.OfType<TabItem>()
                    .Last(t => t.Content is QuerySessionControl);
                session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2; -- unsaved work";

                var task = window.ConfirmAllUnsavedWorkAsync();
                Dispatcher.UIThread.RunJobs();

                /* The restart cannot go ahead while the question is open. */
                Assert.False(task.IsCompleted);
                var prompt = Assert.Single(window.OwnedWindows);

                /* Dismissing the prompt is Cancel, and Cancel has to refuse the restart with
                   everything intact: no save, no close, and no latch set behind the caller's
                   back — the About window aborts and the app carries on as it was. */
                prompt.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.True(task.IsCompleted, "the refused answer still has to arrive");
                Assert.False(task.Result);
                Assert.True(session.IsDirty, "nothing was written and nothing was discarded");
                Assert.True(window.IsVisible, "the walk must not close anything on its own");
                Assert.Contains(tab, window.MainTabControl.Items.OfType<TabItem>());
            }
            finally
            {
                PutAway(window, session);
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void PersistSessionForRestartWritesTheOpenTabsDown()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            var settings = AppSettingsService.Load();
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                /* OnClosed does this on an ordinary shutdown; ApplyUpdatesAndRestart exits
                   without one, and RestoreOpenPlans cleared the list at startup — so the
                   restart path has to write the tabs down itself or the updated app comes
                   back empty-handed. Load returns the process-wide cached instance, the same
                   object the window writes through (see RestoreQueryTabsTests.Seed). */
                window.PersistSessionForRestart();

                Assert.Contains(path, settings.OpenTabs);
            }
            finally
            {
                /* Leave nothing seeded for the next test's MainWindow to restore. */
                settings.OpenTabs.Clear();
                AppSettingsService.Save(settings);
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// Same job as DetachedUnsavedChangesTests.PutAway, for the main window: prompts closed,
    /// session settled, window shut — from a finally, because the run where it matters is the
    /// run where an assertion above it failed (#474).
    /// </summary>
    private static void PutAway(MainWindow? window, QuerySessionControl? session)
    {
        if (window == null)
            return;

        foreach (var prompt in window.OwnedWindows.ToList())
            prompt.Close();

        session?.MarkClean();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }
}
