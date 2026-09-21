using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The app's scrollbars have now been pinned twice, in opposite directions, and the second pin
/// only makes sense if you know about the first.
///
/// <para><b>#464.</b> Every scrollbar collapsed to a sliver until hovered, because Fluent defaults
/// <c>AllowAutoHide</c> to true and nothing set it otherwise. Its idle thumb is
/// <c>scaleX(0.125)</c> of a 16px bar — two pixels. The complaint, about a horizontal bar, where it
/// hurts most: the thing you need to grab is "a couple of pixels tall until after you have found
/// it". The fix turned auto-hide off everywhere, and this file originally asserted exactly that.
/// </para>
///
/// <para><b>What that fix cost.</b> A bar that never hides is a bar that always reserves layout
/// space, and the app nests scrollers. The plan view ended up showing three horizontal bars stacked
/// at its bottom edge at once, each eating a strip of the pane it belonged to. Always-expanded made
/// the bars grabbable by making them permanent furniture.</para>
///
/// <para><b>The contract now.</b> Both, rather than either. Auto-hide is on, so ScrollViewer's own
/// theme spans the content presenter under the bars and they overlay instead of displacing content.
/// The geometry is redefined rather than inherited, so "hidden" still means a 7px rail — half of a
/// 14px bar — that is drawn at all times and opens to the full 14px under the pointer. #464's
/// reporter can still grab it; the plan view gets its strips back.</para>
///
/// <para>Both halves live in App.axaml and neither is correct alone, which is why the thickness is
/// asserted here and not just the flag. Everything is measured off controls that have actually been
/// through layout, never off the App.axaml text: a style that compiles is not a style that matches,
/// and a bare unstyled control reports the Fluent default and would pass for the wrong reason.</para>
/// </summary>
public class ScrollBarVisibilityTests
{
    /// <summary>
    /// The floor #464 established, in logical pixels. Six is not a preference: at 250% display
    /// scaling it is still 15 physical pixels, which is a comfortable pointer target, and it is the
    /// point below which the bar starts being something you hunt for rather than something you see.
    /// App.axaml currently sits at 7. Lower this only with a live re-drive at high DPI.
    /// </summary>
    private const double MinimumIdleRailThickness = 6.0;

    [Fact]
    public void AScrollViewerOverlaysItsBarsInsteadOfReservingSpaceForThem()
    {
        HeadlessUi.Run(() =>
        {
            var scrollViewer = NewScrollViewer();
            Show(scrollViewer);

            Assert.True(
                scrollViewer.AllowAutoHide,
                "auto-hide is what makes the bars an overlay; without it they reserve layout space " +
                "and stack up on panes that nest scrollers");

            var presenter = scrollViewer.GetVisualDescendants()
                .OfType<ScrollContentPresenter>()
                .Single();

            /* The overlay is not a property you can read. It is ScrollViewer's own theme reacting
               to AllowAutoHide by spanning the presenter across both scrollbar cells, so the only
               honest check is whether the content still gets the full width and height. */
            Assert.Equal(scrollViewer.Bounds.Width, presenter.Bounds.Width);
            Assert.Equal(scrollViewer.Bounds.Height, presenter.Bounds.Height);
        });
    }

    [Fact]
    public void AnIdleScrollBarStillLeavesARailThickEnoughToGrab()
    {
        HeadlessUi.Run(() =>
        {
            var scrollViewer = NewScrollViewer();
            Show(scrollViewer);

            var bars = ScrollBarsOf(scrollViewer);

            Assert.Equal(2, bars.Length);

            foreach (var bar in bars)
            {
                Assert.False(bar.IsExpanded, "nothing is pointing at this bar, so it should be idle");

                Assert.True(
                    IdleRailThickness(bar) >= MinimumIdleRailThickness,
                    $"a {bar.Orientation} bar idles at {IdleRailThickness(bar)}px of rail, under the " +
                    $"{MinimumIdleRailThickness}px floor — this is the #464 regression, and it comes " +
                    "back by changing ScrollBarSize or the thumb scale transform in App.axaml " +
                    "without changing the other");
            }
        });
    }

    /// <summary>
    /// The idle rail has to be narrower than the expanded bar, or "expands on hover" is a story
    /// rather than a behaviour — a rail already at full width would satisfy the floor above and
    /// quietly hand back the always-expanded bars that stacked three deep.
    ///
    /// <para><b>Why the pointer is not simulated here.</b> It would assert nothing. Headless input
    /// resolves a hit through the renderer, and this suite has no Skia — a simulated move over the
    /// bar leaves <c>IsPointerOver</c> false, so the test would pass or fail on the harness rather
    /// than on App.axaml. What can be measured honestly is the geometry the hover swaps between,
    /// and both numbers come off a real styled bar. Whether the pointer feels right at 3840x2400 is
    /// a live re-drive question, not a headless one.</para>
    /// </summary>
    [Fact]
    public void AnIdleScrollBarIsNarrowerThanAnExpandedOne()
    {
        HeadlessUi.Run(() =>
        {
            var scrollViewer = NewScrollViewer();
            Show(scrollViewer);

            var bars = ScrollBarsOf(scrollViewer);

            Assert.Equal(2, bars.Length);

            foreach (var bar in bars)
            {
                Assert.False(bar.IsExpanded, "nothing is pointing at this bar, so it should be idle");

                Assert.True(
                    IdleRailThickness(bar) < ExpandedThickness(bar),
                    $"a {bar.Orientation} bar idles at {IdleRailThickness(bar)}px and expands to " +
                    $"{ExpandedThickness(bar)}px — with nothing to expand into, this is an " +
                    "always-on bar wearing an overlay's clothes");
            }
        });
    }

    /// <summary>
    /// Two selectors are needed in App.axaml, not one, and that is the whole reason this case is
    /// asserted separately. A ScrollViewer rule alone looks like it covers the app and does not:
    /// DataGrid does not scroll through a ScrollViewer, its template hosts PART_HorizontalScrollbar
    /// and PART_VerticalScrollbar as bare <see cref="ScrollBar"/>s, and it assigns their
    /// <c>AllowAutoHide</c> in code from the ATTACHED ScrollViewer property it reads off itself —
    /// where a local value outranks any style. A style that compiles is not a style that matches.
    ///
    /// <para><b>Why only the flag is checked here.</b> A DataGrid decides it overflows by measuring
    /// its rows, rows measure their text, and text needs a font — which this suite has no Skia for,
    /// so headlessly the rows come out zero-high, the grid concludes it fits, and both bars stay
    /// <c>IsVisible=false</c> with no template and no thumb to measure. The rail thickness is not
    /// DataGrid-specific anyway: it comes from app-level resources on the shared ScrollBar
    /// ControlTheme, and the ScrollViewer cases above prove those resources land.</para>
    /// </summary>
    [Fact]
    public void ADataGridsOwnScrollBarsFollowTheSameContract()
    {
        HeadlessUi.Run(() =>
        {
            /* The grid needs columns and rows before its template puts scrollbars in the tree,
               so this is a real grid rather than an empty one. */
            var grid = new DataGrid
            {
                ItemsSource = Enumerable.Range(0, 50).Select(i => new { Value = i }).ToList()
            };
            Show(grid);

            /* A shown grid is also the only place the suite can falsify the Avalonia 12 migration
               of DataGridBehaviors.AttachCopyGuard, which moved off the removed
               TopLevel.PlatformSettings onto Visual.GetPlatformSettings(). Pressing Ctrl+C by hand
               on Windows cannot falsify it: the guard falls back to KeyModifiers.Control when the
               lookup yields nothing, and Control is exactly what Windows reports anyway, so a dead
               lookup and a live one behave identically under the fingers. An attached grid has
               platform settings, so this asserts the lookup itself rather than its fallback. */
            Assert.NotNull(grid.GetPlatformSettings());

            var bars = grid.GetVisualDescendants().OfType<ScrollBar>().ToList();

            Assert.NotEmpty(bars);
            Assert.All(bars, bar => Assert.True(
                bar.AllowAutoHide,
                "a DataGrid's scrollbars are bare ScrollBars that take AllowAutoHide from the " +
                "attached property on the grid, so they need their own rule and their own check"));
        });
    }

    /// <summary>
    /// How much of the bar is actually painted while it is idle.
    ///
    /// <para>Fluent collapses a scrollbar by scaling the thumb's render transform, not by resizing
    /// it, so <see cref="Visual.Bounds"/> alone reports the expanded width no matter what the user
    /// can see. The visible rail is the laid-out thickness times the transform's scale on that
    /// axis, and the expanded state clears the transform entirely — which is why this same
    /// measurement serves for both states.</para>
    /// </summary>
    private static double IdleRailThickness(ScrollBar bar)
    {
        var thumb = ThumbOf(bar);
        var scale = thumb.RenderTransform?.Value ?? Matrix.Identity;

        return bar.Orientation == Orientation.Vertical
            ? thumb.Bounds.Width * scale.M11
            : thumb.Bounds.Height * scale.M22;
    }

    /// <summary>
    /// How much of the bar is painted once it expands. The expanded state clears the render
    /// transform outright, so this is simply the laid-out thickness.
    /// </summary>
    private static double ExpandedThickness(ScrollBar bar)
    {
        var thumb = ThumbOf(bar);

        return bar.Orientation == Orientation.Vertical ? thumb.Bounds.Width : thumb.Bounds.Height;
    }

    private static Thumb ThumbOf(ScrollBar bar) => bar.GetVisualDescendants().OfType<Thumb>().Single();

    /// <summary>
    /// The bars of <paramref name="scrollViewer"/> that have actually been templated and laid out.
    /// A bar the layout decided it did not need never applies its template and has no thumb to
    /// measure, so measuring one would throw rather than report anything useful.
    /// </summary>
    private static ScrollBar[] ScrollBarsOf(Visual scrollViewer) =>
        scrollViewer.GetVisualDescendants()
            .OfType<ScrollBar>()
            .Where(bar => bar.GetVisualDescendants().OfType<Thumb>().Any())
            .ToArray();

    /// <summary>
    /// A ScrollViewer whose content overflows on both axes, with both bars pinned visible so the
    /// test measures a bar that exists rather than one the layout decided it did not need. The
    /// content is a bare Border: no text, so nothing here depends on font measurement.
    /// </summary>
    private static ScrollViewer NewScrollViewer() =>
        new()
        {
            Content = new Border { Width = 2000, Height = 2000 },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible
        };

    /// <summary>
    /// Puts a control in a window and forces a layout pass, so styles are applied and templated
    /// children exist. Nothing is asserted before this runs — an unstyled control reports the
    /// Fluent default and would pass for the wrong reason.
    /// </summary>
    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();

        return window;
    }
}
