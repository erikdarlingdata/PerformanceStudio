using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
        if (_serverConnection == null || _selectedDatabase == null)
        {
            SetStatus("Connect to a server first", autoClear: false);
            return;
        }

        // Always rebuild connection string from current database selection
        // to guarantee the picker state is reflected at execution time
        _connectionString = _serverConnection.GetConnectionString(_credentialService, _selectedDatabase);

        var queryText = queryTextOverride?.Trim()
                        ?? GetSelectedTextOrNull()?.Trim()
                        ?? QueryEditor.Text?.Trim();
        if (string.IsNullOrEmpty(queryText))
        {
            SetStatus("Enter a query", autoClear: false);
            return;
        }

        _executionCts?.Cancel();
        _executionCts?.Dispose();
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
            viewer.ConnectionString = _connectionString;
            viewer.SetConnectionServices(_credentialService, _connectionStore);
            if (_serverConnection != null)
                viewer.SetConnectionStatus(_serverConnection.ServerName, _selectedDatabase);
            viewer.OpenInEditorRequested += OnOpenInEditorRequested;
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
        _executionCts?.Dispose();
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
            actualViewer.ConnectionString = _connectionString;
            actualViewer.SetConnectionServices(_credentialService, _connectionStore);
            if (_serverConnection != null)
                actualViewer.SetConnectionStatus(_serverConnection.ServerName, _selectedDatabase);
            actualViewer.OpenInEditorRequested += OnOpenInEditorRequested;
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
