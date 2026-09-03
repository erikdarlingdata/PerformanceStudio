using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using PlanViewer.App;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #458: a user asked for File > Open Query, not knowing the app already opened .sql files —
/// the menu item was labelled "Open .sqlplan..." and said nothing about queries. The feature
/// that shipped is the pair of menu items, and what has to keep working underneath is that a
/// .sql file lands in a query session and can be written back out again.
///
/// These drive LoadSqlFile and SaveQueryToPath rather than the Click handlers, because the
/// handlers exist to raise a file picker and a picker cannot be answered headlessly. The
/// picker chooses a path; everything after that choice is what these cover.
/// </summary>
public class OpenSaveQueryTests
{
    [Fact]
    public void OpeningASqlFileFillsAQuerySessionAndRemembersWhereItCameFrom()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS opened;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;

                Assert.Equal("SELECT 1 AS opened;", session.QueryEditor.Text);
                Assert.Equal(path, session.SourceFilePath);
                Assert.Equal(Path.GetFileName(path), TabLabel(tab));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void SavingWritesTheEditorTextAndRetitlesTheTab()
    {
        HeadlessUi.Run(() =>
        {
            var opened = TempSql("SELECT 1;");
            var savedAs = Path.Combine(Path.GetTempPath(), $"renamed_{Path.GetFileName(opened)}");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(opened);

                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2 AS edited;";

                Assert.True(window.SaveQueryToPath(tab, session, savedAs));

                Assert.Equal("SELECT 2 AS edited;", File.ReadAllText(savedAs));
                /* Save-as retargets the session: a second save has to land on the new file,
                   not silently keep overwriting the one it was opened from. */
                Assert.Equal(savedAs, session.SourceFilePath);
                Assert.Equal(Path.GetFileName(savedAs), TabLabel(tab));
                Assert.Equal("SELECT 1;", File.ReadAllText(opened));
            }
            finally
            {
                File.Delete(opened);
                if (File.Exists(savedAs))
                    File.Delete(savedAs);
            }
        });
    }

    [Fact]
    public void ANewQuerySessionHasNoFileUntilItIsSaved()
    {
        HeadlessUi.Run(() =>
        {
            var savedAs = Path.Combine(Path.GetTempPath(), $"fresh_{Path.GetRandomFileName()}.sql");
            try
            {
                var window = new MainWindow();
                window.NewQuery_Click(window, new Avalonia.Interactivity.RoutedEventArgs());

                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;

                /* Nothing to default the Save picker to, which is why it suggests Query.sql. */
                Assert.Null(session.SourceFilePath);

                session.QueryEditor.Text = "SELECT 3;";
                Assert.True(window.SaveQueryToPath(tab, session, savedAs));
                Assert.Equal(savedAs, session.SourceFilePath);
            }
            finally
            {
                if (File.Exists(savedAs))
                    File.Delete(savedAs);
            }
        });
    }

    [Fact]
    public void SavingInPlaceRoundTripsAndLeavesNoStagingFileBehind()
    {
        HeadlessUi.Run(() =>
        {
            var opened = TempSql("SELECT 1;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(opened);

                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2 AS edited;";

                /* Save-in-place — same path in, same path out — is the route the unsaved-
                   changes prompt takes for a file-backed tab, and the one where a botched
                   write costs the user their only copy. It now stages through a sibling
                   .tmp (AtomicFile), which must be gone once the save has landed. */
                Assert.True(window.SaveQueryToPath(tab, session, opened));

                Assert.Equal("SELECT 2 AS edited;", File.ReadAllText(opened));
                Assert.False(session.IsDirty);
                Assert.Equal(opened, session.SourceFilePath);
                Assert.False(File.Exists(opened + ".tmp"), "the staging file must not linger");
            }
            finally
            {
                File.Delete(opened);
            }
        });
    }

    [Fact]
    public void ASaveThatCannotStageItsTempLeavesTheOriginalFileAlone()
    {
        HeadlessUi.Run(() =>
        {
            var opened = TempSql("SELECT 1;");
            /* A directory squatting where AtomicFile stages its temp, so the staging write
               throws while the target file itself is perfectly writable. */
            var tmpBlocker = opened + ".tmp";
            Directory.CreateDirectory(tmpBlocker);
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(opened);

                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2 AS edited;";

                /* The point of routing SaveQueryToPath through AtomicFile: a save that dies
                   before it finishes must not have truncated the user's file first. Under
                   the old truncate-then-write this save would have gone straight to the
                   target and "succeeded" — failing here, with the original bytes untouched
                   and the session still dirty, is the new contract. */
                Assert.False(window.SaveQueryToPath(tab, session, opened));

                Assert.Equal("SELECT 1;", File.ReadAllText(opened));
                Assert.True(session.IsDirty, "a save that threw has not saved anything");
            }
            finally
            {
                Directory.Delete(tmpBlocker);
                File.Delete(opened);
            }
        });
    }

    [Fact]
    public void AFailedSaveLeavesTheSessionPointingAtItsOriginalFile()
    {
        HeadlessUi.Run(() =>
        {
            var opened = TempSql("SELECT 1;");
            /* A directory that does not exist: File.WriteAllText throws, and the session must not
               come away believing it now lives somewhere it was never written. */
            var unwritable = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nope.sql");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(opened);

                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;

                Assert.False(window.SaveQueryToPath(tab, session, unwritable));
                Assert.Equal(opened, session.SourceFilePath);
                Assert.Equal(Path.GetFileName(opened), TabLabel(tab));
            }
            finally
            {
                File.Delete(opened);
            }
        });
    }

    /// <summary>
    /// The cold-start argv route, exactly as the constructor takes it. This was the one path
    /// still hard-wired to LoadPlanFile — the pipe from a second instance, drag-and-drop, and
    /// session restore all routed by extension — so "PerformanceStudio.exe query.sql" (a shell
    /// open, a double-clicked association) greeted the user with "The XML is not valid" where
    /// their query should have been.
    /// </summary>
    [Fact]
    public void ACommandLineSqlFileLandsInAQuerySessionNotThePlanLoader()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1 AS from_argv;");
            try
            {
                var window = new MainWindow();
                var queryTabsBefore = QueryTabs(window).Count();

                window.OpenFromStartupArgs(new[] { "PerformanceStudio.exe", path });

                Assert.Equal(queryTabsBefore + 1, QueryTabs(window).Count());

                var session = (QuerySessionControl)QueryTabs(window).Last().Content!;
                Assert.Equal("SELECT 1 AS from_argv;", session.QueryEditor.Text);
                Assert.Equal(path, session.SourceFilePath);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The route the constructor was hard-wired for still works through the extension router:
    /// a plan on the command line is still a plan tab.
    /// </summary>
    [Fact]
    public void ACommandLinePlanFileStillOpensAsAPlan()
    {
        HeadlessUi.Run(() =>
        {
            var planPath = Path.Combine(System.AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan");

            var window = new MainWindow();
            var planTabsBefore = PlanTabs(window).Count();

            window.OpenFromStartupArgs(new[] { "PerformanceStudio.exe", planPath });

            Assert.Equal(planTabsBefore + 1, PlanTabs(window).Count());
        });
    }

    /// <summary>
    /// SSMS writes .sql files as UTF-16 LE with a BOM. Opening one always read correctly —
    /// File.ReadAllText honors the mark — but SaveQueryToPath wrote UTF-8 without one, so the
    /// first save-in-place silently transcoded the user's file: every byte changed, the mark
    /// gone, nothing asked. The encoding captured at open now rides the session to the save.
    /// </summary>
    [Fact]
    public void AUtf16FileIsStillUtf16AfterASaveInPlace()
    {
        HeadlessUi.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
            File.WriteAllText(path, "SELECT 1;", Encoding.Unicode);
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2 AS edited;";

                Assert.True(window.SaveQueryToPath(tab, session, path));

                var bytes = File.ReadAllBytes(path);
                Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes.Take(2).ToArray());
                Assert.Equal("SELECT 2 AS edited;", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// The preservation must not cut the other way: a BOM-less file stays BOM-less, and a
    /// scratch query saves as the UTF-8-without-BOM every save always wrote. Stamping marks
    /// onto files that never had one would be the same silent-alteration bug in reverse.
    /// </summary>
    [Fact]
    public void BomlessFilesAndScratchQueriesStillSaveWithoutABom()
    {
        HeadlessUi.Run(() =>
        {
            var bomless = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
            File.WriteAllText(bomless, "SELECT 1;", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var scratch = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
            try
            {
                var window = new MainWindow();

                window.LoadSqlFile(bomless);
                var tab = QueryTabs(window).Last();
                var session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2 AS edited;";
                Assert.True(window.SaveQueryToPath(tab, session, bomless));
                Assert.NotEqual(0xEF, File.ReadAllBytes(bomless)[0]);

                window.NewQuery_Click(window, new Avalonia.Interactivity.RoutedEventArgs());
                var scratchTab = QueryTabs(window).Last();
                var scratchSession = (QuerySessionControl)scratchTab.Content!;
                scratchSession.QueryEditor.Text = "SELECT 3;";
                Assert.True(window.SaveQueryToPath(scratchTab, scratchSession, scratch));
                Assert.NotEqual(0xEF, File.ReadAllBytes(scratch)[0]);
            }
            finally
            {
                File.Delete(bomless);
                if (File.Exists(scratch))
                    File.Delete(scratch);
            }
        });
    }

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }

    private static System.Collections.Generic.IEnumerable<TabItem> PlanTabs(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Where(t => t.Content is DockPanel);

    private static System.Collections.Generic.IEnumerable<TabItem> QueryTabs(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Where(t => t.Content is QuerySessionControl);

    private static string? TabLabel(TabItem tab) =>
        tab.Header is StackPanel header && header.Children.Count > 0 && header.Children[0] is TextBlock text
            ? text.Text
            : null;
}
