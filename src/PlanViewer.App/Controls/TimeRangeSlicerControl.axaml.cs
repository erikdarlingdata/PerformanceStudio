using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.App.Controls;

public partial class TimeRangeSlicerControl : UserControl
{
    private List<QueryStoreTimeSlice> _data = new();
    private string _metric = "cpu";

    // Range as normalised [0..1] positions within _data
    private double _rangeStart;
    private double _rangeEnd = 1.0;

    private const double HandleWidthPx = 8;
    private const double HandleGripWidthPx = 20; // extended hit-test area for easier grabbing
    private const double ChartPaddingTop = 16;
    private const double ChartPaddingBottom = 20;
    private const double MoveBarHeight = 14; // height of the draggable move bar at the top of the selection

    // Cached brushes and objects to avoid allocations on every Redraw
    private static readonly SolidColorBrush FallbackChartFillBrush   = new(Color.Parse("#332EAEF1"));
    private static readonly SolidColorBrush FallbackChartLineBrush   = new(Color.Parse("#2EAEF1"));
    private static readonly SolidColorBrush FallbackLabelBrush       = new(Color.Parse("#99E4E6EB"));
    private static readonly SolidColorBrush FallbackDayLineBrush     = new(Color.Parse("#55E4E6EB"));
    private static readonly SolidColorBrush FallbackForegroundBrush  = new(Color.Parse("#E4E6EB"));
    private static readonly SolidColorBrush FallbackOverlayBrush     = new(Color.Parse("#99000000"));
    private static readonly SolidColorBrush FallbackSelectedBrush    = new(Color.Parse("#22FFFFFF"));
    private static readonly SolidColorBrush FallbackHandleBrush      = new(Color.Parse("#E4E6EB"));
    private static readonly SolidColorBrush MoveBarBrush             = new(Color.Parse("#33FFFFFF"));
    private static readonly SolidColorBrush SelectRectFillBrush      = new(Color.Parse("#442EAEF1"));
    private static readonly Avalonia.Collections.AvaloniaList<double> DashArray = new() { 3, 3 };
    private static readonly Cursor CursorSizeAll     = new(StandardCursorType.SizeAll);
    private static readonly Cursor CursorSizeWE      = new(StandardCursorType.SizeWestEast);
    private static readonly FontFamily TooltipFont   = new("Cascadia Mono,Consolas,monospace");

    private enum DragMode { None, MoveRange, DragStart, DragEnd, SelectRect }
    private DragMode _dragMode = DragMode.None;
    private double _dragOriginX;
    private double _dragOriginRangeStart;
    private double _dragOriginRangeEnd;
    private double _selectRectOriginX;   // canvas-x where drag-select started
    private double _selectRectCurrentX;  // canvas-x of current pointer during drag-select

    private string _activeFilterTag = "24"; // tag of the currently active quick-filter button
    private DispatcherTimer? _rangeChangedDebounce;

    public event EventHandler<TimeRangeChangedEventArgs>? RangeChanged;

    public TimeRangeSlicerControl()
    {
        InitializeComponent();
        SlicerBorder.SizeChanged += (_, _) => Redraw();
        SlicerCanvas.Focusable = true;
        SlicerCanvas.KeyDown += Canvas_KeyDown;
        HighlightActiveFilter();
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
            _activeFilterTag = "24";
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
            HighlightActiveFilter();
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
        if (dt < _data[0].IntervalStartUtc) return 0;
        // Find first bucket whose end is past dt
        var idx = _data.FindIndex(b => b.IntervalStartUtc.AddHours(1) > dt);
        if (idx < 0) return 1.0; // dt is past all buckets
        // If dt is before this bucket's start (gap), snap to this bucket's start
        if (dt < _data[idx].IntervalStartUtc)
            return (double)idx / n;
        var frac = (double)(dt.Ticks - _data[idx].IntervalStartUtc.Ticks) / TimeSpan.TicksPerHour;
        return Math.Clamp((idx + Math.Clamp(frac, 0, 1)) / n, 0, 1);
    }

    private double MinNormInterval
    {
        get
        {
            if (_data.Count == 0) return 0;
            return 1.0 / _data.Count;
        }
    }

    private void CustomFilter_Click(object? sender, RoutedEventArgs e)
    {
        PopulatePickersFromSelection();
        DateTimePopup.IsOpen = true;
    }

    private void PopupApply_Click(object? sender, RoutedEventArgs e)
    {
        if (_data.Count < 2) { DateTimePopup.IsOpen = false; return; }

        var startDate = StartDatePicker.SelectedDate;
        var startTime = StartTimePicker.SelectedTime;
        var endDate = EndDatePicker.SelectedDate;
        var endTime = EndTimePicker.SelectedTime;

        if (!startDate.HasValue || !endDate.HasValue) { DateTimePopup.IsOpen = false; return; }

        var startDt = startDate.Value.Date + (startTime ?? TimeSpan.Zero);
        var endDt = endDate.Value.Date + (endTime ?? TimeSpan.Zero);

        // Convert display time back to UTC
        var startUtc = ConvertFromDisplay(startDt);
        var endUtc = ConvertFromDisplay(endDt);

        if (endUtc <= startUtc) endUtc = startUtc.AddHours(1);

        _rangeStart = GetNormFromDateTime(startUtc);
        _rangeEnd = GetNormFromDateTime(endUtc);

        // Enforce minimum interval
        if (_rangeEnd - _rangeStart < MinNormInterval)
            _rangeEnd = Math.Min(1, _rangeStart + MinNormInterval);

        _activeFilterTag = "0";
        HighlightActiveFilter();

        DateTimePopup.IsOpen = false;
        UpdateRangeLabel();
        Redraw();
        FireRangeChanged();
    }

    private void PopupCancel_Click(object? sender, RoutedEventArgs e)
    {
        DateTimePopup.IsOpen = false;
    }

    private void QuickFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hoursStr && int.TryParse(hoursStr, out var hours))
        {
            _activeFilterTag = hoursStr;
            HighlightActiveFilter();
            ApplyQuickFilter(hours);
        }
    }

    private void ApplyQuickFilter(int hours)
    {
        if (_data.Count < 2) return;
        var last = _data[^1].IntervalStartUtc.AddHours(1);
        var start = last.AddHours(-hours);
        _rangeStart = GetNormFromDateTime(start);
        _rangeEnd = 1.0;
        UpdateRangeLabel();
        Redraw();
        FireRangeChanged();
    }

    private void HighlightActiveFilter()
    {
        var accentBrush = TryFindBrush("AccentBrush", FallbackChartLineBrush);
        var normalFg = TryFindBrush("SlicerLabelBrush", FallbackLabelBrush);
        var normalBorder = TryFindBrush("SlicerBorderBrush", FallbackDayLineBrush);

        foreach (var child in QuickFilterPanel.Children)
        {
            if (child is not Button btn) continue;
            var tag = btn.Tag as string ?? "";
            if (tag == _activeFilterTag)
            {
                btn.BorderBrush = accentBrush;
                btn.Foreground = accentBrush;
            }
            else
            {
                btn.BorderBrush = normalBorder;
                btn.Foreground = normalFg;
            }
        }
    }

    private void PopulatePickersFromSelection()
    {
        if (_data.Count == 0) return;

        var startDisplay = TimeDisplayHelper.ConvertForDisplay(GetDateTimeAtNorm(_rangeStart));
        var endDisplay = TimeDisplayHelper.ConvertForDisplay(GetDateTimeAtNorm(_rangeEnd));

        StartDatePicker.SelectedDate = startDisplay.Date;
        StartTimePicker.SelectedTime = startDisplay.TimeOfDay;

        EndDatePicker.SelectedDate = endDisplay.Date;
        EndTimePicker.SelectedTime = endDisplay.TimeOfDay;

        // Set display date range limits from data bounds
        var firstDisplay = TimeDisplayHelper.ConvertForDisplay(_data[0].IntervalStartUtc);
        var lastDisplay = TimeDisplayHelper.ConvertForDisplay(_data[^1].IntervalStartUtc.AddHours(1));
        StartDatePicker.DisplayDateStart = firstDisplay.Date;
        StartDatePicker.DisplayDateEnd = lastDisplay.Date;
        EndDatePicker.DisplayDateStart = firstDisplay.Date;
        EndDatePicker.DisplayDateEnd = lastDisplay.Date;
    }

    private static DateTime ConvertFromDisplay(DateTime displayTime)
    {
        return TimeDisplayHelper.Current switch
        {
            TimeDisplayMode.Local => displayTime.ToUniversalTime(),
            TimeDisplayMode.Utc => DateTime.SpecifyKind(displayTime, DateTimeKind.Utc),
            TimeDisplayMode.Server => displayTime.AddMinutes(-TimeDisplayHelper.ServerUtcOffsetMinutes),
            _ => displayTime.ToUniversalTime()
        };
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
        // Area fill
        var fillBrush = TryFindBrush("SlicerChartFillBrush", FallbackChartFillBrush);
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
        var lineBrush = TryFindBrush("SlicerChartLineBrush", FallbackChartLineBrush);
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
        var labelBrush = TryFindBrush("SlicerLabelBrush", FallbackLabelBrush);
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
            Foreground = TryFindBrush("ForegroundBrush", FallbackForegroundBrush),
        };
        Canvas.SetRight(metricTb, 4);
        Canvas.SetTop(metricTb, 2);
        SlicerCanvas.Children.Add(metricTb);

        // ── Day-boundary vertical dashed lines ─────────────────────────────
        // Walk buckets and detect when the display-mode date changes.
        var dayLineBrush = TryFindBrush("SlicerLabelBrush", FallbackDayLineBrush);
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
                    StrokeDashArray = DashArray,
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
        var overlayBrush = TryFindBrush("SlicerOverlayBrush", FallbackOverlayBrush);
        var selectedBrush = TryFindBrush("SlicerSelectedBrush", FallbackSelectedBrush);
        var handleBrush = TryFindBrush("SlicerHandleBrush", FallbackHandleBrush);

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

        // ── Move bar at the top of the selection ────────────────────────────
        var moveBar = new Rectangle
        {
            Width  = Math.Max(0, selRight - selLeft),
            Height = MoveBarHeight,
            Fill   = MoveBarBrush,
            Cursor = CursorSizeAll,
        };
        Canvas.SetLeft(moveBar, selLeft);
        Canvas.SetTop(moveBar, 0);
        SlicerCanvas.Children.Add(moveBar);
        // Grip dots in the move bar centre
        var moveBarMidX = (selLeft + selRight) / 2;
        var moveBarMidY = MoveBarHeight / 2;
        for (int gi = -1; gi <= 1; gi++)
        {
            var dot = new Ellipse
            {
                Width = 3, Height = 3,
                Fill = handleBrush,
                Opacity = 0.5,
            };
            Canvas.SetLeft(dot, moveBarMidX + gi * 6 - 1.5);
            Canvas.SetTop(dot, moveBarMidY - 1.5);
            SlicerCanvas.Children.Add(dot);
        }

        // ── Drag-select rectangle overlay ──────────────────────────────────
        if (_dragMode == DragMode.SelectRect)
        {
            var rx1 = Math.Min(_selectRectOriginX, _selectRectCurrentX);
            var rx2 = Math.Max(_selectRectOriginX, _selectRectCurrentX);
            var selRectBrush = TryFindBrush("SlicerChartLineBrush", FallbackChartLineBrush);
            // Semi-transparent fill
            SlicerCanvas.Children.Add(new Rectangle
            {
                Width = rx2 - rx1,
                Height = h,
                Fill = SelectRectFillBrush,
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
        var dotBrush = TryFindBrush("SlicerChartLineBrush", FallbackChartLineBrush);
        for (int i = 0; i < n; i++)
        {
            var bucketDisplay    = TimeDisplayHelper.ConvertForDisplay(_data[i].IntervalStartUtc);
            var bucketDisplayEnd = bucketDisplay.AddHours(1);
            var val              = values[i];
            var valText          = _metric is "executions" ? $"{val:N0}" : $"{val:N2}";

            var tipContent = new TextBlock
            {
                Text = $"{metricLabel}: {valText}\n" +
                       $"{bucketDisplay:yyyy-MM-dd HH:mm} \u2013 {bucketDisplayEnd:HH:mm}{TimeDisplayHelper.Suffix}",
                FontSize   = 12,
                FontFamily = TooltipFont,
                Padding    = new Thickness(6, 4),
            };

            var hitRect = new Rectangle
            {
                Width   = Math.Max(1, stepX),
                Height  = chartHeight,
                Fill    = Brushes.Transparent,
                Opacity = 1,
            };

            ToolTip.SetTip(hitRect, tipContent);
            ToolTip.SetPlacement(hitRect, PlacementMode.Pointer);
            ToolTip.SetHorizontalOffset(hitRect, 0);
            ToolTip.SetVerticalOffset(hitRect, -16);
            ToolTip.SetShowDelay(hitRect, 300);

            Canvas.SetLeft(hitRect, i * stepX);
            Canvas.SetTop(hitRect, chartTop);
            SlicerCanvas.Children.Add(hitRect);
        }

        // ── Peak dot: fixed at the maximum-value bucket within the selection ──
        var selStartIdx = (int)Math.Floor(_rangeStart * n);
        var selEndIdx   = Math.Min(n - 1, (int)Math.Ceiling(_rangeEnd * n) - 1);
        if (selStartIdx <= selEndIdx)
        {
            var peakIdx = selStartIdx;
            for (int pi = selStartIdx + 1; pi <= selEndIdx; pi++)
                if (values[pi] > values[peakIdx]) peakIdx = pi;

            const double DotR = 7;
            var dotX = peakIdx * stepX + stepX / 2;
            var dotY = chartBottom - (values[peakIdx] / max) * chartHeight;
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

        // ── Handle hit zones ──────────────────────────────────────────────
        // Drawn last so they sit above per-bucket tooltip rectangles and
        // receive pointer events in the handle areas without interference.
        var leftHitZone = new Rectangle
        {
            Width  = HandleGripWidthPx * 2,
            Height = h,
            Fill   = Brushes.Transparent,
            Cursor = CursorSizeWE,
        };
        Canvas.SetLeft(leftHitZone, selLeft - HandleGripWidthPx);
        Canvas.SetTop(leftHitZone, 0);
        SlicerCanvas.Children.Add(leftHitZone);

        var rightHitZone = new Rectangle
        {
            Width  = HandleGripWidthPx * 2,
            Height = h,
            Fill   = Brushes.Transparent,
            Cursor = CursorSizeWE,
        };
        Canvas.SetLeft(rightHitZone, selRight - HandleGripWidthPx);
        Canvas.SetTop(rightHitZone, 0);
        SlicerCanvas.Children.Add(rightHitZone);
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

    // ── Interaction ────────────────────────────────────────────────────────

    private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_data.Count < 2) return;
        SlicerCanvas.Focus();
        var pos = e.GetPosition(SlicerCanvas);
        var w = SlicerBorder.Bounds.Width;
        if (w <= 0) return;

        var selLeft = _rangeStart * w;
        var selRight = _rangeEnd * w;

        _dragOriginX = pos.X;
        _dragOriginRangeStart = _rangeStart;
        _dragOriginRangeEnd = _rangeEnd;

        // Click on move bar (top strip of the selection) → move
        if (pos.Y <= MoveBarHeight && pos.X >= selLeft && pos.X <= selRight)
        {
            _dragMode = DragMode.MoveRange;
            e.Pointer.Capture(SlicerCanvas);
            e.Handled = true;
            return;
        }

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

        // Inside selection: Shift+click → move, plain click → box-select (refine)
        if (pos.X >= selLeft && pos.X <= selRight
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _dragMode = DragMode.MoveRange;
            e.Pointer.Capture(SlicerCanvas);
            e.Handled = true;
            return;
        }

        // Default: start drag-select rectangle (works both inside and outside selection)
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

            if (pos.Y <= MoveBarHeight && pos.X >= selLeft && pos.X <= selRight)
            {
                SlicerCanvas.Cursor = CursorSizeAll;
            }
            else if (Math.Abs(pos.X - selLeft) <= HandleGripWidthPx ||
                     Math.Abs(pos.X - selRight) <= HandleGripWidthPx)
            {
                SlicerCanvas.Cursor = CursorSizeWE;
            }
            else
            {
                SlicerCanvas.Cursor = Cursor.Default;
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
        _activeFilterTag = "0";
        HighlightActiveFilter();
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

        _activeFilterTag = "0";
        HighlightActiveFilter();
        UpdateRangeLabel();
        Redraw();
        FireRangeChanged();
        e.Handled = true;
    }

    private void Canvas_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_data.Count < 2) return;
        var n = _data.Count;
        var step = 1.0 / n; // one bucket

        double delta = e.Key switch
        {
            Key.Left  => -step,
            Key.Right =>  step,
            _ => 0,
        };
        if (delta == 0) return;

        var span = _rangeEnd - _rangeStart;
        var newStart = _rangeStart + delta;
        if (newStart < 0) newStart = 0;
        if (newStart + span > 1) newStart = 1 - span;
        _rangeStart = newStart;
        _rangeEnd = newStart + span;

        UpdateRangeLabel();
        Redraw();
        DebouncedFireRangeChanged();
        e.Handled = true;
    }

    private void DebouncedFireRangeChanged()
    {
        _rangeChangedDebounce?.Stop();
        _rangeChangedDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _rangeChangedDebounce.Tick += (_, _) =>
        {
            _rangeChangedDebounce.Stop();
            FireRangeChanged();
        };
        _rangeChangedDebounce.Start();
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
        var spanText = span.TotalHours >= 48
            ? $"{span.TotalDays:F1}d"
            : $"{span.TotalHours:F0}h";
        RangeLabel.Text = $"{start:yyyy-MM-dd HH:mm} → {end:yyyy-MM-dd HH:mm}  ({spanText})";
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

