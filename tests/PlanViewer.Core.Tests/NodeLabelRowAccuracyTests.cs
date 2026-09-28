using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #594: the node label compared an operator's total actual rows to its PER-EXECUTION estimate
/// with no adjustment for how many times the operator ran — correct for a node that only ran
/// once, wildly wrong for one on the inner side of a Nested Loops join. eager_index_spool_plan
/// is the fixture the coordinator worked the numbers from (DOP 8, every RelOp Parallel="true").
///
/// <para>Node 0 (Gather Streams) and Node 1 (the Nested Loops join itself) are not inner-side —
/// both read "609 of 2,983 (20%)", unchanged by the fix. Nodes 4 and 5 (Top and Index Spool) are
/// both inner-side of Node 1 and share identical per-thread counters in this fixture, so both
/// should read "609 of 613 (99%)" — not the "609 of 1 (60,900%)" the old per-execution-blind math
/// showed.</para>
/// </summary>
public class NodeLabelRowAccuracyTests
{
    [Fact]
    public void EagerIndexSpoolPlan_NodeLabels_CompareActualRowsToExecutionAwareEstimate()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("eager_index_spool_plan.sqlplan");
            var window = new Window { Content = viewer, Width = 1600, Height = 1000 };
            window.Show();
            window.UpdateLayout();

            var labels = viewer.GetLogicalDescendants().OfType<TextBlock>()
                .Where(t => t.Text != null && t.Text.StartsWith("609 of "))
                .ToList();

            // Node 0 (Gather Streams) and Node 1 (the join) — not inner-side, unaffected by #594.
            var outerLabels = labels.Where(t => t.Text!.StartsWith("609 of 2,983")).ToList();
            Assert.Equal(2, outerLabels.Count);
            Assert.All(outerLabels, t => Assert.Contains("(20%)", t.Text));

            // Node 4 (Top) and Node 5 (Index Spool) — both inner-side of Node 1.
            var innerLabels = labels.Where(t => t.Text!.StartsWith("609 of 613")).ToList();
            Assert.Equal(2, innerLabels.Count);
            Assert.All(innerLabels, t => Assert.Contains("(99%)", t.Text));

            // Neutral color, matching the outer nodes (well within the divergence band) — not the
            // OrangeRed the pre-fix "609 of 1 (60,900%)" reading would have painted it.
            Assert.All(innerLabels, t => Assert.Equal(outerLabels[0].Foreground, t.Foreground));

            // The bug's own worked example: nothing in this plan reads the old, per-execution-
            // blind "609 of 1" — every 609-actual node compares against an execution-aware total.
            Assert.DoesNotContain(labels, t => t.Text!.StartsWith("609 of 1 ") || t.Text == "609 of 1");
        });
    }

    private static PlanViewerControl LoadPlan(string planFileName)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, planFileName), $"Plan failed to load: {viewer.LastLoadError}");
        return viewer;
    }
}
