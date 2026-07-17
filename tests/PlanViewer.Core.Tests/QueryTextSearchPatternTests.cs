using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

// Locks in the two static helpers on QueryStoreFilter that back the Query Store
// query-text search filter (feature/qs-query-text-search).
// BuildQueryTextSearchPattern mirrors sp_QuickieStore's @query_text_search default:
// trim, then wrap the term in leading/trailing % when not already present. With
// sp_QuickieStore's default @escape_brackets = 0, the LIKE metacharacters %, _, [
// and ] are left live (this is the documented phase-1 wildcard behavior).
// ParseExecutionType is a sibling helper on the same model; its mapping and error
// contract are backfilled here.
public class QueryTextSearchPatternTests
{
    [Theory]
    // Bare term gets wrapped on both sides.
    [InlineData("SELECT", "%SELECT%")]
    // A % already on one end is kept, not doubled.
    [InlineData("%SELECT", "%SELECT%")]
    [InlineData("SELECT%", "%SELECT%")]
    // Fully wrapped term is returned unchanged.
    [InlineData("%SELECT%", "%SELECT%")]
    // Interior wildcards survive; only the ends are padded.
    [InlineData("a%b", "%a%b%")]
    // A lone % already starts and ends with %, so it stays match-anything.
    [InlineData("%", "%")]
    // Surrounding whitespace is trimmed before wrapping.
    [InlineData("  foo  ", "%foo%")]
    // Phase-1 wildcard semantics: brackets and underscore are NOT escaped.
    [InlineData("[dbo]", "%[dbo]%")]
    [InlineData("a_b", "%a_b%")]
    // Null / empty / whitespace -> no filter.
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void BuildQueryTextSearchPattern_WrapsTermInWildcards(string? input, string? expected)
    {
        Assert.Equal(expected, QueryStoreFilter.BuildQueryTextSearchPattern(input));
    }

    [Theory]
    // escape_brackets = 1 (sp_QuickieStore @escape_brackets = 1): [ ] and _ become
    // literals, backslash-escaped for ESCAPE '\', while % stays a live LIKE wildcard.
    [InlineData("[dbo]", @"%\[dbo\]%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData("[a_b]", @"%\[a\_b\]%")]
    [InlineData("[dbo].[t]", @"%\[dbo\].\[t\]%")]
    [InlineData("_", @"%\_%")]
    // Pre-wrapped term keeps its % ends; interior brackets are still escaped.
    [InlineData("%[x]%", @"%\[x\]%")]
    // No bracket/underscore metacharacters -> escaping is a no-op.
    [InlineData("SELECT", "%SELECT%")]
    // Discriminator: % is a wildcard even in escape mode and is never escaped.
    [InlineData("a%b", "%a%b%")]
    // Known parity limitation: a literal backslash in the term is passed through
    // unescaped, so it collides with ESCAPE '\'. Documented here, not fixed.
    [InlineData(@"a\b", @"%a\b%")]
    // Null / empty / whitespace -> no filter, regardless of escape mode.
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void BuildQueryTextSearchPattern_EscapeBrackets_EscapesBracketsAndUnderscore(string? input, string? expected)
    {
        Assert.Equal(expected, QueryStoreFilter.BuildQueryTextSearchPattern(input, escapeBrackets: true));
    }

    [Theory]
    // Single friendly value -> single execution_type_desc.
    [InlineData("regular", new[] { "Regular" })]
    [InlineData("aborted", new[] { "Aborted" })]
    [InlineData("exception", new[] { "Exception" })]
    // "failed" fans out to both failure descs.
    [InlineData("failed", new[] { "Aborted", "Exception" })]
    // "any" and null / empty / whitespace mean no filter.
    [InlineData("any", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    // Matching is case-insensitive (Trim().ToLowerInvariant()).
    [InlineData("REGULAR", new[] { "Regular" })]
    public void ParseExecutionType_MapsFriendlyValues(string? input, string[]? expected)
    {
        Assert.Equal(expected, QueryStoreFilter.ParseExecutionType(input));
    }

    [Fact]
    public void ParseExecutionType_ThrowsOnUnknownValue()
    {
        Assert.Throws<ArgumentException>(() => QueryStoreFilter.ParseExecutionType("garbage"));
    }
}
