using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
/// move anything. The row stays one row, every slot stays at the same X, and the sub-tab row below
/// does not shift — with the widest plausible server name in the label, which is the input that
/// used to cause it.</para>
///
/// <para>Both widths matter now that the row has an overflow menu. At a width that fits the whole
/// row, this is the original contract. At a width that does not, connecting must ALSO not change
/// which commands are on the row: the collapse decision is made from the row's own natural width,
/// and if any of that were a function of the connection text, connecting would push a command into
/// the menu — the same bug as before, wearing a chevron.</para>
/// </summary>
public class SessionToolbarLayoutTests
{
    /// <summary>The slots, in the order the XAML lays them out.</summary>
    private static readonly string[] SlotNames =
    {
        "ExecuteButton", "ExecuteEstButton", "HumanAdviceButton", "RobotAdviceButton",
        "ComparePlansButton", "QueryStoreButton", "QueryStoreOverviewButton", "CopyReproButton",
        "GetActualPlanButton", "FormatButton"
    };

    [Fact]
    public void ConnectingMovesNothingInTheToolbarOrTheSubTabRowBelowIt()
    {
        HeadlessUi.Run(() =>
        {
            /* Wide enough for the whole row (natural 1910px in this harness's font metrics), because
               the first half of this test is about every slot HAVING a geometry to hold still. */
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = Session(window);
                ToolbarOverflowTests.Settle(window, session.Overflow);

                var subTabs = session.FindControl<TabControl>("SubTabControl")!;
                var status = session.FindControl<TextBlock>("StatusText")!;
                var connect = session.FindControl<Button>("ConnectButton")!;
                var server = session.FindControl<TextBlock>("ServerLabel")!;
                var chevron = session.FindControl<Button>("ToolbarOverflowButton")!;

                var slots = SlotNames.Select(name => session.FindControl<Button>(name)!).ToList();

                Assert.False(chevron.IsVisible, "the whole row fits at 2200 — nothing to overflow");

                // one row: a return to wrapping would put the later slots on a second Y
                Assert.All(slots, b => Assert.Equal(connect.Bounds.Y, b.Bounds.Y));

                var subTabsY = subTabs.Bounds.Y;
                var positions = slots.Select(b => b.Bounds.X).ToList();

                // the same shape of change connecting makes to the two width-pinned slots
                // (real code swaps in icon+label content via AppIcons.MakeContent; a plain
                // string is a same-or-narrower stand-in, and the pin is what this asserts)
                connect.Content = "Reconnect";
                server.Text = "sql2022.contoso.example.com (Read-only)";
                ToolbarOverflowTests.Settle(window, session.Overflow);

                Assert.All(slots, b => Assert.Equal(connect.Bounds.Y, b.Bounds.Y));
                Assert.Equal(positions, slots.Select(b => b.Bounds.X));
                Assert.Equal(subTabsY, subTabs.Bounds.Y);
                Assert.False(chevron.IsVisible);

                // a long status trims rather than shoving the toolbar sideways
                status.Text = new string('x', 400);
                ToolbarOverflowTests.Settle(window, session.Overflow);

                Assert.True(status.Bounds.Width <= 240, $"status took {status.Bounds.Width}px");
                Assert.Equal(positions, slots.Select(b => b.Bounds.X));
                Assert.Equal(subTabsY, subTabs.Bounds.Y);

                /* Second half: the same contract at a width the row does not fit. What is on the
                   row must be decided by the row, not by what the connection is called. */
                status.Text = "";
                window.Width = 1576;
                ToolbarOverflowTests.Settle(window, session.Overflow);

                Assert.True(chevron.IsVisible, "at 1576 the tail should have moved into the menu");
                var collapsed = ToolbarOverflowTests.MenuHeaders(session.Overflow);
                var inline = slots.Where(b => b.IsVisible).ToList();
                var inlinePositions = inline.Select(b => b.Bounds.X).ToList();

                connect.Content = "Connect";
                server.Text = "Not connected";
                ToolbarOverflowTests.Settle(window, session.Overflow);

                Assert.Equal(collapsed, ToolbarOverflowTests.MenuHeaders(session.Overflow));
                Assert.Equal(inline, slots.Where(b => b.IsVisible).ToList());
                Assert.Equal(inlinePositions, inline.Select(b => b.Bounds.X));
                Assert.Equal(subTabsY, subTabs.Bounds.Y);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The colour sweep swapped twelve hex literals for dictionary lookups that fall back to those
    /// same literals, which means a lookup that never resolves renders exactly like the code it
    /// replaced — the theme would quietly stop reaching the control and nothing would say so. This
    /// pins the mechanism rather than the colours: the keys the session asks for are keys it can
    /// find, from where it sits in the tree.
    ///
    /// <para>SuccessBrush and ErrorBrush are in the list now that the design-system pass landed
    /// them; they are the two keys whose FindResource fallbacks would mask a silent lookup
    /// failure most convincingly, which is exactly what this test exists to catch.</para>
    /// </summary>
    [Fact]
    public void TheThemeKeysTheSessionAsksForResolve()
    {
        HeadlessUi.Run(() =>
        {
            /* Explicitly wide, unlike the default 1280, because the divider half of this test needs
               all six dividers ON the row to have a width to measure: the overflow takes a group's
               leading divider with it when the last of that group leaves, so at 1280 four of the six
               are legitimately zero-width and this would be testing the wrong thing. */
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = Session(window);
                ToolbarOverflowTests.Settle(window, session.Overflow);

                foreach (var key in new[] { "ForegroundBrush", "BackgroundBrush", "ForegroundMutedBrush",
                    "BackgroundDarkBrush", "BorderBrush", "SuccessBrush", "ErrorBrush", "AppButton" })
                {
                    Assert.True(session.TryFindResource(key, out var value) && value is not null,
                        $"the session cannot resolve {key}");
                }

                /* Same trap, same shape: the group dividers between the toolbar's slots are class-styled
                   Borders, and a class style that matches nothing leaves them zero-width and unpainted —
                   a toolbar quietly missing its dividers, with nothing to see in the XAML. */
                var dividers = session.FindControl<ScrollViewer>("ToolbarScroll")!
                    .GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("separator")).ToList();

                Assert.Equal(6, dividers.Count);
                Assert.All(dividers, d =>
                {
                    Assert.Equal(1, d.Bounds.Width);
                    Assert.NotNull(d.Background);
                });
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    internal static QuerySessionControl Session(MainWindow window) =>
        window.FindControl<TabControl>("MainTabControl")!.Items
            .OfType<TabItem>().Select(t => t.Content).OfType<QuerySessionControl>().Last();
}
