using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.App;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// U4: the session toolbar used to be a WrapPanel, which made its geometry a function of its own
/// text. Connecting swapped "Not connected" for a server name and "Connect" for "Reconnect", both
/// wider, which moved where the panel wrapped, which moved every button after them onto a different
/// row — and dragged the sub-tab row up or down with it. The plan tabs moved while you were
/// reaching for them.
///
/// <para>What is pinned here is the part a screenshot would not catch twice: that connecting cannot
/// move anything. The row stays one row, Format stays in the same slot, and the sub-tab row below
/// does not shift — with the widest plausible server name in the label, which is the input that
/// used to cause it.</para>
/// </summary>
public class SessionToolbarLayoutTests
{
    [Fact]
    public void ConnectingMovesNothingInTheToolbarOrTheSubTabRowBelowIt()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 1280, Height = 800 };
            window.Show();
            window.NewQuery_Click(window, new RoutedEventArgs());
            window.UpdateLayout();

            var session = window.FindControl<TabControl>("MainTabControl")!.Items
                .OfType<TabItem>().Select(t => t.Content).OfType<QuerySessionControl>().Last();

            var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
            var subTabs = session.FindControl<TabControl>("SubTabControl")!;
            var status = session.FindControl<TextBlock>("StatusText")!;
            var connect = session.FindControl<Button>("ConnectButton")!;
            var server = session.FindControl<TextBlock>("ServerLabel")!;

            var slots = new[] { "ExecuteButton", "ExecuteEstButton", "HumanAdviceButton",
                "RobotAdviceButton", "ComparePlansButton", "QueryStoreButton",
                "QueryStoreOverviewButton", "CopyReproButton", "GetActualPlanButton", "FormatButton" }
                .Select(name => session.FindControl<Button>(name)!)
                .ToList();

            Assert.All(slots, b => Assert.Equal(connect.Bounds.Y, b.Bounds.Y)); // one row

            var subTabsY = subTabs.Bounds.Y;
            var positions = slots.Select(b => b.Bounds.X).ToList();

            // exactly what connecting does to the two slots whose content changes
            connect.Content = "Reconnect";
            server.Text = "sql2022.contoso.example.com (Read-only)";
            window.UpdateLayout();

            Assert.All(slots, b => Assert.Equal(connect.Bounds.Y, b.Bounds.Y));
            Assert.Equal(positions, slots.Select(b => b.Bounds.X));
            Assert.Equal(subTabsY, subTabs.Bounds.Y);

            // a long status trims rather than shoving the toolbar sideways
            status.Text = new string('x', 400);
            window.UpdateLayout();

            Assert.True(status.Bounds.Width <= 320, $"status took {status.Bounds.Width}px");
            Assert.Equal(positions, slots.Select(b => b.Bounds.X));
            Assert.Equal(subTabsY, subTabs.Bounds.Y);

            // and when the row is wider than the window it scrolls instead of wrapping
            if (scroll.Extent.Width > scroll.Viewport.Width)
            {
                scroll.Offset = new Avalonia.Vector(40, 0);
                window.UpdateLayout();
                Assert.Equal(40, scroll.Offset.X);
                Assert.Equal(subTabsY, subTabs.Bounds.Y);
            }
        });
    }
}
