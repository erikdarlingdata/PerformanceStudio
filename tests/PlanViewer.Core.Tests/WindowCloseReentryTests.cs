using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// MainWindow.OnClosing cancels the close and runs the unsaved-work walk asynchronously, and
/// _closeConfirmed only latches once that walk says yes — so a second close request arriving
/// WHILE the walk was still asking (a double-clicked X, an Alt+F4 behind a Save As picker)
/// used to start a second concurrent walk: duplicate prompts about the same tab, and a Cancel
/// answered to one walk that the other never heard. Same reentrancy class as the About
/// window's update link (#485 review), and the same walk-in-progress latch closes it.
///
/// <para><b>Both guards, not one.</b> <c>DetachedWindowHelper</c> carries its own copy of the
/// latch for its own copy of the gap, and the two are separate code with separate state. Only
/// the MainWindow half was pinned when the latch landed: deleting the detached
/// <c>closeGuardPending</c> check outright left this class green, which is how a latch gets
/// tidied away by someone who reasonably believes the tests are watching it.</para>
/// </summary>
public class WindowCloseReentryTests
{
    [Fact]
    public void ASecondCloseDuringTheWalkDoesNotStartASecondWalk()
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

                window.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.True(window.IsVisible, "the close is held while the question is up");
                Assert.Single(window.OwnedWindows);

                /* The double-clicked X. Programmatic Close is the same entry: modality only
                   blocks input, not the API, so OnClosing runs again mid-walk. */
                window.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Single(window.OwnedWindows);
                Assert.True(window.IsVisible);

                /* Dismissing the prompt is Cancel: the walk ends refused, the window stays —
                   and the latch has to clear with it, or the X is dead for good. */
                window.OwnedWindows.Single().Close();
                Dispatcher.UIThread.RunJobs();

                Assert.True(window.IsVisible);
                Assert.Empty(window.OwnedWindows);

                window.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Single(window.OwnedWindows); // a fresh close starts a fresh walk
            }
            finally
            {
                if (window != null)
                {
                    foreach (var prompt in window.OwnedWindows.ToList())
                        prompt.Close();
                    session?.MarkClean();
                    Dispatcher.UIThread.RunJobs();
                    window.Close(); // nothing dirty now, so this one goes through
                    Dispatcher.UIThread.RunJobs();
                }

                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ASecondCloseOfADetachedWindowDoesNotStackASecondPrompt()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            MainWindow? window = null;
            Window? detached = null;
            QuerySessionControl? session = null;
            try
            {
                window = new MainWindow();
                window.Show();
                window.LoadSqlFile(path);

                var tab = window.MainTabControl.Items.OfType<TabItem>()
                    .Last(t => t.Content is QuerySessionControl);
                session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2; -- unsaved work";

                detached = window.DetachTabToWindow(tab)!;
                Dispatcher.UIThread.RunJobs();

                detached.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.True(detached.IsVisible, "the close is held while the question is up");
                Assert.Single(detached.OwnedWindows);

                /* The second X. The guard's prompt is modal to the detached window, but a Save As
                   raised from that prompt is not, so this is reachable in the real app. */
                detached.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Single(detached.OwnedWindows);
                Assert.True(detached.IsVisible);

                /* Dismissing is Cancel: the window stays, and the latch has to clear with it or
                   the X is dead for the rest of that window's life. */
                detached.OwnedWindows.Single().Close();
                Dispatcher.UIThread.RunJobs();

                Assert.True(detached.IsVisible);
                Assert.Empty(detached.OwnedWindows);

                detached.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Single(detached.OwnedWindows); // a fresh close starts a fresh question
            }
            finally
            {
                if (detached != null)
                {
                    foreach (var prompt in detached.OwnedWindows.ToList())
                        prompt.Close();
                    session?.MarkClean();
                    Dispatcher.UIThread.RunJobs();
                    detached.Close();
                    Dispatcher.UIThread.RunJobs();
                }

                if (window != null)
                {
                    session?.MarkClean();
                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }

                File.Delete(path);
            }
        });
    }

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }
}
