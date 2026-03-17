using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Dialogs;

public partial class QueryStoreDialog : Window
{
    private readonly string _connectionString;
    private readonly string _database;
    private List<QueryStorePlan>? _plans;
    private readonly List<CheckBox> _rowCheckBoxes = new();
    private CancellationTokenSource? _fetchCts;

    public List<QueryStorePlan> SelectedPlans { get; } = new();

    public QueryStoreDialog(string connectionString, string database)
    {
        _connectionString = connectionString;
        _database = database;
        InitializeComponent();
        Title = $"Query Store — {database}";
    }

    private async void Fetch_Click(object? sender, RoutedEventArgs e)
    {
        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        var topN = (int)(TopNBox.Value ?? 10);
        var hoursBack = (int)(HoursBackBox.Value ?? 24);
        var orderBy = "cpu";
        if (OrderByBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            orderBy = tag;

        FetchButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        StatusText.Text = "Fetching...";
        ResultsPanel.Children.Clear();
        _rowCheckBoxes.Clear();
        HeaderRow.IsVisible = false;

        try
        {
            _plans = await QueryStoreService.FetchTopPlansAsync(
                _connectionString, topN, orderBy, hoursBack, filter: null, ct);

            if (_plans.Count == 0)
            {
                StatusText.Text = "No Query Store data found.";
                return;
            }

            StatusText.Text = $"{_plans.Count} plans";

            // Update metric header based on order-by
            MetricHeader.Text = GetMetricHeader(orderBy);
            HeaderRow.IsVisible = true;

            for (int i = 0; i < _plans.Count; i++)
            {
                var plan = _plans[i];
                var row = BuildResultRow(plan, orderBy, i);
                ResultsPanel.Children.Add(row);
            }

            SelectAllBox.IsChecked = true;
            LoadButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message;
        }
        finally
        {
            FetchButton.IsEnabled = true;
        }
    }

    private Border BuildResultRow(QueryStorePlan plan, string orderBy, int index)
    {
        var cb = new CheckBox { IsChecked = true, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        _rowCheckBoxes.Add(cb);

        var queryPreview = plan.QueryText.Length > 80
            ? plan.QueryText[..80].Replace("\n", " ").Replace("\r", "") + "..."
            : plan.QueryText.Replace("\n", " ").Replace("\r", "");

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("30,70,70,120,100,*"),
            Children =
            {
                SetCol(cb, 0),
                SetCol(new TextBlock { Text = plan.QueryId.ToString(), FontSize = 11,
                    Foreground = Brush.Parse("#E4E6EB"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 1),
                SetCol(new TextBlock { Text = plan.PlanId.ToString(), FontSize = 11,
                    Foreground = Brush.Parse("#E4E6EB"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 2),
                SetCol(new TextBlock { Text = FormatMetric(plan, orderBy), FontSize = 11,
                    Foreground = Brush.Parse("#E4E6EB"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 3),
                SetCol(new TextBlock { Text = plan.CountExecutions.ToString("N0"), FontSize = 11,
                    Foreground = Brush.Parse("#E4E6EB"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 4),
                SetCol(new TextBlock { Text = queryPreview, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Brush.Parse("#B0B6C0"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, 5),
            }
        };

        var bg = index % 2 == 0 ? "#1A1D23" : "#1E2128";
        return new Border
        {
            Background = Brush.Parse(bg),
            Padding = new Thickness(8, 6),
            Child = grid
        };
    }

    private static Control SetCol(Control control, int col)
    {
        Grid.SetColumn(control, col);
        return control;
    }

    private static string GetMetricHeader(string orderBy)
    {
        return orderBy.ToLowerInvariant() switch
        {
            "cpu" => "Total CPU",
            "avg-cpu" => "Avg CPU",
            "duration" => "Total Duration",
            "avg-duration" => "Avg Duration",
            "reads" => "Total Reads",
            "avg-reads" => "Avg Reads",
            "writes" => "Total Writes",
            "avg-writes" => "Avg Writes",
            "physical-reads" => "Total Phys Rds",
            "avg-physical-reads" => "Avg Phys Rds",
            "memory" => "Total Mem Grant",
            "avg-memory" => "Avg Mem Grant",
            "executions" => "Executions",
            _ => "Total CPU"
        };
    }

    private static string FormatMetric(QueryStorePlan plan, string orderBy)
    {
        return orderBy.ToLowerInvariant() switch
        {
            "cpu"              => $"{plan.TotalCpuTimeUs / 1000.0:N0}ms",
            "avg-cpu"          => $"{plan.AvgCpuTimeUs / 1000.0:N1}ms",
            "duration"         => $"{plan.TotalDurationUs / 1000.0:N0}ms",
            "avg-duration"     => $"{plan.AvgDurationUs / 1000.0:N1}ms",
            "reads"            => $"{plan.TotalLogicalIoReads:N0}pg",
            "avg-reads"        => $"{plan.AvgLogicalIoReads:N0}pg",
            "writes"           => $"{plan.TotalLogicalIoWrites:N0}pg",
            "avg-writes"       => $"{plan.AvgLogicalIoWrites:N0}pg",
            "physical-reads"   => $"{plan.TotalPhysicalIoReads:N0}pg",
            "avg-physical-reads" => $"{plan.AvgPhysicalIoReads:N0}pg",
            "memory"           => $"{plan.TotalMemoryGrantPages:N0}pg",
            "avg-memory"       => $"{plan.AvgMemoryGrantPages:N0}pg",
            "executions"       => $"{plan.CountExecutions:N0}",
            _                  => $"{plan.TotalCpuTimeUs / 1000.0:N0}ms"
        };
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var isChecked = SelectAllBox.IsChecked == true;
        foreach (var cb in _rowCheckBoxes)
            cb.IsChecked = isChecked;
    }

    private void Load_Click(object? sender, RoutedEventArgs e)
    {
        if (_plans == null) return;

        for (int i = 0; i < _plans.Count && i < _rowCheckBoxes.Count; i++)
        {
            if (_rowCheckBoxes[i].IsChecked == true)
                SelectedPlans.Add(_plans[i]);
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _fetchCts?.Cancel();
        Close(false);
    }
}
