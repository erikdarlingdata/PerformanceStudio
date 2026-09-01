using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Dialogs;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #462: a query session had no idea whether it had been edited. Open a .sql, change it, close
/// the tab or the window, and the edit was gone with nothing asked and nothing marked.
///
/// The dirty flag is the whole feature — the close prompt and the modified marker are both
/// consumers of it — so most of what is worth testing is the flag itself and the decision the
/// prompt feeds. The dialog cannot be clicked headlessly, which is why the answer is a value
/// (<see cref="UnsavedChangesChoice"/>) handed to <see cref="MainWindow.DecideClose"/> rather
/// than something only the dialog knows. Same reasoning as OpenSaveQueryTests and the file
/// picker: what the human chooses is untestable, everything downstream of the choice is not.
/// </summary>
public class UnsavedQueryChangesTests
{
    [Fact]
    public void EditingAFileBackedQueryMakesItDirtyAndMarksTheTab()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = LastQueryTab(window);
                var session = (QuerySessionControl)tab.Content!;

                Assert.False(session.IsDirty, "a file just loaded is what is on disk");
                Assert.Equal("\u2715", CloseButton(tab).Content);

                session.QueryEditor.Text = "SELECT 2; -- unsaved work";

                Assert.True(session.IsDirty);
                Assert.True(MainWindow.HasUnsavedChanges(tab));
                Assert.Equal("\u25CF", CloseButton(tab).Content);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void TypingBackToTheOriginalTextClearsTheDirtyState()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);

                var tab = LastQueryTab(window);
                var session = (QuerySessionControl)tab.Content!;

                session.QueryEditor.Text = "SELECT 2;";
                Assert.True(session.IsDirty);

                /* The point of comparing text rather than latching a bool on the first keystroke:
                   an undo back to the file's contents is not unsaved work, and being asked about it
                   is what teaches people to click through save prompts without reading them. */
                session.QueryEditor.Text = "SELECT 1;";

                Assert.False(session.IsDirty);
                Assert.Equal("\u2715", CloseButton(tab).Content);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void AScratchTabWithTypedContentIsDirtyAndItsSaveGoesThroughSaveAs()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            window.NewQuery_Click(window, new RoutedEventArgs());

            var tab = LastQueryTab(window);
            var session = (QuerySessionControl)tab.Content!;

            Assert.False(session.IsDirty, "an empty new query has nothing to lose");

            session.QueryEditor.Text = "SELECT 'never saved';";

            Assert.True(session.IsDirty);
            Assert.Null(session.SourceFilePath);
            Assert.Equal("\u25CF", CloseButton(tab).Content);

            /* Nowhere to write it, so Save has to become Save As. Saving "in place" over a null
               path is the one outcome that would throw away the query it was trying to rescue. */
            Assert.Equal(
                MainWindow.CloseAction.SaveAs,
                MainWindow.DecideClose(UnsavedChangesChoice.Save, hasFile: false));
        });
    }

    [Fact]
    public void ASuccessfulSaveSettlesTheTabAndAFailedOneDoesNot()
    {
        HeadlessUi.Run(() =>
        {
            var opened = TempSql("SELECT 1;");
            var savedAs = Path.Combine(Path.GetTempPath(), $"saved_{Path.GetRandomFileName()}.sql");
            /* A directory that does not exist, so File.WriteAllText throws. */
            var unwritable = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nope.sql");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(opened);

                var tab = LastQueryTab(window);
                var session = (QuerySessionControl)tab.Content!;
                session.QueryEditor.Text = "SELECT 2 AS edited;";

                Assert.False(window.SaveQueryToPath(tab, session, unwritable));
                Assert.True(session.IsDirty, "a save that threw has not saved anything");
                Assert.Equal("\u25CF", CloseButton(tab).Content);

                Assert.True(window.SaveQueryToPath(tab, session, savedAs));
                Assert.False(session.IsDirty);
                Assert.Equal("\u2715", CloseButton(tab).Content);
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
    public void TheModifiedMarkerGivesWayToTheCloseButtonUnderThePointer()
    {
        /* Josh's ask, and the reason the marker is a swap rather than an extra glyph: a dot you
           cannot click is a tab you cannot close. */
        Assert.Equal("\u25CF", MainWindow.CloseButtonGlyph(isDirty: true, isPointerOver: false));
        Assert.Equal("\u2715", MainWindow.CloseButtonGlyph(isDirty: true, isPointerOver: true));
        Assert.Equal("\u2715", MainWindow.CloseButtonGlyph(isDirty: false, isPointerOver: false));
        Assert.Equal("\u2715", MainWindow.CloseButtonGlyph(isDirty: false, isPointerOver: true));
    }

    [Fact]
    public void ClosingTheWindowAsksAboutEveryTabNotJustTheSelectedOne()
    {
        HeadlessUi.Run(() =>
        {
            var first = TempSql("SELECT 1;");
            var second = TempSql("SELECT 2;");
            var third = TempSql("SELECT 3;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(first);
                window.LoadSqlFile(second);
                window.LoadSqlFile(third);

                var tabs = QueryTabs(window).ToList();
                var firstTab = tabs[^3];
                var secondTab = tabs[^2];
                var thirdTab = tabs[^1];

                Edit(firstTab, "SELECT 1; -- edited");
                Edit(secondTab, "SELECT 2; -- edited");

                /* The tab in front is the clean one. The edits at risk are behind it, which is the
                   ordinary case and exactly what a window-close prompt is for. */
                window.MainTabControl.SelectedItem = thirdTab;

                Assert.Equal(
                    new[] { firstTab, secondTab },
                    window.TabsWithUnsavedChanges());
            }
            finally
            {
                File.Delete(first);
                File.Delete(second);
                File.Delete(third);
            }
        });
    }

    [Fact]
    public void PlanTabsAreNeverDirty()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow();
            window.LoadPlanFile(Path.Combine("Plans", "row_goal_plan.sqlplan"));

            /* Plans are read-only. Nothing about opening one should make the app ask whether to
               save it, and the restored-on-startup query tab must not be dragged in either. */
            Assert.Empty(window.TabsWithUnsavedChanges());
        });
    }

    [Fact]
    public void CancelRefusesTheCloseAndDontSaveGoesThroughWithIt()
    {
        Assert.Equal(
            MainWindow.CloseAction.Cancel,
            MainWindow.DecideClose(UnsavedChangesChoice.Cancel, hasFile: true));
        Assert.Equal(
            MainWindow.CloseAction.Cancel,
            MainWindow.DecideClose(UnsavedChangesChoice.Cancel, hasFile: false));

        Assert.Equal(
            MainWindow.CloseAction.Close,
            MainWindow.DecideClose(UnsavedChangesChoice.DontSave, hasFile: true));
        Assert.Equal(
            MainWindow.CloseAction.Close,
            MainWindow.DecideClose(UnsavedChangesChoice.DontSave, hasFile: false));

        Assert.Equal(
            MainWindow.CloseAction.SaveInPlace,
            MainWindow.DecideClose(UnsavedChangesChoice.Save, hasFile: true));
    }

    private static void Edit(TabItem tab, string text) =>
        ((QuerySessionControl)tab.Content!).QueryEditor.Text = text;

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }

    private static System.Collections.Generic.IEnumerable<TabItem> QueryTabs(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>().Where(t => t.Content is QuerySessionControl);

    private static TabItem LastQueryTab(MainWindow window) => QueryTabs(window).Last();

    private static Button CloseButton(TabItem tab) =>
        ((StackPanel)tab.Header!).Children.OfType<Button>().Single();
}
