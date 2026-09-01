using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Helpers;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #473: #462 gave query tabs an unsaved-changes prompt on tab close and on window close, and
/// a detached window dodged both. Its close path is the helper's own and asked nobody anything,
/// and while detached the session is out of MainTabControl.Items, so the shutdown walk honestly
/// reported nothing to save with a dirty edit sitting in another window.
///
/// <para>Same testing shape as UnsavedQueryChangesTests: the dialog cannot be clicked headlessly,
/// so the decision is a pure value — <see cref="MainWindow.DetachedContentNeedsSavePrompt"/> for
/// whether to ask at all, <see cref="MainWindow.DecideClose"/> (#462, already pinned) for what to
/// do with the answer — and what is tested here is the walk and the wiring around them.</para>
///
/// <para>Nothing here puts a PlanViewerControl inside a Window. Read-only content is represented
/// by a <see cref="QueryStoreHistoryControl"/>, which detaches through the same helper and takes
/// the same silent path a plan does.</para>
/// </summary>
public class DetachedUnsavedChangesTests
{
    [Fact]
    public void ADirtyDetachedSessionIsSeenWhereACleanOneIsNot()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            Window? detached = null;
            QuerySessionControl? session = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = LastQueryTab(window);
                session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2; -- unsaved work";

                detached = window.DetachTabToWindow(tab)!;

                /* The bug, stated as an assertion: the edit is no longer on the tab strip, which
                   is the only place #462 knew to look. */
                Assert.Empty(window.TabsWithUnsavedChanges());

                Assert.Equal(
                    new[] { session },
                    window.DetachedSessionsWithUnsavedChanges().Select(d => d.Session));

                session.MarkClean();
                Assert.Empty(window.DetachedSessionsWithUnsavedChanges());
                Assert.Single(window.DetachedQuerySessions); // still detached, just not dirty
            }
            finally
            {
                PutAway(detached, session);
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void TheShutdownPromptCountsDetachedSessionsAsWellAsTabs()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            Window? detached = null;
            QuerySessionControl? session = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = LastQueryTab(window);
                session = (QuerySessionControl)tab.Content!;

                Assert.False(window.CloseNeedsConfirmation(), "nothing modified anywhere");

                session.QueryEditor.Text = "SELECT 2; -- unsaved work";
                Assert.True(window.CloseNeedsConfirmation());

                detached = window.DetachTabToWindow(tab)!;

                /* Detaching is not a way to opt out of being asked. Before this change the tab
                   walk was the whole question, so this went back to false the moment the window
                   was torn off and the app shut down over the top of the edit. */
                Assert.Empty(window.TabsWithUnsavedChanges());
                Assert.True(window.CloseNeedsConfirmation());

                session.MarkClean();
                Assert.False(window.CloseNeedsConfirmation());
            }
            finally
            {
                PutAway(detached, session);
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void TheShutdownWalkAsksAboutTabsAndDetachedWindowsAlike()
    {
        HeadlessUi.Run(() =>
        {
            var stays = TempSql("SELECT 1;");
            var leaves = TempSql("SELECT 2;");
            var clean = TempSql("SELECT 3;");
            Window? detached = null;
            QuerySessionControl? torn = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(stays);
                window.LoadSqlFile(leaves);
                window.LoadSqlFile(clean);

                var tabs = window.MainTabControl.Items.OfType<TabItem>()
                    .Where(t => t.Content is QuerySessionControl).ToList();

                var stayingTab = tabs[^3];
                var leavingTab = tabs[^2];
                var cleanTab = tabs[^1];

                var staying = (QuerySessionControl)stayingTab.Content!;
                torn = (QuerySessionControl)leavingTab.Content!;

                staying.QueryEditor.Text = "SELECT 1; -- edited";
                torn.QueryEditor.Text = "SELECT 2; -- edited";

                detached = window.DetachTabToWindow(leavingTab)!;

                /* What the shutdown prompt will actually ask about, in the order it will ask.
                   Tabs first, then the windows that used to be tabs; the clean tab is in neither
                   list. Before this the second half of the walk did not exist, and an edit was
                   one Detach to Window away from being discarded without a question. */
                var work = window.UnsavedWorkOnClose();

                Assert.Equal(new[] { staying, torn }, work.Select(w => w.Session));
                Assert.Equal(new TabItem?[] { stayingTab, null }, work.Select(w => w.Tab));

                /* And the prompt each one gets is owned by the window it is about. A dialog
                   parented to the main window while the session it names is in another one is
                   a question about something the user cannot see. */
                Assert.Same(window, work[0].Owner);
                Assert.Same(detached, work[1].Owner);

                Assert.DoesNotContain(cleanTab.Content, work.Select(w => (object?)w.Session));
            }
            finally
            {
                PutAway(detached, torn);
                File.Delete(stays);
                File.Delete(leaves);
                File.Delete(clean);
            }
        });
    }

    [Fact]
    public void ReadOnlyDetachedContentIsNeverAskedAbout()
    {
        HeadlessUi.Run(() =>
        {
            /* Plans and Query Store windows detach through the same helper and have nothing to
               save. The guard has to answer no for them without a dialog and without cancelling
               anything, or every read-only close grows a detour it has no use for. */
            Assert.False(
                MainWindow.DetachedContentNeedsSavePrompt(new QueryStoreHistoryControl(), isShuttingDown: false));

            var window = new MainWindow();
            Assert.Null(window.DetachedQueryCloseGuard(new QueryStoreHistoryControl(), window));
        });
    }

    [Fact]
    public void OnlyADirtySessionIsAskedAboutAndNotOnceTheAppIsShuttingDown()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);
                var session = (QuerySessionControl)LastQueryTab(window).Content!;

                Assert.False(
                    MainWindow.DetachedContentNeedsSavePrompt(session, isShuttingDown: false),
                    "an unmodified session has nothing to lose");

                session.QueryEditor.Text = "SELECT 2; -- unsaved work";

                Assert.True(
                    MainWindow.DetachedContentNeedsSavePrompt(session, isShuttingDown: false));

                /* The shutdown answer, and the reason it is a parameter rather than something the
                   guard reads for itself in a test. OnClosed force-closes every detached window
                   after the main window is already gone; a prompt raised there is owned by a
                   window nobody is looking at and gates a shutdown that has nowhere left to ask.
                   ConfirmWindowCloseAsync asks while everything is still up, which is what earns
                   this no. */
                Assert.False(
                    MainWindow.DetachedContentNeedsSavePrompt(session, isShuttingDown: true));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void RedockingDoesNotPrompt()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            Window? detached = null;
            QuerySessionControl? session = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = LastQueryTab(window);
                session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2; -- unsaved work";

                detached = window.DetachTabToWindow(tab)!;

                /* Re-dock moves the content, it does not destroy it, so there is nothing to save
                   it from — and the session is dirty, so a guard that did fire here would have
                   plenty to say. */
                RedockButton(detached).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                /* The two assertions that catch a guard firing on this path. Re-dock hands the
                   content back whether or not the window agreed to close, so the tab coming back
                   proves nothing on its own: what a guard would leave behind is the emptied
                   window still open, with a prompt on it, asking about a session that is already
                   somewhere else. */
                Assert.False(detached.IsVisible, "Re-dock has to actually close the window");
                Assert.Empty(detached.OwnedWindows);

                Assert.Empty(window.DetachedQuerySessions);

                var redockedTab = LastQueryTab(window);
                Assert.Same(session, redockedTab.Content);
                Assert.True(MainWindow.HasUnsavedChanges(redockedTab), "the edit came back with it");
            }
            finally
            {
                PutAway(detached, session);
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void TheGuardCancelsACloseAndTheAnswerIsOnlyAskedForOnce()
    {
        HeadlessUi.Run(() =>
        {
            var asked = 0;
            var destroyed = 0;
            var answer = false;

            var detached = DetachedWindowHelper.ShowDetached(
                new TextBlock { Text = "content" },
                title: "Detached",
                icon: null,
                backgroundBrush: null,
                onRedock: _ => { },
                onClosing: _ => destroyed++,
                /* The cap is a fuse, not part of the contract: a helper that lost its latch
                   would re-ask its way round the post-and-close loop forever, and a test that
                   hangs tells you nothing. Four asks is already the failure. */
                closeGuard: (_, _) => { asked++; return Task.FromResult(answer && asked <= 3); });
            try
            {
                detached.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(1, asked);
                Assert.Equal(0, destroyed);
                Assert.True(detached.IsVisible, "Cancel has to actually keep the window open");

                answer = true;
                detached.Close();
                Dispatcher.UIThread.RunJobs();

                /* Two closes, two questions. The re-issued close is not a third, and that is
                   the latch doing its job — without it the close the guard just authorised gets
                   handed straight back to the guard. */
                Assert.Equal(2, asked);
                Assert.Equal(1, destroyed);
            }
            finally
            {
                answer = true;
                PutAway(detached);
            }
        });
    }

    [Fact]
    public void AGuardlessDetachIsUntouchedAndClosesOnTheFirstPass()
    {
        HeadlessUi.Run(() =>
        {
            var destroyed = 0;

            /* The Query Store sub-tab detach passes no guard at all, so its close is the one it
               always was: straight through, nothing cancelled, nothing posted. */
            var detached = DetachedWindowHelper.ShowDetached(
                new QueryStoreHistoryControl(),
                title: "History",
                icon: null,
                backgroundBrush: null,
                onRedock: _ => { },
                onClosing: _ => destroyed++);
            try
            {
                detached.Close();

                Assert.Equal(1, destroyed);
            }
            finally
            {
                PutAway(detached);
            }
        });
    }

    [Fact]
    public void ClosingADetachedWindowWithUnsavedWorkDoesNotJustCloseIt()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            Window? detached = null;
            QuerySessionControl? session = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = LastQueryTab(window);
                session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2; -- unsaved work";

                detached = window.DetachTabToWindow(tab)!;

                detached.Close();
                Dispatcher.UIThread.RunJobs();

                /* The whole of #473 in one assertion. This used to be a closed window and a lost
                   edit; the close is now held while the prompt is up. Headless, nobody ever
                   answers it, so the window is still here — which is the right shape of failure
                   for a close that has a question outstanding. */
                Assert.True(detached.IsVisible);
                Assert.Single(window.DetachedQuerySessions);
                Assert.True(session.IsDirty, "nothing was written and nothing was discarded");
            }
            finally
            {
                PutAway(detached, session);
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ADetachedSessionSavesWithNoTabToRetitle()
    {
        HeadlessUi.Run(() =>
        {
            var opened = TempSql("SELECT 1;");
            var savedAs = Path.Combine(Path.GetTempPath(), $"saved_{Path.GetRandomFileName()}.sql");
            /* A directory that does not exist, so File.WriteAllText throws. */
            var unwritable = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nope.sql");
            Window? detached = null;
            QuerySessionControl? session = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(opened);

                var tab = LastQueryTab(window);
                session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2 AS edited;";

                detached = window.DetachTabToWindow(tab)!;

                /* The save the prompt runs for a detached session has no tab behind it. The tab
                   is what SaveQueryToPath retitles, so it has to tolerate not having one — and
                   everything else about the save, including which side of the write settles the
                   dirty state, still has to hold. */
                Assert.False(window.SaveQueryToPath(null, session, unwritable));
                Assert.True(session.IsDirty, "a save that threw has not saved anything");
                Assert.Single(window.DetachedSessionsWithUnsavedChanges());

                Assert.True(window.SaveQueryToPath(null, session, savedAs));
                Assert.False(session.IsDirty);
                Assert.Equal("SELECT 2 AS edited;", File.ReadAllText(savedAs));
                Assert.Equal(savedAs, session.SourceFilePath);
                Assert.Empty(window.DetachedSessionsWithUnsavedChanges());
            }
            finally
            {
                PutAway(detached, session);
                File.Delete(opened);
                if (File.Exists(savedAs))
                    File.Delete(savedAs);
            }
        });
    }

    [Fact]
    public void ClosingADetachedWindowTakesTheSessionOffTheRegister()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            Window? detached = null;
            QuerySessionControl? session = null;
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = LastQueryTab(window);
                detached = window.DetachTabToWindow(tab)!;
                Assert.Single(window.DetachedQuerySessions);

                /* Clean, so nothing is asked and the close goes through on the first pass. A
                   register that kept the entry would have the shutdown prompt asking about a
                   window that is not there any more. */
                detached.Close();

                Assert.Empty(window.DetachedQuerySessions);
                Assert.False(window.CloseNeedsConfirmation());
            }
            finally
            {
                PutAway(detached, session);
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// Puts a detached window and any prompt it is showing away, from a finally, whatever the
    /// test did or failed to do.
    ///
    /// <para>One Avalonia application serves the whole assembly (see HeadlessUi), so a window
    /// left open outlives the test that made it, and #474 is what that costs: a leaked window
    /// poisons the session for everything queued behind it, and the failure surfaces somewhere
    /// else entirely. Which makes cleanup a finally, not a last line — the run where it matters
    /// is the run where an assertion above it failed.</para>
    ///
    /// <para>Deliberately not routed through the detached register: a test that has just broken
    /// the register still has to be able to tidy up after itself.</para>
    /// </summary>
    private static void PutAway(Window? detached, QuerySessionControl? session = null)
    {
        if (detached == null)
            return;

        // Nothing headless can click one of the prompt's three buttons, so dismissing it is
        // Cancel — which is why the session is settled before the window is closed again.
        foreach (var prompt in detached.OwnedWindows.ToList())
            prompt.Close();

        session?.MarkClean();
        detached.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static Button RedockButton(Window detached) =>
        ((DockPanel)detached.Content!).Children.OfType<StackPanel>().Single()
            .Children.OfType<Button>().Single();

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }

    private static TabItem LastQueryTab(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>()
            .Last(t => t.Content is QuerySessionControl);
}
