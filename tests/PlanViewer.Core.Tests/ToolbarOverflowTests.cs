using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Helpers;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The session toolbar is one fixed row of slots inside a ScrollViewer whose rail is deliberately
/// suppressed. Measured in this harness, that row wants 2116px — it wanted 1910px until the headless
/// text metrics changed under Avalonia 12, which HeadlessUi explains once for every number in these
/// files. Neither figure is what the row measures on the fonts a user has; they are the widths the
/// thresholds below are chosen against. What put this file here is real: on Erik's maximized
/// 1536-logical display, Format and part of Run Repro were simply not on screen, behind a scrollbar
/// that had been hidden on purpose because a rail under a 28px button row grows the strip and drags
/// the sub-tab row with it. Hidden content, hidden affordance.
///
/// <para>The fix is a chevron that holds whatever did not fit. What these tests pin is the part of
/// it that is easy to get subtly wrong and impossible to see in a screenshot: that a command in the
/// menu is the SAME command as the button it came from — same handler, same enabled state, same
/// tooltip — that the buttons which stay do not move by a pixel while the tail leaves, that a
/// button leaves and returns to the same slot, and that a row which hides a button in order to
/// re-measure itself settles instead of oscillating.</para>
/// </summary>
public class ToolbarOverflowTests
{
    /// <summary>
    /// Runs the window to a standstill after a resize, and fails if it never reaches one.
    ///
    /// <para>Three things have to happen and none of them is one layout pass. A headless window's
    /// new Width does not reach layout until the dispatcher is pumped — assign it and run layout
    /// six times and the old width is still arranged, which is worth knowing before writing any
    /// test that resizes one. Hiding a button during a layout pass is only reflected in the row's
    /// measured width by the next pass. And the chevron appearing takes its own bite out of the
    /// space the row has, which can cost one more command.</para>
    ///
    /// <para>So the loop runs until the arranged width AND the number of times the overflow has
    /// changed its mind both stop moving. That second half is not just plumbing: a design where
    /// hiding a button made room to show it again would spin here forever rather than settle, so
    /// every test that touches the toolbar is also a loop check.</para>
    /// </summary>
    internal static void Settle(Window window, params ToolbarOverflow[] overflows)
    {
        var settled = (Width: double.NaN, Revisions: -1);

        for (var round = 0; round < 12; round++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.UpdateLayout();

            var now = (window.Bounds.Width, overflows.Sum(o => o.Revisions));
            if (now == settled)
                return;

            settled = now;
        }

        Assert.Fail("the toolbar never settled: twelve rounds of layout and it was still resizing " +
                    "or still moving buttons in and out of the menu.");
    }

    internal static List<string> MenuHeaders(ToolbarOverflow overflow) =>
        overflow.Menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString() ?? "").ToList();

    /// <summary>
    /// Sizes the window so the row has exactly <paramref name="viewport"/> px to work with,
    /// chevron and all, so a test can name the width a user actually has rather than a window size
    /// that happens to produce it.
    /// </summary>
    private static void SetViewport(Window window, ScrollViewer scroll, ToolbarOverflow overflow, double viewport)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (scroll.Viewport.Width == viewport)
                return;
            window.Width += viewport - scroll.Viewport.Width;
            Settle(window, overflow);
        }

        Assert.Fail($"could not size the toolbar to a {viewport}px viewport (got {scroll.Viewport.Width})");
    }

    /// <summary>
    /// The commands that may leave the row, in the order they leave it: the end of the row first.
    /// The label is what the menu entry says; the name is the button it came from.
    /// </summary>
    private static readonly (string Name, string Label)[] CollapseOrder =
    {
        ("FormatButton", "Format"),
        ("GetActualPlanButton", "Run Repro"),
        ("CopyReproButton", "Copy Repro"),
        ("QueryStoreOverviewButton", "QS Overview"),
        ("QueryStoreButton", "Query Store"),
        ("ComparePlansButton", "Compare Plans"),
        ("RobotAdviceButton", "Robot Advice"),
        ("HumanAdviceButton", "Human Advice")
    };

    private static string[] CollapseLabels => CollapseOrder.Select(c => c.Label).ToArray();

    /// <summary>
    /// The width Erik actually runs at. At a 1520px row the four trailing commands move into the
    /// menu, in the order they left it, and every button in front of them is exactly where it was
    /// on a row wide enough for all of them.
    /// </summary>
    [Fact]
    public void ANarrowRowMovesItsTailIntoTheMenuAndMovesNothingElse()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                Settle(window, session.Overflow);

                var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
                var chevron = session.FindControl<Button>("ToolbarOverflowButton")!;
                var subTabs = session.FindControl<TabControl>("SubTabControl")!;
                var format = session.FindControl<Button>("FormatButton")!;

                // Where everything sits when the whole row fits, to compare against.
                var wide = CollapseOrder.Select(c => session.FindControl<Button>(c.Name)!).ToList();
                var stayers = new[] { "ConnectButton", "ExecuteButton", "ExecuteEstButton" }
                    .Select(name => session.FindControl<Button>(name)!).ToList();
                var positions = stayers.Concat(wide).ToDictionary(b => b, b => b.Bounds.X);
                var subTabsY = subTabs.Bounds.Y;

                Assert.False(chevron.IsVisible);
                Assert.Empty(MenuHeaders(session.Overflow));

                SetViewport(window, scroll, session.Overflow, 1520);

                Assert.True(chevron.IsVisible, "a 1520px row cannot hold a 2116px toolbar");

                /* The one failure every other assertion here would sail past: a chevron that
                   appears on cue, holds the right commands, and opens nothing when you press it. */
                Assert.Same(session.Overflow.Menu, chevron.Flyout);

                /* Four commands, in the order the row gives them up: the end of the row first.
                   Format being the first entry is the contract — the menu grows and shrinks at its
                   end, so an entry never changes position under the pointer as the window moves.

                   How many end up here is a function of the harness's text metrics and is allowed
                   to move with them; it was three until Avalonia 12 made text wider, and QS
                   Overview is simply the next name in CollapseOrder. What is NOT allowed to move
                   is which commands may leave at all: Connect, Execute and Execute-with-estimate
                   are absent from CollapseOrder and must never appear in this menu at any width.
                   A metric change adds the next name in the documented order; a regression takes
                   a protected one. This exact-collection assertion is itself the guard: a
                   protected command that collapsed would appear in this list and fail it. That is
                   why it stays an exact collection and never becomes a count or a containment
                   check. (The X comparison below skips invisible buttons, so it would not catch
                   a stayer leaving — it pins that the survivors did not shift.) */
                Assert.Equal(new[] { "Format", "Run Repro", "Copy Repro", "QS Overview" },
                    MenuHeaders(session.Overflow));
                Assert.False(format.IsVisible);

                // Everything still on the row is where it was, to the pixel, and the sub-tab row
                // below it has not moved either.
                foreach (var button in positions.Keys.Where(b => b.IsVisible))
                    Assert.Equal(positions[button], button.Bounds.X);

                Assert.Equal(subTabsY, subTabs.Bounds.Y);

                /* The chevron is docked in the DockPanel rather than sitting in the row, which is a
                   different coordinate space and a different alignment default: a stretched one
                   would be as tall as the whole strip and a mis-docked one would sit off the row's
                   baseline. Nothing about either shows up in the assertions above, so both are
                   named here — translated into the session's space so they are comparable at all. */
                var execute = session.FindControl<Button>("ExecuteButton")!;
                Assert.Equal(28, chevron.Bounds.Height);
                Assert.Equal(
                    execute.TranslatePoint(new Point(0, 0), session)!.Value.Y,
                    chevron.TranslatePoint(new Point(0, 0), session)!.Value.Y);

                // ...and the row now fits, which is the whole point: no hidden scroll.
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width,
                    $"the row should fit after collapsing: extent {scroll.Extent.Width} viewport {scroll.Viewport.Width}");
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A row with room keeps every command on it, and the chevron does not advertise an empty menu.
    /// </summary>
    [Fact]
    public void AWideRowKeepsEveryCommandOnItAndHidesTheChevron()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                Settle(window, session.Overflow);

                var chevron = session.FindControl<Button>("ToolbarOverflowButton")!;

                Assert.False(chevron.IsVisible);
                Assert.Empty(MenuHeaders(session.Overflow));

                foreach (var (name, label) in CollapseOrder)
                {
                    var button = session.FindControl<Button>(name)!;
                    Assert.True(button.IsVisible, $"{label} should be on the row at 2200px");
                }
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Widening gives the commands back in the reverse of the order they left, so a button always
    /// returns to the slot it vacated rather than to wherever there happened to be room.
    /// </summary>
    [Fact]
    public void WideningReturnsTheCommandsInTheOrderTheyLeft()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                Settle(window, session.Overflow);

                var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
                var chevron = session.FindControl<Button>("ToolbarOverflowButton")!;

                // All the way in: everything that may leave has left.
                window.Width = 700;
                Settle(window, session.Overflow);
                Assert.Equal(CollapseLabels, MenuHeaders(session.Overflow));

                /* Back out again. The menu must only ever shrink from its end, which is what says
                   the restore order is the reverse of the collapse order — walk the widths up and
                   the headers that remain are always a prefix of the order they went in. */
                var seen = CollapseOrder.Length;
                for (var width = 720.0; width <= 2200; width += 20)
                {
                    window.Width = width;
                    Settle(window, session.Overflow);

                    var menu = MenuHeaders(session.Overflow);
                    Assert.True(menu.Count <= seen, $"widening to {width} put a command BACK in the menu");
                    Assert.Equal(CollapseLabels.Take(menu.Count), menu);
                    Assert.Equal(menu.Count > 0, chevron.IsVisible);
                    seen = menu.Count;
                }

                Assert.Empty(MenuHeaders(session.Overflow));
                Assert.False(chevron.IsVisible);
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A menu entry is not a copy of its button, it is a second door onto the same one: clicking it
    /// raises the button's own Click, so the two can never grow apart. Pinned through the real
    /// handler's own observable effect rather than a stub — Copy Repro with no plan tab selected
    /// says so in the status strip, synchronously, which is exactly the evidence wanted here.
    /// </summary>
    [Fact]
    public void AMenuEntryRunsTheButtonsOwnHandler()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                Settle(window, session.Overflow);

                var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
                var copyRepro = session.FindControl<Button>("CopyReproButton")!;
                var status = session.FindControl<TextBlock>("StatusText")!;

                // As a connected session would have it, so the entry is genuinely clickable.
                copyRepro.IsEnabled = true;

                SetViewport(window, scroll, session.Overflow, 1520);

                var entry = session.Overflow.Menu.Items.OfType<MenuItem>()
                    .First(i => (string?)i.Header == "Copy Repro");

                Assert.True(entry.IsEnabled);
                Assert.Equal(ToolTip.GetTip(copyRepro), ToolTip.GetTip(entry));
                Assert.IsType<PathIcon>(entry.Icon);
                Assert.Same(AppIcons.CopyRepro, ((PathIcon)entry.Icon!).Data);

                Assert.NotEqual("Select a plan tab first", status.Text);
                entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.Equal("Select a plan tab first", status.Text);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The menu, actually open. Everything else here reads the flyout's Items collection, which
    /// would be just as happy with entries that render as blank rows: AppIcons deliberately leaves
    /// its icons' Foreground unset so they inherit from whatever presenter they land in, and a
    /// MenuItem's icon presenter is not a toolbar button's content presenter. This opens the thing
    /// and looks at what came out.
    /// </summary>
    [Fact]
    public void TheOpenMenuRendersItsEntriesWithTheirIcons()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                Settle(window, session.Overflow);

                var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
                var chevron = session.FindControl<Button>("ToolbarOverflowButton")!;

                SetViewport(window, scroll, session.Overflow, 1520);
                Assert.True(chevron.IsVisible);

                try
                {
                    session.Overflow.Menu.ShowAt(chevron);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();

                    var entry = session.Overflow.Menu.Items.OfType<MenuItem>()
                        .First(i => (string?)i.Header == "Format");

                    // The entry reached the tree and was given room by the menu's own layout.
                    Assert.True(entry.Bounds.Width > 0 && entry.Bounds.Height > 0,
                        $"the entry was not laid out: {entry.Bounds}");

                    /* The icon is the half that could silently come out blank: it takes the ambient
                       foreground rather than setting one, so a presenter that supplies none would
                       paint nothing and the row would read as an unlabelled gap. */
                    var icon = (PathIcon)entry.Icon!;
                    Assert.True(icon.Bounds.Width > 0 && icon.Bounds.Height > 0,
                        $"the entry's icon was not laid out: {icon.Bounds}");

                    var painted = icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                    Assert.NotNull(painted.Fill);
                    Assert.Same(AppIcons.Format, painted.Data);

                    /* A disabled entry's icon is dimmed by hand, because Fluent's disabled MenuItem
                       dims the header presenter and leaves the icon at full brightness — a lit icon
                       beside greyed text. That hand-dimming is only correct while the theme really
                       is leaving it alone: if a future Fluent applied its own opacity somewhere
                       between the MenuItem and the icon, the two would multiply and the entry would
                       fade to nearly nothing. This is the assertion that would say so. */
                    var disabled = session.Overflow.Menu.Items.OfType<MenuItem>()
                        .First(i => (string?)i.Header == "Copy Repro");
                    Assert.False(disabled.IsEnabled, "Copy Repro has no plan to copy yet");

                    var disabledIcon = (Control)disabled.Icon!;
                    Assert.Equal(0.4, disabledIcon.Opacity);

                    for (var parent = disabledIcon.GetVisualParent();
                         parent is not null && !ReferenceEquals(parent, disabled);
                         parent = parent.GetVisualParent())
                    {
                        Assert.True(parent.Opacity == 1.0,
                            $"{parent.GetType().Name} already dims the icon to {parent.Opacity} — " +
                            "hand-dimming it as well would multiply the two");
                    }
                }
                finally
                {
                    session.Overflow.Menu.Hide();
                    Dispatcher.UIThread.RunJobs();
                }
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A command that has gone into the menu keeps refusing while the button refuses, and starts
    /// accepting the moment the button does. Offering a disabled command as an enabled menu entry
    /// is the failure this rules out, and offering it as a permanently greyed one is the other.
    /// </summary>
    [Fact]
    public void AMenuEntryFollowsItsButtonsEnabledStateBothWays()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                Settle(window, session.Overflow);

                var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
                var copyRepro = session.FindControl<Button>("CopyReproButton")!;

                Assert.False(copyRepro.IsEnabled, "Copy Repro starts disabled — nothing to copy yet");

                SetViewport(window, scroll, session.Overflow, 1520);

                var entry = session.Overflow.Menu.Items.OfType<MenuItem>()
                    .First(i => (string?)i.Header == "Copy Repro");

                Assert.False(entry.IsEnabled);

                copyRepro.IsEnabled = true;
                Assert.True(entry.IsEnabled);

                copyRepro.IsEnabled = false;
                Assert.False(entry.IsEnabled);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The commands that make a query editor a query editor never leave the row at any width, and
    /// never turn up in the menu either. A toolbar that can lose the button which runs the query is
    /// not a toolbar with an overflow, it is a toolbar with a bug.
    /// </summary>
    [Fact]
    public void ConnectingAndRunningNeverLeaveTheRow()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                Settle(window, session.Overflow);

                window.Width = 640; // the window's own MinWidth — as narrow as this gets
                Settle(window, session.Overflow);

                foreach (var name in new[] { "ConnectButton", "ServerLabel", "DatabaseBox",
                    "ExecuteButton", "ExecuteEstButton" })
                {
                    var control = session.FindControl<Control>(name)!;
                    Assert.True(control.IsVisible, $"{name} must stay on the row at any width");
                }

                var menu = MenuHeaders(session.Overflow);
                Assert.Equal(CollapseLabels, menu);
                Assert.DoesNotContain("Actual Plan", menu);
                Assert.DoesNotContain("Est Plan", menu);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Below the width where even the never-collapse commands overflow, the scrolling row is still
    /// there underneath as the last resort. The chevron is the affordance for the normal range; it
    /// does not replace the safety net, and taking the net out with it would put us back to content
    /// that cannot be reached at all.
    /// </summary>
    [Fact]
    public void TheScrollingRowIsStillThereBelowTheCollapseFloor()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                Settle(window, session.Overflow);

                var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
                var subTabs = session.FindControl<TabControl>("SubTabControl")!;
                var subTabsY = subTabs.Bounds.Y;

                /* Connect, the server label, the database picker and the two plan verbs want 746px
                   between them and cannot be collapsed, so under that the row has to scroll. */
                window.Width = 640;
                Settle(window, session.Overflow);

                Assert.Equal(CollapseLabels, MenuHeaders(session.Overflow));
                Assert.True(scroll.Extent.Width > scroll.Viewport.Width,
                    $"below the floor the row must still scroll: extent {scroll.Extent.Width} viewport {scroll.Viewport.Width}");

                scroll.Offset = new Avalonia.Vector(40, 0);
                window.UpdateLayout();
                Assert.Equal(40, scroll.Offset.X);

                // and none of that grew the strip into the tabs below it
                Assert.Equal(subTabsY, subTabs.Bounds.Y);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// Hiding a button re-measures the row, and re-measuring the row is what decides whether to
    /// hide a button. That is a loop unless the two conditions are complementary, and a loop here
    /// would be a window that pins a core while nobody is touching it. A settled toolbar must stop
    /// revising itself entirely — not slow down, stop.
    /// </summary>
    [Fact]
    public void ASettledToolbarStopsRevisingItself()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 800 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());

                var session = SessionToolbarLayoutTests.Session(window);
                var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
                Settle(window, session.Overflow);

                /* Right at the boundary, where one pixel decides whether the last command fits,
                   because that is where an oscillation would live if there were one. */
                SetViewport(window, scroll, session.Overflow, 1520);

                var revisions = session.Overflow.Revisions;
                var menu = MenuHeaders(session.Overflow);

                for (var pass = 0; pass < 20; pass++)
                    window.UpdateLayout();

                Assert.Equal(revisions, session.Overflow.Revisions);
                Assert.Equal(menu, MenuHeaders(session.Overflow));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The plan toolbar's Statements button has two owners at once: the plan decides whether the
    /// command applies at all (a plan with no statement list has no button), and the overflow
    /// decides whether there is room for it. They share one IsVisible and neither may clobber the
    /// other, so the plan says which it means through <see cref="ToolbarOverflow.SetAvailable"/>
    /// and the overflow derives the control's actual visibility from both halves.
    ///
    /// <para>The case that made an observer-based design impossible is in here: once the overflow
    /// has collapsed the button, the plan clearing itself sets an already-false IsVisible to false,
    /// which raises nothing — so a design that read intent off the property would leave
    /// "Statements" sitting in the chevron menu for a plan that has none.</para>
    /// </summary>
    [Fact]
    public void TheStatementsButtonAnswersToThePlanAndTheOverflowAtOnce()
    {
        HeadlessUi.Run(() =>
        {
            var path = Path.Combine("Plans", "exec_stored_procedure_plan.sqlplan");
            var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

            var viewer = new PlanViewerControl();
            Assert.True(viewer.LoadPlan(xml, "exec_stored_procedure_plan.sqlplan"),
                $"the fixture must load: {viewer.LastLoadError}");

            var window = new Window { Content = viewer, Width = 1200, Height = 900 };
            try
            {
                window.Show();
                Settle(window, viewer.Overflow);

                var statements = viewer.FindControl<Button>("StatementsButton")!;
                var divider = viewer.FindControl<Border>("StatementsButtonSeparator")!;
                var chevron = viewer.FindControl<Button>("PlanToolbarOverflowButton")!;

                // Room for everything: on the row, divider and all.
                Assert.True(statements.IsVisible);
                Assert.True(divider.IsVisible);
                Assert.False(chevron.IsVisible);
                Assert.Empty(MenuHeaders(viewer.Overflow));

                // No room: into the menu, and the divider goes with it rather than being stranded.
                window.Width = 800;
                Settle(window, viewer.Overflow);

                Assert.Equal(new[] { "Statements" }, MenuHeaders(viewer.Overflow));
                Assert.False(statements.IsVisible);
                Assert.False(divider.IsVisible);
                Assert.True(chevron.IsVisible);

                // Same alignment pin as the session toolbar's chevron: docked, not in the row, so
                // nothing else here would notice it stretching or sitting off the baseline.
                var save = viewer.FindControl<Button>("SavePlanButton")!;
                Assert.Equal(28, chevron.Bounds.Height);
                Assert.Equal(
                    save.TranslatePoint(new Point(0, 0), viewer)!.Value.Y,
                    chevron.TranslatePoint(new Point(0, 0), viewer)!.Value.Y);

                /* The plan goes away while the button is collapsed. The command no longer applies,
                   so it must be in neither place — and it must not be reserving row width either,
                   which is what lets Save come back out of the menu here. */
                viewer.Clear();
                Settle(window, viewer.Overflow);

                Assert.DoesNotContain("Statements", MenuHeaders(viewer.Overflow));
                Assert.False(statements.IsVisible);
                Assert.False(divider.IsVisible);

                // A new plan, still narrow: the command applies again, and goes where it fits.
                Assert.True(viewer.LoadPlan(xml, "exec_stored_procedure_plan.sqlplan"));
                Settle(window, viewer.Overflow);

                Assert.Contains("Statements", MenuHeaders(viewer.Overflow));
                Assert.False(statements.IsVisible);
                Assert.False(divider.IsVisible);

                // Room again: back on the row, with its divider.
                window.Width = 1200;
                Settle(window, viewer.Overflow);

                Assert.Empty(MenuHeaders(viewer.Overflow));
                Assert.True(statements.IsVisible);
                Assert.True(divider.IsVisible);
                Assert.False(chevron.IsVisible);
            }
            finally
            {
                window.Close();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        });
    }

    /// <summary>
    /// The shape the app actually runs in: a session toolbar with a plan sub-tab's toolbar under
    /// it, two live overflows in one window. LayoutUpdated is raised for every layout pass in the
    /// window, so each overflow hears the passes the OTHER one's visibility writes provoke. What is
    /// pinned is that neither reconsiders because of the other.
    ///
    /// <para>The session strip's own width is genuinely its business — the status message is docked
    /// into that row and takes its 240px cap straight out of the buttons' space, which is the
    /// design, so the session overflow moving when a message appears is correct. The plan toolbar
    /// one row down is the one that must not care, and it is the one asserted on here: it sits in
    /// the sub-tab content, its width has nothing to do with the strip above it, and a revision
    /// from it would mean this class is reacting to layout that is not about it.</para>
    /// </summary>
    [Fact]
    public void OneOverflowsChurnDoesNotReachTheOtherInTheSameWindow()
    {
        HeadlessUi.Run(() =>
        {
            var window = new MainWindow { Width = 2200, Height = 900 };
            try
            {
                window.Show();
                window.NewQuery_Click(window, new RoutedEventArgs());
                var session = SessionToolbarLayoutTests.Session(window);

                var path = Path.Combine("Plans", "exec_stored_procedure_plan.sqlplan");
                var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
                session.OnQueryStorePlansSelected(null, new List<QueryStorePlan>
                {
                    new() { QueryId = 1, PlanId = 1, QueryText = "select 1;", PlanXml = xml }
                });

                var viewer = session.GetPlanTabs().Single().viewer;
                var scroll = session.FindControl<ScrollViewer>("ToolbarScroll")!;
                var status = session.FindControl<TextBlock>("StatusText")!;

                /* Opening the plan leaves a message in the strip, and the strip is docked into this
                   row: a 1520px viewport measured with a message in it is a different baseline from
                   one measured without, and the message goes away on its own a few seconds later.
                   Clear it first so the width this test names is the one it keeps coming back to. */
                status.Text = "";
                SetViewport(window, scroll, session.Overflow, 1520);
                Settle(window, session.Overflow, viewer.Overflow);

                // The session's tail is in its menu; the hosted plan toolbar has dropped its
                // duplicated connection controls and comfortably fits, so its row is intact.
                Assert.NotEmpty(MenuHeaders(session.Overflow));
                Assert.Empty(MenuHeaders(viewer.Overflow));

                var settled = (session.Overflow.Revisions, viewer.Overflow.Revisions);

                // Idle passes move neither — the same standstill both settle to on their own.
                for (var pass = 0; pass < 20; pass++)
                    window.UpdateLayout();

                Assert.Equal(settled, (session.Overflow.Revisions, viewer.Overflow.Revisions));

                /* Now churn the session strip properly: a status message takes its cap out of the
                   row, which is expected to cost the session another command or two. The plan
                   toolbar must not notice, and its buttons must all still be on its row. */
                var planRevisions = viewer.Overflow.Revisions;
                var save = viewer.FindControl<Button>("SavePlanButton")!;
                var saveX = save.Bounds.X;

                status.Text = "a status message long enough to take its whole 240px cap out of the row";
                Settle(window, session.Overflow, viewer.Overflow);

                Assert.Equal(planRevisions, viewer.Overflow.Revisions);
                Assert.Empty(MenuHeaders(viewer.Overflow));
                Assert.Equal(saveX, save.Bounds.X);

                // ...and taking the message away gives the session back what it cost, without the
                // plan toolbar having moved at any point in between.
                status.Text = "";
                Settle(window, session.Overflow, viewer.Overflow);

                Assert.Equal(planRevisions, viewer.Overflow.Revisions);
                Assert.Equal(new[] { "Format", "Run Repro", "Copy Repro", "QS Overview" },
                    MenuHeaders(session.Overflow));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }
}
