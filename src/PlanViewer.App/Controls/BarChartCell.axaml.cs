using Avalonia;
using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace PlanViewer.App.Controls;

/// <summary>
/// A DataGrid cell control that shows a text value above a proportional bar.
/// The bar colour is resolved from application resources using the priority:
///   BarChart.&lt;ColumnId&gt;  →  BarChart.Sorted (when isSorted)  →  BarChart.Default
/// </summary>
public partial class BarChartCell : UserControl
{
    // ── Avalonia properties ────────────────────────────────────────────────

    public static readonly StyledProperty<string> DisplayTextProperty =
        AvaloniaProperty.Register<BarChartCell, string>(nameof(DisplayText), string.Empty);

    /// <summary>Value in [0..1] that controls the bar width.</summary>
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<BarChartCell, double>(nameof(Ratio), 0.0);

    /// <summary>Column identifier used to look up per-column brush overrides.</summary>
    public static readonly StyledProperty<string> ColumnIdProperty =
        AvaloniaProperty.Register<BarChartCell, string>(nameof(ColumnId), string.Empty);

    /// <summary>When true the "Sorted" brush is preferred over "Default".</summary>
    public static readonly StyledProperty<bool> IsSortedColumnProperty =
        AvaloniaProperty.Register<BarChartCell, bool>(nameof(IsSortedColumn), false);

    // ── CLR accessors ──────────────────────────────────────────────────────

    public string DisplayText
    {
        get => GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    public string ColumnId
    {
        get => GetValue(ColumnIdProperty);
        set => SetValue(ColumnIdProperty, value);
    }

    public bool IsSortedColumn
    {
        get => GetValue(IsSortedColumnProperty);
        set => SetValue(IsSortedColumnProperty, value);
    }

    // ── Constructor ────────────────────────────────────────────────────────

    public BarChartCell()
    {
        InitializeComponent();
    }

    // ── Property-change overrides ──────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DisplayTextProperty)
            UpdateText();
        else if (change.Property == RatioProperty || change.Property == BoundsProperty)
            UpdateBar();
        else if (change.Property == ColumnIdProperty || change.Property == IsSortedColumnProperty)
            UpdateBarColor();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateText();
        UpdateBar();
        UpdateBarColor();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void UpdateText()
    {
        if (ValueText != null)
            ValueText.Text = DisplayText;
    }

    private void UpdateBar()
    {
        if (BarTrack == null || BarFill == null) return;

        var ratio = global::System.Math.Max(0.0, global::System.Math.Min(1.0, Ratio));
        var trackWidth = BarTrack.Bounds.Width;

        // Fall back to control width when layout hasn't run yet
        if (trackWidth <= 0)
            trackWidth = Bounds.Width - 4; // subtract Margin="2,1" × 2

        BarFill.Width = trackWidth > 0 ? ratio * trackWidth : 0;
    }

    private void UpdateBarColor()
    {
        if (BarFill == null) return;

        // 1. Per-column override
        if (!string.IsNullOrEmpty(ColumnId) &&
            Application.Current!.TryFindResource($"BarChart.{ColumnId}", out var colRes) &&
            colRes is IBrush colBrush)
        {
            BarFill.Background = colBrush;
            return;
        }

        // 2. Sorted vs Default
        var key = IsSortedColumn ? "BarChart.Sorted" : "BarChart.Default";
        if (Application.Current!.TryFindResource(key, out var res) && res is IBrush brush)
            BarFill.Background = brush;
        else
            BarFill.Background = IsSortedColumn
                ? new SolidColorBrush(Color.FromRgb(0x2E, 0xAE, 0xF1))
                : new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0xA8));
    }

    // ── Layout override to update bar after measure ─────────────────────--

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateBar();
        return result;
    }
}
