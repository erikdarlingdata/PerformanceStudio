using System.IO;
using System.Linq;
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

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }

    private static System.Collections.Generic.IEnumerable<TabItem> QueryTabs(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Where(t => t.Content is QuerySessionControl);

    private static string? TabLabel(TabItem tab) =>
        tab.Header is StackPanel header && header.Children.Count > 0 && header.Children[0] is TextBlock text
            ? text.Text
            : null;
}
