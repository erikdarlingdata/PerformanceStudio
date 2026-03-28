using System;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Controls;

public partial class WaitStatsProfileControl : UserControl
{
    private enum ViewMode { Bar, Ribbon }

    private ViewMode _viewMode = ViewMode.Ribbon;
    private bool _isCollapsed;
    private WaitProfile? _currentProfile;
    private Popup? _legendPopup;

    public event EventHandler<string>? CategoryClicked;
    public event EventHandler<string>? CategoryDoubleClicked;
    public event EventHandler<bool>? CollapsedChanged;

    public bool IsCollapsed => _isCollapsed;

    // All known wait categories in the order they appear in the theme
    private static readonly string[] AllWaitCategories =
    [
        "Unknown", "CPU", "Worker Thread", "Lock", "Latch", "Buffer Latch",
        "Buffer IO", "Compilation", "SQL CLR", "Mirroring", "Transaction",
        "Preemptive", "Service Broker", "Tran Log IO", "Network IO",
        "Parallelism", "Memory", "Tracing", "Full Text Search",
        "Other Disk IO", "Replication", "Log Rate Governor", "Others"
    ];

    public WaitStatsProfileControl()
    {
        InitializeComponent();
        GlobalBar.CategoryClicked += (_, cat) => CategoryClicked?.Invoke(this, cat);
        GlobalBar.CategoryDoubleClicked += (_, cat) => CategoryDoubleClicked?.Invoke(this, cat);
        GlobalRibbon.CategoryClicked += (_, cat) => CategoryClicked?.Invoke(this, cat);
        GlobalRibbon.CategoryDoubleClicked += (_, cat) => CategoryDoubleClicked?.Invoke(this, cat);
    }

    public void SetBarProfile(WaitProfile? profile)
    {
        _currentProfile = profile;
        GlobalBar.SetProfile(profile);
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
        LegendButton.IsVisible = true;
        CollapsedChanged?.Invoke(this, false);
    }

    public void Collapse()
    {
        if (_isCollapsed) return;
        _isCollapsed = true;
        ContentArea.IsVisible = false;
        TitleText.IsVisible = false;
        ToggleChartButton.IsVisible = false;
        LegendButton.IsVisible = false;
        CollapsedChanged?.Invoke(this, true);
    }

    public void SetLoading(bool isLoading)
    {
        WaitLoadingOverlay.IsVisible = isLoading;
    }

    private void ToggleChart_Click(object? sender, RoutedEventArgs e)
    {
        _viewMode = _viewMode == ViewMode.Bar ? ViewMode.Ribbon : ViewMode.Bar;
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        GlobalBar.IsVisible = _viewMode == ViewMode.Bar;
        GlobalRibbon.IsVisible = _viewMode == ViewMode.Ribbon;
        ToggleChartButton.Content = _viewMode == ViewMode.Ribbon ? "☰" : "▤";
    }

    private void Legend_Click(object? sender, RoutedEventArgs e)
    {
        if (_legendPopup != null)
        {
            _legendPopup.IsOpen = !_legendPopup.IsOpen;
            return;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        for (int i = 0; i < AllWaitCategories.Length; i++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int i = 0; i < AllWaitCategories.Length; i++)
        {
            var cat = AllWaitCategories[i];
            var brush = TryFindBrush($"WaitCategory.{cat}", new SolidColorBrush(Color.Parse("#555D66")));

            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                Background = brush,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(4, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(swatch, i);
            Grid.SetColumn(swatch, 0);
            grid.Children.Add(swatch);

            var label = new TextBlock
            {
                Text = cat,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 2, 8, 2),
            };
            if (this.TryFindResource("ForegroundBrush", this.ActualThemeVariant, out var fg) && fg is IBrush fgBrush)
                label.Foreground = fgBrush;
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
        }

        var scroll = new ScrollViewer
        {
            Content = grid,
            MaxHeight = 300,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var bgBrush = TryFindBrush("BackgroundLightBrush", new SolidColorBrush(Color.Parse("#22252D")));
        var borderBrush = TryFindBrush("BorderBrush", new SolidColorBrush(Color.Parse("#3A3D45")));
        var container = new Border
        {
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = scroll,
        };

        _legendPopup = new Popup
        {
            Child = container,
            IsLightDismissEnabled = true,
            Placement = PlacementMode.Bottom,
            PlacementTarget = LegendButton,
        };

        // Add to visual tree so DynamicResources resolve
        if (this.Content is Grid rootGrid)
            rootGrid.Children.Add(_legendPopup);

        _legendPopup.IsOpen = true;
    }

    private IBrush TryFindBrush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, this.ActualThemeVariant, out var resource) && resource is IBrush brush)
            return brush;
        return fallback;
    }
}
