using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using PlanViewer.App;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The tab strip used to be a WrapPanel, so tabs stacked into a second and third row as they
/// accumulated — eight tabs made three rows at laptop width, and with the session toolbar under
/// them that was six rows of chrome sitting on top of the query. It is one scrolling row now.
///
/// <para><b>Why this is a test and not a screenshot.</b> The rule that trims a long tab name is a
/// style, and a style that matches nothing is not an error: the first spelling of it lived on the
/// items panel, compiled, rendered, and did absolutely nothing, because Avalonia matches selectors
/// against the LOGICAL tree and the items panel is not in a header's logical chain. Nothing about
/// the running app said so. What is pinned here is therefore the outcome — the cap and the ellipsis
/// actually reaching the label, the close button surviving next to it, the row never wrapping, the
/// strip overflowing into something scrollable — and the other half of that selector's job: the cap
/// must NOT leak into a query session's own sub-tab strip, which is the same TabItem/StackPanel/
/// TextBlock shape one level further down and which every looser spelling of the rule also hit.</para>
/// </summary>
public class TabStripLayoutTests
{
    [Fact]
    public void TabStripStaysOneScrollingRowWithEllipsizedHeaders()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 900, Height = 700 };
            window.Show();

            for (var i = 0; i < 20; i++)
                window.NewQuery_Click(window, new RoutedEventArgs());

            var tabs = window.FindControl<TabControl>("MainTabControl")!;

            // a name long enough to need the ellipsis
            var wide = tabs.Items.OfType<TabItem>().Select(t => t.Header).OfType<StackPanel>()
                .First().Children.OfType<TextBlock>().First();
            wide.Text = "a very long query tab name that nobody would ever type on purpose.sql";

            window.UpdateLayout();

            Assert.True(wide.Bounds.Width is > 0 and <= 170, $"long label took {wide.Bounds.Width}px");

            var headers = tabs.Items.OfType<TabItem>().Select(t => t.Header).OfType<StackPanel>().ToList();
            Assert.True(headers.Count >= 20, $"headers {headers.Count} of {tabs.Items.Count} tabs");

            foreach (var header in headers)
            {
                Assert.Equal(2, header.Children.Count); // label + close button
                var label = Assert.IsType<TextBlock>(header.Children[0]);
                Assert.Equal(170, label.MaxWidth);
                Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
            }

            // the cap must not leak down into a session's own sub-tab strip
            var sub = window.GetVisualDescendants().OfType<TabControl>()
                .First(t => t.Name == "SubTabControl");
            var subLabel = sub.Items.OfType<TabItem>().Select(t => t.Header).OfType<StackPanel>()
                .First().Children.OfType<TextBlock>().First();
            Assert.Equal(double.PositiveInfinity, subLabel.MaxWidth);

            var strip = window.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(sv => sv.Name == "TabStripScroll");
            Assert.NotNull(strip);

            var bounds = tabs.Items.OfType<TabItem>().Select(t => t.Bounds).ToList();
            Assert.All(bounds, b => Assert.Equal(bounds[0].Y, b.Y)); // one row, never wrapped

            Assert.True(strip!.Extent.Width > strip.Viewport.Width,
                $"the strip should overflow and scroll: extent {strip.Extent.Width} viewport {strip.Viewport.Width}");
        });
    }
}
