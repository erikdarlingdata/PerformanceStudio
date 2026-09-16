using System.Linq;
using Avalonia;
using Avalonia.Controls;
using PlanViewer.App.Controls;
using Xunit;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A plan landing in a document whose header has been scrolled out of the strip still reaches the
/// screen.
/// </summary>
/// <remarks>
/// <para>The document host is fed from <c>SelectedContent</c>, which the TabControl maintains by
/// subscribing to the selected container's own Content — and it can only do that for a container it
/// has realised. That is why the header panel is a plain <see cref="StackPanel"/> and the comment
/// beside it says to keep it one. This covers the state where the difference would show: the
/// selected document's header scrolled out of the strip when its plan arrives.</para>
///
/// <para>What this does NOT do is guard the panel choice, and the comment it replaced claimed
/// otherwise. Swapping in a <c>VirtualizingStackPanel</c> was measured: this test still passes,
/// while four of the surface pins fail outright (a plan reaching the host, returning to a document,
/// a plan arriving behind a view, and the neighbour picked on close). The panel is already guarded,
/// loudly, by tests that were not written for it — so the worry that virtualising would pass the
/// whole suite was wrong, and this is a scenario test rather than the canary.</para>
/// </remarks>
public class ScrolledOutDocumentTests
{
    [Fact]
    public void APlanArrivingInAScrolledOutDocumentStillReachesTheHost()
    {
        HeadlessUi.Run(() =>
        {
            // Narrow enough that fourteen headers cannot all fit, so there is something to scroll.
            var (window, session) = SessionHarness.NewSession(width: 700);
            try
            {
                var documents = SessionHarness.OpenPlanDocuments(session, 14);
                var scrolledOut = documents[0];
                SessionHarness.PressHeader(session, scrolledOut);
                window.UpdateLayout();

                var scroller = SessionHarness.StripScroller(session);
                Assert.True(scroller.Extent.Width > scroller.Viewport.Width,
                    "fourteen documents should overflow a 700px strip, or this proves nothing");

                scroller.Offset = new Vector(scroller.Extent.Width - scroller.Viewport.Width, 0);
                window.UpdateLayout();

                var header = (Control)scrolledOut.Header!;
                var headerRight = header.TranslatePoint(new Point(header.Bounds.Width, 0), scroller)!.Value.X;
                Assert.True(headerRight < 0,
                    $"the selected document's header should be scrolled off the left, was at {headerRight}");

                /* The spinner-to-plan swap, which changes no selection and raises no
                   SelectionChanged - the path that only SelectedContent covers. */
                session.ShowCapturedPlan(scrolledOut, SessionHarness.SamplePlanXml(), "Plan 1", "select 1;");
                window.UpdateLayout();

                var host = SessionHarness.DocumentHost(session);
                Assert.IsType<PlanViewerControl>(scrolledOut.Content);
                Assert.Same(scrolledOut.Content, host.Content);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }
}
