using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// "Open in Query Editor" pasted a plan's statement over whatever was in the editor, no
/// questions asked — the one wholesale overwrite in the app that skipped #462's dirty
/// tracking entirely, so a typed-but-unsaved query was simply gone.
///
/// <para>Same testing shape as UnsavedQueryChangesTests: whether to ask is a pure value
/// (<see cref="QuerySessionControl.ReplaceNeedsConfirmation"/>, split out to be testable the
/// way CollectOpenTabEntries was), and the handler is driven directly with the prompt answered
/// by closing it — which is Cancel — or by raising a click on its Replace button.</para>
/// </summary>
public class OpenInEditorOverwriteTests
{
    [Fact]
    public void OnlyADirtyNonEmptyEditorNeedsAsking()
    {
        Assert.False(QuerySessionControl.ReplaceNeedsConfirmation(isDirty: false, currentText: ""));
        Assert.False(QuerySessionControl.ReplaceNeedsConfirmation(isDirty: false, currentText: "SELECT 1;"));

        /* Dirty-but-empty is a buffer the user deleted everything out of. Replacing nothing
           loses nothing, so it stays as frictionless as a clean editor. */
        Assert.False(QuerySessionControl.ReplaceNeedsConfirmation(isDirty: true, currentText: ""));
        Assert.False(QuerySessionControl.ReplaceNeedsConfirmation(isDirty: true, currentText: null));

        Assert.True(QuerySessionControl.ReplaceNeedsConfirmation(isDirty: true, currentText: "SELECT 1;"));
    }

    [Fact]
    public void ACleanEditorIsReplacedWithoutAPrompt()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            try
            {
                var window = new MainWindow();
                window.LoadSqlFile(path);
                var session = LastSession(window);

                /* The window is deliberately never shown: a prompt raised here would need a
                   visible owner and blow up, so replacement going through quietly is itself
                   the no-prompt assertion. This is the everyday path and it must not grow a
                   detour — a clean editor is already on disk. */
                session.OnOpenInEditorRequested(null, "SELECT 99 AS from_plan;");

                Assert.Equal("SELECT 99 AS from_plan;", session.QueryEditor.Text);
                Assert.Equal(0, session.SubTabControl.SelectedIndex);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ADismissedPromptLeavesTheDirtyEditorUntouched()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            MainWindow? window = null;
            QuerySessionControl? session = null;
            try
            {
                window = new MainWindow();
                window.Show(); // the prompt's ShowDialog needs a visible owner
                window.LoadSqlFile(path);
                window.UpdateLayout(); // attach the tab's content so the session can find its window

                session = LastSession(window);
                session.QueryEditor.Text = "SELECT 2; -- typed, not saved";

                session.OnOpenInEditorRequested(null, "SELECT 99 AS from_plan;");
                Dispatcher.UIThread.RunJobs();

                /* The question is up and nothing has been replaced while it is. */
                var prompt = Assert.Single(window.OwnedWindows);
                Assert.Equal("SELECT 2; -- typed, not saved", session.QueryEditor.Text);

                /* Dismissing the prompt is a no, and a no leaves the work exactly where it
                   was — this line is the whole bug, stated as an assertion. */
                prompt.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("SELECT 2; -- typed, not saved", session.QueryEditor.Text);
                Assert.True(session.IsDirty, "the unsaved work is still unsaved, not gone");
            }
            finally
            {
                PutAway(window, session);
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void AnsweringReplaceGoesThroughWithIt()
    {
        HeadlessUi.Run(() =>
        {
            var path = TempSql("SELECT 1;");
            MainWindow? window = null;
            QuerySessionControl? session = null;
            try
            {
                window = new MainWindow();
                window.Show();
                window.LoadSqlFile(path);
                window.UpdateLayout();

                session = LastSession(window);
                session.QueryEditor.Text = "SELECT 2; -- typed, not saved";

                session.OnOpenInEditorRequested(null, "SELECT 99 AS from_plan;");
                Dispatcher.UIThread.RunJobs();

                var prompt = Assert.Single(window.OwnedWindows);
                PromptButton(prompt, "Replace").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                /* An explicit yes is still a yes: the statement lands and the editor sub-tab
                   comes to the front, exactly as the unguarded path always did. */
                Assert.Equal("SELECT 99 AS from_plan;", session.QueryEditor.Text);
                Assert.Equal(0, session.SubTabControl.SelectedIndex);
            }
            finally
            {
                PutAway(window, session);
                File.Delete(path);
            }
        });
    }

    /// <summary>
    /// Digs the named button out of a ConfirmationDialog: content StackPanel, then the button
    /// row, then the caption. Same shape as DetachedUnsavedChangesTests.RedockButton.
    /// </summary>
    private static Button PromptButton(Window prompt, string caption) =>
        ((StackPanel)prompt.Content!).Children.OfType<StackPanel>().Single()
            .Children.OfType<Button>().Single(b => (string?)b.Content == caption);

    /// <summary>
    /// Same job as DetachedUnsavedChangesTests.PutAway: prompts closed, session settled,
    /// window shut — from a finally, because the run where it matters is the run where an
    /// assertion above it failed (#474).
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

    private static QuerySessionControl LastSession(MainWindow window) =>
        window.MainTabControl.Items.OfType<TabItem>()
            .Select(t => t.Content).OfType<QuerySessionControl>().Last();

    private static string TempSql(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sql");
        File.WriteAllText(path, text);
        return path;
    }
}
