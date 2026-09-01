using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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

    public MainWindow()
    {
        _credentialService = CredentialServiceFactory.Create();
        _connectionStore = new ConnectionStore();
        _appSettings = AppSettingsService.Load();

        // Apply user preferences on startup
        if (Enum.TryParse<TimeDisplayMode>(_appSettings.QueryStoreDefaultTimeDisplay, true, out var tdm))
            TimeDisplayHelper.Current = tdm;

        // Listen for file paths from other instances (e.g. SSMS extension)
        StartPipeServer();

        InitializeComponent();

        // Check for updates on startup (non-blocking)
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
           seventeenth. */
        if (MainTabControl.Items is INotifyCollectionChanged tabs)
            tabs.CollectionChanged += (_, _) => RefreshComparePlanAvailability();

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
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            LoadPlanFile(args[1]);
        }
        else
        {
            // Restore plans that were open in the previous session
            RestoreOpenPlans();
        }

        // Start MCP server if enabled in settings
        StartMcpServer();
    }

    private void StartPipeServer()
    {
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
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && TabsWithUnsavedChanges().Count > 0)
        {
            e.Cancel = true;
            _ = ConfirmWindowCloseAsync();
            return;
        }

        base.OnClosing(e);
    }

    private async Task ConfirmWindowCloseAsync()
    {
        foreach (var tab in MainTabControl.Items.OfType<TabItem>().ToList())
        {
            if (!await ConfirmCloseAsync(tab))
                return; // one Cancel cancels the shutdown
        }

        _closeConfirmed = true;
        Close();
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
        dialog.ShowDialog(this);
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
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
