using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.TextMate;
using Microsoft.Data.SqlClient;
using PlanViewer.App.Dialogs;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;
using TextMateSharp.Grammars;

namespace PlanViewer.App.Controls;

public partial class QuerySessionControl : UserControl
{
    private readonly ICredentialService _credentialService;
    private readonly ConnectionStore _connectionStore;

    private ServerConnection? _serverConnection;
    private string? _connectionString;
    private string? _selectedDatabase;
    private int _planCounter;
    private CancellationTokenSource? _executionCts;
    private ServerMetadata? _serverMetadata;

    // TextMate installation for syntax highlighting
    private TextMate.Installation? _textMateInstallation;
    private CancellationTokenSource? _statusClearCts;
    private CompletionWindow? _completionWindow;

    public QuerySessionControl(ICredentialService credentialService, ConnectionStore connectionStore)
    {
        _credentialService = credentialService;
        _connectionStore = connectionStore;
        InitializeComponent();

        // Initialize editor with empty text so the document is ready
        QueryEditor.Text = "";
        ZoomBox.SelectedIndex = 2; // 100%

        SetupSyntaxHighlighting();
        SetupEditorContextMenu();

        // Keybindings: F5/Ctrl+E for Execute, Ctrl+L for Estimated Plan
        KeyDown += OnKeyDown;

        // Ctrl+mousewheel for font zoom — use Tunnel so it fires before ScrollViewer consumes scroll-down
        QueryEditor.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnEditorPointerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Code completion
        QueryEditor.TextArea.TextEntering += OnTextEntering;
        QueryEditor.TextArea.TextEntered += OnTextEntered;

        // Focus the editor when the control is attached to the visual tree
        AttachedToVisualTree += (_, _) =>
        {
            QueryEditor.Focus();
            QueryEditor.TextArea.Focus();
        };

        // Focus the editor when the Editor tab is selected; toggle plan-dependent buttons
        SubTabControl.SelectionChanged += (_, _) =>
        {
            if (SubTabControl.SelectedIndex == 0)
            {
                QueryEditor.Focus();
                QueryEditor.TextArea.Focus();
            }
            UpdatePlanTabButtonState();
        };
    }

    private void SetupSyntaxHighlighting()
    {
        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _textMateInstallation = QueryEditor.InstallTextMate(registryOptions);
        _textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId("sql"));
    }

    private void SetupEditorContextMenu()
    {
        var cutItem = new MenuItem { Header = "Cut" };
        cutItem.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var selection = QueryEditor.TextArea.Selection;
            if (selection.IsEmpty) return;
            var text = selection.GetText();
            await clipboard.SetTextAsync(text);
            selection.ReplaceSelectionWithText("");
        };

        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var selection = QueryEditor.TextArea.Selection;
            if (selection.IsEmpty) return;
            await clipboard.SetTextAsync(selection.GetText());
        };

        var pasteItem = new MenuItem { Header = "Paste" };
        pasteItem.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            QueryEditor.TextArea.PerformTextInput(text);
        };

        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Click += (_, _) =>
        {
            QueryEditor.SelectAll();
        };

        var executeFromCursorItem = new MenuItem { Header = "Execute from Cursor" };
        executeFromCursorItem.Click += async (_, _) =>
        {
            var text = GetTextFromCursor();
            if (!string.IsNullOrWhiteSpace(text))
                await CaptureAndShowPlan(estimated: false, queryTextOverride: text);
        };

        var executeCurrentBatchItem = new MenuItem { Header = "Execute Current Batch" };
        executeCurrentBatchItem.Click += async (_, _) =>
        {
            var text = GetCurrentBatch();
            if (!string.IsNullOrWhiteSpace(text))
                await CaptureAndShowPlan(estimated: false, queryTextOverride: text);
        };

        QueryEditor.TextArea.ContextMenu = new ContextMenu
        {
            Items = { cutItem, copyItem, pasteItem, new Separator(), selectAllItem, new Separator(), executeFromCursorItem, executeCurrentBatchItem }
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // F5 or Ctrl+E → Execute (actual plan)
        if ((e.Key == Key.F5 || (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control))
            && ExecuteButton.IsEnabled)
        {
            Execute_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        // Ctrl+L → Estimated plan
        else if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control
                 && ExecuteEstButton.IsEnabled)
        {
            ExecuteEstimated_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        // Escape → Cancel running query
        else if (e.Key == Key.Escape && _executionCts != null && !_executionCts.IsCancellationRequested)
        {
            _executionCts.Cancel();
            e.Handled = true;
        }
    }

    private void OnEditorPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;

        var delta = e.Delta.Y > 0 ? 1 : -1;
        var newSize = QueryEditor.FontSize + delta;
        QueryEditor.FontSize = Math.Clamp(newSize, 7, 52);
        SyncZoomDropdown();
        e.Handled = true;
    }

    private void Zoom_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is ComboBoxItem item && item.Tag is string tagStr
            && int.TryParse(tagStr, out var size))
        {
            QueryEditor.FontSize = size;
        }
    }

    private void SyncZoomDropdown()
    {
        // Find the closest matching zoom level
        var fontSize = (int)Math.Round(QueryEditor.FontSize);
        int bestIdx = 2; // default 100%
        int bestDist = int.MaxValue;

        for (int i = 0; i < ZoomBox.Items.Count; i++)
        {
            if (ZoomBox.Items[i] is ComboBoxItem item && item.Tag is string tagStr
                && int.TryParse(tagStr, out var size))
            {
                var dist = Math.Abs(size - fontSize);
                if (dist < bestDist) { bestDist = dist; bestIdx = i; }
            }
        }

        ZoomBox.SelectionChanged -= Zoom_SelectionChanged;
        ZoomBox.SelectedIndex = bestIdx;
        ZoomBox.SelectionChanged += Zoom_SelectionChanged;
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow == null || string.IsNullOrEmpty(e.Text)) return;

        // If the user types a non-identifier character, let the completion window
        // decide whether to commit (it handles Tab/Enter/Space automatically)
        var ch = e.Text[0];
        if (!char.IsLetterOrDigit(ch) && ch != '_')
        {
            _completionWindow.CompletionList.RequestInsertion(e);
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow != null) return;
        if (string.IsNullOrEmpty(e.Text) || !char.IsLetter(e.Text[0])) return;

        var (prefix, wordStart) = GetWordBeforeCaret();
        if (prefix.Length < 2) return;

        var matches = SqlKeywords.All
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0) return;

        _completionWindow = new CompletionWindow(QueryEditor.TextArea);
        _completionWindow.StartOffset = wordStart;
        _completionWindow.Closed += (_, _) => _completionWindow = null;

        foreach (var kw in matches)
            _completionWindow.CompletionList.CompletionData.Add(new SqlCompletionData(kw));

        _completionWindow.Show();
    }

    private (string prefix, int startOffset) GetWordBeforeCaret()
    {
        var doc = QueryEditor.Document;
        var offset = QueryEditor.CaretOffset;
        var start = offset;

        while (start > 0)
        {
            var ch = doc.GetCharAt(start - 1);
            if (char.IsLetterOrDigit(ch) || ch == '_')
                start--;
            else
                break;
        }

        return (doc.GetText(start, offset - start), start);
    }

    private string? GetSelectedTextOrNull()
    {
        var selection = QueryEditor.TextArea.Selection;
        if (selection.IsEmpty) return null;
        return selection.GetText();
    }

    private string GetTextFromCursor()
    {
        var doc = QueryEditor.Document;
        var offset = QueryEditor.CaretOffset;
        return doc.GetText(offset, doc.TextLength - offset);
    }

    private string? GetCurrentBatch()
    {
        var doc = QueryEditor.Document;
        var caretOffset = QueryEditor.CaretOffset;
        var text = doc.Text;
        var goPattern = new Regex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var matches = goPattern.Matches(text);

        int batchStart = 0;
        int batchEnd = text.Length;

        foreach (Match m in matches)
        {
            if (m.Index + m.Length <= caretOffset)
            {
                batchStart = m.Index + m.Length;
            }
            else if (m.Index >= caretOffset)
            {
                batchEnd = m.Index;
                break;
            }
        }

        return text[batchStart..batchEnd].Trim();
    }

    private void SetStatus(string text, bool autoClear = true)
    {
        _statusClearCts?.Cancel();
        StatusText.Text = text;

        if (autoClear && !string.IsNullOrEmpty(text))
        {
            _statusClearCts = new CancellationTokenSource();
            var token = _statusClearCts.Token;
            _ = Task.Delay(3000, token).ContinueWith(_ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText.Text = "");
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog(_credentialService, _connectionStore);
        var result = await dialog.ShowDialog<bool?>(GetParentWindow());

        if (result == true && dialog.ResultConnection != null)
        {
            _serverConnection = dialog.ResultConnection;
            _selectedDatabase = dialog.ResultDatabase;
            _connectionString = _serverConnection.GetConnectionString(_credentialService, _selectedDatabase);

            ServerLabel.Text = _serverConnection.ServerName;
            ServerLabel.Foreground = Brushes.LimeGreen;
            ConnectButton.Content = "Reconnect";

            // Populate database dropdown
            await PopulateDatabases();

            // Collect server metadata for advice output
            await FetchServerMetadataAsync();

            // Select the database chosen in the dialog
            if (_selectedDatabase != null)
            {
                for (int i = 0; i < DatabaseBox.Items.Count; i++)
                {
                    if (DatabaseBox.Items[i]?.ToString() == _selectedDatabase)
                    {
                        DatabaseBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            // Collect database metadata for the initial database
            await FetchDatabaseMetadataAsync();

            ExecuteButton.IsEnabled = true;
            ExecuteEstButton.IsEnabled = true;
        }
    }

    private async Task ShowConnectionDialogAsync()
    {
        var dialog = new ConnectionDialog(_credentialService, _connectionStore);
        var result = await dialog.ShowDialog<bool?>(GetParentWindow());

        if (result == true && dialog.ResultConnection != null)
        {
            _serverConnection = dialog.ResultConnection;
            _selectedDatabase = dialog.ResultDatabase;
            _connectionString = _serverConnection.GetConnectionString(_credentialService, _selectedDatabase);

            ServerLabel.Text = _serverConnection.ServerName;
            ServerLabel.Foreground = Brushes.LimeGreen;
            ConnectButton.Content = "Reconnect";

            await PopulateDatabases();
            await FetchServerMetadataAsync();

            if (_selectedDatabase != null)
            {
                for (int i = 0; i < DatabaseBox.Items.Count; i++)
                {
                    if (DatabaseBox.Items[i]?.ToString() == _selectedDatabase)
                    {
                        DatabaseBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            await FetchDatabaseMetadataAsync();

            ExecuteButton.IsEnabled = true;
            ExecuteEstButton.IsEnabled = true;
        }
    }

    private async Task PopulateDatabases()
    {
        if (_serverConnection == null) return;

        try
        {
            var connStr = _serverConnection.GetConnectionString(_credentialService, "master");
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            var databases = new List<string>();
            using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databases.Add(reader.GetString(0));

            DatabaseBox.ItemsSource = databases;
            DatabaseBox.IsEnabled = true;
        }
        catch
        {
            DatabaseBox.IsEnabled = false;
        }
    }

    private async void Database_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_serverConnection == null || DatabaseBox.SelectedItem == null) return;

        _selectedDatabase = DatabaseBox.SelectedItem.ToString();
        _connectionString = _serverConnection.GetConnectionString(_credentialService, _selectedDatabase);

        // Refresh database metadata for the new context
        await FetchDatabaseMetadataAsync();
    }

    private bool IsAzureConnection =>
        _serverConnection != null &&
        (_serverConnection.ServerName.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
         _serverConnection.ServerName.Contains(".database.azure.com", StringComparison.OrdinalIgnoreCase));

    private async Task FetchServerMetadataAsync()
    {
        if (_connectionString == null) return;
        try
        {
            _serverMetadata = await ServerMetadataService.FetchServerMetadataAsync(
                _connectionString, IsAzureConnection);
        }
        catch
        {
            // Non-fatal — advice will just lack server context
            _serverMetadata = null;
        }
    }

    private async Task FetchDatabaseMetadataAsync()
    {
        if (_connectionString == null || _serverMetadata == null) return;
        try
        {
            _serverMetadata.Database = await ServerMetadataService.FetchDatabaseMetadataAsync(
                _connectionString, _serverMetadata.SupportsScopedConfigs);
        }
        catch
        {
            // Non-fatal — advice will just lack database context
        }
    }

    private async void Execute_Click(object? sender, RoutedEventArgs e)
    {
        await CaptureAndShowPlan(estimated: false);
    }

    private async void ExecuteEstimated_Click(object? sender, RoutedEventArgs e)
    {
        await CaptureAndShowPlan(estimated: true);
    }

    private async Task CaptureAndShowPlan(bool estimated, string? queryTextOverride = null)
    {
        if (_connectionString == null || _selectedDatabase == null)
        {
            SetStatus("Connect to a server first", autoClear: false);
            return;
        }

        var queryText = queryTextOverride?.Trim()
                        ?? GetSelectedTextOrNull()?.Trim()
                        ?? QueryEditor.Text?.Trim();
        if (string.IsNullOrEmpty(queryText))
        {
            SetStatus("Enter a query", autoClear: false);
            return;
        }

        _executionCts?.Cancel();
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;

        var planType = estimated ? "Estimated" : "Actual";

        // Create loading tab with cancel button
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

        var statusLabel = new TextBlock
        {
            Text = $"Capturing {planType.ToLower()} plan...",
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
        cancelBtn.Click += (_, _) => _executionCts?.Cancel();

        loadingPanel.Children.Add(progressBar);
        loadingPanel.Children.Add(statusLabel);
        loadingPanel.Children.Add(cancelBtn);

        var loadingContainer = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Focusable = true,
            Children = { loadingPanel }
        };
        loadingContainer.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape) { _executionCts?.Cancel(); ke.Handled = true; }
        };

        // Add loading tab and switch to it
        _planCounter++;
        var tabLabel = estimated ? $"Est Plan {_planCounter}" : $"Plan {_planCounter}";
        var headerText = new TextBlock
        {
            Text = tabLabel,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        var closeBtn = new Button
        {
            Content = "\u2715",
            MinWidth = 22, MinHeight = 22, Width = 22, Height = 22,
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
        var loadingTab = new TabItem { Header = header, Content = loadingContainer };
        closeBtn.Tag = loadingTab;
        closeBtn.Click += ClosePlanTab_Click;

        SubTabControl.Items.Add(loadingTab);
        SubTabControl.SelectedItem = loadingTab;
        loadingContainer.Focus();

        try
        {
            var sw = Stopwatch.StartNew();
            string? planXml;

            var isAzure = _serverConnection!.ServerName.Contains(".database.windows.net",
                              StringComparison.OrdinalIgnoreCase) ||
                          _serverConnection.ServerName.Contains(".database.azure.com",
                              StringComparison.OrdinalIgnoreCase);

            if (estimated)
            {
                planXml = await EstimatedPlanExecutor.GetEstimatedPlanAsync(
                    _connectionString, _selectedDatabase, queryText, timeoutSeconds: 0, ct);
            }
            else
            {
                planXml = await ActualPlanExecutor.ExecuteForActualPlanAsync(
                    _connectionString, _selectedDatabase, queryText,
                    planXml: null, isolationLevel: null,
                    isAzureSqlDb: isAzure, timeoutSeconds: 0, ct);
            }

            sw.Stop();

            if (string.IsNullOrEmpty(planXml))
            {
                statusLabel.Text = $"No plan returned ({sw.Elapsed.TotalSeconds:F1}s)";
                progressBar.IsVisible = false;
                cancelBtn.IsVisible = false;
                return;
            }

            // Replace loading content with the plan viewer
            SetStatus($"{planType} plan captured ({sw.Elapsed.TotalSeconds:F1}s)");
            var viewer = new PlanViewerControl();
            viewer.Metadata = _serverMetadata;
            viewer.LoadPlan(planXml, tabLabel, queryText);
            loadingTab.Content = viewer;
            HumanAdviceButton.IsEnabled = true;
            RobotAdviceButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled");
            SubTabControl.Items.Remove(loadingTab);
        }
        catch (SqlException ex)
        {
            statusLabel.Text = ex.Message.Length > 100 ? ex.Message[..100] + "..." : ex.Message;
            progressBar.IsVisible = false;
            cancelBtn.IsVisible = false;
        }
        catch (Exception ex)
        {
            statusLabel.Text = ex.Message.Length > 100 ? ex.Message[..100] + "..." : ex.Message;
            progressBar.IsVisible = false;
            cancelBtn.IsVisible = false;
        }
    }

    private AnalysisResult? GetCurrentAnalysis()
    {
        // Find the currently selected plan tab's PlanViewerControl
        if (SubTabControl.SelectedItem is TabItem tab && tab.Content is PlanViewerControl viewer
            && viewer.CurrentPlan != null)
        {
            return ResultMapper.Map(viewer.CurrentPlan, "query editor", _serverMetadata);
        }

        // Fallback: find the most recent plan tab
        for (int i = SubTabControl.Items.Count - 1; i >= 0; i--)
        {
            if (SubTabControl.Items[i] is TabItem planTab && planTab.Content is PlanViewerControl v
                && v.CurrentPlan != null)
            {
                return ResultMapper.Map(v.CurrentPlan, "query editor");
            }
        }

        return null;
    }

    private void HumanAdvice_Click(object? sender, RoutedEventArgs e)
    {
        var analysis = GetCurrentAnalysis();
        if (analysis == null) { SetStatus("No plan to analyze", autoClear: false); return; }

        var text = TextFormatter.Format(analysis);
        ShowAdviceWindow("Advice for Humans", text, analysis);
    }

    private void RobotAdvice_Click(object? sender, RoutedEventArgs e)
    {
        var analysis = GetCurrentAnalysis();
        if (analysis == null) { SetStatus("No plan to analyze", autoClear: false); return; }

        var json = JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
        ShowAdviceWindow("Advice for Robots", json);
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
            Icon = GetParentWindow().Icon,
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

        window.Show(GetParentWindow());
    }

    private void AddPlanTab(string planXml, string queryText, bool estimated, string? labelOverride = null)
    {
        _planCounter++;
        var label = labelOverride ?? (estimated ? $"Est Plan {_planCounter}" : $"Plan {_planCounter}");

        var viewer = new PlanViewerControl();
        viewer.Metadata = _serverMetadata;
        viewer.LoadPlan(planXml, label, queryText);

        // Build tab header with close button and right-click rename
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

        var tab = new TabItem { Header = header, Content = viewer };
        closeBtn.Tag = tab;
        closeBtn.Click += ClosePlanTab_Click;

        // Right-click context menu
        var contextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Rename Tab", Tag = new object[] { header, headerText } },
                new Separator(),
                new MenuItem { Header = "Close", Tag = tab, InputGesture = new KeyGesture(Key.W, KeyModifiers.Control) },
                new MenuItem { Header = "Close Other Tabs", Tag = tab },
                new MenuItem { Header = "Close All Tabs" }
            }
        };

        foreach (var item in contextMenu.Items.OfType<MenuItem>())
            item.Click += PlanTabContextMenu_Click;

        header.ContextMenu = contextMenu;

        SubTabControl.Items.Add(tab);
        SubTabControl.SelectedItem = tab;
        UpdateCompareButtonState();
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
                    textBox.Text = headerText.Text;
                CommitRename();
                ke.Handled = true;
            }
        };

        textBox.LostFocus += (_, _) => CommitRename();
    }

    private void ClosePlanTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TabItem tab)
        {
            SubTabControl.Items.Remove(tab);
            UpdateCompareButtonState();
        }
    }

    private void PlanTabContextMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;

        switch (item.Header?.ToString())
        {
            case "Rename Tab":
                if (item.Tag is object[] parts)
                    StartRename((StackPanel)parts[0], (TextBlock)parts[1]);
                break;

            case "Close":
                if (item.Tag is TabItem tab)
                {
                    SubTabControl.Items.Remove(tab);
                    UpdateCompareButtonState();
                }
                break;

            case "Close Other Tabs":
                if (item.Tag is TabItem keepTab)
                {
                    // Keep the Editor tab (index 0) and the selected tab
                    var others = SubTabControl.Items.Cast<object>()
                        .OfType<TabItem>()
                        .Where(t => t != keepTab && t.Content is PlanViewerControl)
                        .ToList();
                    foreach (var t in others)
                        SubTabControl.Items.Remove(t);
                    SubTabControl.SelectedItem = keepTab;
                    UpdateCompareButtonState();
                }
                break;

            case "Close All Tabs":
                var planTabs = SubTabControl.Items.Cast<object>()
                    .OfType<TabItem>()
                    .Where(t => t.Content is PlanViewerControl)
                    .ToList();
                foreach (var t in planTabs)
                    SubTabControl.Items.Remove(t);
                SubTabControl.SelectedIndex = 0; // back to Editor
                UpdateCompareButtonState();
                break;
        }
    }

    private void UpdateCompareButtonState()
    {
        int planCount = 0;
        foreach (var item in SubTabControl.Items)
        {
            if (item is TabItem t && t.Content is PlanViewerControl v && v.CurrentPlan != null)
                planCount++;
        }
        ComparePlansButton.IsEnabled = planCount >= 2;
    }

    public IEnumerable<(string label, PlanViewerControl viewer)> GetPlanTabs()
    {
        foreach (var item in SubTabControl.Items)
        {
            if (item is TabItem tab && tab.Content is PlanViewerControl viewer
                && viewer.CurrentPlan != null)
            {
                yield return (GetTabLabel(tab), viewer);
            }
        }
    }

    private static string GetTabLabel(TabItem tab)
    {
        if (tab.Header is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
            return tb.Text ?? "Plan";
        if (tab.Header is string s)
            return s;
        return "Plan";
    }

    private bool HasQueryStoreTab()
    {
        return SubTabControl.Items.OfType<TabItem>()
            .Any(t => t.Content is QueryStoreGridControl);
    }

    private async void QueryStore_Click(object? sender, RoutedEventArgs e)
    {
        // If a QS tab already exists, always show connection dialog for a fresh tab
        if (HasQueryStoreTab() || _connectionString == null || _selectedDatabase == null)
        {
            await ShowConnectionDialogAsync();
            if (_connectionString == null || _selectedDatabase == null)
                return;
        }

        // Check if Query Store is enabled
        SetStatus("Checking Query Store...");
        try
        {
            var (enabled, state) = await QueryStoreService.CheckEnabledAsync(_connectionString);
            if (!enabled)
            {
                SetStatus($"Query Store not enabled ({state ?? "unknown"})");
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message, autoClear: false);
            return;
        }

        SetStatus("");

        // Build database list from the current DatabaseBox
        var databases = DatabaseBox.Items.OfType<string>().ToList();

        var grid = new QueryStoreGridControl(_serverConnection!, _credentialService,
            _selectedDatabase!, databases);
        grid.PlansSelected += OnQueryStorePlansSelected;

        var headerText = new TextBlock
        {
            Text = $"Query Store — {_selectedDatabase}",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 12
        };

        // Update tab header when database is changed via the grid's picker
        grid.DatabaseChanged += (_, db) =>
        {
            headerText.Text = $"Query Store — {db}";
        };

        var closeBtn = new Button
        {
            Content = "\u2715",
            MinWidth = 22, MinHeight = 22, Width = 22, Height = 22,
            Padding = new Avalonia.Thickness(0),
            FontSize = 11,
            Margin = new Avalonia.Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var header = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { headerText, closeBtn }
        };

        var tab = new TabItem { Header = header, Content = grid };
        closeBtn.Tag = tab;
        closeBtn.Click += (s, _) =>
        {
            if (s is Button btn && btn.Tag is TabItem t)
                SubTabControl.Items.Remove(t);
        };

        SubTabControl.Items.Add(tab);
        SubTabControl.SelectedItem = tab;
    }

    private void OnQueryStorePlansSelected(object? sender, List<QueryStorePlan> plans)
    {
        foreach (var qsPlan in plans)
        {
            var tabLabel = $"QS {qsPlan.QueryId} / {qsPlan.PlanId}";
            AddPlanTab(qsPlan.PlanXml, qsPlan.QueryText, estimated: true, labelOverride: tabLabel);
        }

        SetStatus($"{plans.Count} Query Store plans loaded");
        HumanAdviceButton.IsEnabled = true;
        RobotAdviceButton.IsEnabled = true;
    }

    private void ComparePlans_Click(object? sender, RoutedEventArgs e)
    {
        var planTabs = GetPlanTabs().ToList();
        if (planTabs.Count < 2)
        {
            SetStatus("Need at least 2 plan tabs to compare");
            return;
        }

        ShowComparePickerDialog(planTabs);
    }

    private void ShowComparePickerDialog(List<(string label, PlanViewerControl viewer)> planTabs)
    {
        var items = planTabs.Select(t => t.label).ToList();

        var comboA = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = 0,
            Width = 200,
            Height = 28,
            FontSize = 12,
            Margin = new Avalonia.Thickness(8, 0, 0, 0)
        };

        var comboB = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = items.Count > 1 ? 1 : 0,
            Width = 200,
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
            Width = 380,
            Height = 220,
            MinWidth = 380,
            MinHeight = 220,
            Icon = GetParentWindow().Icon,
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

            var analysisA = ResultMapper.Map(viewerA.CurrentPlan!, "query editor", _serverMetadata);
            var analysisB = ResultMapper.Map(viewerB.CurrentPlan!, "query editor", _serverMetadata);

            var comparison = ComparisonFormatter.Compare(analysisA, analysisB, labelA, labelB);
            dialog.Close();
            ShowAdviceWindow("Plan Comparison", comparison);
        };

        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.ShowDialog(GetParentWindow());
    }

    /// <summary>
    /// Gets the PlanViewerControl for the currently selected plan tab, or null if
    /// the Editor tab or no plan tab is selected.
    /// </summary>
    private PlanViewerControl? GetSelectedPlanViewer()
    {
        if (SubTabControl.SelectedItem is TabItem tab && tab.Content is PlanViewerControl viewer
            && viewer.CurrentPlan != null)
        {
            return viewer;
        }
        return null;
    }

    /// <summary>
    /// Enables or disables buttons that require a plan tab to be selected.
    /// Called when the SubTabControl selection changes and after plan tabs are added/removed.
    /// </summary>
    private void UpdatePlanTabButtonState()
    {
        var hasPlanTab = GetSelectedPlanViewer() != null;
        var hasConnection = _connectionString != null && _selectedDatabase != null;

        CopyReproButton.IsEnabled = hasPlanTab;
        GetActualPlanButton.IsEnabled = hasPlanTab && hasConnection;

        // Advice buttons also depend on a plan being selected
        HumanAdviceButton.IsEnabled = hasPlanTab;
        RobotAdviceButton.IsEnabled = hasPlanTab;
    }

    private async void CopyRepro_Click(object? sender, RoutedEventArgs e)
    {
        var viewer = GetSelectedPlanViewer();
        if (viewer == null)
        {
            SetStatus("Select a plan tab first");
            return;
        }

        var planXml = viewer.RawXml;
        var queryText = viewer.QueryText ?? "";

        if (string.IsNullOrEmpty(queryText) && string.IsNullOrEmpty(planXml))
        {
            SetStatus("No query or plan data available");
            return;
        }

        /* Extract database name from plan XML StmtSimple/@DatabaseContext if available,
           otherwise fall back to the currently selected database */
        var database = ExtractDatabaseFromPlanXml(planXml) ?? _selectedDatabase;

        var reproScript = ReproScriptBuilder.BuildReproScript(
            queryText,
            database,
            planXml,
            isolationLevel: null,
            source: "Performance Studio",
            isAzureSqlDb: IsAzureConnection);

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(reproScript);
                SetStatus("Repro script copied to clipboard");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard error: {ex.Message}");
        }
    }

    private async void GetActualPlan_Click(object? sender, RoutedEventArgs e)
    {
        var viewer = GetSelectedPlanViewer();
        if (viewer == null)
        {
            SetStatus("Select a plan tab first");
            return;
        }

        if (_connectionString == null || _selectedDatabase == null)
        {
            SetStatus("Connect to a server first", autoClear: false);
            return;
        }

        var queryText = viewer.QueryText ?? "";
        var planXml = viewer.RawXml;

        if (string.IsNullOrEmpty(queryText))
        {
            SetStatus("No query text available for this plan");
            return;
        }

        /* Show confirmation dialog */
        var confirmed = await ShowConfirmationDialog(
            "Get Actual Plan",
            "The query will execute with SET STATISTICS XML ON to capture the actual plan.\n\nAll data results will be discarded.\n\nContinue?");

        if (!confirmed) return;

        _executionCts?.Cancel();
        _executionCts = new CancellationTokenSource();
        var ct = _executionCts.Token;

        // Create loading tab with cancel button
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

        var statusLabel = new TextBlock
        {
            Text = "Capturing actual plan...",
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
        cancelBtn.Click += (_, _) => _executionCts?.Cancel();

        loadingPanel.Children.Add(progressBar);
        loadingPanel.Children.Add(statusLabel);
        loadingPanel.Children.Add(cancelBtn);

        var loadingContainer = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Focusable = true,
            Children = { loadingPanel }
        };
        loadingContainer.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape) { _executionCts?.Cancel(); ke.Handled = true; }
        };

        _planCounter++;
        var tabLabel = $"Plan {_planCounter}";
        var headerText = new TextBlock
        {
            Text = tabLabel,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        var closeBtn = new Button
        {
            Content = "\u2715",
            MinWidth = 22, MinHeight = 22, Width = 22, Height = 22,
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
        var loadingTab = new TabItem { Header = header, Content = loadingContainer };
        closeBtn.Tag = loadingTab;
        closeBtn.Click += ClosePlanTab_Click;

        SubTabControl.Items.Add(loadingTab);
        SubTabControl.SelectedItem = loadingTab;
        loadingContainer.Focus();

        try
        {
            var sw = Stopwatch.StartNew();
            var isAzure = IsAzureConnection;

            var actualPlanXml = await ActualPlanExecutor.ExecuteForActualPlanAsync(
                _connectionString, _selectedDatabase, queryText,
                planXml, isolationLevel: null,
                isAzureSqlDb: isAzure, timeoutSeconds: 0, ct);

            sw.Stop();

            if (string.IsNullOrEmpty(actualPlanXml))
            {
                statusLabel.Text = $"No actual plan returned ({sw.Elapsed.TotalSeconds:F1}s)";
                progressBar.IsVisible = false;
                cancelBtn.IsVisible = false;
                return;
            }

            SetStatus($"Actual plan captured ({sw.Elapsed.TotalSeconds:F1}s)");
            var actualViewer = new PlanViewerControl();
            actualViewer.Metadata = _serverMetadata;
            actualViewer.LoadPlan(actualPlanXml, tabLabel, queryText);
            loadingTab.Content = actualViewer;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled");
            SubTabControl.Items.Remove(loadingTab);
        }
        catch (SqlException ex)
        {
            statusLabel.Text = ex.Message.Length > 100 ? ex.Message[..100] + "..." : ex.Message;
            progressBar.IsVisible = false;
            cancelBtn.IsVisible = false;
        }
        catch (Exception ex)
        {
            statusLabel.Text = ex.Message.Length > 100 ? ex.Message[..100] + "..." : ex.Message;
            progressBar.IsVisible = false;
            cancelBtn.IsVisible = false;
        }
        finally
        {
            UpdatePlanTabButtonState();
        }
    }

    /// <summary>
    /// Shows a modal confirmation dialog and returns true if the user clicked OK.
    /// </summary>
    private async Task<bool> ShowConfirmationDialog(string title, string message)
    {
        var result = false;

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var okBtn = new Button
        {
            Content = "OK",
            Height = 32,
            Width = 80,
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
            Width = 80,
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
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);

        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children = { messageText, buttonPanel }
        };

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 200,
            MinWidth = 420,
            MinHeight = 200,
            Icon = GetParentWindow().Icon,
            Background = new SolidColorBrush(Color.Parse("#1A1D23")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        okBtn.Click += (_, _) => { result = true; dialog.Close(); };
        cancelBtn.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(GetParentWindow());
        return result;
    }

    /// <summary>
    /// Extracts the database name from plan XML's StmtSimple DatabaseContext attribute.
    /// Returns null if not found.
    /// </summary>
    private static string? ExtractDatabaseFromPlanXml(string? planXml)
    {
        if (string.IsNullOrEmpty(planXml)) return null;

        try
        {
            var doc = XDocument.Parse(planXml);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

            /* Try StmtSimple first — most queries have this */
            var stmt = doc.Descendants(ns + "StmtSimple").FirstOrDefault();
            var dbContext = stmt?.Attribute("DatabaseContext")?.Value;

            if (!string.IsNullOrEmpty(dbContext))
            {
                /* DatabaseContext is typically "[dbname]" — strip brackets */
                return dbContext.Trim('[', ']');
            }
        }
        catch
        {
            /* XML parse failure — fall through to null */
        }

        return null;
    }

    private Window GetParentWindow()
    {
        var parent = this.VisualRoot;
        return parent as Window ?? throw new InvalidOperationException("No parent window");
    }
}
