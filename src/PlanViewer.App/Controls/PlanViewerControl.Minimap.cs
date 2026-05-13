using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PlanViewer.App.Helpers;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace PlanViewer.App.Controls;

public partial class PlanViewerControl : UserControl
{
    private void MinimapToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (MinimapPanel.IsVisible)
            CloseMinimapPanel();
        else
            OpenMinimapPanel();
    }

    private void MinimapClose_Click(object? sender, RoutedEventArgs e)
    {
        CloseMinimapPanel();
    }

    private void OpenMinimapPanel()
    {
        MinimapPanel.Width = _minimapWidth;
        MinimapPanel.Height = _minimapHeight;
        MinimapPanel.IsVisible = true;
        RenderMinimap();
    }

    private void CloseMinimapPanel()
    {
        MinimapPanel.IsVisible = false;
        _minimapDragging = false;
        _minimapResizing = false;
    }

    private void RenderMinimap()
    {
        MinimapCanvas.Children.Clear();
        _minimapNodeMap.Clear();
        _minimapViewportBox = null;
        _minimapSelectedNode = null;

        // Guard: don't render if the panel was closed between a deferred post and execution
        if (!MinimapPanel.IsVisible) return;

        if (_currentStatement?.RootNode == null || PlanCanvas.Width <= 0 || PlanCanvas.Height <= 0)
            return;

        var canvasW = MinimapCanvas.Bounds.Width;
        var canvasH = MinimapCanvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0)
        {
            // Defer until layout is ready
            Avalonia.Threading.Dispatcher.UIThread.Post(RenderMinimap, Avalonia.Threading.DispatcherPriority.Loaded);
            return;
        }

        var scaleX = canvasW / PlanCanvas.Width;
        var scaleY = canvasH / PlanCanvas.Height;
        var scale = Math.Min(scaleX, scaleY);

        // Cache the non-expensive node border brush for this render cycle
        _minimapNodeBorderBrushCache = FindBrushResource("ForegroundBrush") is SolidColorBrush fg
            ? new SolidColorBrush(Color.FromArgb(0x80, fg.Color.R, fg.Color.G, fg.Color.B))
            : FindBrushResource("BorderBrush");

        // Render branch areas with transparent colored backgrounds
        RenderMinimapBranches(_currentStatement.RootNode, scale);

        // Render edges
        var minimapDivergenceLimit = Math.Max(2.0, AppSettingsService.Load().AccuracyRatioDivergenceLimit);
        RenderMinimapEdges(_currentStatement.RootNode, scale, minimapDivergenceLimit);

        // Render nodes
        RenderMinimapNodes(_currentStatement.RootNode, scale);

        // Render viewport indicator
        RenderMinimapViewportBox(scale);

        // Re-apply selection highlight if a node is selected
        if (_selectedNode != null)
            UpdateMinimapSelection(_selectedNode);
    }

    private void RenderMinimapBranches(PlanNode root, double scale)
    {

        for (int i = 0; i < root.Children.Count; i++)
        {
            var child = root.Children[i];
            var color = MinimapBranchColors[i % MinimapBranchColors.Length];

            // Collect bounds of all nodes in this subtree
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            CollectSubtreeBounds(child, ref minX, ref minY, ref maxX, ref maxY);

            var rect = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = (maxX - minX + PlanLayoutEngine.NodeWidth) * scale + 4,
                Height = (maxY - minY + PlanLayoutEngine.GetNodeHeight(child)) * scale + 4,
                Fill = new SolidColorBrush(color),
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(rect, minX * scale - 2);
            Canvas.SetTop(rect, minY * scale - 2);
            MinimapCanvas.Children.Add(rect);
        }
    }

    private static void CollectSubtreeBounds(PlanNode node, ref double minX, ref double minY, ref double maxX, ref double maxY)
    {
        if (node.X < minX) minX = node.X;
        if (node.Y < minY) minY = node.Y;
        if (node.X > maxX) maxX = node.X;
        var bottom = node.Y + PlanLayoutEngine.GetNodeHeight(node);
        if (bottom > maxY) maxY = bottom;

        foreach (var child in node.Children)
            CollectSubtreeBounds(child, ref minX, ref minY, ref maxX, ref maxY);
    }

    private void RenderMinimapEdges(PlanNode node, double scale, double divergenceLimit)
    {
        foreach (var child in node.Children)
        {
            var parentRight = (node.X + PlanLayoutEngine.NodeWidth) * scale;
            var parentCenterY = (node.Y + PlanLayoutEngine.GetNodeHeight(node) / 2) * scale;
            var childLeft = child.X * scale;
            var childCenterY = (child.Y + PlanLayoutEngine.GetNodeHeight(child) / 2) * scale;
            var midX = (parentRight + childLeft) / 2;

            // Proportional thickness matching the plan viewer (logarithmic, scaled down)
            var rows = child.HasActualStats ? child.ActualRows : child.EstimateRows;
            var fullThickness = Math.Max(2, Math.Min(Math.Floor(Math.Log(Math.Max(1, rows))), 12));
            var thickness = Math.Max(0.5, fullThickness * scale);

            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(parentRight, parentCenterY), IsClosed = false };
            figure.Segments!.Add(new LineSegment { Point = new Point(midX, parentCenterY) });
            figure.Segments.Add(new LineSegment { Point = new Point(midX, childCenterY) });
            figure.Segments.Add(new LineSegment { Point = new Point(childLeft, childCenterY) });
            geometry.Figures!.Add(figure);

            var linkBrush = GetLinkColorBrush(child, divergenceLimit);

            var path = new AvaloniaPath
            {
                Data = geometry,
                Stroke = linkBrush,
                StrokeThickness = thickness,
                StrokeJoin = PenLineJoin.Round
            };
            MinimapCanvas.Children.Add(path);

            RenderMinimapEdges(child, scale, divergenceLimit);
        }
    }

    private void RenderMinimapNodes(PlanNode node, double scale)
    {
        var w = PlanLayoutEngine.NodeWidth * scale;
        var h = PlanLayoutEngine.GetNodeHeight(node) * scale;
        // Use theme background colors with transparency
        var bgBrush = node.IsExpensive
            ? MinimapExpensiveNodeBgBrush
            : FindBrushResource("BackgroundLightBrush");
        var borderBrush = node.IsExpensive ? OrangeRedBrush : _minimapNodeBorderBrushCache;

        var border = new Border
        {
            Width = Math.Max(4, w),
            Height = Math.Max(4, h),
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(1)
        };

        // Show a small icon inside the node if space allows
        var iconBitmap = IconHelper.LoadIcon(node.IconName);
        if (iconBitmap != null)
        {
            var iconSize = Math.Min(Math.Min(w * 0.7, h * 0.7), 16);
            if (iconSize >= 6)
            {
                border.Child = new Image
                {
                    Source = iconBitmap,
                    Width = iconSize,
                    Height = iconSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        Canvas.SetLeft(border, node.X * scale);
        Canvas.SetTop(border, node.Y * scale);
        MinimapCanvas.Children.Add(border);

        _minimapNodeMap[border] = node;

        foreach (var child in node.Children)
            RenderMinimapNodes(child, scale);
    }

    private void RenderMinimapViewportBox(double scale)
    {
        var viewW = PlanScrollViewer.Bounds.Width;
        var viewH = PlanScrollViewer.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return;

        var contentW = PlanCanvas.Width * _zoomLevel;
        var contentH = PlanCanvas.Height * _zoomLevel;

        var boxW = Math.Min(viewW / contentW, 1.0) * PlanCanvas.Width * scale;
        var boxH = Math.Min(viewH / contentH, 1.0) * PlanCanvas.Height * scale;
        var boxX = (PlanScrollViewer.Offset.X / _zoomLevel) * scale;
        var boxY = (PlanScrollViewer.Offset.Y / _zoomLevel) * scale;

        var accentColor = FindBrushResource("AccentBrush") is SolidColorBrush ab
            ? ab.Color
            : Color.FromRgb(0x2E, 0xAE, 0xF1);
        var themeBrush = new SolidColorBrush(Color.FromArgb(0x40, accentColor.R, accentColor.G, accentColor.B));
        var borderBrush = new SolidColorBrush(Color.FromArgb(0xB0, accentColor.R, accentColor.G, accentColor.B));

        _minimapViewportBox = new Border
        {
            Width = Math.Max(4, boxW),
            Height = Math.Max(4, boxH),
            Background = themeBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(1),
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        Canvas.SetLeft(_minimapViewportBox, boxX);
        Canvas.SetTop(_minimapViewportBox, boxY);
        MinimapCanvas.Children.Add(_minimapViewportBox);
    }

    private void UpdateMinimapViewportBox()
    {
        if (!MinimapPanel.IsVisible || _minimapViewportBox == null || _currentStatement?.RootNode == null)
            return;
        if (PlanCanvas.Width <= 0 || PlanCanvas.Height <= 0) return;

        var canvasW = MinimapCanvas.Bounds.Width;
        var canvasH = MinimapCanvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0) return;

        var scaleX = canvasW / PlanCanvas.Width;
        var scaleY = canvasH / PlanCanvas.Height;
        var scale = Math.Min(scaleX, scaleY);

        var viewW = PlanScrollViewer.Bounds.Width;
        var viewH = PlanScrollViewer.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return;

        var contentW = PlanCanvas.Width * _zoomLevel;
        var contentH = PlanCanvas.Height * _zoomLevel;

        _minimapViewportBox.Width = Math.Max(4, Math.Min(viewW / contentW, 1.0) * PlanCanvas.Width * scale);
        _minimapViewportBox.Height = Math.Max(4, Math.Min(viewH / contentH, 1.0) * PlanCanvas.Height * scale);
        Canvas.SetLeft(_minimapViewportBox, (PlanScrollViewer.Offset.X / _zoomLevel) * scale);
        Canvas.SetTop(_minimapViewportBox, (PlanScrollViewer.Offset.Y / _zoomLevel) * scale);
    }

    private double GetMinimapScale()
    {
        if (PlanCanvas.Width <= 0 || PlanCanvas.Height <= 0) return 1;
        var canvasW = MinimapCanvas.Bounds.Width;
        var canvasH = MinimapCanvas.Bounds.Height;
        if (canvasW <= 0 || canvasH <= 0) return 1;
        return Math.Min(canvasW / PlanCanvas.Width, canvasH / PlanCanvas.Height);
    }

    private void UpdateMinimapSelection(PlanNode node)
    {
        if (!MinimapPanel.IsVisible) return;

        // Reset previous selection highlight
        if (_minimapSelectedNode != null)
        {
            var prevNode = _minimapNodeMap.GetValueOrDefault(_minimapSelectedNode);
            _minimapSelectedNode.BorderBrush = prevNode is { IsExpensive: true }
                ? OrangeRedBrush
                : _minimapNodeBorderBrushCache;
            _minimapSelectedNode.BorderThickness = new Thickness(0.5);
            _minimapSelectedNode = null;
        }

        // Find and highlight the new node
        foreach (var (border, n) in _minimapNodeMap)
        {
            if (n == node)
            {
                border.BorderBrush = SelectionBrush;
                border.BorderThickness = new Thickness(2);
                _minimapSelectedNode = border;
                break;
            }
        }
    }

    private void MinimapCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(MinimapCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;

        var pos = point.Position;
        var scale = GetMinimapScale();

        // Check if clicking on a node (single click = center, double click = zoom)
        if (e.ClickCount == 2)
        {
            // Double click: find node under pointer and zoom to it
            var node = FindMinimapNodeAt(pos);
            if (node != null)
            {
                ZoomToNode(node);
                e.Handled = true;
                return;
            }
        }

        if (e.ClickCount == 1)
        {
            // Check if over a minimap node for single-click centering
            var node = FindMinimapNodeAt(pos);
            if (node != null)
            {
                CenterOnNode(node);
                e.Handled = true;
                return;
            }
        }

        // Start viewport box drag
        _minimapDragging = true;

        // Move viewport center to click position
        ScrollPlanViewerToMinimapPoint(pos, scale);

        e.Pointer.Capture(MinimapCanvas);
        e.Handled = true;
    }

    private void MinimapCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_minimapDragging) return;

        var pos = e.GetPosition(MinimapCanvas);
        var scale = GetMinimapScale();
        ScrollPlanViewerToMinimapPoint(pos, scale);
        e.Handled = true;
    }

    private void MinimapCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_minimapDragging) return;
        _minimapDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ScrollPlanViewerToMinimapPoint(Point minimapPoint, double scale)
    {
        if (scale <= 0) return;
        // Convert minimap coords to plan content coords
        var contentX = minimapPoint.X / scale;
        var contentY = minimapPoint.Y / scale;

        // Center the viewport on this content point
        var viewW = PlanScrollViewer.Bounds.Width;
        var viewH = PlanScrollViewer.Bounds.Height;
        var offsetX = Math.Max(0, contentX * _zoomLevel - viewW / 2);
        var offsetY = Math.Max(0, contentY * _zoomLevel - viewH / 2);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlanScrollViewer.Offset = new Vector(offsetX, offsetY);
        });
    }

    private PlanNode? FindMinimapNodeAt(Point pos)
    {
        foreach (var (border, node) in _minimapNodeMap)
        {
            var left = Canvas.GetLeft(border);
            var top = Canvas.GetTop(border);
            if (pos.X >= left && pos.X <= left + border.Width &&
                pos.Y >= top && pos.Y <= top + border.Height)
                return node;
        }
        return null;
    }

    private void CenterOnNode(PlanNode node)
    {
        var nodeW = PlanLayoutEngine.NodeWidth;
        var nodeH = PlanLayoutEngine.GetNodeHeight(node);
        var viewW = PlanScrollViewer.Bounds.Width;
        var viewH = PlanScrollViewer.Bounds.Height;
        var centerX = (node.X + nodeW / 2) * _zoomLevel - viewW / 2;
        var centerY = (node.Y + nodeH / 2) * _zoomLevel - viewH / 2;
        centerX = Math.Max(0, centerX);
        centerY = Math.Max(0, centerY);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlanScrollViewer.Offset = new Vector(centerX, centerY);
        });
    }

    private void ZoomToNode(PlanNode node)
    {
        var viewW = PlanScrollViewer.Bounds.Width;
        var viewH = PlanScrollViewer.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return;

        var nodeW = PlanLayoutEngine.NodeWidth;
        var nodeH = PlanLayoutEngine.GetNodeHeight(node);

        // Zoom so the node takes about 1/3 of the viewport
        var fitZoom = Math.Min(viewW / (nodeW * 3), viewH / (nodeH * 3));
        fitZoom = Math.Max(MinZoom, Math.Min(MaxZoom, fitZoom));
        SetZoom(fitZoom);

        // Center on the node
        var centerX = (node.X + nodeW / 2) * _zoomLevel - viewW / 2;
        var centerY = (node.Y + nodeH / 2) * _zoomLevel - viewH / 2;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PlanScrollViewer.Offset = new Vector(Math.Max(0, centerX), Math.Max(0, centerY));
        });

        // Also select the node in the plan
        foreach (var (border, n) in _nodeBorderMap)
        {
            if (n == node)
            {
                SelectNode(border, node);
                break;
            }
        }
    }

    private void MinimapResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(MinimapPanel);
        if (!point.Properties.IsLeftButtonPressed) return;
        _minimapResizing = true;
        _minimapResizeStart = point.Position;
        _minimapResizeStartW = MinimapPanel.Width;
        _minimapResizeStartH = MinimapPanel.Height;
        e.Pointer.Capture((Control)sender!);
        e.Handled = true;
    }

    private void MinimapResizeGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_minimapResizing) return;
        var current = e.GetPosition(MinimapPanel);
        var dx = current.X - _minimapResizeStart.X;
        var dy = current.Y - _minimapResizeStart.Y;
        var newW = Math.Max(MinimapMinSize, Math.Min(MinimapMaxSize, _minimapResizeStartW + dx));
        var newH = Math.Max(MinimapMinSize, Math.Min(MinimapMaxSize, _minimapResizeStartH + dy));
        MinimapPanel.Width = newW;
        MinimapPanel.Height = newH;
        _minimapWidth = newW;
        _minimapHeight = newH;
        e.Handled = true;

        // Re-render after resize
        Avalonia.Threading.Dispatcher.UIThread.Post(RenderMinimap, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void MinimapResizeGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_minimapResizing) return;
        _minimapResizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        RenderMinimap();
    }
}
