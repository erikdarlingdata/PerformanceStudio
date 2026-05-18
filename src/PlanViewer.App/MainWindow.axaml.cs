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
                    case Key.W:
                        if (MainTabControl.SelectedItem is TabItem selected)
                        {
                            MainTabControl.Items.Remove(selected);
                            UpdateEmptyOverlay();
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
                    // Pipe error — restart the listener
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


    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void About_Click(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow();
        about.ShowDialog(this);
    }

#pragma warning disable CS0618 // Data/DataFormats.Files deprecated but IDataTransfer API differs
    private static readonly string[] _supportedExtensions = { ".sqlplan", ".xml", ".sql" };


#pragma warning restore CS0618


    private List<(string label, PlanViewerControl viewer)> CollectAllPlanTabs()
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
