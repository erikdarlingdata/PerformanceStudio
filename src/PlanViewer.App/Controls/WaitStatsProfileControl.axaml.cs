using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Controls;

public partial class WaitStatsProfileControl : UserControl
{
    private enum ViewMode { Bar, Ribbon, Table }

    private ViewMode _viewMode = ViewMode.Bar;
    private bool _isCollapsed;
    private WaitProfile? _currentProfile;
    private readonly ObservableCollection<WaitTableRow> _tableRows = new();

    public event EventHandler<string>? CategoryClicked;
    public event EventHandler<string>? CategoryDoubleClicked;
    public event EventHandler<bool>? CollapsedChanged;

    public bool IsCollapsed => _isCollapsed;

    public WaitStatsProfileControl()
    {
        InitializeComponent();
        TableGrid.ItemsSource = _tableRows;
        GlobalBar.CategoryClicked += (_, cat) => CategoryClicked?.Invoke(this, cat);
        GlobalBar.CategoryDoubleClicked += (_, cat) => CategoryDoubleClicked?.Invoke(this, cat);
        GlobalRibbon.CategoryClicked += (_, cat) => CategoryClicked?.Invoke(this, cat);
        GlobalRibbon.CategoryDoubleClicked += (_, cat) => CategoryDoubleClicked?.Invoke(this, cat);
    }

    public void SetBarProfile(WaitProfile? profile)
    {
        _currentProfile = profile;
        GlobalBar.SetProfile(profile);
        RefreshTableRows();
    }

    public void SetRibbonData(List<WaitCategoryTimeSlice> data)
    {
        GlobalRibbon.SetData(data);
    }

    public void SetHighlight(string? category)
    {
        GlobalBar.SetHighlight(category);
        GlobalRibbon.SetHighlight(category);
    }

    public void Expand()
    {
        if (!_isCollapsed) return;
        _isCollapsed = false;
        ContentArea.IsVisible = true;
        TitleText.IsVisible = true;
        ToggleChartButton.IsVisible = true;
        TableViewButton.IsVisible = true;
        CollapsedChanged?.Invoke(this, false);
    }

    public void Collapse()
    {
        if (_isCollapsed) return;
        _isCollapsed = true;
        ContentArea.IsVisible = false;
        TitleText.IsVisible = false;
        ToggleChartButton.IsVisible = false;
        TableViewButton.IsVisible = false;
        CollapsedChanged?.Invoke(this, true);
    }

    public void SetLoading(bool isLoading)
    {
        WaitLoadingOverlay.IsVisible = isLoading;
    }

    private void ToggleChart_Click(object? sender, RoutedEventArgs e)
    {
        // Cycle: Bar -> Ribbon -> Bar (skip table; table has its own button)
        if (_viewMode == ViewMode.Table)
        {
            // If in table mode, toggle goes back to bar
            _viewMode = ViewMode.Bar;
        }
        else
        {
            _viewMode = _viewMode == ViewMode.Bar ? ViewMode.Ribbon : ViewMode.Bar;
        }
        ApplyViewMode();
    }

    private void TableView_Click(object? sender, RoutedEventArgs e)
    {
        _viewMode = _viewMode == ViewMode.Table ? ViewMode.Bar : ViewMode.Table;
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        GlobalBar.IsVisible = _viewMode == ViewMode.Bar;
        GlobalRibbon.IsVisible = _viewMode == ViewMode.Ribbon;
        TableGrid.IsVisible = _viewMode == ViewMode.Table;
        ToggleChartButton.Content = _viewMode == ViewMode.Ribbon ? "▤" : "☰";

        // The ContentArea lives inside an Auto-sized parent row, so a *-row
        // DataGrid would collapse to zero height.  Give an explicit height
        // when in table mode; reset to NaN (auto) for chart modes.
        ContentArea.Height = _viewMode == ViewMode.Table ? 120 : double.NaN;
    }

    private void RefreshTableRows()
    {
        _tableRows.Clear();
        if (_currentProfile == null) { return; }
        foreach (var seg in _currentProfile.Segments.OrderByDescending(s => s.Ratio))
        {
            _tableRows.Add(new WaitTableRow
            {
                Category = seg.Category,
                WaitRatioText = seg.WaitRatio.ToString("P2"),
                RatioText = seg.Ratio.ToString("P1")
            });
        }
    }
}

public class WaitTableRow
{
    public string Category { get; set; } = "";
    public string WaitRatioText { get; set; } = "";
    public string RatioText { get; set; } = ""; 
}
