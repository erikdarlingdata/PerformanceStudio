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
    private bool HasQueryStoreTab()
    {
        return SubTabControl.Items.OfType<TabItem>()
            .Any(t => t.Content is QueryStoreGridControl);
    }

    public void TriggerQueryStore() => QueryStore_Click(null, new RoutedEventArgs());

    private async void QueryStoreOverview_Click(object? sender, RoutedEventArgs e)
    {
        if (_serverConnection == null || _connectionString == null)
        {
            await ShowConnectionDialogAsync();
            if (_serverConnection == null || _connectionString == null)
                return;
        }

        SetStatus("Loading Query Store Overview...");

        var supportsWaitStats = _serverMetadata?.SupportsQueryStoreWaitStats ?? false;
        var overview = new QueryStoreOverviewControl(_serverConnection, _credentialService,
            supportsWaitStats: supportsWaitStats);
        overview.DrillDownRequested += async (_, args) =>
        {
            // Open a single-database Query Store tab directly (no connection dialog)
            _selectedDatabase = args.Database;
            _connectionString = _serverConnection!.GetConnectionString(_credentialService, args.Database);
            await OpenQueryStoreForDatabaseAsync(args.Database, args.StartUtc, args.EndUtc);
        };

        var headerText = new TextBlock
        {
            Text = "QS Overview",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
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
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var header = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { headerText, closeBtn }
        };

        var tab = new TabItem { Header = header, Content = overview };
        closeBtn.Tag = tab;
        closeBtn.Click += (s, _) =>
        {
            if (s is Button btn && btn.Tag is TabItem t)
                SubTabControl.Items.Remove(t);
        };

        SubTabControl.Items.Add(tab);
        SubTabControl.SelectedItem = tab;

        try
        {
            await overview.LoadAsync();
            SetStatus("");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message, autoClear: false);
        }
    }

    private async Task OpenQueryStoreForDatabaseAsync(string database, DateTime? initialStartUtc = null, DateTime? initialEndUtc = null)
    {
        var connStr = _serverConnection!.GetConnectionString(_credentialService, database);

        // Check if Query Store is enabled
        SetStatus($"Checking Query Store on {database}...");
        try
        {
            var (enabled, state) = await QueryStoreService.CheckEnabledAsync(connStr);
            if (!enabled)
            {
                SetStatus($"Query Store not enabled on {database} ({state ?? "unknown"})");
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message, autoClear: false);
            return;
        }

        SetStatus("");

        // Check if wait stats are supported
        var supportsWaitStats = _serverMetadata?.SupportsQueryStoreWaitStats ?? false;
        if (supportsWaitStats)
        {
            try
            {
                supportsWaitStats = await QueryStoreService.IsWaitStatsCaptureEnabledAsync(connStr);
            }
            catch { supportsWaitStats = false; }
        }

        var databases = DatabaseBox.Items.OfType<string>().ToList();

        var grid = new QueryStoreGridControl(_serverConnection!, _credentialService,
            database, databases, supportsWaitStats);
        if (initialStartUtc.HasValue && initialEndUtc.HasValue)
            grid.SetInitialTimeRange(initialStartUtc.Value, initialEndUtc.Value);
        grid.PlansSelected += OnQueryStorePlansSelected;

        var headerText = new TextBlock
        {
            Text = $"Query Store — {database}",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 12
        };
        grid.DatabaseChanged += (_, db) => headerText.Text = $"Query Store — {db}";

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

        // Check if wait stats are supported (SQL 2017+ / Azure) and capture is enabled
        var supportsWaitStats = _serverMetadata?.SupportsQueryStoreWaitStats ?? false;
        if (supportsWaitStats)
        {
            try
            {
                var connStr = _serverConnection!.GetConnectionString(_credentialService, _selectedDatabase!);
                supportsWaitStats = await QueryStoreService.IsWaitStatsCaptureEnabledAsync(connStr);
            }
            catch
            {
                supportsWaitStats = false;
            }
        }

        // Build database list from the current DatabaseBox
        var databases = DatabaseBox.Items.OfType<string>().ToList();

        var grid = new QueryStoreGridControl(_serverConnection!, _credentialService,
            _selectedDatabase!, databases, supportsWaitStats);
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
}
