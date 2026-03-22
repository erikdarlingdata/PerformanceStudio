using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Controls;

public partial class WaitProfileBarControl : UserControl
{
    public static readonly StyledProperty<WaitProfile?> ProfileProperty =
        AvaloniaProperty.Register<WaitProfileBarControl, WaitProfile?>(nameof(Profile));

    public static readonly StyledProperty<string?> HighlightCategoryProperty =
        AvaloniaProperty.Register<WaitProfileBarControl, string?>(nameof(HighlightCategory));

    public static readonly StyledProperty<bool> PercentModeProperty =
        AvaloniaProperty.Register<WaitProfileBarControl, bool>(nameof(PercentMode));

    public static readonly StyledProperty<double> MaxGrandTotalRatioProperty =
        AvaloniaProperty.Register<WaitProfileBarControl, double>(nameof(MaxGrandTotalRatio), 1.0);

    public WaitProfile? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public string? HighlightCategory
    {
        get => GetValue(HighlightCategoryProperty);
        set => SetValue(HighlightCategoryProperty, value);
    }

    public bool PercentMode
    {
        get => GetValue(PercentModeProperty);
        set => SetValue(PercentModeProperty, value);
    }

    public double MaxGrandTotalRatio
    {
        get => GetValue(MaxGrandTotalRatioProperty);
        set => SetValue(MaxGrandTotalRatioProperty, value);
    }

    public event EventHandler<string>? CategoryClicked;
    public event EventHandler<string>? CategoryDoubleClicked;

    public WaitProfileBarControl()
    {
        InitializeComponent();
        BarBorder.SizeChanged += (_, _) => Redraw();
    }

    public void SetProfile(WaitProfile? profile) => Profile = profile;
    public void SetHighlight(string? category) => HighlightCategory = category;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProfileProperty || change.Property == HighlightCategoryProperty
            || change.Property == PercentModeProperty || change.Property == MaxGrandTotalRatioProperty)
            Redraw();
    }

    private void Redraw()
    {
        BarCanvas.Children.Clear();
        var profile = Profile;
        if (profile == null || profile.GrandTotalRatio <= 0) return;

        var w = BarBorder.Bounds.Width;
        var h = BarBorder.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // In percent mode the bar fills 100% width (segments by Ratio).
        // In value mode the bar is scaled so the row with the highest
        // GrandTotalRatio fills 100% and others are proportionally shorter.
        var barScale = PercentMode || MaxGrandTotalRatio <= 0
            ? 1.0
            : profile.GrandTotalRatio / MaxGrandTotalRatio;
        var barW = w * Math.Min(barScale, 1.0);

        var highlight = HighlightCategory;
        double x = 0;
        foreach (var seg in profile.Segments)
        {
            var segW = seg.Ratio * barW;
            if (segW < 0.5) continue;

            var brush = ResolveBrush(seg.Category, seg.IsNamed);
            var rect = new Rectangle
            {
                Width = segW,
                Height = h,
                Fill = brush,
            };

            if (highlight != null && seg.Category == highlight)
            {
                rect.Opacity = 1.0;
                rect.StrokeThickness = 1;
                rect.Stroke = TryFindBrush("WaitCategory.Highlight", Brushes.White);
            }
            else if (highlight != null)
            {
                rect.Opacity = 0.3;
            }

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            BarCanvas.Children.Add(rect);

            ToolTip.SetTip(rect, $"{seg.Category}: {seg.WaitRatio:P2} ({seg.Ratio:P1} of total)");

            var capturedCategory = seg.Category;
            rect.PointerPressed += (_, e) =>
            {
                if (e.ClickCount == 2)
                    CategoryDoubleClicked?.Invoke(this, capturedCategory);
                else
                    CategoryClicked?.Invoke(this, capturedCategory);
                e.Handled = true;
            };

            x += segW;
        }
    }

    private IBrush ResolveBrush(string category, bool isNamed)
    {
        if (!isNamed)
            return TryFindBrush("WaitCategory.Others", new SolidColorBrush(Color.Parse("#555D66")));
        return TryFindBrush($"WaitCategory.{category}", new SolidColorBrush(Color.Parse("#555D66")));
    }

    private IBrush TryFindBrush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, this.ActualThemeVariant, out var resource) && resource is IBrush brush)
            return brush;
        return fallback;
    }
}
