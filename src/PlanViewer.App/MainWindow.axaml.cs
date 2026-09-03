using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PlanViewer.App.Controls;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Helpers;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.App.Mcp;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App;

public partial class MainWindow : Window
{
    private const string PipeName = "SQLPerformanceStudio_OpenFile";

    private readonly ICredentialService _credentialService;
    private readonly ConnectionStore _connectionStore;
    private readonly CancellationTokenSource _pipeCts = new();
    private McpHostService? _mcpHost;
    private CancellationTokenSource? _mcpCts;
    private int _queryCounter;
    private AppSettings _appSettings;
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// Set to true when the main window is closing. Detached windows check this
    /// to avoid re-docking into torn-down controls.
    /// </summary>
    internal bool IsShuttingDown { get; private set; }

    /// <summary>
    /// Whether this window's process-external startup services actually launched. Set by the
    /// launch methods themselves rather than by the <see cref="AppRuntimeMode"/> gate, so the
    /// test pinning that they stay off under the harness (#451) still fails if a future change
    /// starts one through a new path. Never read by the app.
    /// </summary>
    internal bool PipeServerStarted { get; private set; }
    internal bool StartupUpdateCheckStarted { get; private set; }
    internal bool McpServerStartAttempted { get; private set; }

    public MainWindow()
    {
        _credentialService = CredentialServiceFactory.Create();
        _connectionStore = new ConnectionStore();
        _appSettings = AppSettingsService.Load();

        // Apply user preferences on startup
        if (Enum.TryParse<TimeDisplayMode>(_appSettings.QueryStoreDefaultTimeDisplay, true, out var tdm))
            TimeDisplayHelper.Current = tdm;

        // Listen for file paths from other instances (e.g. SSMS extension).
        // Not in the test host (#451): every test window would grab the machine's single
        // SQLPerformanceStudio_OpenFile pipe slot and never release it — OnClosed never
        // runs there — racing any real Studio instance on the same box.
        if (!AppRuntimeMode.IsTestHost)
            StartPipeServer();

        InitializeComponent();

        // Check for updates on startup (non-blocking).
        // Not in the test host (#451): one GitHub hit per constructed window meant ~36
        // update checks per local test run.
        if (!AppRuntimeMode.IsTestHost)
            _ = CheckForUpdatesOnStartupAsync();

        // Build the Recent Plans submenu from saved state
        RebuildRecentPlansMenu();

        // Wire up drag-and-drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

        // Track tab changes to update empty overlay
        MainTabControl.SelectionChanged += (_, _) => UpdateEmptyOverlay();

        /* #447: one subscription rather than a call at each of the sixteen places that add or
           remove a tab. Compare Plans depends on how many plans exist across the WHOLE window, so
           opening or closing any tab can change whether it is available in every OTHER tab, and a
           refresh that has to be remembered at sixteen call sites is one that gets forgotten at the
           seventeenth.

           Watching content replacement as well as the collection is the part the first attempt at
           this got wrong: Get Actual Plan opens a tab holding a spinner and later swaps in the plan,
           so the tab that gains a plan is one this window already had. */
        TabContentWatcher.Watch(MainTabControl, RefreshComparePlanAvailability);

        // Global hotkeys via tunnel routing so they fire before AvaloniaEdit consumes them
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control)
            {
                switch (e.Key)
                {
                    case Key.N:
                        NewQuery_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Key.O:
                        OpenFile_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        break;
                    case Key.S:
                        _ = SaveQueryAsync();
                        e.Handled = true;
                        break;
                    case Key.W:
                        if (MainTabControl.SelectedItem is TabItem selected)
                        {
                            _ = TryCloseTabAsync(selected);
                            e.Handled = true;
                        }
                        break;
                    case Key.V:
                        // Only intercept paste when focus is NOT in a text editor
                        if (e.Source is not TextBox && e.Source is not AvaloniaEdit.Editing.TextArea)
                        {
                            _ = PasteXmlAsync();
                            e.Handled = true;
                        }
                        break;
                    case Key.Tab:
                        var tabCount = MainTabControl.Items.Count;
                        if (tabCount > 1)
                        {
                            MainTabControl.SelectedIndex = (MainTabControl.SelectedIndex + 1) % tabCount;
                            e.Handled = true;
                        }
                        break;
                }
            }
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.O)
            {
                OpenQuery_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Tab)
            {
                var tabCount = MainTabControl.Items.Count;
                if (tabCount > 1)
                {
                    MainTabControl.SelectedIndex = (MainTabControl.SelectedIndex - 1 + tabCount) % tabCount;
                    e.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);

        // Accept command-line argument or restore previously open plans
        OpenFromStartupArgs(Environment.GetCommandLineArgs());

        // Start MCP server if enabled in settings.
        // Not in the test host (#451): this reads the user's real ~/.planview settings and,
        // when enabled there, binds a real TCP port from inside the test runner. The menu
        // item needs no fallback write — its XAML default is already "MCP Server: Off".
        if (!AppRuntimeMode.IsTestHost)
            StartMcpServer();
    }

    /// <summary>
    /// Routes the file handed over on the command line — a double-clicked file association, or
    /// "PerformanceStudio.exe path" from a shell. Split from the constructor so the routing can
    /// be tested with an argv of the test's choosing.
    ///
    /// <para>Routed by extension, not straight to LoadPlanFile: every other way a path reaches
    /// this window (the pipe from a second instance, drag-and-drop, session restore) already
    /// goes through <see cref="OpenFileByExtension"/>, and RestoreOpenPlans' own doc comment
    /// describes exactly what skipping it does — "Sending a .sql file to LoadPlanFile would
    /// greet the user with 'the XML is not valid' where their query used to be." That greeting
    /// is precisely what "PerformanceStudio.exe query.sql" produced. Cold start was the one
    /// path left hard-wired to the plan loader.</para>
    /// </summary>
    internal void OpenFromStartupArgs(string[] args)
    {
        if (args.Length > 1 && File.Exists(args[1]))
        {
            OpenFileByExtension(args[1]);
        }
        else
        {
            // Restore plans that were open in the previous session
            RestoreOpenPlans();
        }
    }

    private void StartPipeServer()
    {
        PipeServerStarted = true;

        var token = _pipeCts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server);
                    var filePath = await reader.ReadLineAsync();

                    if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            OpenFileByExtension(filePath);
                            Activate();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Pipe error (e.g. another instance already holds the single
                    // server slot). Back off before retrying so we don't spin at
                    // 100% CPU recreating the listener in a tight loop.
                    try { await Task.Delay(1000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, token);
    }

    private void StartMcpServer()
    {
        // Set before the settings read: reading the user's real ~/.planview file is itself
        // part of what the test host must not do, so "attempted" starts here.
        McpServerStartAttempted = true;

        var settings = McpSettings.Load();
        if (!settings.Enabled)
        {
            McpStatusMenuItem.Header = "MCP Server: Off";
            return;
        }

        _mcpCts = new CancellationTokenSource();
        _mcpHost = new McpHostService(
            PlanSessionManager.Instance, _connectionStore, _credentialService, settings.Port);

        _ = _mcpHost.StartAsync(_mcpCts.Token);
        McpStatusMenuItem.Header = $"MCP Server: Running (port {settings.Port})";
    }

    protected override async void OnClosed(EventArgs e)
    {
        try
        {
            IsShuttingDown = true;

            // Save the list of currently open file-based plans for session restore
            SaveOpenPlans();

            _pipeCts.Cancel();

            if (_mcpHost != null && _mcpCts != null)
            {
                _mcpCts.Cancel();
                await _mcpHost.StopAsync(CancellationToken.None);
                _mcpHost = null;
            }

            // Close all detached free-floating windows
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var otherWindows = desktop.Windows.Where(w => w != this).ToList();
                foreach (var w in otherWindows)
                    w.Close();
            }
        }
        catch (Exception)
        {
            // Prevent unhandled exceptions from async void during shutdown
        }

        base.OnClosed(e);
    }

    private void UpdateEmptyOverlay()
    {
        EmptyOverlay.IsVisible = MainTabControl.Items.Count == 0;
    }


    // ── Unsaved query changes (#462) ──────────────────────────────────────

    /// <summary>
    /// Set once every tab has been asked about, so the second pass through
    /// <see cref="OnClosing"/> does not ask the whole window again.
    /// </summary>
    private bool _closeConfirmed;

    /* _closeConfirmed only latches AFTER the walk answers yes, so it does nothing about a
       second close request arriving WHILE the walk is still asking — a double-clicked X, or
       another Close() between two of the walk's dialogs (each prompt is modal, but the window
       is briefly its own again between them, and for the whole of a Save As picker). Each
       such request used to start a second concurrent walk: duplicate prompts about the same
       tabs, and a Cancel to one walk that the other never heard about. Same reentrancy class,
       and same one-latch answer, as the About window's update link (#485 review). */
    private bool _closeWalkInProgress;

    private const string CloseGlyph = "\u2715";   // ✕
    private const string ModifiedGlyph = "\u25CF"; // ●

    /// <summary>
    /// What a tab's close button shows. Dirty tabs get a filled dot, but it reverts to the
    /// × while the pointer is over the button — a marker you cannot click through is a tab
    /// you cannot close, which is the trade VS Code makes and the one Josh asked for.
    /// </summary>
    internal static string CloseButtonGlyph(bool isDirty, bool isPointerOver) =>
        isDirty && !isPointerOver ? ModifiedGlyph : CloseGlyph;

    /// <summary>
    /// Whether closing this tab would throw work away. Plan tabs are read-only, so only a
    /// query session can ever answer yes.
    /// </summary>
    internal static bool HasUnsavedChanges(TabItem tab) =>
        tab.Content is QuerySessionControl { IsDirty: true };

    /// <summary>
    /// Every tab that would lose work if the window closed right now, in tab order.
    ///
    /// <para>Deliberately not "the selected tab": the edit at risk is usually in the tab the
    /// user is not looking at, which is the whole reason a window-close prompt exists.</para>
    /// </summary>
    internal List<TabItem> TabsWithUnsavedChanges() =>
        MainTabControl.Items.OfType<TabItem>().Where(HasUnsavedChanges).ToList();

    // ── Unsaved changes in a detached window (#473) ───────────────────────

    /// <summary>
    /// The query sessions living in a detached window right now, and the window each is in.
    ///
    /// <para>A detached session is not in <see cref="MainTabControl"/>.Items, so
    /// <see cref="TabsWithUnsavedChanges"/> cannot see it: without this register the shutdown
    /// prompt honestly reports nothing to save while a dirty edit sits in another window.
    /// Detaching adds to it; re-docking and closing both take back out, which is why the
    /// register is keyed on the content control rather than on the tab it came from — by then
    /// there is no tab.</para>
    ///
    /// <para>Only query sessions go on it. Plan and Query Store windows are read-only and have
    /// nothing to lose, so there is nothing to remember about them.</para>
    /// </summary>
    private readonly List<(Window Window, QuerySessionControl Session)> _detachedQuerySessions = new();

    /// <summary>Every query session currently detached, dirty or not.</summary>
    internal IReadOnlyList<(Window Window, QuerySessionControl Session)> DetachedQuerySessions =>
        _detachedQuerySessions;

    internal void RememberDetachedQuerySession(Window window, Control content)
    {
        if (content is QuerySessionControl session)
            _detachedQuerySessions.Add((window, session));
    }

    internal void ForgetDetachedQuerySession(Control content)
    {
        if (content is QuerySessionControl session)
            _detachedQuerySessions.RemoveAll(d => d.Session == session);
    }

    /// <summary>
    /// Whether closing a detached window has to stop and ask.
    ///
    /// <para>Static and pure so the decision is testable without a window to click, the same
    /// trade <see cref="DecideClose"/> makes. Three ways to answer no, and each one matters:
    /// read-only content (a plan, a Query Store window) has nothing to save, an unmodified
    /// session has nothing to save, and once the app is shutting down the question has already
    /// been asked — <see cref="ConfirmWindowCloseAsync"/> asks it while the main window is
    /// still up, precisely so that <see cref="OnClosed"/> can force these windows shut without
    /// raising a dialog over a window that no longer exists.</para>
    /// </summary>
    internal static bool DetachedContentNeedsSavePrompt(Control content, bool isShuttingDown) =>
        !isShuttingDown && content is QuerySessionControl { IsDirty: true };

    /// <summary>
    /// The close guard handed to every detached window. Returning null is the whole read-only
    /// path: no prompt, no cancelled close, no detour.
    /// </summary>
    internal Task<bool>? DetachedQueryCloseGuard(Control content, Window detachedWindow) =>
        DetachedContentNeedsSavePrompt(content, IsShuttingDown)
            ? ConfirmDetachedCloseAsync((QuerySessionControl)content, detachedWindow)
            : null;

    /// <summary>
    /// Asks about a detached session, owned by the window that is being closed rather than by
    /// the main one — the answer is about that window's content, and a prompt parented to a
    /// window behind it is a prompt nobody can see. The file picker a Save As needs is taken
    /// from the same window for the same reason.
    /// </summary>
    /// <returns>Whether the close may proceed.</returns>
    private async Task<bool> ConfirmDetachedCloseAsync(QuerySessionControl session, Window owner)
    {
        // The shutdown walk works from a snapshot, and a save taken during it settles more than
        // its own entry. Same early out ConfirmCloseAsync has, for the same reason.
        if (!session.IsDirty)
            return true;

        owner.Activate(); // show what is being asked about, as the tab walk does with selection

        var choice = await UnsavedChangesDialog.ShowAsync(owner, owner.Title ?? "this query");

        return DecideClose(choice, session.SourceFilePath != null) switch
        {
            CloseAction.Cancel => false,
            CloseAction.Close => true,
            CloseAction.SaveInPlace => SaveQueryToPath(null, session, session.SourceFilePath!),
            _ => await SaveQueryAsync(null, session, owner.StorageProvider)
        };
    }

    /// <summary>
    /// Every detached window that would lose work if the app closed right now.
    /// </summary>
    internal List<(Window Window, QuerySessionControl Session)> DetachedSessionsWithUnsavedChanges() =>
        _detachedQuerySessions.Where(d => d.Session.IsDirty).ToList();

    /// <summary>
    /// Everything closing the app would throw away, in the order it will be asked about: the
    /// tabs first in tab order, then the detached windows.
    ///
    /// <para>One list rather than two walks, because two walks is how the second one gets
    /// forgotten — which is the whole of #473. <c>Tab</c> is null for a detached session; there
    /// is no tab, and <c>Owner</c> is the window that has to own its prompt.</para>
    /// </summary>
    internal List<(TabItem? Tab, QuerySessionControl Session, Window Owner)> UnsavedWorkOnClose()
    {
        var work = new List<(TabItem? Tab, QuerySessionControl Session, Window Owner)>();

        foreach (var tab in TabsWithUnsavedChanges())
            work.Add((tab, (QuerySessionControl)tab.Content!, this));

        foreach (var detached in DetachedSessionsWithUnsavedChanges())
            work.Add((null, detached.Session, detached.Window));

        return work;
    }

    /// <summary>
    /// Whether closing the main window has anything to ask about at all. Tabs and detached
    /// windows both count: "Detach to Window" is not a way to opt out of being asked.
    /// </summary>
    internal bool CloseNeedsConfirmation() => UnsavedWorkOnClose().Count > 0;

    /// <summary>
    /// What the close path does with an answer to the prompt.
    /// </summary>
    internal enum CloseAction
    {
        /// <summary>Proceed with the close.</summary>
        Close,

        /// <summary>Leave the tab, and the window, alone.</summary>
        Cancel,

        /// <summary>Overwrite the file the session came from, then close.</summary>
        SaveInPlace,

        /// <summary>Ask where to put it first, then close if it was actually written.</summary>
        SaveAs
    }

    /// <summary>
    /// Turns an answer into an action, split out from the dialog so the decision can be
    /// tested without a window to click. A never-saved scratch tab has nowhere to write, so
    /// its Save has to become a Save As rather than silently doing nothing.
    /// </summary>
    internal static CloseAction DecideClose(UnsavedChangesChoice choice, bool hasFile) => choice switch
    {
        UnsavedChangesChoice.Cancel => CloseAction.Cancel,
        UnsavedChangesChoice.DontSave => CloseAction.Close,
        _ => hasFile ? CloseAction.SaveInPlace : CloseAction.SaveAs
    };

    /// <summary>
    /// Asks about a tab if it needs asking about. Returns whether the close may proceed —
    /// false means the user cancelled, or a save they asked for failed.
    /// </summary>
    private async Task<bool> ConfirmCloseAsync(TabItem tab)
    {
        if (!HasUnsavedChanges(tab))
            return true;

        var session = (QuerySessionControl)tab.Content!;

        MainTabControl.SelectedItem = tab; // show what is being asked about
        var choice = await UnsavedChangesDialog.ShowAsync(this, GetTabLabel(tab));

        return DecideClose(choice, session.SourceFilePath != null) switch
        {
            CloseAction.Cancel => false,
            CloseAction.Close => true,
            CloseAction.SaveInPlace => SaveQueryToPath(tab, session, session.SourceFilePath!),
            _ => await SaveQueryAsync(tab, session)
        };
    }

    /// <summary>
    /// Closes one tab, asking first. Returns false when the close was refused, so callers
    /// closing several tabs can stop rather than plough on past a Cancel.
    /// </summary>
    private async Task<bool> TryCloseTabAsync(TabItem tab)
    {
        if (!await ConfirmCloseAsync(tab))
            return false;

        MainTabControl.Items.Remove(tab);
        UpdateEmptyOverlay();
        return true;
    }

    /// <summary>
    /// Closes a run of tabs. Cancelling any one of them abandons the rest: "close all tabs"
    /// answered with Cancel means the user changed their mind about all of them, not about
    /// that one.
    /// </summary>
    private async Task CloseTabsAsync(IEnumerable<TabItem> tabs)
    {
        foreach (var tab in tabs.ToList())
        {
            if (!await TryCloseTabAsync(tab))
                return;
        }
    }

    /// <summary>
    /// Closes everything but one tab, then puts the survivor back in front — it may not have
    /// been the selected tab, and <see cref="ConfirmCloseAsync"/> moves the selection around
    /// to show what it is asking about.
    /// </summary>
    private async Task CloseOtherTabsAsync(TabItem keepTab)
    {
        await CloseTabsAsync(MainTabControl.Items.OfType<TabItem>().Where(t => t != keepTab));

        if (MainTabControl.Items.Contains(keepTab))
            MainTabControl.SelectedItem = keepTab;
    }

    /// <summary>
    /// Holds the window open long enough to ask about every modified tab.
    ///
    /// <para>The prompt is async and Closing is not, so the first pass cancels the close
    /// outright and re-issues it from <see cref="ConfirmWindowCloseAsync"/> once every tab
    /// has answered. Anything that is not a query tab, and any window with nothing modified,
    /// closes on the first pass without a detour.</para>
    ///
    /// <para>#473: "every tab" is not everything. A detached session is off the tab strip and
    /// still holds unsaved work, so <see cref="CloseNeedsConfirmation"/> counts those too.</para>
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && CloseNeedsConfirmation())
        {
            /* The cancel is unconditional — a close arriving mid-walk must not fall through
               to base and succeed while the walk is still asking — but only the FIRST one may
               start a walk. The latch check lives inside this branch on purpose: the walk's
               own Close() at the end re-enters here with _closeWalkInProgress still true, and
               it has to sail through on _closeConfirmed above, not be swallowed as a
               duplicate. */
            e.Cancel = true;

            if (!_closeWalkInProgress)
            {
                _closeWalkInProgress = true;
                _ = ConfirmWindowCloseAsync();
            }

            return;
        }

        base.OnClosing(e);
    }

    private async Task ConfirmWindowCloseAsync()
    {
        try
        {
            if (!await ConfirmAllUnsavedWorkAsync())
                return; // one Cancel cancels the shutdown

            _closeConfirmed = true;
            Close();
        }
        finally
        {
            /* Cleared however the walk ends — confirmed, cancelled, or a prompt that threw —
               so a cancelled shutdown leaves the X able to start a fresh walk. */
            _closeWalkInProgress = false;
        }
    }

    /// <summary>
    /// Asks about every piece of unsaved work in the app, and answers whether whatever is
    /// about to destroy that work may proceed.
    ///
    /// <para>Split out of <see cref="ConfirmWindowCloseAsync"/> because the window close is
    /// not the only way the process ends: the About window's Velopack "Restart Now" calls
    /// ApplyUpdatesAndRestart, which exits without ever raising Closing — so #462's and
    /// #473's walk never ran on that route and dirty edits were discarded without a word.
    /// This is only the walk: it does not latch <see cref="_closeConfirmed"/> and does not
    /// Close, so the restart path can take the answer without the close's bookkeeping.</para>
    ///
    /// <para>#473: the sessions that are no longer tabs are asked about here, while every
    /// window is still up, rather than from OnClosed where they are force-closed. By then the
    /// main window is gone and the app is on its way out — a dialog raised there is at best a
    /// window nobody expects and at worst a shutdown that never finishes. Asking here is what
    /// earns DetachedContentNeedsSavePrompt the right to wave that force-close through.</para>
    /// </summary>
    /// <returns>False the moment anyone answers Cancel, or a save they asked for fails.</returns>
    internal async Task<bool> ConfirmAllUnsavedWorkAsync()
    {
        foreach (var work in UnsavedWorkOnClose())
        {
            var mayClose = work.Tab != null
                ? await ConfirmCloseAsync(work.Tab)
                : await ConfirmDetachedCloseAsync(work.Session, work.Owner);

            if (!mayClose)
                return false;
        }

        return true;
    }


    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_appSettings);
        _settingsWindow.SettingsSaved += settings =>
        {
            _appSettings = settings;
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }

    private void About_Click(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow();
        about.ShowDialog(this);
    }

#pragma warning disable CS0618 // Data/DataFormats.Files deprecated but IDataTransfer API differs
    private static readonly string[] _supportedExtensions = { ".sqlplan", ".xml", ".sql" };


#pragma warning restore CS0618


    /// <summary>
    /// Re-decides whether Compare Plans is offered, for every query session in the window (#447).
    ///
    /// <para>The button used to be enabled from a session's OWN plan count, so two queries in two
    /// separate sessions — one plan each — left it disabled in both, even though comparing them is
    /// exactly what it is for. The plans were always reachable: <see cref="CollectAllPlanTabs"/>
    /// spans sessions and is what the file-mode Compare button has always used, which is why saving
    /// a plan and reopening it worked around this.</para>
    /// </summary>
    internal void RefreshComparePlanAvailability()
    {
        var comparable = CollectAllPlanTabs().Count >= 2;
        foreach (var item in MainTabControl.Items)
        {
            if (item is TabItem { Content: QuerySessionControl session })
                session.SetCompareAvailability(comparable);
        }
    }

    internal List<(string label, PlanViewerControl viewer)> CollectAllPlanTabs()
    {
        var entries = new List<(string label, PlanViewerControl viewer)>();

        foreach (var item in MainTabControl.Items)
        {
            if (item is not TabItem tab) continue;

            // File-mode tabs: DockPanel containing PlanViewerControl
            if (tab.Content is DockPanel dock)
            {
                var viewer = dock.Children.OfType<PlanViewerControl>().FirstOrDefault();
                if (viewer?.CurrentPlan != null)
                {
                    var label = GetTabLabel(tab);
                    entries.Add((label, viewer));
                }
            }

            // Query session tabs: iterate sub-tabs
            if (tab.Content is QuerySessionControl session)
            {
                var sessionLabel = GetTabLabel(tab);
                foreach (var (planLabel, viewer) in session.GetPlanTabs())
                {
                    entries.Add(($"{sessionLabel} > {planLabel}", viewer));
                }
            }
        }

        return entries;
    }


    // ── Recent Plans & Session Restore ────────────────────────────────────


    /// <summary>
    /// One modal for everything that goes wrong outside a file operation — a plan that is not
    /// a plan, a clipboard with nothing in it, advice that could not be built.
    ///
    /// <para>Shown ownerless until this window is visible, for the same reason ShowFileError is.
    /// Both the command-line open and the restore of the previous session's tabs run from the
    /// constructor, so a plan file that fails XML validation at startup reaches here before there
    /// is anything to be modal over, and ShowDialog against a window that has not been shown
    /// throws rather than reporting the problem it was called about.</para>
    /// </summary>
    private void ShowError(string message)
    {
        var dialog = new Window
        {
            Title = "Performance Studio",
            Width = 450,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = this.Icon,
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.Parse("#E4E6EB"))
                    }
                }
            }
        };

        if (IsVisible)
            dialog.ShowDialog(this);
        else
            dialog.Show();
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        // Before the first await, so the flag is visible to a caller synchronously.
        StartupUpdateCheckStarted = true;

        try
        {
            await Task.Delay(5000); // Don't slow down startup

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            {
                try
                {
                    var mgr = new Velopack.UpdateManager(
                        new Velopack.Sources.GithubSource(
                            "https://github.com/erikdarlingdata/PerformanceStudio", null, false));

                    var update = await mgr.CheckForUpdatesAsync();
                    if (update != null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            Title = $"Performance Studio — Update v{update.TargetFullRelease.Version} available (Help > About)";
                        });
                        return;
                    }
                }
                catch
                {
                    // Velopack not available — fall through
                }
            }

            var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0);
            var result = await UpdateChecker.CheckAsync(currentVersion);
            if (result.UpdateAvailable)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Title = $"Performance Studio — Update {result.LatestVersion} available (Help > About)";
                });
            }
        }
        catch
        {
            // Never crash on update check
        }
    }
}
