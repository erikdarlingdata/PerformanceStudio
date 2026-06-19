using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class QueryStoreOverviewControl : UserControl
{
    // ── Donut Chart ──────────────────────────────────────────────────────────

    private void DrawDonut()
    {
        DonutCanvas.Children.Clear();
        if (_states.Count == 0) return;

        var w = DonutCanvas.Bounds.Width;
        var h = DonutCanvas.Bounds.Height;
        if (w < 10 || h < 10) return;

        var cx = w / 2;
        var cy = h / 2;
        var radius = Math.Min(w, h) / 2 - 10;
        var innerRadius = radius * 0.6;

        int rwCount = _states.Count(s => s.State == QueryStoreState.ReadWrite);
        int roCount = _states.Count(s => s.State == QueryStoreState.ReadOnly);
        int offCount = _states.Count(s => s.State == QueryStoreState.Off);
        int errCount = _states.Count(s => s.State == QueryStoreState.Error);
        int total = _states.Count;
        int activeCount = rwCount + roCount;

        var segmentInfos = new List<(int count, string label, QueryStoreState state, Color color)>();
        if (rwCount > 0) segmentInfos.Add((rwCount, "Read Write", QueryStoreState.ReadWrite, ReadWriteColor));
        if (roCount > 0) segmentInfos.Add((roCount, "Read Only", QueryStoreState.ReadOnly, ReadOnlyColor));
        if (offCount > 0) segmentInfos.Add((offCount, "OFF", QueryStoreState.Off, OffColor));
        if (errCount > 0) segmentInfos.Add((errCount, "Error", QueryStoreState.Error, ErrorColor));

        double startAngle = -Math.PI / 2;
        foreach (var (count, label, state, color) in segmentInfos)
        {
            var fraction = (double)count / total;
            var sweepAngle = fraction * 2 * Math.PI;
            var path = CreateArcPath(cx, cy, radius, innerRadius, startAngle, startAngle + sweepAngle, color);

            // Tooltip with details
            var pct = fraction * 100;
            var tipText = $"{label}: {count} database{(count != 1 ? "s" : "")} ({pct:F0}%)";
            ToolTip.SetTip(path, tipText);
            ToolTip.SetShowDelay(path, 200);

            // Click → popup with database list
            var capturedState = state;
            path.Cursor = new Cursor(StandardCursorType.Hand);
            path.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ShowDonutPopup(capturedState);
            };

            DonutCanvas.Children.Add(path);

            // Percentage label on the arc midpoint
            if (fraction > 0.05) // only show label if segment is large enough
            {
                var midAngle = startAngle + sweepAngle / 2;
                var labelR = (radius + innerRadius) / 2;
                var lx = cx + labelR * Math.Cos(midAngle);
                var ly = cy + labelR * Math.Sin(midAngle);
                var pctLabel = new TextBlock
                {
                    Text = $"{pct:F0}%",
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                };
                pctLabel.Measure(Size.Infinity);
                Canvas.SetLeft(pctLabel, lx - pctLabel.DesiredSize.Width / 2);
                Canvas.SetTop(pctLabel, ly - pctLabel.DesiredSize.Height / 2);
                DonutCanvas.Children.Add(pctLabel);
            }

            startAngle += sweepAngle;
        }

        // Center text
        var centerText = new TextBlock
        {
            Text = $"{activeCount}/{total}",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        centerText.Measure(Size.Infinity);
        Canvas.SetLeft(centerText, cx - centerText.DesiredSize.Width / 2);
        Canvas.SetTop(centerText, cy - centerText.DesiredSize.Height / 2);
        DonutCanvas.Children.Add(centerText);

        // Legend below
        var legendY = cy + radius + 5;
        DrawLegendItem(DonutCanvas, 4, legendY, ReadWriteColor, $"RW: {rwCount}");
        DrawLegendItem(DonutCanvas, 4, legendY + 16, ReadOnlyColor, $"RO: {roCount}");
        DrawLegendItem(DonutCanvas, 4, legendY + 32, OffColor, $"OFF: {offCount}");
        if (errCount > 0) DrawLegendItem(DonutCanvas, 4, legendY + 48, ErrorColor, $"Err: {errCount}");
    }

    private void ShowDonutPopup(QueryStoreState state)
    {
        var dbs = _states.Where(s => s.State == state).OrderBy(s => s.DatabaseName).ToList();
        var stateLabel = state switch
        {
            QueryStoreState.ReadWrite => "Read Write",
            QueryStoreState.ReadOnly => "Read Only",
            QueryStoreState.Error => "Error",
            _ => "OFF"
        };

        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        stack.Children.Add(new TextBlock
        {
            Text = $"{stateLabel} ({dbs.Count})",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
            Margin = new Thickness(0, 0, 0, 4)
        });

        foreach (var db in dbs)
        {
            var text = db.DatabaseName;
            if (db.State == QueryStoreState.Error && !string.IsNullOrEmpty(db.ErrorMessage))
                text += $" — {db.ErrorMessage}";
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#E4E6EB")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }

        var popup = new Popup
        {
            PlacementTarget = DonutCanvas,
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = this.FindResource("BackgroundLightBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#22252D")),
                BorderBrush = this.FindResource("BorderBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#3A3D45")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                MinWidth = 180,
                MaxHeight = 300,
                Child = new ScrollViewer { Content = stack }
            }
        };

        // Add popup to the visual tree temporarily
        if (DonutCanvas.Parent is Panel parentPanel)
        {
            parentPanel.Children.Add(popup);
            popup.IsOpen = true;
            popup.Closed += (_, _) => parentPanel.Children.Remove(popup);
        }
    }

    private void DrawLegendItem(Canvas canvas, double x, double y, Color color, string text)
    {
        if (y + 14 > canvas.Bounds.Height) return;
        var rect = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(color) };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EB"))
        };
        Canvas.SetLeft(tb, x + 14);
        Canvas.SetTop(tb, y - 1);
        canvas.Children.Add(tb);
    }

    private Path CreateArcPath(double cx, double cy, double outerR, double innerR,
        double startAngle, double endAngle, Color fill)
    {
        var largeArc = (endAngle - startAngle) > Math.PI;
        var outerStart = new Point(cx + outerR * Math.Cos(startAngle), cy + outerR * Math.Sin(startAngle));
        var outerEnd = new Point(cx + outerR * Math.Cos(endAngle), cy + outerR * Math.Sin(endAngle));
        var innerStart = new Point(cx + innerR * Math.Cos(endAngle), cy + innerR * Math.Sin(endAngle));
        var innerEnd = new Point(cx + innerR * Math.Cos(startAngle), cy + innerR * Math.Sin(startAngle));

        var fig = new PathFigure { StartPoint = outerStart, IsClosed = true };
        fig.Segments!.Add(new ArcSegment
        {
            Point = outerEnd,
            Size = new Size(outerR, outerR),
            IsLargeArc = largeArc,
            SweepDirection = SweepDirection.Clockwise
        });
        fig.Segments.Add(new LineSegment { Point = innerStart });
        fig.Segments.Add(new ArcSegment
        {
            Point = innerEnd,
            Size = new Size(innerR, innerR),
            IsLargeArc = largeArc,
            SweepDirection = SweepDirection.CounterClockwise
        });

        var geo = new PathGeometry();
        geo.Figures!.Add(fig);
        return new Path { Data = geo, Fill = new SolidColorBrush(fill) };
    }

}
