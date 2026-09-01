using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #472: <b>Copy Path</b> answered the question "is there a path to copy?" once, while the tab was
/// being built, and never again. Open a scratch query and save it — the session gains a
/// SourceFilePath, the tab is retitled after the file, and the menu item is still hidden, because
/// it was told there was nothing to copy back when that was true. Only a restart, which rebuilds
/// the tab, put it right.
///
/// The fix asks on <see cref="ContextMenu.Opening"/>, the one moment the menu is actually consulted,
/// so these drive that event rather than reading the flag straight after construction — reading it
/// cold is what the old code got right and is exactly what these have to not do.
///
/// <para><b>How the menu is opened here.</b> Avalonia raises ContextMenu.Opening from its
/// ContextRequested handler on the attached control, not from ContextMenu.Open(): calling Open()
/// directly sets IsOpen and never asks. So <see cref="RightClick"/> raises ContextRequestedEvent on
/// the tab header, which is the same path a real right-click takes.</para>
/// </summary>
public class CopyPathVisibilityTests
{
    /// <summary>
    /// The report, end to end: scratch query, no path, save, right-click, and the item is there
    /// and copies the file it was saved to.
    /// </summary>
    [Fact]
    public void CopyPathAppearsOnceAScratchQueryHasBeenSaved()
    {
        HeadlessUi.Run(() =>
        {
            var savedAs = Path.Combine(Path.GetTempPath(), $"saved_{Path.GetRandomFileName()}.sql");
            try
            {
                var window = new MainWindow();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;
                var copyPath = ContextMenuItem(tab, "Copy Path");

                RightClick(tab);
                Assert.False(copyPath.IsVisible,
                    "a never-saved scratch query has no file, so there is nothing to copy");

                session.QueryEditor.Text = "SELECT 1 AS saved;";
                Assert.True(window.SaveQueryToPath(tab, session, savedAs));

                RightClick(tab);
                Assert.True(copyPath.IsVisible,
                    "the query has a file now, and this menu is being opened after the save");

                copyPath.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(savedAs, Pump(ClipboardHelper.TryGetTextAsync(window)));
            }
            finally
            {
                if (File.Exists(savedAs))
                    File.Delete(savedAs);
            }
        });
    }

    /// <summary>
    /// The other direction, which recomputing gets for free: a tab that stops having a file stops
    /// offering to copy one. A stale <c>true</c> would put a Copy Path on the menu that copies
    /// nothing when clicked.
    /// </summary>
    [Fact]
    public void CopyPathGoesAwayAgainWhenTheTabStopsHavingAFile()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS opened;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = QueryTabs(window).Last();
                var copyPath = ContextMenuItem(tab, "Copy Path");

                RightClick(tab);
                Assert.True(copyPath.IsVisible, "the query was opened from a file");

                ((QuerySessionControl)tab.Content!).SourceFilePath = null;

                RightClick(tab);
                Assert.False(copyPath.IsVisible, "there is no longer a path behind this tab");
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// Raises the event a real right-click raises on the tab header, then closes the menu again so
    /// the next call is a fresh open rather than a no-op on an already-open menu.
    /// </summary>
    private static void RightClick(TabItem tab)
    {
        var header = (StackPanel)tab.Header!;
        header.RaiseEvent(new ContextRequestedEventArgs());
        header.ContextMenu!.Close();
    }

    private static MenuItem ContextMenuItem(TabItem tab, string header) =>
        ((StackPanel)tab.Header!).ContextMenu!.Items
            .OfType<MenuItem>()
            .Single(i => (i.Header as string) == header);

    /// <summary>
    /// Drains the UI queue until the clipboard call finishes: Copy Path starts its write without
    /// awaiting it, so the read that follows can be a dispatcher turn early.
    /// </summary>
    private static T Pump<T>(Task<T> task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
            Dispatcher.UIThread.RunJobs();

        Assert.True(task.IsCompleted, "the clipboard call never completed");
        return task.GetAwaiter().GetResult();
    }

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }

    private static IEnumerable<TabItem> QueryTabs(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Where(t => t.Content is QuerySessionControl);
}
