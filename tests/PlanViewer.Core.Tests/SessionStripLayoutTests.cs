using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PlanViewer.App;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The band under a session's toolbar holds two things side by side: the view bar, which switches
/// between the editor and the Overview, and the document strip, which holds everything the session
/// has opened. They share one row, and the row must never move.
///
/// <para><b>Why a height is worth four assertions.</b> Everything below the band is the query, the
/// plan or the Overview — the thing being read. A band that is one height empty and another with a
/// document open drops all of that a few pixels the first time you open anything, and a band that
/// wraps as documents accumulate drops it again per row. Neither shows up in the Y-stability tests
/// the toolbar already has: those measure the strip's own top, and it is everything underneath that
/// moves.</para>
/// </summary>
public class SessionStripLayoutTests
{
    /// <summary>
    /// The strip is the same height with nothing open as with twenty things open, and the number is
    /// the one the band has always been.
    /// </summary>
    /// <remarks>
    /// 50 is measured, not chosen: a document header is 48, which is the theme's minimum for a tab
    /// header and well above what a 12px label and a 22px close button need, and the scroller around
    /// it measures two more. It is also what this band was when the editor was one of these headers,
    /// which is the whole of the claim that the chrome above a query is unchanged. The floor in the
    /// XAML is what holds it open while the strip is empty; without it the row falls to the view
    /// bar's own height and everything below jumps down when the first document opens.
    /// </remarks>
    [Fact]
    public void TheStripIsTheSameHeightEmptyAsItIsFull()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var strip = SessionHarness.Strip(session);

                Assert.Empty(strip.Items);
                Assert.Equal(50, strip.Bounds.Height);

                SessionHarness.OpenPlanDocuments(session);
                window.UpdateLayout();
                Assert.Equal(50, strip.Bounds.Height);

                SessionHarness.OpenPlanDocuments(session, 19);
                window.UpdateLayout();
                Assert.Equal(20, strip.Items.Count);
                Assert.Equal(50, strip.Bounds.Height);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// More documents than the row is wide leaves the row exactly as tall, on one line, with the
    /// ones that no longer fit reachable by scrolling to them.
    /// </summary>
    /// <remarks>
    /// The stock items panel wraps, and a spike measured fourteen documents at 700px wrapping into
    /// five rows — 254px of band. In a shared, height-pinned row that either pushes the query down a
    /// row at a time or clips the second line, which puts documents out of reach exactly when there
    /// are enough of them to matter. Swapping the panel for a horizontal one on its own is the other
    /// half of the same trap: the overflow stops wrapping and becomes unreachable instead. That is
    /// why the panel and the scroller landed as one change, and why this asserts all three things.
    /// </remarks>
    [Fact]
    public void MoreDocumentsThanFitStayOnOneRowAndStayReachable()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession(width: 900);
            try
            {
                var strip = SessionHarness.Strip(session);
                var emptyHeight = strip.Bounds.Height;

                var documents = SessionHarness.OpenPlanDocuments(session, 14);
                window.UpdateLayout();

                Assert.Equal(emptyHeight, strip.Bounds.Height);

                // One line: a wrapping panel would have put these on several different Y values.
                var tops = documents.Select(d => d.Bounds.Y).Distinct().ToList();
                Assert.Single(tops);

                var scroller = SessionHarness.StripScroller(session);
                Assert.True(scroller.Extent.Width > scroller.Viewport.Width,
                    $"fourteen documents should not fit: extent {scroller.Extent.Width} " +
                    $"viewport {scroller.Viewport.Width}");

                /* Reachable means the scroller can be taken all the way to the end of its content,
                   which is where the last header is. A panel swapped in without this would report
                   the same extent and refuse to move. */
                var end = scroller.Extent.Width - scroller.Viewport.Width;
                scroller.Offset = new Vector(end, 0);
                window.UpdateLayout();

                Assert.Equal(end, scroller.Offset.X);
                Assert.Equal(emptyHeight, strip.Bounds.Height);
                Assert.Single(documents.Select(d => d.Bounds.Y).Distinct());
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The view bar holds its height across every width the toolbar tests drag the window through,
    /// which is the same sweep that used to move the row below by rewrapping.
    /// </summary>
    /// <remarks>
    /// The bar takes the toolbar's three-part rule verbatim — fixed-height children, no wrapping, no
    /// scrollbar — and its overflow story is that it cannot overflow: two segments and a zoom picker
    /// are nowhere near any width this window can be dragged to. The strip's height is asserted
    /// alongside because the two share a row: a bar that grew would take the row with it, and the
    /// strip, which fills the row, is where that becomes visible.
    /// </remarks>
    [Fact]
    public void TheViewBarHoldsItsHeightAtEveryWidth()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession(width: 2200);
            try
            {
                SessionHarness.OpenPlanDocuments(session, 3);
                ToolbarOverflowTests.Settle(window, session.Overflow);

                var bar = ViewBar(session);
                var strip = SessionHarness.Strip(session);
                var editor = SessionHarness.EditorSegment(session);
                var overview = SessionHarness.OverviewSegment(session);
                var zoom = session.FindControl<ComboBox>("ZoomBox")!;

                var barHeight = bar.Bounds.Height;
                var stripHeight = strip.Bounds.Height;
                var segmentHeight = editor.Bounds.Height;
                var zoomHeight = zoom.Bounds.Height;

                Assert.True(barHeight > 0 && segmentHeight > 0, "nothing was laid out to measure");

                // The window's own floor is 640; below the toolbar's collapse floor is where a row
                // that could wrap would start doing it.
                for (var width = 2200.0; width >= 640; width -= 40)
                {
                    window.Width = width;
                    ToolbarOverflowTests.Settle(window, session.Overflow);

                    Assert.Equal(barHeight, bar.Bounds.Height);
                    Assert.Equal(stripHeight, strip.Bounds.Height);
                    Assert.Equal(segmentHeight, editor.Bounds.Height);
                    Assert.Equal(segmentHeight, overview.Bounds.Height);
                    Assert.Equal(zoomHeight, zoom.Bounds.Height);

                    // One row, all three of them, at every width.
                    Assert.Equal(editor.Bounds.Y, overview.Bounds.Y);
                    Assert.Equal(editor.Bounds.Y, zoom.Bounds.Y);
                }
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The three places the app builds a document header build the same header.
    /// </summary>
    /// <remarks>
    /// A plan tab, a Query Store or History tab and a schema tab are constructed in three different
    /// files, each spelling out its own label and close button, and the sizes were only ever the
    /// same because three authors happened to type the same numbers. A round-1 fix corrected a
    /// fourth header that had drifted — the editor's, which is not in the strip any more — and
    /// shipped without a test, which left the remaining three still agreeing by coincidence.
    /// </remarks>
    [Fact]
    public void EveryKindOfDocumentGetsTheSameSizeOfHeader()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                var plan = SessionHarness.OpenPlanDocuments(session).Single();

                session.AddHistorySubTab("History — hash", SessionHarness.NewHistory());
                SessionHarness.OpenSchemaDocument(session, "Table — dbo.Users", "create table dbo.Users;");
                window.UpdateLayout();

                var documents = SessionHarness.Documents(session);
                Assert.Equal(3, documents.Count);
                Assert.Same(plan, documents[0]);

                foreach (var document in documents)
                {
                    var header = (StackPanel)document.Header!;
                    var label = header.Children.OfType<TextBlock>().First();
                    var close = SessionHarness.CloseButton(document);

                    Assert.Equal(12, label.FontSize);
                    Assert.Equal(11, close.FontSize);
                    Assert.Equal(22, close.Width);
                    Assert.Equal(22, close.Height);
                    Assert.Equal(22, close.Bounds.Width);
                    Assert.Equal(22, close.Bounds.Height);
                }

                // Same label size and same button means same header, and same header means a strip
                // whose rows do not step up and down as you open different kinds of thing.
                var heights = documents.Select(d => ((StackPanel)d.Header!).Bounds.Height)
                    .Distinct().ToList();
                Assert.Single(heights);

                var tabHeights = documents.Select(d => d.Bounds.Height).Distinct().ToList();
                Assert.Single(tabHeights);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The band's left half, which the XAML leaves unnamed because nothing but a test has ever
    /// needed to hold it: the one Border the session's own layout grid puts in row 1, column 0.
    /// </summary>
    /// <remarks>
    /// Found through the strip's parent rather than by searching the session, because a document
    /// open in the row below has a whole control's worth of its own grid rows in it — searching
    /// descendants finds those too, and the first version of this did.
    /// </remarks>
    private static Border ViewBar(QuerySessionControl session) =>
        SessionHarness.Strip(session).GetVisualParent()!.GetVisualChildren().OfType<Border>()
            .Single(b => Grid.GetRow(b) == 1 && Grid.GetColumn(b) == 0);
}
