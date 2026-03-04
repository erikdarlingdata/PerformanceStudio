using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class QueryStoreGridControl : UserControl
{
    private readonly string _connectionString;
    private readonly string _database;
    private CancellationTokenSource? _fetchCts;
    private ObservableCollection<QueryStoreRow> _rows = new();

    public event EventHandler<List<QueryStorePlan>>? PlansSelected;

    public QueryStoreGridControl(string connectionString, string database)
    {
        _connectionString = connectionString;
        _database = database;
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
    }

    private async void Fetch_Click(object? sender, RoutedEventArgs e)
    {
        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        var topN = (int)(TopNBox.Value ?? 25);
        var hoursBack = (int)(HoursBackBox.Value ?? 24);
        var orderBy = (OrderByBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cpu";

        FetchButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        StatusText.Text = "Fetching...";
        _rows.Clear();

        try
        {
            var plans = await QueryStoreService.FetchTopPlansAsync(
                _connectionString, topN, orderBy, hoursBack, ct);

            if (plans.Count == 0)
            {
                StatusText.Text = "No Query Store data found.";
                return;
            }

            foreach (var plan in plans)
                _rows.Add(new QueryStoreRow(plan));

            StatusText.Text = $"{plans.Count} plans";
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

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsSelected = true;
    }

    private void SelectNone_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.IsSelected = false;
    }

    private void LoadSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Plan).ToList();
        if (selected.Count > 0)
            PlansSelected?.Invoke(this, selected);
    }

    private void LoadHighlightedPlan_Click(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is QueryStoreRow row)
            PlansSelected?.Invoke(this, new List<QueryStorePlan> { row.Plan });
    }
}

public class QueryStoreRow : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public QueryStoreRow(QueryStorePlan plan)
    {
        Plan = plan;
    }

    public QueryStorePlan Plan { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public long QueryId => Plan.QueryId;
    public long PlanId => Plan.PlanId;

    public string ExecsDisplay => Plan.CountExecutions.ToString("N0");
    public string TotalCpuDisplay => (Plan.TotalCpuTimeUs / 1000.0).ToString("N0");
    public string AvgCpuDisplay => (Plan.AvgCpuTimeUs / 1000.0).ToString("N1");
    public string TotalDurDisplay => (Plan.TotalDurationUs / 1000.0).ToString("N0");
    public string AvgDurDisplay => (Plan.AvgDurationUs / 1000.0).ToString("N1");
    public string TotalReadsDisplay => Plan.TotalLogicalIoReads.ToString("N0");
    public string AvgReadsDisplay => Plan.AvgLogicalIoReads.ToString("N0");
    public string TotalWritesDisplay => Plan.TotalLogicalIoWrites.ToString("N0");
    public string AvgWritesDisplay => Plan.AvgLogicalIoWrites.ToString("N0");
    public string TotalPhysReadsDisplay => Plan.TotalPhysicalIoReads.ToString("N0");
    public string AvgPhysReadsDisplay => Plan.AvgPhysicalIoReads.ToString("N0");
    public string TotalMemDisplay => (Plan.TotalMemoryGrantPages * 8.0 / 1024.0).ToString("N1");
    public string AvgMemDisplay => (Plan.AvgMemoryGrantPages * 8.0 / 1024.0).ToString("N1");
    public string LastExecutedLocal => Plan.LastExecutedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string QueryPreview => Plan.QueryText.Length > 80
        ? Plan.QueryText[..80].Replace("\n", " ").Replace("\r", "") + "..."
        : Plan.QueryText.Replace("\n", " ").Replace("\r", "");
    public string FullQueryText => Plan.QueryText;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
