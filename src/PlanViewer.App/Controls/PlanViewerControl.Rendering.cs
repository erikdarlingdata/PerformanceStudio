using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
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
    private static void CountNodeWarnings(PlanNode node, ref int total, ref int critical)
    {
        total += node.Warnings.Count;
        critical += node.Warnings.Count(w => w.Severity == PlanWarningSeverity.Critical);
        foreach (var child in node.Children)
            CountNodeWarnings(child, ref total, ref critical);
    }

    private void RenderStatement(PlanStatement statement)
    {
        _currentStatement = statement;
        PlanCanvas.Children.Clear();
        _nodeBorderMap.Clear();
        _selectedNodeBorder = null;
        _selectedNode = null;

        if (statement.RootNode == null) return;

        // Layout
        PlanLayoutEngine.Layout(statement);
        var (width, height) = PlanLayoutEngine.GetExtents(statement.RootNode);
        PlanCanvas.Width = width;
        PlanCanvas.Height = height;

        // Render edges first (behind nodes)
        var divergenceLimit = Math.Max(2.0, AppSettingsService.Load().AccuracyRatioDivergenceLimit);
        RenderEdges(statement.RootNode, divergenceLimit);

        // Render nodes — pass total warning count to root node for badge
        var allWarnings = new List<PlanWarning>();
        CollectWarnings(statement.RootNode, allWarnings);
        RenderNodes(statement.RootNode, divergenceLimit, allWarnings.Count);

        // Update banners
        ShowMissingIndexes(statement.MissingIndexes);
        ShowParameters(statement);
        ShowWaitStats(statement.WaitStats, statement.WaitBenefits, statement.QueryTimeStats != null);
        ShowRuntimeSummary(statement);
        UpdateInsightsHeader();

        // Scroll to top-left so the plan root is immediately visible
        PlanScrollViewer.Offset = new Avalonia.Vector(0, 0);

        // Canvas-level context menu (zoom, advice, repro, save)
        // Set on ScrollViewer, not Canvas — Canvas has no background so it's not hit-testable
        PlanScrollViewer.ContextMenu = BuildCanvasContextMenu();

        CostText.Text = "";

        // Update minimap if visible
        if (MinimapPanel.IsVisible)
            Avalonia.Threading.Dispatcher.UIThread.Post(RenderMinimap, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void RenderNodes(PlanNode node, double divergenceLimit, int totalWarningCount = -1)
    {
        var visual = CreateNodeVisual(node, divergenceLimit, totalWarningCount);
        Canvas.SetLeft(visual, node.X);
        Canvas.SetTop(visual, node.Y);
        PlanCanvas.Children.Add(visual);

        foreach (var child in node.Children)
            RenderNodes(child, divergenceLimit);
    }

    private Border CreateNodeVisual(PlanNode node, double divergenceLimit, int totalWarningCount = -1)
    {
        var isExpensive = node.IsExpensive;

        var bgBrush = isExpensive
            ? new SolidColorBrush(Color.FromArgb(0x30, 0xE5, 0x73, 0x73))
            : FindBrushResource("BackgroundLightBrush");

        var borderBrush = isExpensive
            ? OrangeRedBrush
            : FindBrushResource("BorderBrush");

        var border = new Border
        {
            Width = PlanLayoutEngine.NodeWidth,
            MinHeight = PlanLayoutEngine.NodeHeightMin,
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(isExpensive ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // Map border to node (replaces WPF Tag)
        _nodeBorderMap[border] = node;

        // Tooltip — root node gets all collected warnings so the tooltip shows them
        if (totalWarningCount > 0)
        {
            var allWarnings = new List<PlanWarning>();
            if (_currentStatement != null)
                allWarnings.AddRange(_currentStatement.PlanWarnings);
            CollectWarnings(node, allWarnings);
            ToolTip.SetTip(border, BuildNodeTooltipContent(node, allWarnings));
        }
        else
        {
            ToolTip.SetTip(border, BuildNodeTooltipContent(node));
        }

        // Click to select + show properties
        border.PointerPressed += Node_Click;

        // Right-click context menu
        border.ContextMenu = BuildNodeContextMenu(node);

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        // Icon row: icon + optional warning/parallel indicators
        var iconRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var iconBitmap = IconHelper.LoadIcon(node.IconName);
        if (iconBitmap != null)
        {
            iconRow.Children.Add(new Image
            {
                Source = iconBitmap,
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        // Warning indicator badge (orange triangle with !)
        if (node.HasWarnings)
        {
            var warnBadge = new Grid
            {
                Width = 20, Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            warnBadge.Children.Add(new AvaloniaPath
            {
                Data = StreamGeometry.Parse("M 10,0 L 20,18 L 0,18 Z"),
                Fill = OrangeBrush
            });
            warnBadge.Children.Add(new TextBlock
            {
                Text = "!",
                FontSize = 12,
                FontWeight = FontWeight.ExtraBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });
            iconRow.Children.Add(warnBadge);
        }

        // Parallel indicator badge (amber circle with arrows)
        if (node.Parallel)
        {
            var parBadge = new Grid
            {
                Width = 20, Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            parBadge.Children.Add(new Ellipse
            {
                Width = 20, Height = 20,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))
            });
            parBadge.Children.Add(new TextBlock
            {
                Text = "\u21C6",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            iconRow.Children.Add(parBadge);
        }

        // Nonclustered index count badge (modification operators maintaining multiple NC indexes)
        if (node.NonClusteredIndexCount > 0)
        {
            var ncBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x6C, 0x75, 0x7D)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"+{node.NonClusteredIndexCount} NC",
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White
                }
            };
            iconRow.Children.Add(ncBadge);
        }

        stack.Children.Add(iconRow);

        // Operator name
        var fgBrush = FindBrushResource("ForegroundBrush");

        // Operator name — for exchanges, show "Parallelism" + "(Gather Streams)" etc.
        var opLabel = node.PhysicalOp;
        if (node.PhysicalOp == "Parallelism" && !string.IsNullOrEmpty(node.LogicalOp)
            && node.LogicalOp != "Parallelism")
        {
            opLabel = $"Parallelism\n({node.LogicalOp})";
        }
        stack.Children.Add(new TextBlock
        {
            Text = opLabel,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = fgBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = PlanLayoutEngine.NodeWidth - 16,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        // Cost percentage — only highlight in estimated plans; actual plans use duration/CPU colors
        IBrush costColor = !node.HasActualStats && node.CostPercent >= 50 ? OrangeRedBrush
            : !node.HasActualStats && node.CostPercent >= 25 ? OrangeBrush
            : fgBrush;

        stack.Children.Add(new TextBlock
        {
            Text = $"Cost: {node.CostPercent}%",
            FontSize = 10,
            Foreground = costColor,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        // Actual plan stats: elapsed time, CPU time, and row counts
        if (node.HasActualStats)
        {
            // Compute own time (subtract children in row mode)
            var ownElapsedMs = GetOwnElapsedMs(node);
            var ownCpuMs = GetOwnCpuMs(node);

            // Elapsed time -- color based on own time, not cumulative
            var ownElapsedSec = ownElapsedMs / 1000.0;
            IBrush elapsedBrush = ownElapsedSec >= 1.0 ? OrangeRedBrush
                : ownElapsedSec >= 0.1 ? OrangeBrush : fgBrush;
            stack.Children.Add(new TextBlock
            {
                Text = $"{ownElapsedSec:F3}s",
                FontSize = 10,
                Foreground = elapsedBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // CPU time -- color based on own time
            var ownCpuSec = ownCpuMs / 1000.0;
            IBrush cpuBrush = ownCpuSec >= 1.0 ? OrangeRedBrush
                : ownCpuSec >= 0.1 ? OrangeBrush : fgBrush;
            stack.Children.Add(new TextBlock
            {
                Text = $"CPU: {ownCpuSec:F3}s",
                FontSize = 10,
                Foreground = cpuBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Actual rows of Estimated rows (accuracy %) -- red if off by divergence limit
            var estRows = node.EstimateRows;
            var accuracyRatio = estRows > 0 ? node.ActualRows / estRows : (node.ActualRows > 0 ? double.MaxValue : 1.0);
            IBrush rowBrush = (accuracyRatio < 1.0 / divergenceLimit || accuracyRatio > divergenceLimit) ? OrangeRedBrush : fgBrush;
            var accuracy = estRows > 0
                ? $" ({accuracyRatio * 100:F0}%)"
                : "";
            stack.Children.Add(new TextBlock
            {
                Text = $"{node.ActualRows:N0} of {estRows:N0}{accuracy}",
                FontSize = 10,
                Foreground = rowBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = PlanLayoutEngine.NodeWidth - 16
            });
        }

        // Object name -- show full object name, wrap if needed
        if (!string.IsNullOrEmpty(node.ObjectName))
        {
            var objBlock = new TextBlock
            {
                Text = node.FullObjectName ?? node.ObjectName,
                FontSize = 10,
                Foreground = fgBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = PlanLayoutEngine.NodeWidth - 16,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(objBlock);
        }

        // Total warning count badge on root node
        if (totalWarningCount > 0)
        {
            var badgeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            badgeRow.Children.Add(new TextBlock
            {
                Text = "\u26A0",
                FontSize = 13,
                Foreground = OrangeBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            badgeRow.Children.Add(new TextBlock
            {
                Text = $"{totalWarningCount} warning{(totalWarningCount == 1 ? "" : "s")}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = OrangeBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(badgeRow);
        }

        border.Child = stack;
        return border;
    }

    private void RenderEdges(PlanNode node, double divergenceLimit)
    {
        foreach (var child in node.Children)
        {
            var path = CreateElbowConnector(node, child, divergenceLimit);
            PlanCanvas.Children.Add(path);

            RenderEdges(child, divergenceLimit);
        }
    }

    /// <summary>
    /// Returns a color brush for a link based on the accuracy ratio of the child node.
    /// Only applies to actual plans; estimated plans use the default edge brush.
    /// </summary>
    private static IBrush GetLinkColorBrush(PlanNode child, double divergenceLimit)
    {
        if (!child.HasActualStats)
            return EdgeBrush;

        divergenceLimit = Math.Max(2.0, divergenceLimit);
        var estRows = child.EstimateRows;
        var accuracyRatio = estRows > 0
            ? child.ActualRows / estRows
            : (child.ActualRows > 0 ? double.MaxValue : 1.0);

        // Within the neutral band — keep default color
        if (accuracyRatio >= 1.0 / divergenceLimit && accuracyRatio <= divergenceLimit)
            return EdgeBrush;

        // Underestimated bands (accuracyRatio > 1 means more actual rows than estimated)
        if (accuracyRatio > divergenceLimit)
        {
            if (accuracyRatio >= divergenceLimit * 100)
                return LinkFluoRedBrush;
            if (accuracyRatio >= divergenceLimit * 10)
                return LinkFluoOrangeBrush;
            return LinkLightOrangeBrush;
        }

        // Overestimated bands (accuracyRatio < 1 means fewer actual rows than estimated)
        if (accuracyRatio < 1.0 / (divergenceLimit * 100))
            return LinkFluoBlueBrush;
        if (accuracyRatio < 1.0 / (divergenceLimit * 10))
            return LinkLightBlueBrush;
        return LinkBlueBrush;
    }

    private AvaloniaPath CreateElbowConnector(PlanNode parent, PlanNode child, double divergenceLimit)
    {
        var parentRight = parent.X + PlanLayoutEngine.NodeWidth;
        var parentCenterY = parent.Y + PlanLayoutEngine.GetNodeHeight(parent) / 2;
        var childLeft = child.X;
        var childCenterY = child.Y + PlanLayoutEngine.GetNodeHeight(child) / 2;

        // Arrow thickness based on row estimate (logarithmic)
        var rows = child.HasActualStats ? child.ActualRows : child.EstimateRows;
        var thickness = Math.Max(2, Math.Min(Math.Floor(Math.Log(Math.Max(1, rows))), 12));

        var midX = (parentRight + childLeft) / 2;

        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(parentRight, parentCenterY),
            IsClosed = false
        };
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
        ToolTip.SetTip(path, BuildEdgeTooltipContent(child));
        return path;
    }

    private object BuildEdgeTooltipContent(PlanNode child)
    {
        var panel = new StackPanel { MinWidth = 240 };

        void AddRow(string label, string value)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var lbl = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontSize = 12,
                Margin = new Thickness(0, 1, 12, 1)
            };
            var val = new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 1, 0, 1)
            };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(val, 1);
            row.Children.Add(lbl);
            row.Children.Add(val);
            panel.Children.Add(row);
        }

        if (child.HasActualStats)
            AddRow("Actual Number of Rows for All Executions", $"{child.ActualRows:N0}");

        AddRow("Estimated Number of Rows Per Execution", $"{child.EstimateRows:N0}");

        var executions = 1.0 + child.EstimateRebinds + child.EstimateRewinds;
        var estimatedRowsAllExec = child.EstimateRows * executions;
        AddRow("Estimated Number of Rows for All Executions", $"{estimatedRowsAllExec:N0}");

        if (child.EstimatedRowSize > 0)
        {
            AddRow("Estimated Row Size", FormatBytes(child.EstimatedRowSize));
            var dataSize = estimatedRowsAllExec * child.EstimatedRowSize;
            AddRow("Estimated Data Size", FormatBytes(dataSize));
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(4),
            Child = panel
        };
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:N0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024 * 1024):N0} MB";
        return $"{bytes / (1024L * 1024 * 1024):N1} GB";
    }

    private static string FormatBenefitPercent(double pct) =>
        pct >= 100 ? $"{pct:N0}" : $"{pct:N1}";

    private static bool HasSpillInPlanTree(PlanNode node)
    {
        foreach (var w in node.Warnings)
            if (w.WarningType.EndsWith(" Spill", StringComparison.Ordinal)) return true;
        foreach (var child in node.Children)
            if (HasSpillInPlanTree(child)) return true;
        return false;
    }

    private static void CollectWarnings(PlanNode node, List<PlanWarning> warnings)
    {
        warnings.AddRange(node.Warnings);
        foreach (var child in node.Children)
            CollectWarnings(child, warnings);
    }

    private IBrush FindBrushResource(string key)
    {
        if (this.TryFindResource(key, out var resource) && resource is IBrush brush)
            return brush;

        // Fallback brushes in case resources are not found
        return key switch
        {
            "BackgroundLightBrush" => new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E)),
            "BorderBrush" => new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x45)),
            "ForegroundBrush" => new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xEB)),
            "ForegroundMutedBrush" => new SolidColorBrush(Color.FromRgb(0xB0, 0xB6, 0xC0)),
            _ => Brushes.White
        };
    }
}
