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

    public MainWindow()
    {
        _credentialService = CredentialServiceFactory.Create();
        _connectionStore = new ConnectionStore();
        _appSettings = AppSettingsService.Load();

        // Listen for file paths from other instances (e.g. SSMS extension)
        StartPipeServer();

        InitializeComponent();

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
        // Save the list of currently open file-based plans for session restore
        SaveOpenPlans();

        _pipeCts.Cancel();

        if (_mcpHost != null && _mcpCts != null)
        {
            _mcpCts.Cancel();
            await _mcpHost.StopAsync(CancellationToken.None);
            _mcpHost = null;
        }

        base.OnClosed(e);
    }

    private void UpdateEmptyOverlay()
    {
        EmptyOverlay.IsVisible = MainTabControl.Items.Count == 0;
    }

    private void NewQuery_Click(object? sender, RoutedEventArgs e)
    {
        _queryCounter++;
        var label = $"Query {_queryCounter}";

        var session = new QuerySessionControl(_credentialService, _connectionStore);
        var tab = CreateTab(label, session);

        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        UpdateEmptyOverlay();
    }

    private async void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQL Server Execution Plans")
                {
                    Patterns = new[] { "*.sqlplan" }
                },
                new FilePickerFileType("SQL Scripts")
                {
                    Patterns = new[] { "*.sql" }
                },
                new FilePickerFileType("XML Files")
                {
                    Patterns = new[] { "*.xml" }
                },
                FilePickerFileTypes.All
            }
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path != null)
                OpenFileByExtension(path);
        }
    }

    private async void PasteXml_Click(object? sender, RoutedEventArgs e)
    {
        await PasteXmlAsync();
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

    private static bool IsSupportedFile(string? path)
    {
        return path != null && _supportedExtensions.Any(ext =>
            path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null && files.Any(f => IsSupportedFile(f.TryGetLocalPath())))
                e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;

        var files = e.Data.GetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (IsSupportedFile(path))
                OpenFileByExtension(path!);
        }
    }
#pragma warning restore CS0618

    private void OpenFileByExtension(string filePath)
    {
        if (filePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            LoadSqlFile(filePath);
        else
            LoadPlanFile(filePath);
    }

    private void LoadSqlFile(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var fileName = Path.GetFileName(filePath);

            _queryCounter++;
            var session = new QuerySessionControl(_credentialService, _connectionStore);
            session.QueryEditor.Text = text;

            var tab = CreateTab(fileName, session);
            MainTabControl.Items.Add(tab);
            MainTabControl.SelectedItem = tab;
            UpdateEmptyOverlay();
        }
        catch (Exception ex)
        {
            var dialog = new Window
            {
                Title = "Error Opening File",
                Width = 450,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Failed to open: {Path.GetFileName(filePath)}",
                            FontWeight = FontWeight.Bold,
                            Margin = new Avalonia.Thickness(0, 0, 0, 10)
                        },
                        new TextBlock
                        {
                            Text = ex.Message,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            };
            dialog.ShowDialog(this);
        }
    }

    private void LoadPlanFile(string filePath)
    {
        try
        {
            var xml = File.ReadAllText(filePath);

            // SSMS saves plans as UTF-16 with encoding="utf-16" in the XML declaration.
            // File.ReadAllText auto-detects the BOM, but the resulting C# string still
            // contains encoding="utf-16" which causes XDocument.Parse to fail.
            xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

            var fileName = Path.GetFileName(filePath);

            if (!ValidatePlanXml(xml, fileName))
                return;

            var viewer = new PlanViewerControl();
            viewer.LoadPlan(xml, fileName);
            viewer.SourceFilePath = filePath;

            // Wrap viewer with advice toolbar
            var content = CreatePlanTabContent(viewer);

            var tab = CreateTab(fileName, content);
            MainTabControl.Items.Add(tab);
            MainTabControl.SelectedItem = tab;
            UpdateEmptyOverlay();

            // Track in recent plans list and persist
            TrackRecentPlan(filePath);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to open {Path.GetFileName(filePath)}:\n\n{ex.Message}");
        }
    }

    private async Task PasteXmlAsync()
    {
        var clipboard = this.Clipboard;
        if (clipboard == null) return;

        var xml = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(xml))
        {
            ShowError("The clipboard does not contain any text.");
            return;
        }

        xml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        if (!ValidatePlanXml(xml, "Pasted Plan"))
            return;

        var viewer = new PlanViewerControl();
        viewer.LoadPlan(xml, "Pasted Plan");

        var content = CreatePlanTabContent(viewer);
        var tab = CreateTab("Pasted Plan", content);
        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        UpdateEmptyOverlay();
    }

    private bool ValidatePlanXml(string xml, string label)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            if (doc.Root?.Name.LocalName != "ShowPlanXML" &&
                doc.Descendants(ns + "ShowPlanXML").FirstOrDefault() == null)
            {
                ShowError($"{label}: XML is valid but does not appear to be a SQL Server execution plan.\n\nExpected root element: ShowPlanXML");
                return false;
            }
            return true;
        }
        catch (System.Xml.XmlException ex)
        {
            ShowError($"{label}: The XML is not valid.\n\n{ex.Message}");
            return false;
        }
    }

    private DockPanel CreatePlanTabContent(PlanViewerControl viewer)
    {
        var humanBtn = new Button
        {
            Content = "\U0001f9d1 Human Advice",
            Height = 28,
            Padding = new Avalonia.Thickness(10, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        var robotBtn = new Button
        {
            Content = "\U0001f916 Robot Advice",
            Height = 28,
            Padding = new Avalonia.Thickness(10, 0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        Action showHumanAdvice = () =>
        {
            if (viewer.CurrentPlan == null) return;
            var analysis = ResultMapper.Map(viewer.CurrentPlan, "file", viewer.Metadata);
            ShowAdviceWindow("Advice for Humans", TextFormatter.Format(analysis), analysis);
        };

        Action showRobotAdvice = () =>
        {
            if (viewer.CurrentPlan == null) return;
            var analysis = ResultMapper.Map(viewer.CurrentPlan, "file", viewer.Metadata);
            var json = JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
            ShowAdviceWindow("Advice for Robots", json);
        };

        humanBtn.Click += (_, _) => showHumanAdvice();
        robotBtn.Click += (_, _) => showRobotAdvice();

        var compareBtn = new Button
        {
            Content = "\u2194 Compare Plans",
            Height = 28,
            Padding = new Avalonia.Thickness(10, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        compareBtn.Click += (_, _) => ShowCompareDialog();

        var separator1 = new TextBlock
        {
            Text = "|",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Margin = new Avalonia.Thickness(4, 0)
        };

        var copyReproBtn = new Button
        {
            Content = "\U0001f4cb Copy Repro",
            Height = 28,
            Padding = new Avalonia.Thickness(10, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        Func<System.Threading.Tasks.Task> copyRepro = async () =>
        {
            if (viewer.CurrentPlan == null) return;
            var queryText = GetQueryTextFromPlan(viewer);
            var planXml = viewer.RawXml;
            var database = ExtractDatabaseFromPlanXml(planXml);

            var reproScript = ReproScriptBuilder.BuildReproScript(
                queryText, database, planXml,
                isolationLevel: null, source: "Performance Studio");

            var clipboard = this.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(reproScript);
            }
        };

        copyReproBtn.Click += async (_, _) => await copyRepro();

        // Wire up context menu events from PlanViewerControl
        viewer.HumanAdviceRequested += (_, _) => showHumanAdvice();
        viewer.RobotAdviceRequested += (_, _) => showRobotAdvice();
        viewer.CopyReproRequested += async (_, _) => await copyRepro();

        var getActualPlanBtn = new Button
        {
            Content = "\u25b6 Run Repro",
            Height = 28,
            Padding = new Avalonia.Thickness(10, 0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        getActualPlanBtn.Click += async (_, _) =>
        {
            if (viewer.CurrentPlan == null) return;
            await GetActualPlanFromFile(viewer);
        };

        var separator2 = new TextBlock
        {
            Text = "|",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Margin = new Avalonia.Thickness(4, 0)
        };

        var queryStoreBtn = new Button
        {
            Content = "\U0001f4ca Query Store",
            Height = 28,
            Padding = new Avalonia.Thickness(10, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };
        ToolTip.SetTip(queryStoreBtn, "Open a Query Store session");
        queryStoreBtn.Click += (_, _) =>
        {
            _queryCounter++;
            var session = new QuerySessionControl(_credentialService, _connectionStore);
            var tab = CreateTab($"Query {_queryCounter}", session);
            MainTabControl.Items.Add(tab);
            MainTabControl.SelectedItem = tab;
            UpdateEmptyOverlay();
            session.TriggerQueryStore();
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Avalonia.Thickness(8, 6),
            Children = { humanBtn, robotBtn, compareBtn, separator1, copyReproBtn, getActualPlanBtn, separator2, queryStoreBtn }
        };

        var panel = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        panel.Children.Add(toolbar);
        panel.Children.Add(viewer);

        return panel;
    }

    private void ShowAdviceWindow(string title, string content, AnalysisResult? analysis = null)
    {
        var styledContent = AdviceContentBuilder.Build(content, analysis);

        var scrollViewer = new ScrollViewer
        {
            Content = styledContent,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var copyBtn = new Button
        {
            Content = "Copy to Clipboard",
            Height = 32,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        var closeBtn = new Button
        {
            Content = "Close",
            Height = 32,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 8, 0, 0)
        };
        buttonPanel.Children.Add(copyBtn);
        buttonPanel.Children.Add(closeBtn);

        var panel = new DockPanel { Margin = new Avalonia.Thickness(12) };
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        panel.Children.Add(buttonPanel);
        panel.Children.Add(scrollViewer);

        var window = new Window
        {
            Title = $"Performance Studio — {title}",
            Width = 700,
            Height = 600,
            MinWidth = 400,
            MinHeight = 300,
            Icon = this.Icon,
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Content = panel
        };

        copyBtn.Click += async (_, _) =>
        {
            var clipboard = window.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(content);
                copyBtn.Content = "Copied!";
                await Task.Delay(1500);
                copyBtn.Content = "Copy to Clipboard";
            }
        };

        closeBtn.Click += (_, _) => window.Close();

        window.Show(this);
    }

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

    private static string GetTabLabel(TabItem tab)
    {
        if (tab.Header is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
            return tb.Text ?? "Tab";
        if (tab.Header is string s)
            return s;
        return "Tab";
    }

    private void ShowCompareDialog()
    {
        var planTabs = CollectAllPlanTabs();
        if (planTabs.Count < 2)
        {
            // Not enough plans to compare
            return;
        }

        var items = planTabs.Select(t => t.label).ToList();

        var comboA = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = 0,
            Width = 250,
            Height = 28,
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0)
        };

        var comboB = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = items.Count > 1 ? 1 : 0,
            Width = 250,
            Height = 28,
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0)
        };

        var compareBtn = new Button
        {
            Content = "Compare",
            Height = 32,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Height = 32,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        void UpdateCompareEnabled()
        {
            compareBtn.IsEnabled = comboA.SelectedIndex >= 0 && comboB.SelectedIndex >= 0
                && comboA.SelectedIndex != comboB.SelectedIndex;
        }

        comboA.SelectionChanged += (_, _) => UpdateCompareEnabled();
        comboB.SelectionChanged += (_, _) => UpdateCompareEnabled();
        UpdateCompareEnabled();

        var rowA = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
            Children =
            {
                new TextBlock { Text = "Plan A:", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Width = 55 },
                comboA
            }
        };

        var rowB = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "Plan B:", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Width = 55 },
                comboB
            }
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
            Children = { compareBtn, cancelBtn }
        };

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = "Select two plans to compare:", FontSize = 14, Margin = new Avalonia.Thickness(0, 0, 0, 12) },
                rowA,
                rowB,
                buttonPanel
            }
        };

        var dialog = new Window
        {
            Title = "Compare Plans",
            Width = 420,
            Height = 220,
            MinWidth = 420,
            MinHeight = 220,
            Icon = this.Icon,
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        compareBtn.Click += (_, _) =>
        {
            var idxA = comboA.SelectedIndex;
            var idxB = comboB.SelectedIndex;
            if (idxA < 0 || idxB < 0 || idxA == idxB) return;

            var (labelA, viewerA) = planTabs[idxA];
            var (labelB, viewerB) = planTabs[idxB];

            var analysisA = ResultMapper.Map(viewerA.CurrentPlan!, "file");
            var analysisB = ResultMapper.Map(viewerB.CurrentPlan!, "file");

            var comparison = ComparisonFormatter.Compare(analysisA, analysisB, labelA, labelB);
            dialog.Close();
            ShowAdviceWindow("Plan Comparison", comparison);
        };

        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.ShowDialog(this);
    }

    private TabItem CreateTab(string label, Control content)
    {
        var headerText = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };

        var closeBtn = new Button
        {
            Content = "\u2715",
            MinWidth = 22,
            MinHeight = 22,
            Width = 22,
            Height = 22,
            Padding = new Avalonia.Thickness(0),
            FontSize = 11,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { headerText, closeBtn }
        };

        var tab = new TabItem { Header = header, Content = content };
        closeBtn.Tag = tab;
        closeBtn.Click += CloseTab_Click;

        // Right-click context menu
        var copyPathItem = new MenuItem { Header = "Copy Path", Tag = tab };
        // Only visible when tab content has a file path
        var filePath = GetTabFilePath(tab);
        copyPathItem.IsVisible = filePath != null;

        var contextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Rename Tab", Tag = new object[] { header, headerText } },
                copyPathItem,
                new Separator(),
                new MenuItem { Header = "Close", Tag = tab, InputGesture = new KeyGesture(Key.W, KeyModifiers.Control) },
                new MenuItem { Header = "Close Other Tabs", Tag = tab },
                new MenuItem { Header = "Close All Tabs" }
            }
        };

        foreach (var item in contextMenu.Items.OfType<MenuItem>())
            item.Click += TabContextMenu_Click;

        header.ContextMenu = contextMenu;

        return tab;
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TabItem tab)
        {
            MainTabControl.Items.Remove(tab);
            UpdateEmptyOverlay();
        }
    }

    private void TabContextMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;

        var headerText = item.Header?.ToString();

        switch (headerText)
        {
            case "Rename Tab":
                if (item.Tag is object[] parts)
                    StartRename((StackPanel)parts[0], (TextBlock)parts[1]);
                break;

            case "Copy Path":
                if (item.Tag is TabItem pathTab)
                {
                    var path = GetTabFilePath(pathTab);
                    if (path != null)
                        _ = this.Clipboard?.SetTextAsync(path);
                }
                break;

            case "Close":
                if (item.Tag is TabItem tab)
                {
                    MainTabControl.Items.Remove(tab);
                    UpdateEmptyOverlay();
                }
                break;

            case "Close Other Tabs":
                if (item.Tag is TabItem keepTab)
                {
                    var others = MainTabControl.Items.Cast<TabItem>().Where(t => t != keepTab).ToList();
                    foreach (var t in others)
                        MainTabControl.Items.Remove(t);
                    MainTabControl.SelectedItem = keepTab;
                    UpdateEmptyOverlay();
                }
                break;

            case "Close All Tabs":
                MainTabControl.Items.Clear();
                UpdateEmptyOverlay();
                break;
        }
    }

    private static string? GetTabFilePath(TabItem tab)
    {
        // Plans opened from file are wrapped in a DockPanel with the viewer as the last child
        if (tab.Content is DockPanel dp)
        {
            foreach (var child in dp.Children)
            {
                if (child is PlanViewerControl v)
                    return v.SourceFilePath;
            }
        }
        return null;
    }

    private void StartRename(StackPanel header, TextBlock headerText)
    {
        var textBox = new TextBox
        {
            Text = headerText.Text,
            FontSize = 12,
            MinWidth = 80,
            Padding = new Avalonia.Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Hide the text, show the textbox
        headerText.IsVisible = false;
        header.Children.Insert(0, textBox);
        textBox.Focus();
        textBox.SelectAll();

        void CommitRename()
        {
            var newName = textBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newName))
                headerText.Text = newName;

            headerText.IsVisible = true;
            header.Children.Remove(textBox);
        }

        textBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter || ke.Key == Key.Escape)
            {
                if (ke.Key == Key.Escape)
                    textBox.Text = headerText.Text; // revert
                CommitRename();
                ke.Handled = true;
            }
        };

        textBox.LostFocus += (_, _) => CommitRename();
    }

    /// <summary>
    /// Gets query text from a PlanViewerControl — uses QueryText if set,
    /// otherwise concatenates StatementText from all parsed statements.
    /// </summary>
    private static string GetQueryTextFromPlan(PlanViewerControl viewer)
    {
        if (!string.IsNullOrEmpty(viewer.QueryText))
            return viewer.QueryText;

        if (viewer.CurrentPlan == null)
            return "";

        var statements = viewer.CurrentPlan.Batches
            .SelectMany(b => b.Statements)
            .Select(s => s.StatementText)
            .Where(t => !string.IsNullOrEmpty(t));

        return string.Join(Environment.NewLine, statements);
    }

    /// <summary>
    /// Extracts the database name from plan XML's StmtSimple DatabaseContext attribute.
    /// </summary>
    private static string? ExtractDatabaseFromPlanXml(string? planXml)
    {
        if (string.IsNullOrEmpty(planXml)) return null;

        try
        {
            var doc = XDocument.Parse(planXml);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var stmt = doc.Descendants(ns + "StmtSimple").FirstOrDefault();
            var dbContext = stmt?.Attribute("DatabaseContext")?.Value;
            if (!string.IsNullOrEmpty(dbContext))
                return dbContext.Trim('[', ']');
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Prompts for a connection, then executes the query from the plan to get an actual plan.
    /// </summary>
    private async Task GetActualPlanFromFile(PlanViewerControl viewer)
    {
        var queryText = GetQueryTextFromPlan(viewer);
        if (string.IsNullOrEmpty(queryText))
        {
            ShowError("No query text available in this plan.");
            return;
        }

        // Show connection dialog
        var dialog = new Dialogs.ConnectionDialog(_credentialService, _connectionStore);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result != true || dialog.ResultConnection == null)
            return;

        var database = dialog.ResultDatabase ?? ExtractDatabaseFromPlanXml(viewer.RawXml);
        var connectionString = dialog.ResultConnection.GetConnectionString(_credentialService, database);
        var isAzure = dialog.ResultConnection.ServerName.Contains(".database.windows.net",
                          StringComparison.OrdinalIgnoreCase) ||
                      dialog.ResultConnection.ServerName.Contains(".database.azure.com",
                          StringComparison.OrdinalIgnoreCase);

        // Create a loading placeholder tab immediately
        var loadingPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 300
        };

        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            Margin = new Avalonia.Thickness(0, 0, 0, 12)
        };

        var statusText = new TextBlock
        {
            Text = "Executing query...",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#B0B6C0")),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var cancelBtn = new Button
        {
            Content = "\u25A0 Cancel",
            Height = 32,
            Width = 120,
            Padding = new Avalonia.Thickness(16, 0),
            FontSize = 13,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("AppButton")!
        };

        loadingPanel.Children.Add(progressBar);
        loadingPanel.Children.Add(statusText);
        loadingPanel.Children.Add(cancelBtn);

        var cts = new System.Threading.CancellationTokenSource();
        cancelBtn.Click += (_, _) => cts.Cancel();

        var loadingContainer = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Focusable = true,
            Children = { loadingPanel }
        };
        loadingContainer.KeyDown += (_, ke) =>
        {
            if (ke.Key == Avalonia.Input.Key.Escape) { cts.Cancel(); ke.Handled = true; }
        };

        var tab = CreateTab("Actual Plan", loadingContainer);
        MainTabControl.Items.Add(tab);
        MainTabControl.SelectedItem = tab;
        UpdateEmptyOverlay();
        loadingContainer.Focus();

        try
        {
            // Fetch server metadata for advice and Plan Insights
            ServerMetadata? metadata = null;
            try
            {
                metadata = await ServerMetadataService.FetchServerMetadataAsync(
                    connectionString, isAzure);
                metadata.Database = await ServerMetadataService.FetchDatabaseMetadataAsync(
                    connectionString, metadata.SupportsScopedConfigs);
            }
            catch { /* Non-fatal — advice will just lack server context */ }

            statusText.Text = "Capturing actual plan...";

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var actualPlanXml = await ActualPlanExecutor.ExecuteForActualPlanAsync(
                connectionString, database, queryText,
                viewer.RawXml, isolationLevel: null,
                isAzureSqlDb: isAzure, timeoutSeconds: 0, cts.Token);

            sw.Stop();

            if (string.IsNullOrEmpty(actualPlanXml))
            {
                statusText.Text = $"No actual plan returned ({sw.Elapsed.TotalSeconds:F1}s).";
                progressBar.IsVisible = false;
                return;
            }

            // Replace loading content with the actual plan
            var actualViewer = new PlanViewerControl();
            actualViewer.Metadata = metadata;
            actualViewer.LoadPlan(actualPlanXml, "Actual Plan", queryText);

            tab.Content = CreatePlanTabContent(actualViewer);
        }
        catch (Exception ex)
        {
            statusText.Text = $"Error: {ex.Message}";
            progressBar.IsVisible = false;
        }
    }

    // ── Recent Plans & Session Restore ────────────────────────────────────

    /// <summary>
    /// Adds a file path to the recent plans list, saves settings, and rebuilds the menu.
    /// </summary>
    private void TrackRecentPlan(string filePath)
    {
        AppSettingsService.AddRecentPlan(_appSettings, filePath);
        AppSettingsService.Save(_appSettings);
        RebuildRecentPlansMenu();
    }

    /// <summary>
    /// Rebuilds the Recent Plans submenu from the current settings.
    /// Shows a disabled "(empty)" item when the list is empty, plus a Clear Recent separator.
    /// </summary>
    private void RebuildRecentPlansMenu()
    {
        RecentPlansMenu.Items.Clear();

        if (_appSettings.RecentPlans.Count == 0)
        {
            var emptyItem = new MenuItem
            {
                Header = "(empty)",
                IsEnabled = false
            };
            RecentPlansMenu.Items.Add(emptyItem);
            return;
        }

        foreach (var path in _appSettings.RecentPlans)
        {
            var fileName = Path.GetFileName(path);
            var directory = Path.GetDirectoryName(path) ?? "";

            // Show "filename  —  directory" so the user can distinguish same-named files
            var displayText = string.IsNullOrEmpty(directory)
                ? fileName
                : $"{fileName}  —  {directory}";

            var item = new MenuItem
            {
                Header = displayText,
                Tag = path
            };

            item.Click += RecentPlanItem_Click;
            RecentPlansMenu.Items.Add(item);
        }

        RecentPlansMenu.Items.Add(new Separator());

        var clearItem = new MenuItem { Header = "Clear Recent Plans" };
        clearItem.Click += ClearRecentPlans_Click;
        RecentPlansMenu.Items.Add(clearItem);
    }

    private void RecentPlanItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string path)
            return;

        if (!File.Exists(path))
        {
            // File was moved or deleted — remove from the list and notify the user
            AppSettingsService.RemoveRecentPlan(_appSettings, path);
            AppSettingsService.Save(_appSettings);
            RebuildRecentPlansMenu();

            ShowError($"The file no longer exists and has been removed from recent plans:\n\n{path}");
            return;
        }

        LoadPlanFile(path);
    }

    private void ClearRecentPlans_Click(object? sender, RoutedEventArgs e)
    {
        _appSettings.RecentPlans.Clear();
        AppSettingsService.Save(_appSettings);
        RebuildRecentPlansMenu();
    }

    /// <summary>
    /// Saves the file paths of all currently open file-based plan tabs.
    /// </summary>
    private void SaveOpenPlans()
    {
        _appSettings.OpenPlans.Clear();

        foreach (var item in MainTabControl.Items)
        {
            if (item is not TabItem tab) continue;

            var path = GetTabFilePath(tab);
            if (!string.IsNullOrEmpty(path))
                _appSettings.OpenPlans.Add(path);
        }

        AppSettingsService.Save(_appSettings);
    }

    /// <summary>
    /// Restores plan tabs from the previous session. Skips files that no longer exist.
    /// Falls back to a new query tab if nothing was restored.
    /// </summary>
    private void RestoreOpenPlans()
    {
        var restored = false;

        foreach (var path in _appSettings.OpenPlans)
        {
            if (File.Exists(path))
            {
                LoadPlanFile(path);
                restored = true;
            }
        }

        // Clear the open plans list now that we've restored
        _appSettings.OpenPlans.Clear();
        AppSettingsService.Save(_appSettings);

        if (!restored)
        {
            // Nothing to restore — open a fresh query editor like before
            NewQuery_Click(this, new RoutedEventArgs());
        }
    }

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
}
