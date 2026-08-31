using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #464: every scrollbar in the app collapsed to a sliver until hovered, because Fluent defaults
/// <c>AllowAutoHide</c> to true and nothing ever set it otherwise. The reporter's complaint was
/// about a horizontal bar, which is where it hurts most — the thing you need to grab is a couple
/// of pixels tall until after you have found it.
///
/// <para>Two selectors are needed, not one, and that is the whole reason this test exists. A
/// <see cref="ScrollViewer"/> rule alone looks like it covers the app and does not: DataGrid does
/// not scroll through a ScrollViewer, its template hosts PART_HorizontalScrollbar and
/// PART_VerticalScrollbar as bare <see cref="ScrollBar"/>s. A style that compiles is not a style
/// that matches, so both are asserted against controls that have actually been styled rather than
/// against the App.axaml text.</para>
/// </summary>
public class ScrollBarVisibilityTests
{
    [Fact]
    public void AScrollViewerDoesNotAutoHideItsBars()
    {
        HeadlessUi.Run(() =>
        {
            var scrollViewer = new ScrollViewer { Content = new TextBlock { Text = "x" } };
            Show(scrollViewer);

            Assert.False(
                scrollViewer.AllowAutoHide,
                "the app-wide ScrollViewer style should have turned auto-hide off");
        });
    }

    [Fact]
    public void ADataGridsOwnScrollBarsDoNotAutoHideEither()
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

            var bars = grid.GetVisualDescendants().OfType<ScrollBar>().ToList();

            Assert.NotEmpty(bars);
            Assert.All(bars, bar => Assert.False(
                bar.AllowAutoHide,
                "a DataGrid's scrollbars are bare ScrollBars and need their own rule"));
        });
    }

    /// <summary>
    /// Puts a control in a window and forces a layout pass, so styles are applied and templated
    /// children exist. Nothing is asserted before this runs — an unstyled control reports the
    /// Fluent default and would pass for the wrong reason.
    /// </summary>
    private static void Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();
    }
}
