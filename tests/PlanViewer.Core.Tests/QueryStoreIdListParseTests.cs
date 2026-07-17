using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

// Locks in QueryStoreFilter.ParseIdList — the comma-separated id-list parser behind the
// Query Store include/ignore query-id and plan-id filters. Contract: distinct ids in
// first-seen order (never sorted), empty tokens from stray commas skipped, and any
// non-integer or overflowing token throws ArgumentException.
public class QueryStoreIdListParseTests
{
    [Theory]
    // Basic comma-separated list and a lone id.
    [InlineData("1,2,3", new long[] { 1, 2, 3 })]
    [InlineData("5", new long[] { 5 })]
    // Whitespace around each token is trimmed.
    [InlineData(" 1, 2 , 3 ", new long[] { 1, 2, 3 })]
    // Empty tokens from doubled / trailing / leading commas are skipped.
    [InlineData("1,,2", new long[] { 1, 2 })]
    [InlineData("1,2,", new long[] { 1, 2 })]
    [InlineData(",1", new long[] { 1 })]
    // Duplicates collapse to the first occurrence.
    [InlineData("1,1,2", new long[] { 1, 2 })]
    // Order is first-seen, NOT sorted.
    [InlineData("3,1,2,1", new long[] { 3, 1, 2 })]
    // Negative ids are valid integers.
    [InlineData("-1", new long[] { -1 })]
    // Null / empty / whitespace / comma-only input -> no filter.
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(",", null)]
    [InlineData(" , ", null)]
    public void ParseIdList_ParsesDistinctFirstSeen(string? input, long[]? expected)
    {
        Assert.Equal(expected, QueryStoreFilter.ParseIdList(input));
    }

    [Theory]
    // A non-integer token throws.
    [InlineData("abc")]
    [InlineData("1,x,2")]
    // A token past Int64 range throws like any other non-integer token.
    [InlineData("9999999999999999999999")]
    // An internal space is not a separator, so "1 2" is one invalid token.
    [InlineData("1 2")]
    public void ParseIdList_ThrowsOnNonIntegerToken(string input)
    {
        // Assert the exception TYPE only — never its message text.
        Assert.Throws<ArgumentException>(() => QueryStoreFilter.ParseIdList(input));
    }
}
