using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

/// <summary>
/// Stacked ribbon chart showing wait stats over time (X = hours, Y = stacked ms).
/// Top 3 categories get distinct colors; the rest go into "Others".
/// </summary>
public partial class WaitStatsRibbonControl : UserControl
{
    private List<WaitCategoryTimeSlice> _data = new();
    private string? _highlightCategory;

    public event EventHandler<string>? CategoryClicked;
    public event EventHandler<string>? CategoryDoubleClicked;

    private const double PaddingTop = 4;
    private const double PaddingBottom = 16;

    public WaitStatsRibbonControl()
    {
        InitializeComponent();
        RibbonBorder.SizeChanged += (_, _) => Redraw();
    }

    public void SetData(List<WaitCategoryTimeSlice> data)
    {
        _data = data;
        Redraw();
    }

    public void SetHighlight(string? category)
    {
        _highlightCategory = category;
        Redraw();
    }

    private void Redraw()
    {
        RibbonCanvas.Children.Clear();
        if (_data.Count == 0) return;

        var w = RibbonBorder.Bounds.Width;
        var h = RibbonBorder.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var chartH = h - PaddingTop - PaddingBottom;
        if (chartH <= 0) return;

        // Determine top-3 categories globally
        var globalTotals = _data
            .GroupBy(d => d.WaitCategoryDesc)
            .Select(g => (Cat: g.Key, Total: g.Sum(x => x.WaitRatio)))
            .OrderByDescending(x => x.Total)
            .ToList();

        var top3 = new HashSet<string>(globalTotals.Take(3).Select(x => x.Cat));

        // Group by hour into a lookup
        var bucketLookup = _data
            .GroupBy(d => d.IntervalStartUtc)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (bucketLookup.Count == 0) return;

        // Build a complete hourly timeline from min to max
        var minHour = bucketLookup.Keys.Min();
        var maxHour = bucketLookup.Keys.Max();
        var allHours = new List<DateTime>();
        for (var hr = minHour; hr <= maxHour; hr = hr.AddHours(1))
            allHours.Add(hr);

        var n = allHours.Count;
        if (n == 0) return;
        var barGap = Math.Min(2.0, Math.Max(0.5, w / n * 0.1));
        var stepX = w / n;

        // Compute max total per bucket for Y scaling (only buckets with data)
        var maxTotal = bucketLookup.Values.Max(b => b.Sum(x => x.WaitRatio));
        if (maxTotal <= 0) maxTotal = 1;

        // Build ordered category list: top3 first, then "Others"
        var orderedCats = globalTotals
            .Where(x => top3.Contains(x.Cat))
            .Select(x => x.Cat)
            .ToList();
        orderedCats.Add("Others");

        for (int i = 0; i < n; i++)
        {
            var hour = allHours[i];
            var x = i * stepX;

            // Skip drawing if no data for this hour (gap in timeline)
            if (!bucketLookup.TryGetValue(hour, out var bucketItems))
                continue;

            double y = PaddingTop + chartH; // bottom

            // Aggregate into top3 + Others per bucket
            var catValues = new Dictionary<string, double>();
            foreach (var cat in orderedCats)
                catValues[cat] = 0;

            foreach (var s in bucketItems)
            {
                if (top3.Contains(s.WaitCategoryDesc))
                    catValues[s.WaitCategoryDesc] += s.WaitRatio;
                else
                    catValues["Others"] += s.WaitRatio;
            }

            // Draw stacked from bottom
            foreach (var cat in orderedCats)
            {
                var ratio = catValues[cat];
                if (ratio <= 0) continue;

                var segH = (ratio / maxTotal) * chartH;
                y -= segH;

                var brush = ResolveBrush(cat, cat != "Others");
                var opacity = (_highlightCategory != null && cat != _highlightCategory) ? 0.25 : 1.0;

                var rect = new Rectangle
                {
                    Width = Math.Max(1, stepX - barGap),
                    Height = Math.Max(0.5, segH),
                    Fill = brush,
                    Opacity = opacity,
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                RibbonCanvas.Children.Add(rect);

                var intervalStart = hour;
                var intervalEnd = intervalStart.AddHours(1);
                var startDisplay = TimeDisplayHelper.FormatForDisplay(intervalStart, "yyyy-MM-dd HH:mm");
                var endDisplay = intervalStart.Date == intervalEnd.Date
                    ? TimeDisplayHelper.FormatForDisplay(intervalEnd, "HH:mm")
                    : TimeDisplayHelper.FormatForDisplay(intervalEnd, "yyyy-MM-dd HH:mm");
                var tipBlock = new TextBlock
                {
                    Text = $"{cat}: {ratio:P2}\n{startDisplay} \u2013 {endDisplay}",
                    FontSize = 13,
                    Padding = new Thickness(6, 4),
                };
                ToolTip.SetTip(rect, tipBlock);
                ToolTip.SetVerticalOffset(rect, 12);
                ToolTip.SetShowDelay(rect, 200);

                var capturedCat = cat;
                rect.PointerPressed += (_, pe) =>
                {
                    if (pe.ClickCount == 2)
                        CategoryDoubleClicked?.Invoke(this, capturedCat);
                    else
                        CategoryClicked?.Invoke(this, capturedCat);
                    pe.Handled = true;
                };
            }
        }

        // X-axis labels
        var labelBrush = TryFindBrush("SlicerLabelBrush", new SolidColorBrush(Color.Parse("#99E4E6EB")));
        int labelInterval = Math.Max(1, n / 6);
        for (int i = 0; i < n; i += labelInterval)
        {
            var dt = allHours[i];
            var tb = new TextBlock
            {
                Text = TimeDisplayHelper.FormatForDisplay(dt, "MM/dd HH:mm"),
                FontSize = 8,
                Foreground = labelBrush,
            };
            Canvas.SetLeft(tb, i * stepX);
            Canvas.SetTop(tb, h - PaddingBottom + 1);
            RibbonCanvas.Children.Add(tb);
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
