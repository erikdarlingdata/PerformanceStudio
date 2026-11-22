using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The plan viewer's Plan Insights panel starts collapsed in a viewer too short to give the plan graph
/// most of the height with the panel open, and expanded in a full-size one. The rendered side of the
/// rule is in <see cref="InsightsStripLayoutTests"/>; this pins the threshold itself.
/// </summary>
public class PlanInsightsLayoutTests
{
    [Theory]
    [InlineData(480, false)]
    [InlineData(600, false)]
    [InlineData(639.5, false)]
    [InlineData(640, true)]
    [InlineData(800, true)]
    public void TheInsightsPanel_StartsCollapsedInAShortViewer_AndExpandedInATallOne(double viewerHeight, bool expanded)
    {
        Assert.Equal(expanded, PlanInsightsLayout.StartsExpanded(viewerHeight));
    }
}
