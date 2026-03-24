using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class TimeRangeSlicerControl : UserControl
{
    private List<QueryStoreTimeSlice> _data = new();
    private string _metric = "cpu";
    private bool _isExpanded = true;

    // Range as normalised [0..1] positions within _data
    private double _rangeStart;
    private double _rangeEnd = 1.0;

    private const double HandleWidthPx = 8;
    private const double HandleGripWidthPx = 20; // extended hit-test area for easier grabbing
    private const double MinIntervalHours = 3;
    private const double ChartPaddingTop = 16;
    private const double ChartPaddingBottom = 20;

    // Line points computed in Redraw(), used for hover hit-testing
    private Point[] _linePoints = Array.Empty<Point>();
    private double  _stepX;

    private enum DragMode { None, MoveRange, DragStart, DragEnd, SelectRect }
    private DragMode _dragMode = DragMode.None;
    private double _dragOriginX;
    private double _dragOriginRangeStart;
    private double _dragOriginRangeEnd;
    private double _selectRectOriginX;   // canvas-x where drag-select started
    private double _selectRectCurrentX;  // canvas-x of current pointer during drag-select

    private int _hoveredIndex = -1;  // bucket index under the mouse (-1 = none)

    public event EventHandler<TimeRangeChangedEventArgs>? RangeChanged;

    public TimeRangeSlicerControl()
    {
        InitializeComponent();
        SlicerBorder.SizeChanged += (_, _) => Redraw();
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            SlicerBorder.IsVisible = _isExpanded;
            ToggleIcon.Text = _isExpanded ? "▾" : "▸";
        }
    }

    public void LoadData(List<QueryStoreTimeSlice> data, string metric,
        DateTime? selectionStart = null, DateTime? selectionEnd = null)
    {
        _data = data;
        _metric = metric;

        if (selectionStart.HasValue && selectionEnd.HasValue && _data.Count >= 2)
        {
            // Restore a previous selection
            _rangeStart = GetNormFromDateTime(selectionStart.Value);
            _rangeEnd = GetNormFromDateTime(selectionEnd.Value);
        }
        else
        {
            // Default selection: last 24 hours
            _rangeEnd = 1.0;
            if (_data.Count >= 2)
            {
                var last = _data[^1].IntervalStartUtc.AddHours(1);
                var start24h = last.AddHours(-24);
                _rangeStart = GetNormFromDateTime(start24h);
            }
            else
            {
                _rangeStart = 0;
            }
        }

        UpdateRangeLabel();
        Redraw();
        FireRangeChanged();
    }

    public void SetMetric(string metric)
    {
        _metric = metric;
        Redraw();
    }

    public DateTime? SelectionStart => _data.Count > 0
        ? GetDateTimeAtNorm(_rangeStart)
        : null;

    public DateTime? SelectionEnd => _data.Count > 0
        ? GetDateTimeAtNorm(_rangeEnd)
        : null;

    private DateTime GetDateTimeAtNorm(double norm)
    {
        if (_data.Count == 0) return DateTime.UtcNow;
        var n = _data.Count;
        // norm is in bucket-index space: 0 = start of first bucket, 1 = end of last bucket
        var pos = norm * n;                       // fractional bucket index
        var idx = (int)Math.Floor(pos);
        idx = Math.Clamp(idx, 0, n - 1);
        var frac = pos - idx;                     // fraction within that bucket [0..1)
        var bucketStart = _data[idx].IntervalStartUtc;
        var ticks = bucketStart.Ticks + (long)(frac * TimeSpan.TicksPerHour);
        var last = _data[^1].IntervalStartUtc.AddHours(1);
        ticks = Math.Clamp(ticks, _data[0].IntervalStartUtc.Ticks, last.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private double GetNormFromDateTime(DateTime dt)
    {
        if (_data.Count == 0) return 0;
        var n = _data.Count;
        // Binary-search for the bucket containing dt
        var idx = _data.FindIndex(b => b.IntervalStartUtc.AddHours(1) > dt);
        if (idx < 0) return 1.0; // dt is past all buckets
        if (dt < _data[0].IntervalStartUtc) return 0;
        var frac = (double)(dt.Ticks - _data[idx].IntervalStartUtc.Ticks) / TimeSpan.TicksPerHour;
        return Math.Clamp((idx + Math.Clamp(frac, 0, 1)) / n, 0, 1);
    }

    private double MinNormInterval
    {
        get
        {
            if (_data.Count == 0) return 0;
            var n = _data.Count;
            return Math.Min(MinIntervalHours / n, 1);
        }
    }

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        IsExpanded = !IsExpanded;
    }

    private double[] GetMetricValues()
    {
        return _metric switch
        {
            "cpu" => _data.Select(d => d.TotalCpu).ToArray(),
            "avg-cpu" => _data.Select(d => d.AvgCpu).ToArray(),
            "duration" => _data.Select(d => d.TotalDuration).ToArray(),
            "avg-duration" => _data.Select(d => d.AvgDuration).ToArray(),
            "reads" => _data.Select(d => d.TotalReads).ToArray(),
            "avg-reads" => _data.Select(d => d.AvgReads).ToArray(),
            "writes" => _data.Select(d => d.TotalWrites).ToArray(),
            "avg-writes" => _data.Select(d => d.AvgWrites).ToArray(),
            "physical-reads" => _data.Select(d => d.TotalPhysicalReads).ToArray(),
            "avg-physical-reads" => _data.Select(d => d.AvgPhysicalReads).ToArray(),
            "memory" => _data.Select(d => d.TotalMemory).ToArray(),
            "avg-memory" => _data.Select(d => d.AvgMemory).ToArray(),
            "executions" => _data.Select(d => (double)d.TotalExecutions).ToArray(),
            _ => _data.Select(d => d.TotalCpu).ToArray(),
        };
    }

    private string GetMetricLabel()
    {
        return _metric switch
        {
            "cpu" => "Total CPU (ms)",
            "avg-cpu" => "Avg CPU (ms)",
            "duration" => "Total Duration (ms)",
            "avg-duration" => "Avg Duration (ms)",
            "reads" => "Total Reads",
            "avg-reads" => "Avg Reads",
            "writes" => "Total Writes",
            "avg-writes" => "Avg Writes",
            "physical-reads" => "Total Physical Reads",
            "avg-physical-reads" => "Avg Physical Reads",
            "memory" => "Total Memory (MB)",
            "avg-memory" => "Avg Memory (MB)",
            "executions" => "Executions",
            _ => "Total CPU (ms)",
        };
    }

    // ── Drawing ────────────────────────────────────────────────────────────

    public void Redraw()
    {
        SlicerCanvas.Children.Clear();
        if (_data.Count < 2) return;

        // Use the parent Border bounds — Canvas has no intrinsic size
        var w = SlicerBorder.Bounds.Width;
        var h = SlicerBorder.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var values = GetMetricValues();
        var max = values.Max();
        if (max <= 0) max = 1;

        var chartTop = ChartPaddingTop;
        var chartBottom = h - ChartPaddingBottom;
        var chartHeight = chartBottom - chartTop;
        if (chartHeight <= 0) return;

        var n = values.Length;
        var stepX = w / n;

        // Draw filled area + line for the chart
        var linePoints = new List<Point>(n);
        for (int i = 0; i < n; i++)
        {
            var x = i * stepX + stepX / 2;
            var y = chartBottom - (values[i] / max) * chartHeight;
            linePoints.Add(new Point(x, y));
        }
        // Cache for Y-proximity hit-testing in pointer events
        _linePoints = linePoints.ToArray();
        _stepX      = stepX;

        // Area fill
        var fillBrush = TryFindBrush("SlicerChartFillBrush", new SolidColorBrush(Color.Parse("#332EAEF1")));
        var areaGeometry = new StreamGeometry();
        using (var ctx = areaGeometry.Open())
        {
            ctx.BeginFigure(new Point(linePoints[0].X, chartBottom), true);
            foreach (var pt in linePoints)
                ctx.LineTo(pt);
            ctx.LineTo(new Point(linePoints[^1].X, chartBottom));
            ctx.EndFigure(true);
        }
        SlicerCanvas.Children.Add(new Path
        {
            Data = areaGeometry,
            Fill = fillBrush,
        });

        // Line
        var lineBrush = TryFindBrush("SlicerChartLineBrush", new SolidColorBrush(Color.Parse("#2EAEF1")));
        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
        {
            ctx.BeginFigure(linePoints[0], false);
            for (int i = 1; i < linePoints.Count; i++)
                ctx.LineTo(linePoints[i]);
            ctx.EndFigure(false);
        }
        SlicerCanvas.Children.Add(new Path
        {
            Data = lineGeometry,
            Stroke = lineBrush,
            StrokeThickness = 1.5,
        });

        // X-axis labels (show a few ticks)
        var labelBrush = TryFindBrush("SlicerLabelBrush", new SolidColorBrush(Color.Parse("#99E4E6EB")));
        int labelInterval = Math.Max(1, n / 8);
        for (int i = 0; i < n; i += labelInterval)
        {
            var x = i * stepX + stepX / 2;
            var dt = TimeDisplayHelper.ConvertForDisplay(_data[i].IntervalStartUtc);
            var label = dt.ToString("MM/dd HH:mm");
            var tb = new TextBlock
            {
                Text = label,
                FontSize = 9,
                Foreground = labelBrush,
            };
            Canvas.SetLeft(tb, x - 25);
            Canvas.SetTop(tb, chartBottom + 2);
            SlicerCanvas.Children.Add(tb);
        }

        // Metric label top-right
        var metricTb = new TextBlock
        {
            Text = GetMetricLabel(),
            FontSize = 12,
            Foreground = TryFindBrush("ForegroundBrush", new SolidColorBrush(Color.Parse("#E4E6EB"))),
        };
        Canvas.SetRight(metricTb, 4);
        Canvas.SetTop(metricTb, 2);
        SlicerCanvas.Children.Add(metricTb);

        // ── Day-boundary vertical dashed lines ─────────────────────────────
        // Walk buckets and detect when the display-mode date changes.
        var dayLineBrush = TryFindBrush("SlicerLabelBrush", new SolidColorBrush(Color.Parse("#55E4E6EB")));
        for (int di = 1; di < n; di++)
        {
            var prevDisplay = TimeDisplayHelper.ConvertForDisplay(_data[di - 1].IntervalStartUtc);
            var curDisplay  = TimeDisplayHelper.ConvertForDisplay(_data[di].IntervalStartUtc);
            if (curDisplay.Date != prevDisplay.Date)
            {
                var xDay = di * stepX; // left edge of the bucket where the new day starts
                SlicerCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(xDay, chartTop),
                    EndPoint   = new Point(xDay, chartBottom),
                    Stroke = dayLineBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 3 },
                    Opacity = 0.5,
                });
                var dayLabel = new TextBlock
                {
                    Text = curDisplay.ToString("MM/dd"),
                    FontSize = 8,
                    Foreground = dayLineBrush,
                    Opacity = 0.8,
                };
                Canvas.SetLeft(dayLabel, xDay + 2);
                Canvas.SetTop(dayLabel, chartTop);
                SlicerCanvas.Children.Add(dayLabel);
            }
        }

        // ── Overlays + selection ───────────────────────────────────────────
        var overlayBrush = TryFindBrush("SlicerOverlayBrush", new SolidColorBrush(Color.Parse("#99000000")));
        var selectedBrush = TryFindBrush("SlicerSelectedBrush", new SolidColorBrush(Color.Parse("#22FFFFFF")));
        var handleBrush = TryFindBrush("SlicerHandleBrush", new SolidColorBrush(Color.Parse("#E4E6EB")));

        var selLeft = _rangeStart * w;
        var selRight = _rangeEnd * w;

        // Left overlay
        if (selLeft > 0)
        {
            SlicerCanvas.Children.Add(new Rectangle
            {
                Width = selLeft,
                Height = h,
                Fill = overlayBrush,
            });
            Canvas.SetLeft(SlicerCanvas.Children[^1], 0);
            Canvas.SetTop(SlicerCanvas.Children[^1], 0);
        }

        // Right overlay
        if (selRight < w)
        {
            SlicerCanvas.Children.Add(new Rectangle
            {
                Width = w - selRight,
                Height = h,
                Fill = overlayBrush,
            });
            Canvas.SetLeft(SlicerCanvas.Children[^1], selRight);
            Canvas.SetTop(SlicerCanvas.Children[^1], 0);
        }

        // Selected region (darker = more visible)
        SlicerCanvas.Children.Add(new Rectangle
        {
            Width = Math.Max(0, selRight - selLeft),
            Height = h,
            Fill = selectedBrush,
        });
        Canvas.SetLeft(SlicerCanvas.Children[^1], selLeft);
        Canvas.SetTop(SlicerCanvas.Children[^1], 0);

        // Left handle
        DrawHandle(selLeft, h, handleBrush);
        // Right handle
        DrawHandle(selRight - HandleWidthPx, h, handleBrush);

        // Selection border top and bottom lines
        var borderBrush = TryFindBrush("SlicerHandleBrush", handleBrush);
        // Top border of selection
        SlicerCanvas.Children.Add(new Line
        {
            StartPoint = new Point(selLeft, 0),
            EndPoint = new Point(selRight, 0),
            Stroke = borderBrush,
            StrokeThickness = 1,
            Opacity = 0.5,
        });
        // Bottom border of selection
        SlicerCanvas.Children.Add(new Line
        {
            StartPoint = new Point(selLeft, h),
            EndPoint = new Point(selRight, h),
            Stroke = borderBrush,
            StrokeThickness = 1,
            Opacity = 0.5,
        });

        // ── Drag-select rectangle overlay ──────────────────────────────────
        if (_dragMode == DragMode.SelectRect)
        {
            var rx1 = Math.Min(_selectRectOriginX, _selectRectCurrentX);
            var rx2 = Math.Max(_selectRectOriginX, _selectRectCurrentX);
            var selRectBrush = TryFindBrush("SlicerChartLineBrush", new SolidColorBrush(Color.Parse("#2EAEF1")));
            // Semi-transparent fill
            SlicerCanvas.Children.Add(new Rectangle
            {
                Width = rx2 - rx1,
                Height = h,
                Fill = new SolidColorBrush(Color.Parse("#442EAEF1")),
            });
            Canvas.SetLeft(SlicerCanvas.Children[^1], rx1);
            Canvas.SetTop(SlicerCanvas.Children[^1], 0);
            // Border
            SlicerCanvas.Children.Add(new Rectangle
            {
                Width = rx2 - rx1,
                Height = h,
                Stroke = selRectBrush,
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent,
            });
            Canvas.SetLeft(SlicerCanvas.Children[^1], rx1);
            Canvas.SetTop(SlicerCanvas.Children[^1], 0);
        }

        // ── Per-bucket hit rectangles: tooltip + hover dot ───────────────────────────
        // Drawn last so they are on top of all overlays and receive pointer events.
        var metricLabel = GetMetricLabel();
        var dotBrush = TryFindBrush("SlicerChartLineBrush", new SolidColorBrush(Color.Parse("#2EAEF1")));
        for (int i = 0; i < n; i++)
        {
            var bucketDisplay    = TimeDisplayHelper.ConvertForDisplay(_data[i].IntervalStartUtc);
            var bucketDisplayEnd = bucketDisplay.AddHours(1);
            var val              = values[i];
            var valText          = _metric is "executions" ? $"{val:N0}" : $"{val:N2}";

            TextBlock MakeTipContent() => new TextBlock
            {
                Text = $"{metricLabel}: {valText}\n" +
                       $"{bucketDisplay:yyyy-MM-dd HH:mm} \u2013 {bucketDisplayEnd:HH:mm}{TimeDisplayHelper.Suffix}",
                FontSize   = 12,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
                Padding    = new Thickness(6, 4),
            };

            var hitRect = new Rectangle
            {
                Width   = Math.Max(1, stepX),
                Height  = chartHeight,
                Fill    = Brushes.Transparent,
                Opacity = 1,
            };

            ToolTip.SetTip(hitRect, MakeTipContent());
            ToolTip.SetShowDelay(hitRect, 300);
            ToolTip.SetVerticalOffset(hitRect, 10);

            Canvas.SetLeft(hitRect, i * stepX);
            Canvas.SetTop(hitRect, chartTop);
            SlicerCanvas.Children.Add(hitRect);
        }

        // ── Hover dot ──────────────────────────────────────────────────
        var dotIdx = _hoveredIndex;
        if (dotIdx >= 0 && dotIdx < n)
        {
            const double DotR = 7;
            var dotX = dotIdx * stepX + stepX / 2;
            var dotY = chartBottom - (values[dotIdx] / max) * chartHeight;
            var dot = new Ellipse
            {
                Width           = DotR * 2,
                Height          = DotR * 2,
                Fill            = dotBrush,
                Stroke          = TryFindBrush("BackgroundBrush", Brushes.Black),
                StrokeThickness = 2,
            };
            Canvas.SetLeft(dot, dotX - DotR);
            Canvas.SetTop(dot,  dotY - DotR);
            SlicerCanvas.Children.Add(dot);
        }
    }

    private void DrawHandle(double x, double canvasHeight, IBrush brush)
    {
        // Handle bar
        SlicerCanvas.Children.Add(new Rectangle
        {
            Width = HandleWidthPx,
            Height = canvasHeight,
            Fill = brush,
            Opacity = 0.7,
        });
        Canvas.SetLeft(SlicerCanvas.Children[^1], x);
        Canvas.SetTop(SlicerCanvas.Children[^1], 0);

        // Grip lines (3 short horizontal lines in the middle of the handle)
        var midY = canvasHeight / 2;
        for (int i = -1; i <= 1; i++)
        {
            var gy = midY + i * 5;
            SlicerCanvas.Children.Add(new Line
            {
                StartPoint = new Point(x + 2, gy),
                EndPoint = new Point(x + HandleWidthPx - 2, gy),
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Opacity = 0.6,
            });
        }
    }

    private IBrush TryFindBrush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, this.ActualThemeVariant, out var resource) && resource is IBrush brush)
            return brush;
        return fallback;
    }

    /// <summary>
    /// Finds the bucket index whose line point is closest horizontally to <paramref name="pos"/>,
    /// or -1 if none qualifies.
    /// </summary>
    private int FindNearestLineIndex(Point pos)
    {
        if (_linePoints.Length == 0) return -1;
        var best = -1;
        var bestDist = double.MaxValue;
        for (int i = 0; i < _linePoints.Length; i++)
        {
            var lp = _linePoints[i];
            var dx = Math.Abs(pos.X - lp.X);
            if (dx <= _stepX / 2 + 2 && dx < bestDist)
            {
                bestDist = dx;
                best = i;
            }
        }
        return best;
    }

    // ── Interaction ────────────────────────────────────────────────────────

    private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_data.Count < 2) return;
        var pos = e.GetPosition(SlicerCanvas);
        var w = SlicerBorder.Bounds.Width;
        if (w <= 0) return;

        var selLeft = _rangeStart * w;
        var selRight = _rangeEnd * w;

        _dragOriginX = pos.X;
        _dragOriginRangeStart = _rangeStart;
        _dragOriginRangeEnd = _rangeEnd;

        // Check if near left handle
        if (Math.Abs(pos.X - selLeft) <= HandleGripWidthPx)
        {
            _dragMode = DragMode.DragStart;
            e.Pointer.Capture(SlicerCanvas);
            e.Handled = true;
            return;
        }

        // Check if near right handle
        if (Math.Abs(pos.X - selRight) <= HandleGripWidthPx)
        {
            _dragMode = DragMode.DragEnd;
            e.Pointer.Capture(SlicerCanvas);
            e.Handled = true;
            return;
        }

        // Check if inside selection → move
        if (pos.X >= selLeft && pos.X <= selRight)
        {
            _dragMode = DragMode.MoveRange;
            e.Pointer.Capture(SlicerCanvas);
            e.Handled = true;
            return;
        }

        // Click outside selection → start drag-select rectangle
        _selectRectOriginX = pos.X;
        _selectRectCurrentX = pos.X;
        _dragMode = DragMode.SelectRect;
        e.Pointer.Capture(SlicerCanvas);
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_data.Count < 2) return;
        var w = SlicerBorder.Bounds.Width;
        if (w <= 0) return;

        var pos = e.GetPosition(SlicerCanvas);

        if (_dragMode == DragMode.None)
        {
            // Update cursor
            var selLeft  = _rangeStart * w;
            var selRight = _rangeEnd * w;

            if (Math.Abs(pos.X - selLeft) <= HandleGripWidthPx ||
                Math.Abs(pos.X - selRight) <= HandleGripWidthPx)
            {
                SlicerCanvas.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            }
            else if (pos.X >= selLeft && pos.X <= selRight)
            {
                SlicerCanvas.Cursor = new Cursor(StandardCursorType.SizeAll);
            }
            else
            {
                SlicerCanvas.Cursor = Cursor.Default;
            }

            // Y-proximity hover for dot
            var newHover = FindNearestLineIndex(pos);
            if (newHover != _hoveredIndex)
            {
                _hoveredIndex = newHover;
                Redraw();
            }
            return;
        }

        var deltaX = pos.X - _dragOriginX;
        var deltaNorm = deltaX / w;
        var minInterval = MinNormInterval;

        switch (_dragMode)
        {
            case DragMode.DragStart:
            {
                var newStart = Math.Clamp(_dragOriginRangeStart + deltaNorm, 0, _rangeEnd - minInterval);
                _rangeStart = newStart;
                break;
            }
            case DragMode.DragEnd:
            {
                var newEnd = Math.Clamp(_dragOriginRangeEnd + deltaNorm, _rangeStart + minInterval, 1);
                _rangeEnd = newEnd;
                break;
            }
            case DragMode.MoveRange:
            {
                var span = _dragOriginRangeEnd - _dragOriginRangeStart;
                var newStart = _dragOriginRangeStart + deltaNorm;
                if (newStart < 0) newStart = 0;
                if (newStart + span > 1) newStart = 1 - span;
                _rangeStart = newStart;
                _rangeEnd = newStart + span;
                break;
            }
            case DragMode.SelectRect:
            {
                _selectRectCurrentX = Math.Clamp(pos.X, 0, w);
                UpdateRangeLabel();
                Redraw();
                e.Handled = true;
                return;
            }
        }

        UpdateRangeLabel();
        Redraw();
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragMode == DragMode.None) return;

        if (_dragMode == DragMode.SelectRect)
        {
            var w = SlicerBorder.Bounds.Width;
            if (w > 0)
            {
                var rx1 = Math.Min(_selectRectOriginX, _selectRectCurrentX);
                var rx2 = Math.Max(_selectRectOriginX, _selectRectCurrentX);
                // Only apply if the rectangle has meaningful width (> 4px)
                if (rx2 - rx1 > 4)
                {
                    _rangeStart = Math.Clamp(rx1 / w, 0, 1);
                    _rangeEnd   = Math.Clamp(rx2 / w, 0, 1);
                    // Enforce minimum interval
                    if (_rangeEnd - _rangeStart < MinNormInterval)
                        _rangeEnd = Math.Min(1, _rangeStart + MinNormInterval);
                }
            }
        }

        _dragMode = DragMode.None;
        e.Pointer.Capture(null);
        UpdateRangeLabel();
        Redraw();
        FireRangeChanged();
        e.Handled = true;
    }

    private void Canvas_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_data.Count < 2) return;
        // Only zoom if Ctrl is held
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        var w = SlicerBorder.Bounds.Width;
        if (w <= 0) return;

        var pos = e.GetPosition(SlicerCanvas);
        var pivot = Math.Clamp(pos.X / w, 0, 1);
        var span = _rangeEnd - _rangeStart;
        var minInterval = MinNormInterval;

        // Zoom in (wheel up) → shrink span; zoom out (wheel down) → expand span
        var zoomFactor = e.Delta.Y > 0 ? 0.85 : 1.0 / 0.85;
        var newSpan = Math.Clamp(span * zoomFactor, minInterval, 1.0);

        // Keep the pivot point stable in the range
        var pivotInRange = (pivot - _rangeStart) / span;
        var newStart = pivot - pivotInRange * newSpan;
        var newEnd = newStart + newSpan;

        if (newStart < 0) { newStart = 0; newEnd = newSpan; }
        if (newEnd > 1) { newEnd = 1; newStart = 1 - newSpan; }

        _rangeStart = Math.Max(0, newStart);
        _rangeEnd = Math.Min(1, newEnd);

        UpdateRangeLabel();
        Redraw();
        FireRangeChanged();
        e.Handled = true;
    }

    private void UpdateRangeLabel()
    {
        if (_data.Count == 0)
        {
            RangeLabel.Text = "";
            return;
        }
        var start = TimeDisplayHelper.ConvertForDisplay(GetDateTimeAtNorm(_rangeStart));
        var end = TimeDisplayHelper.ConvertForDisplay(GetDateTimeAtNorm(_rangeEnd));
        var span = end - start;
        RangeLabel.Text = $"{start:yyyy-MM-dd HH:mm} → {end:yyyy-MM-dd HH:mm}  ({span.TotalHours:F0}h)";
    }

    private void FireRangeChanged()
    {
        if (_data.Count == 0) return;
        RangeChanged?.Invoke(this, new TimeRangeChangedEventArgs(
            GetDateTimeAtNorm(_rangeStart),
            GetDateTimeAtNorm(_rangeEnd)));
    }
}

public class TimeRangeChangedEventArgs : EventArgs
{
    public DateTime StartUtc { get; }
    public DateTime EndUtc { get; }

    public TimeRangeChangedEventArgs(DateTime startUtc, DateTime endUtc)
    {
        StartUtc = startUtc;
        EndUtc = endUtc;
    }
}

