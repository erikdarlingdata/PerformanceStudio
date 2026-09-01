using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #463: plan tabs came back after a restart and query tabs did not, even when the query had been
/// opened from a file and its path was sitting right there on the session. The saved-tab list was
/// built from GetTabFilePath, which knew how to look inside a plan tab and nothing else, so a query
/// tab was invisible to it — nothing was written down, so nothing could be restored.
///
/// The same blind spot hid <b>Copy Path</b> on the tab context menu, which is shown only when
/// GetTabFilePath answers, and so had never once appeared on a query tab. Both halves are covered
/// here because they are one defect, and the menu half is the easier one to fix by accident and
/// never actually check.
///
/// These drive LoadSqlFile and the MainWindow constructor rather than the menu handlers, for the
/// same reason <see cref="OpenSaveQueryTests"/> does: a file picker cannot be answered headlessly.
/// </summary>
public class RestoreQueryTabsTests
{
    [Fact]
    public void AQueryOpenedFromAFileIsWrittenDownForTheNextSession()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS restored;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                Assert.Contains(path, window.CollectOpenTabPaths());
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The deliberate edge of the fix. A never-saved scratch buffer has no path, so there is
    /// nothing to write down and it does not come back — persisting unsaved text is #462's job,
    /// not this one's.
    /// </summary>
    [Fact]
    public void AScratchQueryHasNoFileAndIsNotWrittenDown()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            window.NewQuery_Click(window, new RoutedEventArgs());

            var session = Sessions(window).Last();
            Assert.Null(session.SourceFilePath);
            Assert.Empty(window.CollectOpenTabPaths());
        });
    }

    [Fact]
    public void AQueryFileComesBackAsAQueryTabOnTheNextStart()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS came_back;");
            try
            {
                Seed(path);

                var window = new MainWindow();

                var session = Sessions(window).SingleOrDefault(s => s.SourceFilePath == path);
                Assert.NotNull(session);
                Assert.Equal("SELECT 1 AS came_back;", session!.QueryEditor.Text);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The saved list now holds both kinds of file, so restore routes on extension. This is the
    /// half that would break if that routing sent everything to LoadSqlFile instead.
    /// </summary>
    [Fact]
    public void APlanFileStillComesBackAsAPlanTab()
    {
        HeadlessUi.Run(() =>
        {
            var path = Path.Combine(System.AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan");
            Seed(path);

            var window = new MainWindow();

            Assert.Contains(path, window.CollectOpenTabPaths());
            Assert.Contains(Viewers(window), v => v.SourceFilePath == path);
        });
    }

    [Fact]
    public void CopyPathIsOfferedOnAQueryTabAndCopiesTheFile()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS copied;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = window.MainTabControl.Items
                    .OfType<TabItem>()
                    .Last(t => t.Content is QuerySessionControl);

                var copyPath = ContextMenuItem(tab, "Copy Path");
                Assert.True(copyPath.IsVisible,
                    "the query came from a file, so there is a path to copy");

                copyPath.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.Equal(path, Pump(ClipboardHelper.TryGetTextAsync(window)));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// A scratch tab has no path, so the menu item stays hidden — the gate still gates.
    /// </summary>
    [Fact]
    public void CopyPathStaysHiddenOnAQueryTabWithNoFile()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            window.NewQuery_Click(window, new RoutedEventArgs());

            var tab = window.MainTabControl.Items
                .OfType<TabItem>()
                .Last(t => t.Content is QuerySessionControl);

            Assert.False(ContextMenuItem(tab, "Copy Path").IsVisible);
        });
    }

    /// <summary>
    /// The list outgrew the name "open_plans" once it started holding queries. Renaming the key
    /// is free for a new install and expensive for an existing one, so the old key is still read.
    /// </summary>
    [Fact]
    public void TabsRecordedUnderTheOldSettingsKeyAreStillRestored()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"open_plans":["/tmp/one.sqlplan","/tmp/two.sql"]}""")!;

        AppSettingsService.MigrateOpenTabs(settings);

        Assert.Equal(new[] { "/tmp/one.sqlplan", "/tmp/two.sql" }, settings.OpenTabs);
        Assert.Null(settings.LegacyOpenPlans);
    }

    /// <summary>
    /// Both keys present means a downgrade wrote the old one after the new one already existed.
    /// The current key is the one that reflects the last session.
    /// </summary>
    [Fact]
    public void TheCurrentSettingsKeyWinsOverTheOldOne()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"open_plans":["/tmp/stale.sqlplan"],"open_tabs":["/tmp/current.sql"]}""")!;

        AppSettingsService.MigrateOpenTabs(settings);

        Assert.Equal(new[] { "/tmp/current.sql" }, settings.OpenTabs);
        Assert.Null(settings.LegacyOpenPlans);
    }

    /// <summary>
    /// Puts one path where the next MainWindow will look for the previous session's tabs.
    /// Load returns the process-wide cached instance, which is the same object the window reads,
    /// and restore clears it again on the way out — so this does not leak into other tests.
    /// </summary>
    private static void Seed(string path)
    {
        var settings = AppSettingsService.Load();
        settings.OpenTabs.Clear();
        settings.OpenTabs.Add(path);
    }

    private static MenuItem ContextMenuItem(TabItem tab, string header) =>
        ((StackPanel)tab.Header!).ContextMenu!.Items
            .OfType<MenuItem>()
            .Single(i => (i.Header as string) == header);

    /// <summary>
    /// Drains the UI queue until the clipboard call finishes. Copy Path starts its write and
    /// does not await it, so the read that follows can be a dispatcher turn early. Fails rather
    /// than hangs if the headless clipboard never answers.
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

    private static IEnumerable<QuerySessionControl> Sessions(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Select(t => t.Content).OfType<QuerySessionControl>();

    private static IEnumerable<PlanViewerControl> Viewers(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>()
            .Select(t => t.Content)
            .OfType<DockPanel>()
            .SelectMany(d => d.Children)
            .OfType<PlanViewerControl>();
}
