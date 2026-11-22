using PlanViewer.Core.Models;

namespace PlanViewer.Core.Services;

/// <summary>
/// Compares an operator's actual row count to its estimate when the estimate is per execution
/// and the actual is a running total — across every execution, or, in a parallel zone, across
/// every thread. Shared by the plan viewer's node label, edge color and minimap (App and Web)
/// and by the analyzer's row-estimate rules, so every one of them agrees on what "the estimate"
/// means for a node that runs more than once (#594).
/// </summary>
public static class RowEstimateHelper
{
    /// <summary>
    /// True when <paramref name="node"/> sits anywhere inside the second (inner) input of a
    /// Nested Loops join, or the third input of an Adaptive Join (build, hash probe, nested-loops
    /// inner side) — walking up through every ancestor the same way
    /// <c>PlanAnalyzer.Detection.CollectBareOuterReferences</c> does, so a nested-loops-within-
    /// loops chain still counts as inner. A node on this side runs once per outer row, so its
    /// estimate, which is per execution, has to be scaled by how often it actually ran
    /// (<see cref="GetActualExecutions"/>).
    /// </summary>
    public static bool IsInnerSideOfNestedLoops(PlanNode node) => FindInnerSide(node, stopAtExchange: false);

    /// <summary>
    /// How many times <paramref name="node"/> ran as a whole — the unit its EstimateRows is per.
    /// ActualExecutions is recorded per thread and summed, and what that sum means depends on
    /// where the node sits. Inside a nested loop's inner side, with no exchange in between, each
    /// thread runs complete executions for the outer rows it holds, so the sum is the count.
    /// Anywhere else in a parallel zone the threads share each execution: a DOP 8 scan reports one
    /// execution on each of eight threads and ran once, with its pages split between them. Every
    /// thread opens the node once per execution — N times when the whole parallel branch is
    /// re-run N times beneath a serial Nested Loops, through a Gather Streams on its inner side —
    /// so the most any one thread recorded is the count, and the sum would be N x DOP.
    /// </summary>
    public static long GetActualExecutions(PlanNode node)
    {
        if (node.PerThreadStats.Count <= 1 || FindInnerSide(node, stopAtExchange: true))
            return node.ActualExecutions;

        return node.PerThreadStats.Max(t => t.ActualExecutions);
    }

    /// <summary>
    /// EstimateRows is always per execution. ActualRows is the total across every execution — or,
    /// in a parallel zone, across every thread. Scaling EstimateRows by the executions only
    /// applies to a node that runs once per outer row (<see cref="IsInnerSideOfNestedLoops"/>),
    /// and then by the executions of the node as a whole (<see cref="GetActualExecutions"/>), not
    /// the per-thread sum: a parallel branch re-run under a serial loop would otherwise have its
    /// expected total inflated by DOP. Everywhere else the estimate stays at one execution.
    /// </summary>
    public static double GetExpectedRows(PlanNode node)
    {
        if (!IsInnerSideOfNestedLoops(node))
            return node.EstimateRows;

        var executions = GetActualExecutions(node);
        return executions > 0 ? node.EstimateRows * executions : node.EstimateRows;
    }

    /// <summary>
    /// Walks up from <paramref name="node"/> looking for a Nested Loops it is on the inner side of
    /// (or the third input of an Adaptive Join). With <paramref name="stopAtExchange"/>, gives up at
    /// the first Parallelism operator: the node is then on that exchange's producer side, run by a
    /// different set of threads than the join above it.
    /// </summary>
    private static bool FindInnerSide(PlanNode node, bool stopAtExchange)
    {
        var child = node;
        var ancestor = node.Parent;

        while (ancestor != null)
        {
            if (stopAtExchange && ancestor.PhysicalOp == "Parallelism")
                return false;

            if (ancestor.PhysicalOp == "Nested Loops" &&
                ancestor.Children.Count > 1 &&
                ancestor.Children[1] == child)
            {
                return true;
            }

            if (ancestor.PhysicalOp == "Adaptive Join" &&
                ancestor.Children.Count > 2 &&
                ancestor.Children[2] == child)
            {
                return true;
            }

            child = ancestor;
            ancestor = ancestor.Parent;
        }

        return false;
    }

    /// <summary>
    /// ActualRows / <see cref="GetExpectedRows"/>, with the same zero-estimate handling used
    /// throughout the viewer: no expected rows and no actual rows either is a match (1.0); no
    /// expected rows but some actual rows is an unbounded miss (double.MaxValue).
    /// </summary>
    public static double GetRowAccuracyRatio(PlanNode node)
    {
        var expectedRows = GetExpectedRows(node);
        return expectedRows > 0
            ? node.ActualRows / expectedRows
            : (node.ActualRows > 0 ? double.MaxValue : 1.0);
    }

    /// <summary>
    /// How often the optimizer expects <paramref name="node"/> to run: once, plus once per rebind
    /// and once per rewind. The Properties panel's Est. Executions (App and Web), the arrow
    /// tooltip's Estimated Number of Rows for All Executions and the HTML export all count it
    /// here, so an inner-side spool with 10 rebinds and 990 rewinds reads 1,001 runs on every
    /// surface (#677). The estimates are fractional, so the count is too.
    /// </summary>
    public static double GetEstimatedExecutions(PlanNode node) => 1 + node.EstimateRebinds + node.EstimateRewinds;

    /// <summary>
    /// The rows the optimizer expects <paramref name="node"/> to return across every run:
    /// <see cref="PlanNode.EstimateRows"/>, which is per execution, times
    /// <see cref="GetEstimatedExecutions"/>.
    /// </summary>
    public static double GetEstimatedRowsAllExecutions(PlanNode node) => node.EstimateRows * GetEstimatedExecutions(node);
}
