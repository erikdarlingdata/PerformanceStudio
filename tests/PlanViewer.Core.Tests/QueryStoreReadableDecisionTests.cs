using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

// Locks in the Query Store readability gate, including the readable-secondary-replica case
// from issue #378 where the local state reports OFF but the primary's captured data is
// replicated and readable. See QueryStoreService.IsQueryStoreReadable.
public class QueryStoreReadableDecisionTests
{
    [Theory]
    // Writable primary with Query Store on -> readable.
    [InlineData("READ_WRITE", false, false, true)]
    [InlineData("READ_ONLY", false, false, true)]
    // SQL 2022+/Azure secondary-replica feature: local state is a READ* value -> readable.
    [InlineData("READ_CAPTURE_SECONDARY", true, true, true)]
    // #378: read-only replica reports OFF but the primary's data is replicated -> readable.
    [InlineData("OFF", true, true, true)]
    // Read-only replica that holds no Query Store data yet -> nothing to read.
    [InlineData("OFF", true, false, false)]
    // Writable primary with Query Store genuinely off or errored -> not readable.
    [InlineData("OFF", false, false, false)]
    [InlineData("ERROR", false, false, false)]
    // Writable primary that is off: stale rows alone do not make it readable (only replicas relax).
    [InlineData("OFF", false, true, false)]
    // No row / null state -> not readable.
    [InlineData(null, false, false, false)]
    public void IsQueryStoreReadable_MatchesExpectedGate(
        string? state, bool readOnlyReplica, bool hasData, bool expected)
    {
        Assert.Equal(expected, QueryStoreService.IsQueryStoreReadable(state, readOnlyReplica, hasData));
    }
}
