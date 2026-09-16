using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The session's own shortcuts: Ctrl+F4 to close the document being looked at, Ctrl+1 and Ctrl+2 to
/// reach the two views without the mouse.
///
/// <para><b>Every keystroke here is pressed inside the session</b>, at the text area where the caret
/// actually is, rather than raised on the session itself. That is the difference between pinning
/// these shortcuts and pinning the handler: a key pressed in a session runs the window's tunnel
/// handler first — the one that claims Ctrl+W, Ctrl+N and Ctrl+Tab and marks them handled before the
/// session sees anything — and then bubbles up through AvaloniaEdit, which is why the window's own
/// hotkeys had to tunnel in the first place. A binding that works only when the route is skipped is
/// a binding the user does not have.</para>
/// </summary>
public class SessionKeyboardTests
{
    /// <summary>
    /// Ctrl+F4 closes the document on screen, and on a view it closes nothing at all — in
    /// particular not the top-level tab, which is where Ctrl+W would have gone and is the surprise
    /// this shortcut exists to avoid.
    /// </summary>
    [Fact]
    public void CtrlF4ClosesTheSelectedDocumentAndNothingElse()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var documents = SessionHarness.OpenPlanDocuments(session, 2);
                var keys = session.QueryEditor.TextArea;

                /* The session has a tab of its own to lose, which is what makes the second half of
                   this test an assertion rather than a sentence about an empty window. */
                var topLevelTabs = window.MainTabControl.Items.Count;
                Assert.True(topLevelTabs > 0);

                Assert.Same(documents[1], SessionHarness.Strip(session).SelectedItem);
                SessionHarness.PressKey(keys, Key.F4, KeyModifiers.Control);

                Assert.Equal([documents[0]], SessionHarness.Strip(session).Items.Cast<TabItem>());
                Assert.Equal(topLevelTabs, window.MainTabControl.Items.Count);

                // Now with a view showing, where there is no selected document to be closing.
                SessionHarness.EditorSegment(session).IsChecked = true;
                SessionHarness.PressKey(keys, Key.F4, KeyModifiers.Control);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal([documents[0]], SessionHarness.Strip(session).Items.Cast<TabItem>());
                Assert.Equal(topLevelTabs, window.MainTabControl.Items.Count);
                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A plan viewer holds a registration that only closing it releases, so the keyboard close has
    /// to be the same close the ✕ is rather than a second one that skips the release half.
    /// </summary>
    [Fact]
    public void CtrlF4ReleasesThePlanItCloses()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var document = SessionHarness.OpenPlanDocuments(session).Single();
                var viewer = (PlanViewerControl)document.Content!;
                Assert.NotNull(viewer.CurrentPlan);

                SessionHarness.PressKey(session.QueryEditor.TextArea, Key.F4, KeyModifiers.Control);

                Assert.Empty(SessionHarness.Strip(session).Items);
                Assert.Null(viewer.CurrentPlan);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Ctrl+1 is the Editor segment, including the part of pressing that segment which is not about
    /// changing the surface: arriving at the editor is what puts the caret back in it, so asking for
    /// an editor you are already on has to do that much by itself or the shortcut means two
    /// different things depending on where you were.
    /// </summary>
    [Fact]
    public void Ctrl1SelectsTheEditorAndPutsTheCaretBackInIt()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var document = SessionHarness.OpenPlanDocuments(session).Single();
                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);

                SessionHarness.PressKey(SessionHarness.Strip(session), Key.D1, KeyModifiers.Control);

                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
                Assert.True(SessionHarness.EditorSegment(session).IsChecked);
                Assert.True(SessionHarness.EditorView(session).IsVisible);
                Assert.Null(SessionHarness.Strip(session).SelectedItem);
                Assert.True(session.QueryEditor.TextArea.IsFocused, "the caret went with it");

                // Focus somewhere else in the session, then ask for the editor it is already on.
                var connect = session.FindControl<Button>("ConnectButton")!;
                connect.Focus();
                Assert.False(session.QueryEditor.TextArea.IsFocused);

                SessionHarness.PressKey(connect, Key.D1, KeyModifiers.Control);

                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
                Assert.True(session.QueryEditor.TextArea.IsFocused,
                    "Ctrl+1 on the editor you are already looking at did nothing at all");

                // And the document it left is still open, because this is a view switch, not a close.
                Assert.Equal([document], SessionHarness.Strip(session).Items.Cast<TabItem>());
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Ctrl+2 is the Overview segment, and gets there by running the segment's own implementation
    /// rather than a keyboard copy of it.
    /// </summary>
    [Fact]
    public void Ctrl2SelectsTheOverviewView()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);

                SessionHarness.PressKey(session.QueryEditor.TextArea, Key.D2, KeyModifiers.Control);

                Assert.Equal(QuerySessionControl.SessionSurface.Overview, session.SelectedView);
                Assert.True(SessionHarness.OverviewSegment(session).IsChecked);
                Assert.False(SessionHarness.EditorSegment(session).IsChecked);
                Assert.True(SessionHarness.OverviewHost(session).IsVisible);
                Assert.IsType<QueryStoreOverviewControl>(SessionHarness.OverviewHost(session).Content);

                SessionHarness.StopOverviewLoad(SessionHarness.OverviewView(session));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Asking for the Overview on a session that has never connected asks for a server, and
    /// cancelling that leaves you exactly where you were with the bar still saying so.
    ///
    /// <para>This is the parity that made Ctrl+2 call the segment's handler instead of writing its
    /// own: the surface machine latches a segment the moment it is pressed, so a cancelled
    /// connection has to put the bar back, and a keyboard path with its own idea of opening the
    /// Overview would have had to remember that too.</para>
    /// </summary>
    [Fact]
    public void Ctrl2WithNoServerAsksForOneAndACancelChangesNothing()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PressKey(session.QueryEditor.TextArea, Key.D2, KeyModifiers.Control);
                Dispatcher.UIThread.RunJobs();

                var dialog = Assert.Single(window.OwnedWindows);

                // Dismissing is a no, and a no is not a reason to move the user.
                dialog.Close();
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
                Assert.True(SessionHarness.EditorSegment(session).IsChecked);
                Assert.False(SessionHarness.OverviewSegment(session).IsChecked,
                    "the segment latched when the shortcut was pressed and nothing put it back");
                Assert.Null(SessionHarness.OverviewHost(session).Content);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }
}
