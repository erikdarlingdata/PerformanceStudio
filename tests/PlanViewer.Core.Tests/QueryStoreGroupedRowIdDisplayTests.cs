using PlanViewer.App.Controls;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

// Locks in what the Query Store grid puts in the QueryId / PlanId columns. A grouped
// parent row aggregates many plans, so the synthetic plan behind it never gets either id
// and both read 0 — which in a column of real Query Store ids looks like an id rather than
// "not applicable". The display strings blank those; the numeric properties keep the 0
// because sorting, filtering and the bar ratios are all keyed on them.
public class QueryStoreGroupedRowIdDisplayTests
{
    private static QueryStoreRow LeafRow(long queryId, long planId) =>
        new(new QueryStorePlan { QueryId = queryId, PlanId = planId });

    // How QueryStoreGridControl builds a grouped parent: AggregateGroupedRows returns a
    // QueryStorePlan with the metric totals summed and no ids set at all.
    private static QueryStoreRow AggregateRow(params QueryStoreRow[] children) =>
        new(new QueryStorePlan { QueryHash = "0x1A2B3C" }, 0, "0x1A2B3C", children.ToList());

    [Fact]
    public void LeafRowsShowTheIdsTheyHave()
    {
        var row = LeafRow(12345, 678);

        Assert.Equal("12345", row.QueryIdDisplay);
        Assert.Equal("678", row.PlanIdDisplay);
    }

    [Fact]
    public void AggregateRowsShowNothingRatherThanZero()
    {
        var row = AggregateRow(LeafRow(12345, 678), LeafRow(12346, 679));

        Assert.Equal("", row.QueryIdDisplay);
        Assert.Equal("", row.PlanIdDisplay);
    }

    [Fact]
    public void BlankingIsDisplayOnlyAndLeavesTheSortAndFilterKeysAlone()
    {
        /* The columns sort on SortMemberPath="QueryId"/"PlanId" and filter through
           NumericAccessors, both of which read the numeric properties. Blanking the text
           must not reach them, or a grouped view would sort on a string. */
        var row = AggregateRow(LeafRow(12345, 678));

        Assert.Equal(0, row.QueryId);
        Assert.Equal(0, row.PlanId);
    }

    [Fact]
    public void ChildrenOfAGroupKeepTheirOwnIds()
    {
        // Only the aggregate loses its ids — the leaves under it are still real plans.
        var leaf = LeafRow(12345, 678);
        var group = AggregateRow(leaf);

        Assert.Equal("", group.QueryIdDisplay);
        Assert.Equal("12345", Assert.Single(group.Children).QueryIdDisplay);
    }
}
