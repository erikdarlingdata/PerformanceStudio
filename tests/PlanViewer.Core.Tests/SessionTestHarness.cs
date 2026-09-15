using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// What the session-IA tests need in common: a real session in a real window, documents opened
/// through the app's own path, and the named parts of the row the restructure created.
///
/// <para><b>Documents are opened, never hand-built.</b> A <see cref="TabItem"/> a test constructs
/// itself is not a document — it has whatever header the test felt like giving it and none of the
/// close, rename or release wiring the app attaches. Everything below goes through
/// <see cref="QuerySessionControl.OnQueryStorePlansSelected"/> with a real .sqlplan off disk, which
/// is the no-server way to make the app build one for you.</para>
///
/// <para><b>Why some of this is reflection.</b> Three fields say "this session has a server" and
/// nothing else in the app sets them: the connection dialog does, and a dialog needs a server to
/// talk to. The same trade DetachedUnsavedChangesTests.DirtyStateSubscriberCount takes — the
/// alternative is product API that exists for a test, on a class whose whole point this quarter was
/// having fewer ways in. Nothing here dials anything: the pretend server's connection string is
/// only ever read as "not null", and the one test that does start a load cancels it first.</para>
/// </summary>
internal static class SessionHarness
{
    /// <summary>
    /// A window with one query session in it, laid out and ready to be driven.
    /// </summary>
    internal static (MainWindow Window, QuerySessionControl Session) NewSession(
        double width = 1400, double height = 800)
    {
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        window.NewQuery_Click(window, new RoutedEventArgs());
        window.UpdateLayout();

        return (window, SessionToolbarLayoutTests.Session(window));
    }

    /// <summary>
    /// Opens <paramref name="count"/> plan documents the way the Query Store grid does, and hands
    /// back the tabs in strip order. The last one is selected, because every add pairs with a
    /// select; the session is on the Documents surface afterwards.
    /// </summary>
    internal static List<TabItem> OpenPlanDocuments(QuerySessionControl session, int count = 1)
    {
        var xml = SamplePlanXml();

        session.OnQueryStorePlansSelected(null, Enumerable.Range(1, count).Select(i =>
            new QueryStorePlan
            {
                QueryId = i,
                PlanId = i,
                QueryText = $"select {i};",
                PlanXml = xml,
            }).ToList());

        return Documents(session);
    }

    /// <summary>The .sqlplan the suite's other chrome tests open, as the app wants it.</summary>
    internal static string SamplePlanXml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan"))
            .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

    internal static TabControl Strip(QuerySessionControl session) =>
        session.FindControl<TabControl>("SubTabControl")!;

    internal static List<TabItem> Documents(QuerySessionControl session) =>
        Strip(session).Items.OfType<TabItem>().ToList();

    /// <summary>The row-2 host the strip's selected content is presented in.</summary>
    internal static ContentControl DocumentHost(QuerySessionControl session) =>
        session.FindControl<ContentControl>("DocumentHost")!;

    internal static ContentControl OverviewHost(QuerySessionControl session) =>
        session.FindControl<ContentControl>("OverviewHost")!;

    internal static Control EditorView(QuerySessionControl session) =>
        session.FindControl<Grid>("EditorView")!;

    internal static Border EmptyStateOverlay(QuerySessionControl session) =>
        session.FindControl<Border>("EmptyStateOverlay")!;

    internal static RadioButton EditorSegment(QuerySessionControl session) =>
        session.FindControl<RadioButton>("EditorViewSegment")!;

    internal static RadioButton OverviewSegment(QuerySessionControl session) =>
        session.FindControl<RadioButton>("OverviewViewSegment")!;

    /// <summary>
    /// The scroller the strip's template wraps its headers in. It lives in that template's own
    /// namescope, so FindControl from the session cannot see it and the visual tree is the way in —
    /// the same reason the wheel handler reaches it through its sender.
    /// </summary>
    internal static ScrollViewer StripScroller(QuerySessionControl session) =>
        Strip(session).GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.Name == "DocumentStripScroll");

    /// <summary>
    /// Presses a key at <paramref name="target"/>, which is a control inside the session, so the
    /// event takes the route a real keystroke takes: down the window's tunnel handler first — the
    /// one that claims Ctrl+W and Ctrl+Tab — and only then up to the session's own.
    /// </summary>
    internal static void PressKey(InputElement target, Key key, KeyModifiers modifiers)
    {
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });
    }

    /// <summary>
    /// Brings a document to the front the way pressing its header does: a selection change on the
    /// strip, which under deselect-on-leave is always a change from nothing.
    /// </summary>
    /// <remarks>
    /// A synthetic pointer press would prove less, not more: headless pointer input needs a render
    /// backend to hit-test against, and what a press does once it lands is set this property. The
    /// session subscribes to the strip's SelectionChanged precisely because a press is the one
    /// arrival its own seam does not make.
    /// </remarks>
    internal static void PressHeader(QuerySessionControl session, TabItem document) =>
        Strip(session).SelectedItem = document;

    /// <summary>
    /// A document's own ✕. Last, not first: a History header carries a detach button between its
    /// label and its close.
    /// </summary>
    internal static Button CloseButton(TabItem document) =>
        ((StackPanel)document.Header!).Children.OfType<Button>().Last();

    /// <summary>Closes a document the way its header does.</summary>
    internal static void CloseFromHeader(TabItem document) =>
        CloseButton(document).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// Gives the session a server without one existing. Only the three fields a connection sets,
    /// and only so the code under test can see that it has one: the connection string is read as
    /// "not null" by the button gate and handed to a control that is never allowed to open it.
    /// </summary>
    internal static ServerConnection PretendConnected(
        QuerySessionControl session, string database = "master", string? serverName = null)
    {
        /* Port 1, which nothing listens on, so a load that escaped would fail rather than reach
           whatever SQL Server the machine running this happens to have. Same address the overview
           card tests use for the same reason. */
        var connection = new ServerConnection
        {
            ServerName = serverName ?? "tcp:127.0.0.1,1",
            DisplayName = "unit test",
        };

        SetField(session, "_serverConnection", connection);
        SetField(session, "_selectedDatabase", database);
        SetField(session, "_connectionString",
            connection.GetConnectionString(username: null, password: null, database));

        return connection;
    }

    /// <summary>
    /// The Overview the session is currently holding, or null if it has none.
    /// </summary>
    internal static QueryStoreOverviewControl? OverviewView(QuerySessionControl session) =>
        (QueryStoreOverviewControl?)GetField(session, "_overviewView");

    /// <summary>
    /// Calls the session's private <c>InvalidateOverviewView</c>, which is the last line of the
    /// connect block and the only thing in the app that runs it.
    /// </summary>
    /// <remarks>
    /// Reached this way because the block above it is a connection dialog and three round trips to
    /// a server. What that costs the pin is stated rather than hidden: these tests pin what
    /// invalidation DOES, not that connecting calls it. The call site is one line of
    /// <c>ShowConnectionDialogAsync</c> with a comment explaining its position.
    /// </remarks>
    internal static void InvalidateOverview(QuerySessionControl session) =>
        typeof(QuerySessionControl)
            .GetMethod("InvalidateOverviewView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, null);

    /// <summary>
    /// Opens a read-only schema document, the third of the three places in the app that builds a
    /// document header. Its own entry point fetches DDL from a server first.
    /// </summary>
    internal static void OpenSchemaDocument(QuerySessionControl session, string label, string content) =>
        typeof(QuerySessionControl)
            .GetMethod("AddSchemaTab", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [label, content, true]);

    /// <summary>
    /// The token the Overview's current load is running on, or null before it has started one.
    /// Two asks produce two of these, which is how a refresh is told from a redraw.
    /// </summary>
    internal static CancellationTokenSource? OverviewLoadToken(QueryStoreOverviewControl overview) =>
        (CancellationTokenSource?)GetField(overview, "_cts");

    /// <summary>
    /// A History document, built the way the app builds one rather than through the designer's
    /// parameterless constructor.
    /// </summary>
    /// <remarks>
    /// The difference is not cosmetic: the real constructor is where the control subscribes to its
    /// own detach and cancels its fetch there, and the designer one wires none of that. An empty
    /// connection string is a deliberate dead end — SqlConnection refuses it before opening a
    /// socket, so the load this kicks off on attach fails instantly and locally instead of dialling
    /// whatever is listening on the machine running the suite.
    /// </remarks>
    internal static QueryStoreHistoryControl NewHistory(string label = "0xABC") =>
        new(connectionString: "", queryHash: label, queryText: "select 1;", database: "master");

    /// <summary>
    /// Puts a live fetch token into a History control, standing in for a fetch still waiting on a
    /// server. Everything downstream cancels whatever it finds in this field, and a test has no way
    /// to hold a real fetch open without a real server to hold it open against.
    /// </summary>
    internal static CancellationTokenSource PlantHistoryFetch(QueryStoreHistoryControl history)
    {
        var fetch = new CancellationTokenSource();
        SetField(history, "_fetchCts", fetch);
        return fetch;
    }

    /// <summary>
    /// Stops an Overview load before it can reach a socket, then drains what it left behind.
    ///
    /// <para>A load is started by the view being asked for, and by the time control comes back the
    /// control is parked on its first dispatcher hop with a live token and nothing sent. Cancelling
    /// there means every await after it unwinds immediately — SqlClient's OpenAsync returns a
    /// cancelled task without dialling — so the test neither waits on a network timeout nor leaves
    /// a continuation to land in someone else's test.</para>
    /// </summary>
    internal static void StopOverviewLoad(QueryStoreOverviewControl? overview)
    {
        if (overview == null)
            return;

        ((CancellationTokenSource?)GetField(overview, "_cts"))?.Cancel();

        for (var pump = 0; pump < 10; pump++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void SetField(object target, string name, object? value) =>
        FieldOf(target, name).SetValue(target, value);

    private static object? GetField(object target, string name) =>
        FieldOf(target, name).GetValue(target);

    private static FieldInfo FieldOf(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name} no longer has a field called {name} — the test reaching " +
                "for it needs updating, not deleting.");
}
