using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using PlanViewer.App.Controls;
using Xunit;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Every plan document carries the same right-click menu, whichever way it was opened.
/// </summary>
/// <remarks>
/// <para>It did not. Plan documents are opened by three paths — the Query Store/file path, and the
/// two execute paths — and only the first built the menu; the execute paths built their own header
/// and attached nothing. Executing a query is the commonest way to open a plan, so the menu was
/// missing exactly where it was most expected, and a right-click there did nothing at all: no
/// Rename, no Close Other Tabs, no Close All. Nothing revealed it, because the header looks the
/// same either way and the ✕ still worked.</para>
///
/// <para>The pin is on the header rather than on one call site because what was wrong was that a
/// caller COULD forget. Both paths now go through the one builder, and this asserts the thing a
/// user would check: right-click a plan and the menu is there, with Close honestly advertising the
/// gesture that closes it.</para>
/// </remarks>
public class PlanTabContextMenuTests
{
    [Fact]
    public void APlanOpenedFromQueryStoreCarriesItsMenu()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                AssertPlanMenu(SessionHarness.OpenPlanDocuments(session, 1)[0]);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// The execute path, which is the one that had no menu at all.
    /// </summary>
    /// <remarks>
    /// The loading tab is built before the server is dialled, so a session that only pretends to
    /// have a connection gets far enough to prove what the tab is wearing. The capture is cancelled
    /// rather than left running: the pretend server is a port nothing listens on, and a refused
    /// connection is still a connection attempt.
    /// </remarks>
    [Fact]
    public void APlanOpenedByExecutingAQueryCarriesTheSameMenu()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session);
                session.QueryEditor.Text = "select 1;";

                var capture = typeof(QuerySessionControl).GetMethod(
                    "CaptureAndShowPlan", BindingFlags.NonPublic | BindingFlags.Instance)!;
                _ = (Task)capture.Invoke(session, new object?[] { false, null })!;

                var document = Assert.Single(SessionHarness.Documents(session));
                AssertPlanMenu(document);
            }
            finally
            {
                SessionHarness.CancelExecution(session);
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    private static void AssertPlanMenu(TabItem document)
    {
        var header = Assert.IsType<StackPanel>(document.Header);
        Assert.NotNull(header.ContextMenu);

        var items = header.ContextMenu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(
            new[] { "Rename Tab", "Close", "Close Other Tabs", "Close All Tabs" },
            items.Select(i => i.Header?.ToString()));

        /* The label and the binding change together or the menu starts lying - the same rule the
           gesture's own comment states where it is set. */
        var close = items.Single(i => i.Header?.ToString() == "Close");
        Assert.Equal(new KeyGesture(Key.F4, KeyModifiers.Control), close.InputGesture);

        /* Transparent, not null: a panel with no background hit-tests only its children's pixels,
           so a right-click between the label and the ✕ would fall through to the TabItem, which
           has no menu to open. */
        Assert.NotNull(header.Background);
    }
}
