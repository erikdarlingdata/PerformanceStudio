using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;

namespace PlanViewer.App.Controls;

public partial class PlanViewerControl : UserControl
{
    private void Node_Click(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border
            && e.GetCurrentPoint(border).Properties.IsLeftButtonPressed
            && _nodeBorderMap.TryGetValue(border, out var node))
        {
            SelectNode(border, node);
            e.Handled = true;
        }
    }

    private void SelectNode(Border border, PlanNode node)
    {
        // Deselect previous
        if (_selectedNodeBorder != null)
        {
            _selectedNodeBorder.BorderBrush = _selectedNodeOriginalBorder;
            _selectedNodeBorder.BorderThickness = _selectedNodeOriginalThickness;
        }

        // Select new
        _selectedNodeOriginalBorder = border.BorderBrush;
        _selectedNodeOriginalThickness = border.BorderThickness;
        _selectedNodeBorder = border;
        border.BorderBrush = SelectionBrush;
        border.BorderThickness = new Thickness(2);

        _selectedNode = node;
        ShowPropertiesPanel(node);
        UpdateMinimapSelection(node);
    }

    /// <summary>
    /// Selects the operator with <paramref name="nodeId"/> and scrolls it into view, so a warning
    /// can take you to where it came from (#440). Returns false when the plan has no such operator,
    /// which is what keeps a stale or wrong origin from silently scrolling somewhere arbitrary.
    /// </summary>
    private bool TryNavigateToNode(int nodeId)
    {
        foreach (var child in PlanCanvas.Children)
        {
            if (child is not Border border ||
                !_nodeBorderMap.TryGetValue(border, out var node) ||
                node.NodeId != nodeId)
            {
                continue;
            }

            SelectNode(border, node);
            ScrollNodeIntoView(node);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Centres the operator in the viewport. Node coordinates are unscaled layout positions, so they
    /// are multiplied by the zoom level to get canvas pixels; the offset is then clamped, because
    /// asking a ScrollViewer for a negative offset on a plan smaller than the viewport just leaves
    /// it where it was and looks like the navigation did nothing.
    /// </summary>
    private void ScrollNodeIntoView(PlanNode node)
    {
        var targetX = node.X * _zoomLevel - (PlanScrollViewer.Bounds.Width / 2);
        var targetY = node.Y * _zoomLevel - (PlanScrollViewer.Bounds.Height / 2);

        var maxX = Math.Max(0, PlanScrollViewer.Extent.Width - PlanScrollViewer.Viewport.Width);
        var maxY = Math.Max(0, PlanScrollViewer.Extent.Height - PlanScrollViewer.Viewport.Height);

        PlanScrollViewer.Offset = new Vector(
            Math.Clamp(targetX, 0, maxX),
            Math.Clamp(targetY, 0, maxY));
    }

    private ContextMenu BuildNodeContextMenu(PlanNode node)
    {
        var menu = new ContextMenu();

        var propsItem = new MenuItem { Header = "Properties" };
        propsItem.Click += (_, _) =>
        {
            foreach (var child in PlanCanvas.Children)
            {
                if (child is Border b && _nodeBorderMap.TryGetValue(b, out var n) && n == node)
                {
                    SelectNode(b, node);
                    break;
                }
            }
        };
        menu.Items.Add(propsItem);

        menu.Items.Add(new Separator());

        var copyOpItem = new MenuItem { Header = "Copy Operator Name" };
        copyOpItem.Click += async (_, _) => await SetClipboardTextAsync(node.PhysicalOp);
        menu.Items.Add(copyOpItem);

        if (!string.IsNullOrEmpty(node.FullObjectName))
        {
            var copyObjItem = new MenuItem { Header = "Copy Object Name" };
            copyObjItem.Click += async (_, _) => await SetClipboardTextAsync(node.FullObjectName!);
            menu.Items.Add(copyObjItem);
        }

        if (!string.IsNullOrEmpty(node.Predicate))
        {
            var copyPredItem = new MenuItem { Header = "Copy Predicate" };
            copyPredItem.Click += async (_, _) => await SetClipboardTextAsync(node.Predicate!);
            menu.Items.Add(copyPredItem);
        }

        if (!string.IsNullOrEmpty(node.SeekPredicates))
        {
            var copySeekItem = new MenuItem { Header = "Copy Seek Predicate" };
            copySeekItem.Click += async (_, _) => await SetClipboardTextAsync(node.SeekPredicates!);
            menu.Items.Add(copySeekItem);
        }

        // Schema lookup items (Show Indexes, Show Table Definition)
        AddSchemaMenuItems(menu, node);

        return menu;
    }

    private ContextMenu BuildCanvasContextMenu()
    {
        var menu = new ContextMenu();

        // Zoom
        var zoomInItem = new MenuItem { Header = "Zoom In" };
        zoomInItem.Click += (_, _) => SetZoom(_zoomLevel + ZoomStep);
        menu.Items.Add(zoomInItem);

        var zoomOutItem = new MenuItem { Header = "Zoom Out" };
        zoomOutItem.Click += (_, _) => SetZoom(_zoomLevel - ZoomStep);
        menu.Items.Add(zoomOutItem);

        var fitItem = new MenuItem { Header = "Fit to View" };
        fitItem.Click += ZoomFit_Click;
        menu.Items.Add(fitItem);

        menu.Items.Add(new Separator());

        // Advice
        var humanAdviceItem = new MenuItem { Header = "Human Advice" };
        humanAdviceItem.Click += (_, _) => HumanAdviceRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(humanAdviceItem);

        var robotAdviceItem = new MenuItem { Header = "Robot Advice" };
        robotAdviceItem.Click += (_, _) => RobotAdviceRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(robotAdviceItem);

        menu.Items.Add(new Separator());

        // Repro & Save
        var copyReproItem = new MenuItem { Header = "Copy Repro Script" };
        copyReproItem.Click += (_, _) => CopyReproRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(copyReproItem);

        var saveItem = new MenuItem { Header = "Save .sqlplan" };
        saveItem.Click += SavePlan_Click;
        menu.Items.Add(saveItem);

        return menu;
    }

    private System.Threading.Tasks.Task SetClipboardTextAsync(string text)
        => ClipboardHelper.TrySetTextAsync(this, text);

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetZoom(_zoomLevel + ZoomStep);

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetZoom(_zoomLevel - ZoomStep);

    private void ZoomFit_Click(object? sender, RoutedEventArgs e)
    {
        if (PlanCanvas.Width <= 0 || PlanCanvas.Height <= 0) return;

        var viewWidth = PlanScrollViewer.Bounds.Width;
        var viewHeight = PlanScrollViewer.Bounds.Height;
        if (viewWidth <= 0 || viewHeight <= 0) return;

        var fitZoom = Math.Min(viewWidth / PlanCanvas.Width, viewHeight / PlanCanvas.Height);
        SetZoom(Math.Min(fitZoom, 1.0));
        PlanScrollViewer.Offset = new Avalonia.Vector(0, 0);
    }

    private void SetZoom(double level)
    {
        _zoomLevel = Math.Max(MinZoom, Math.Min(MaxZoom, level));
        _zoomTransform.ScaleX = _zoomLevel;
        _zoomTransform.ScaleY = _zoomLevel;
        ZoomLevelText.Text = $"{(int)(_zoomLevel * 100)}%";
        UpdateMinimapViewportBox();
    }

    /// <summary>
    /// Sets the zoom level and adjusts the scroll offset so that the content point
    /// under <paramref name="viewportAnchor"/> stays fixed in the viewport.
    /// </summary>
    private void SetZoomAtPoint(double level, Point viewportAnchor)
    {
        var newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, level));
        if (Math.Abs(newZoom - _zoomLevel) < 0.001)
            return;

        // Content point under the anchor at the current zoom level
        var contentX = (PlanScrollViewer.Offset.X + viewportAnchor.X) / _zoomLevel;
        var contentY = (PlanScrollViewer.Offset.Y + viewportAnchor.Y) / _zoomLevel;

        // Apply the new zoom
        SetZoom(newZoom);

        // Adjust offset so the same content point stays under the anchor
        var newOffsetX = Math.Max(0, contentX * _zoomLevel - viewportAnchor.X);
        var newOffsetY = Math.Max(0, contentY * _zoomLevel - viewportAnchor.Y);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlanScrollViewer.Offset = new Vector(newOffsetX, newOffsetY);
            UpdateMinimapViewportBox();
        });
    }

    private void PlanScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            var newLevel = _zoomLevel + (e.Delta.Y > 0 ? ZoomStep : -ZoomStep);
            SetZoomAtPoint(newLevel, e.GetPosition(PlanScrollViewer));
        }
    }

    private void PlanScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Don't intercept scrollbar interactions
        if (IsScrollBarAtPoint(e))
            return;

        var point = e.GetCurrentPoint(PlanScrollViewer);
        var isMiddle = point.Properties.IsMiddleButtonPressed;
        var isLeft = point.Properties.IsLeftButtonPressed;

        // Middle mouse always pans; left-click pans only on empty canvas (not on nodes)
        if (isMiddle || (isLeft && !IsNodeAtPoint(e)))
        {
            _isPanning = true;
            _panStart = point.Position;
            _panStartOffsetX = PlanScrollViewer.Offset.X;
            _panStartOffsetY = PlanScrollViewer.Offset.Y;
            PlanScrollViewer.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(PlanScrollViewer);
            e.Handled = true;
        }
    }

    private void PlanScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;

        var current = e.GetPosition(PlanScrollViewer);
        var dx = current.X - _panStart.X;
        var dy = current.Y - _panStart.Y;

        var newX = Math.Max(0, _panStartOffsetX - dx);
        var newY = Math.Max(0, _panStartOffsetY - dy);

        // Defer offset change so the ScrollViewer doesn't overwrite it during layout
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlanScrollViewer.Offset = new Vector(newX, newY);
            UpdateMinimapViewportBox();
        });

        e.Handled = true;
    }

    private void PlanScrollViewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        PlanScrollViewer.Cursor = Cursor.Default;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>Check if the pointer event originated from a node Border.</summary>
    private bool IsNodeAtPoint(PointerPressedEventArgs e)
    {
        // Walk up the visual tree from the source to see if we hit a node border
        var source = e.Source as Control;
        while (source != null && source != PlanCanvas)
        {
            if (source is Border b && _nodeBorderMap.ContainsKey(b))
                return true;
            source = source.Parent as Control;
        }
        return false;
    }

    /// <summary>Check if the pointer event originated from a ScrollBar.</summary>
    private bool IsScrollBarAtPoint(PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        while (source != null && source != PlanScrollViewer)
        {
            if (source is ScrollBar)
                return true;
            source = source.Parent as Control;
        }
        return false;
    }

    private async void SavePlan_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || string.IsNullOrEmpty(_currentPlan.RawXml)) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Plan",
            DefaultExtension = "sqlplan",
            SuggestedFileName = $"plan_{DateTime.Now:yyyyMMdd_HHmmss}.sqlplan",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("SQL Plan Files") { Patterns = new[] { "*.sqlplan" } },
                new FilePickerFileType("XML Files") { Patterns = new[] { "*.xml" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (file != null)
        {
            try
            {
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(_currentPlan.RawXml);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SavePlan failed: {ex.Message}");
                /* #452's mirror, the sixth site (spotted during the review sweep): pre-cutting to
                   60 characters threw away exactly the part of an I/O error that says what to fix.
                   Full message out; the tooltip carries whatever the label clips. */
                CostText.Text = $"Save failed: {ex.Message}";
                ToolTip.SetTip(CostText, ex.Message);
            }
        }
    }
}
