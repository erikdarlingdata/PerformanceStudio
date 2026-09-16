using System.Linq;
using Avalonia.Controls;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A query session shows exactly one surface — the editor, the Overview, or a document — and the
/// rule that keeps the rest of it simple is DESELECT-ON-LEAVE: a view never coexists with a selected
/// document. Almost everything here is a consequence of that one sentence, which is why these are
/// tests rather than a paragraph: each consequence is invisible from the others, and several of them
/// fail silently.
///
/// <para><b>The one that fails silently loudest.</b> The strip's SelectionMode is set to Single from
/// code, through the property registry, because the property is unreachable from XAML or a theme.
/// Its default coerces a deselect straight back without complaining. Lose that line and the strip
/// keeps a document selected underneath every view, the host keeps presenting it, and nothing
/// anywhere raises — which is what the first test below exists to say out loud.</para>
/// </summary>
public class SessionSurfaceTests
{
    /// <summary>
    /// Leaving the documents behind really does leave nothing selected, on a strip that still has
    /// documents in it, and it stays that way once the layout pass has had its say.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. A strip that has been emptied is deselected for a reason that
    /// has nothing to do with this, so the two documents have to still be there for the assertion to
    /// be about deselection at all. And the coercion this guards against happens during layout, not
    /// at the assignment — the default mode lets the write appear to work and puts the selection
    /// back afterwards.
    /// </remarks>
    [Fact]
    public void LeavingTheDocumentsDeselectsAStripThatStillHasSome()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.OpenPlanDocuments(session, 2);
                var strip = SessionHarness.Strip(session);

                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);
                Assert.NotNull(strip.SelectedItem);

                SessionHarness.EditorSegment(session).IsChecked = true;

                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
                Assert.Equal(2, strip.Items.Count);
                Assert.Null(strip.SelectedItem);
                Assert.Null(SessionHarness.DocumentHost(session).Content);

                window.UpdateLayout();

                Assert.Equal(2, strip.Items.Count);
                Assert.Null(strip.SelectedItem);
                Assert.Null(SessionHarness.DocumentHost(session).Content);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Coming back to the document you left is an ordinary selection change, because you left it
    /// deselected. The design note worth pinning is what that dissolved: with a view able to sit on
    /// top of a still-selected document, returning to it would have been a press on something
    /// already selected, which raises nothing and would have needed a pointer handler of its own.
    /// </summary>
    [Fact]
    public void ReturningToTheDocumentYouLeftShowsItAgain()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var document = SessionHarness.OpenPlanDocuments(session).Single();
                var content = document.Content;

                SessionHarness.EditorSegment(session).IsChecked = true;
                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);

                SessionHarness.PressHeader(session, document);

                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);
                Assert.Same(content, SessionHarness.DocumentHost(session).Content);
                Assert.True(SessionHarness.DocumentHost(session).IsVisible);
                Assert.False(SessionHarness.EditorView(session).IsVisible);
                Assert.False(SessionHarness.EditorSegment(session).IsChecked);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The four commands that act on the plan you are looking at go dead when you stop looking at
    /// one. That used to be a claim about a sub-tab index; it is now a claim about the surface, and
    /// it is the reason nothing else in the app may write these four properties.
    ///
    /// <para>Both halves are asserted here on purpose. A test that only watched them go dark would
    /// pass just as happily against a session where they were never lit — which is what the pin this
    /// replaces did, by driving a toolbar with no documents open at all. So they are dark, then a
    /// plan opens and all four light, and only then does leaving for the editor put them back.</para>
    /// </summary>
    [Fact]
    public void ThePlanCommandsLightWithAPlanAndGoDarkOnAView()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                /* Run Repro needs a server as well as a plan — it re-runs the query — so a session
                   with no connection could never light all four however many plans it opened. */
                SessionHarness.PretendConnected(session);

                var copyRepro = session.FindControl<Button>("CopyReproButton")!;
                var runRepro = session.FindControl<Button>("GetActualPlanButton")!;
                var humanAdvice = session.FindControl<Button>("HumanAdviceButton")!;
                var robotAdvice = session.FindControl<Button>("RobotAdviceButton")!;
                var commands = new[] { copyRepro, runRepro, humanAdvice, robotAdvice };

                Assert.All(commands, b => Assert.False(b.IsEnabled, "nothing is open yet"));

                SessionHarness.OpenPlanDocuments(session);

                Assert.True(copyRepro.IsEnabled, "Copy Repro with a plan selected");
                Assert.True(runRepro.IsEnabled, "Run Repro with a plan selected and a server");
                Assert.True(humanAdvice.IsEnabled, "Human Advice with a plan selected");
                Assert.True(robotAdvice.IsEnabled, "Robot Advice with a plan selected");

                SessionHarness.EditorSegment(session).IsChecked = true;

                Assert.All(commands, b => Assert.False(b.IsEnabled,
                    "a command that acts on the selected plan, offered on a surface with no plan on it"));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Closing the document you are looking at, with others still open, moves you to a neighbour and
    /// leaves you among the documents.
    /// </summary>
    /// <remarks>
    /// This rule exists because of what a deselectable strip does on removal: it clears the
    /// selection outright rather than moving it along, even when documents remain. Without the rule
    /// the user closes one of three plans and lands on an empty surface with two plans still in the
    /// strip above it.
    /// </remarks>
    [Fact]
    public void ClosingTheSelectedDocumentAmongSeveralSelectsANeighbour()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var documents = SessionHarness.OpenPlanDocuments(session, 3);
                var strip = SessionHarness.Strip(session);

                SessionHarness.PressHeader(session, documents[1]);
                SessionHarness.CloseFromHeader(documents[1]);

                // Whatever took its place in the strip, which is the one that was after it.
                Assert.Equal([documents[0], documents[2]], strip.Items.Cast<TabItem>());
                Assert.Same(documents[2], strip.SelectedItem);
                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);
                Assert.Same(documents[2].Content, SessionHarness.DocumentHost(session).Content);

                // And at the end of the strip there is no "after it", so it is the one before.
                SessionHarness.CloseFromHeader(documents[2]);

                Assert.Same(documents[0], strip.SelectedItem);
                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Closing the last document lands on the editor, with its get-started panel back if there is
    /// nothing typed — the session has nowhere else to be.
    /// </summary>
    [Fact]
    public void ClosingTheLastDocumentLandsOnTheEditor()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var document = SessionHarness.OpenPlanDocuments(session).Single();
                Assert.False(SessionHarness.EmptyStateOverlay(session).IsVisible);

                SessionHarness.CloseFromHeader(document);

                Assert.Empty(SessionHarness.Strip(session).Items);
                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
                Assert.True(SessionHarness.EditorView(session).IsVisible);
                Assert.True(SessionHarness.EditorSegment(session).IsChecked);
                Assert.True(SessionHarness.EmptyStateOverlay(session).IsVisible,
                    "an empty editor with nothing open is what the get-started panel is for");
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Closing a document while looking at a view moves nothing. The strip and its close buttons are
    /// visible from every surface, so this is a gesture the user really can make, and answering it
    /// by yanking them onto the documents would be the app deciding where they wanted to be.
    /// </summary>
    [Fact]
    public void ClosingADocumentFromAViewLeavesTheViewAlone()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var documents = SessionHarness.OpenPlanDocuments(session, 2);
                SessionHarness.EditorSegment(session).IsChecked = true;

                SessionHarness.CloseFromHeader(documents[0]);

                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);
                Assert.Null(SessionHarness.Strip(session).SelectedItem);
                Assert.Equal([documents[1]], SessionHarness.Strip(session).Items.Cast<TabItem>());
                Assert.True(SessionHarness.EditorView(session).IsVisible);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A plan arriving in the document already on screen reaches the screen.
    ///
    /// <para>This is the one failure the restructure could have shipped silently. A plan is captured
    /// by swapping the tab's content from a progress spinner in place — no selection changes, no
    /// surface changes — so a host fed by anything except the strip's own SelectedContent would keep
    /// showing the spinner forever. What makes it silent is the second half below: the tab count,
    /// the labels and the plans behind them are all identical before and after, so every test that
    /// counts tabs passes over the top of it.</para>
    /// </summary>
    [Fact]
    public void APlanArrivingInTheSelectedDocumentReachesTheHost()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var document = SessionHarness.OpenPlanDocuments(session).Single();
                var strip = SessionHarness.Strip(session);
                var host = SessionHarness.DocumentHost(session);

                var before = document.Content;
                var tabsBefore = session.GetPlanTabs().Select(p => p.label).ToList();
                Assert.Same(before, host.Content);

                session.ShowCapturedPlan(document, SessionHarness.SamplePlanXml(),
                    "Plan 1", "select 1;");

                Assert.NotSame(before, document.Content);
                Assert.Same(document.Content, host.Content);
                Assert.Same(document, strip.SelectedItem);
                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);

                /* The half that says why the assertion above is on the host's content rather than
                   on the strip: reading the strip cannot see this happen. */
                Assert.Equal(tabsBefore, session.GetPlanTabs().Select(p => p.label));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A plan arriving in a document the user is not looking at presents nothing until they come
    /// back to it, and then presents the plan rather than what was there before.
    /// </summary>
    /// <remarks>
    /// The first half is by design and will look like a bug to whoever debugs it: with nothing
    /// selected the strip has no live subscription to any container's content, so an in-place swap
    /// raises nothing at all. That is correct — there is no surface for it to reach. Returning
    /// re-subscribes and the current value is pushed on subscription, which is the entire mechanism
    /// behind the second half.
    /// </remarks>
    [Fact]
    public void APlanArrivingBehindAViewShowsUpOnTheWayBack()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var documents = SessionHarness.OpenPlanDocuments(session, 2);
                var unselected = documents[0];
                var host = SessionHarness.DocumentHost(session);

                SessionHarness.EditorSegment(session).IsChecked = true;
                Assert.Null(host.Content);

                session.ShowCapturedPlan(unselected, SessionHarness.SamplePlanXml(),
                    "Plan 1", "select 1;");

                Assert.Null(host.Content);
                Assert.Equal(QuerySessionControl.SessionSurface.Editor, session.SelectedView);

                SessionHarness.PressHeader(session, unselected);

                Assert.Same(unselected.Content, host.Content);
                Assert.Equal(QuerySessionControl.SessionSurface.Documents, session.SelectedView);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A removal that empties the strip still empties the status bar.
    ///
    /// <para>Cancelling a running query writes nothing to the strip on purpose — the spinner tab
    /// disappearing is the whole answer — and relies on the removal to take down whatever was there
    /// before. That was free while the editor sat in the strip, because something was always
    /// selected afterwards. It is not free now: the removal can leave the collection with nothing in
    /// it, and this is the case where a message about the thing that just went away would otherwise
    /// stay up over the editor.</para>
    /// </summary>
    [Fact]
    public void EmptyingTheStripStillClearsTheStatus()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var status = session.FindControl<TextBlock>("StatusText")!;
                var document = SessionHarness.OpenPlanDocuments(session).Single();

                Assert.False(string.IsNullOrEmpty(status.Text),
                    "opening a Query Store plan reports itself, which is what gives this something to clear");

                SessionHarness.CloseFromHeader(document);

                Assert.Empty(SessionHarness.Strip(session).Items);
                Assert.True(string.IsNullOrEmpty(status.Text), $"the strip still said \"{status.Text}\"");
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }
}
