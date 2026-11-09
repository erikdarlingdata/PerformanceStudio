namespace PlanViewer.App.Controls;

/// <summary>
/// Whether the plan viewer's Plan Insights panel opens expanded. The panel's cards take up to 220 px
/// under the toolbar and over the plan graph, so in a viewer under <see cref="ExpandedFromHeight"/> px
/// an expanded panel leaves the graph less than half the height (285 px in a 600 px viewer, 165 px in
/// a 480 px one). Under that height the panel starts collapsed and the graph gets the room; the person
/// opens the panel when they want it. At that height or more it starts expanded, as it does in a
/// full-size window.
/// </summary>
public static class PlanInsightsLayout
{
    /// <summary>The viewer height, in px, from which the insights panel starts expanded.</summary>
    public const double ExpandedFromHeight = 640;

    /// <summary>True when a viewer <paramref name="viewerHeight"/> px tall opens the insights panel expanded.</summary>
    public static bool StartsExpanded(double viewerHeight) => viewerHeight >= ExpandedFromHeight;
}
