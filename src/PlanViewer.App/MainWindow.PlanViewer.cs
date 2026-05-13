using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PlanViewer.App.Controls;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.App;

public partial class MainWindow : Window
{
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
            ShowAdviceWindow("Advice for Humans", TextFormatter.Format(analysis), analysis, viewer);
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
        viewer.OpenInEditorRequested += (_, queryText) =>
        {
            _queryCounter++;
            var session = new QuerySessionControl(_credentialService, _connectionStore);
            session.QueryEditor.Text = queryText;
            var tab = CreateTab($"Query {_queryCounter}", session);
            MainTabControl.Items.Add(tab);
            MainTabControl.SelectedItem = tab;
            UpdateEmptyOverlay();
        };

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

    private void ShowAdviceWindow(string title, string content, AnalysisResult? analysis = null, PlanViewerControl? sourceViewer = null)
    {
        AdviceWindowHelper.Show(this, title, content, analysis, sourceViewer);
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
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
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
}
