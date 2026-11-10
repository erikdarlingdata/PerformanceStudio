using System;

namespace PlanViewer.App.Controls;

/// <summary>
/// How the plan viewer's Statements panel, plan canvas and Properties panel share the viewer's width. Each panel opens at
/// the width it asks for: 640 px for the Statements panel, or the width it was dragged to, and the remembered width for the
/// Properties panel. At their defaults the two and their splitters take 1,031 px, which leaves the canvas nothing in a
/// 900 px viewer and pushes the Properties panel past the viewer's edge. When the widths asked for leave the canvas less
/// than <see cref="MinCanvasWidth"/> px, the Statements panel narrows first, no further than
/// <see cref="MinStatementsWidth"/> px, and then the Properties panel, no further than its own minimum. A viewer narrower
/// than the three minimums gives the canvas what the two panels leave at theirs.
/// Ported from erikdarlingdata/PerformanceMonitor#8538.
/// </summary>
internal static class PlanPanelLayout
{
    /// <summary>The width, in px, the Statements panel opens at.</summary>
    public const double DefaultStatementsWidth = 640;

    /// <summary>The width, in px, the Statements panel is not narrowed below to make room for the canvas.</summary>
    public const double MinStatementsWidth = 200;

    /// <summary>The canvas width, in px, the panels make room for: an operator's box after the layout's padding, and a scroll bar.</summary>
    public const double MinCanvasWidth = 240;

    /// <summary>The width, in px, of the splitter beside the open Statements panel.</summary>
    public const double StatementsSplitterWidth = 5;

    /// <summary>
    /// The widths the two panels take in a viewer <paramref name="viewerWidth"/> px wide, and the minimum the canvas column
    /// holds. A null width is a closed panel, which takes no width and has no splitter. A panel asked for at less than its
    /// minimum keeps the width asked for.
    /// </summary>
    public static PlanPanelWidths Fit(
        double viewerWidth, double? statementsWidth, double? propertiesWidth, double minPropertiesWidth, double propertiesSplitterWidth)
    {
        var statements = statementsWidth ?? 0;
        var properties = propertiesWidth ?? 0;
        var splitters = (statementsWidth is null ? 0 : StatementsSplitterWidth) + (propertiesWidth is null ? 0 : propertiesSplitterWidth);

        var over = statements + properties + splitters + MinCanvasWidth - viewerWidth;
        if (over > 0)
        {
            var given = Math.Min(over, Math.Max(0, statements - MinStatementsWidth));
            statements -= given;
            over -= given;
        }

        if (over > 0)
            properties -= Math.Min(over, Math.Max(0, properties - minPropertiesWidth));

        var canvas = Math.Max(0, viewerWidth - statements - properties - splitters);
        return new PlanPanelWidths(statements, properties, Math.Min(MinCanvasWidth, canvas));
    }
}

/// <summary>The widths, in px, of the Statements panel and the Properties panel, and the minimum width the canvas column holds.</summary>
internal readonly record struct PlanPanelWidths(double Statements, double Properties, double CanvasMinimum);
