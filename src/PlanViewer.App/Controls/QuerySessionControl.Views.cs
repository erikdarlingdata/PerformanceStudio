using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PlanViewer.App.Controls;

/* The session's two views, and the bar that switches between them.

   A view is not a document. The editor exists from construction and cannot be closed; the Overview
   is a live look at the whole server rather than something the session opened. Both are held in the
   tree and shown or hidden, where a document is realised only while it is the selected one — and
   that difference is deliberate in both directions. A view rebuilt on every visit would throw away
   the editor's undo history and the Overview's fetched data; a document kept alive off-screen would
   stop a History fetch noticing that the user has navigated away from it. */
public partial class QuerySessionControl : UserControl
{
    /// <summary>
    /// The Overview, once something has asked for it. Built on first use and kept, so switching
    /// away and back does not throw away what it fetched; thrown away when the session connects
    /// somewhere else (see <see cref="InvalidateOverviewView"/>).
    /// </summary>
    private QueryStoreOverviewControl? _overviewView;

    /// <summary>
    /// Up while the surface machine is latching the view bar, so a latch does not come back round
    /// as though the user had pressed a segment.
    /// </summary>
    private bool _latchingViewBar;

    /// <summary>
    /// Wires the view bar's segments up. Called once, from the constructor.
    /// </summary>
    private void SetupViewBar()
    {
        EditorViewSegment.IsCheckedChanged += ViewSegment_IsCheckedChanged;
        OverviewViewSegment.IsCheckedChanged += ViewSegment_IsCheckedChanged;
    }

    /// <summary>
    /// Shows the surface the session is on and latches the segment that matches it.
    /// </summary>
    private void ApplySurface()
    {
        _latchingViewBar = true;
        try
        {
            EditorViewSegment.IsChecked = _surface == SessionSurface.Editor;
            OverviewViewSegment.IsChecked = _surface == SessionSurface.Overview;
        }
        finally
        {
            _latchingViewBar = false;
        }

        EditorView.IsVisible = _surface == SessionSurface.Editor;
        OverviewHost.IsVisible = _surface == SessionSurface.Overview;
        DocumentHost.IsVisible = _surface == SessionSurface.Documents;
    }

    /// <summary>
    /// A segment being switched on is a request for that view. Both segments raise this on every
    /// switch — one clearing, one setting — so only the one going on is the request.
    /// </summary>
    private async void ViewSegment_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_latchingViewBar)
            return;

        if (sender is not RadioButton segment || segment.IsChecked != true)
            return;

        if (ReferenceEquals(segment, EditorViewSegment))
            SelectEditor();
        else
            await ShowOverviewAsync();
    }

    /// <summary>
    /// Shows the Query Store Overview: built the first time it is asked for, refreshed every time.
    /// </summary>
    /// <remarks>
    /// <para>Every time, because the control has no timer of its own — an explicit trigger is the
    /// only thing that ever reloads it, so the alternative is coming back to a view that quietly
    /// shows yesterday's numbers. The refresh keeps whatever range the user had chosen on the
    /// slicer, so asking for the view again cannot silently throw their window away.</para>
    ///
    /// <para>Strict parity with the toolbar button this replaces for a session that has never
    /// connected: the segment is always enabled and asks for a connection rather than sitting
    /// disabled behind an explanation nobody asked for, and cancelling that dialog leaves the user
    /// where they were.</para>
    /// </remarks>
    private async Task ShowOverviewAsync()
    {
        if (_serverConnection == null || _connectionString == null)
        {
            await ShowConnectionDialogAsync();
            if (_serverConnection == null || _connectionString == null)
            {
                /* The segment latched the moment it was pressed and the surface never moved, so
                   the bar has to be put back to what is actually showing. */
                ApplySurface();
                return;
            }
        }

        if (_overviewView == null)
        {
            _overviewView = BuildOverviewView();
            OverviewHost.Content = _overviewView;
        }

        /* Held locally because a reconnect during the load below throws the field away, and the
           failure from the load that was already in flight still has to go somewhere. */
        var overview = _overviewView;

        SelectOverview();

        /* After the surface flip, not before: the flip clears the strip, so a "loading" message set
           ahead of it would be wiped by the very switch that made it worth saying. */
        SetStatus("Loading Query Store Overview...");

        try
        {
            await overview.LoadAsync();
            if (_surface == SessionSurface.Overview)
                ClearStatus();
        }
        catch (Exception ex)
        {
            /* A load now outlives the view it was started from — switching away is a hide, not a
               detach, so nothing cancels it. Its outcome goes to the session's strip only while the
               Overview is still what the user is looking at. Otherwise it goes to the control's own
               badge, where a failed slicer refresh already reports itself, and is waiting there
               when they come back rather than hanging over whatever they moved to. */
            if (_surface == SessionSurface.Overview)
                SetStatusFromException(ex);
            else
                overview.ShowRefreshError(ex);
        }
    }

    private QueryStoreOverviewControl BuildOverviewView()
    {
        var supportsWaitStats = _serverMetadata?.SupportsQueryStoreWaitStats ?? false;
        var overview = new QueryStoreOverviewControl(_serverConnection!, _credentialService,
            supportsWaitStats: supportsWaitStats);

        overview.DrillDownRequested += async (_, args) =>
        {
            // Open a single-database Query Store tab directly (no connection dialog)
            _selectedDatabase = args.Database;
            _connectionString = _serverConnection!.GetConnectionString(_credentialService, args.Database);
            await OpenQueryStoreForDatabaseAsync(args.Database, args.StartUtc, args.EndUtc);
        };

        return overview;
    }

    /// <summary>
    /// Throws the Overview away, because the session has connected somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>The control asks its server what it supports once, at construction, and holds the
    /// answer for good — a wait-stats answer from the old server would be wrong about the new one
    /// for the rest of the session, invisibly. Rebuilding also matches what the toolbar button
    /// always did, which was a fresh control per click.</para>
    ///
    /// <para>Only reconnecting invalidates it. Changing the database picker does not: the Overview
    /// is master-scoped and looks at every database on the server, so the session's current
    /// database says nothing about it. A reconnect to the SAME server does invalidate,
    /// deliberately — reconnect is this app's hard-refresh gesture.</para>
    /// </remarks>
    private void InvalidateOverviewView()
    {
        _overviewView = null;
        OverviewHost.Content = null;

        /* Nothing to show and nothing to rebuild from yet, so the user cannot be left staring at a
           view that is no longer there. */
        if (_surface == SessionSurface.Overview)
            SelectEditor();
    }
}
