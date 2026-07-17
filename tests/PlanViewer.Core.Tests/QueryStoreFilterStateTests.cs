using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

// Locks in QueryStoreServerFilterState — the UI-side model that holds parsed Query Store
// filter criteria, normalizes them through typed setters, projects active criteria to
// ordered chips, and builds a QueryStoreFilter for fetching. EscapeBrackets is a pure
// modifier: it never counts toward IsEmpty, never produces its own chip, and is always
// copied onto the built filter.
public class QueryStoreFilterStateTests
{
    // ---- IsEmpty / chip basics ----

    [Fact]
    public void NewState_IsEmpty_NoChips_NoFilter()
    {
        var s = new QueryStoreServerFilterState();
        Assert.True(s.IsEmpty);
        Assert.Empty(s.GetActiveChips());
        Assert.Null(s.BuildFilter());
    }

    [Fact]
    public void SingleSetter_ProducesOneChipOfThatKind_WithValueInLabel()
    {
        var s = new QueryStoreServerFilterState();
        s.SetMinExecutions(100);

        var chip = Assert.Single(s.GetActiveChips());
        Assert.Equal(ServerFilterKind.MinExecutions, chip.Kind);
        Assert.Contains("100", chip.Label);
        Assert.False(s.IsEmpty);
    }

    [Fact]
    public void QueryTextSearch_Chip_ContainsSearchValue()
    {
        var s = new QueryStoreServerFilterState();
        s.SetQueryTextSearch("Votes");

        var chip = Assert.Single(s.GetActiveChips());
        Assert.Equal(ServerFilterKind.QueryTextSearch, chip.Kind);
        Assert.Contains("Votes", chip.Label);
    }

    [Fact]
    public void GetActiveChips_EnumeratesInEnumDeclarationOrder_RegardlessOfSetOrder()
    {
        var s = new QueryStoreServerFilterState();

        // Every criterion, set in a deliberately scrambled order.
        s.SetModule("dbo.p");
        s.SetMinDurationMs(30);
        s.SetIgnorePlanIds(new long[] { 9 });
        s.SetQueryId(5);
        s.SetQueryTextSearchNot("#temp");
        s.SetMinExecutions(100);
        s.SetExecutionType(new[] { "Regular" });
        s.SetIncludePlanIds(new long[] { 7 });
        s.SetQueryHash("0xAA");
        s.SetMinCpuMs(20);
        s.SetPlanId(6);
        s.SetIncludeQueryIds(new long[] { 1, 2 });
        s.SetQueryTextSearch("Votes");
        s.SetQueryPlanHash("0xBB");
        s.SetIgnoreQueryIds(new long[] { 3 });

        var kinds = s.GetActiveChips().Select(c => c.Kind).ToArray();

        Assert.Equal(new[]
        {
            ServerFilterKind.MinExecutions,
            ServerFilterKind.MinCpuMs,
            ServerFilterKind.MinDurationMs,
            ServerFilterKind.QueryTextSearch,
            ServerFilterKind.QueryTextSearchNot,
            ServerFilterKind.IncludeQueryIds,
            ServerFilterKind.IgnoreQueryIds,
            ServerFilterKind.IncludePlanIds,
            ServerFilterKind.IgnorePlanIds,
            ServerFilterKind.QueryId,
            ServerFilterKind.PlanId,
            ServerFilterKind.QueryHash,
            ServerFilterKind.QueryPlanHash,
            ServerFilterKind.Module,
            ServerFilterKind.ExecutionType,
        }, kinds);
    }

    [Fact]
    public void IsEmpty_TracksChipCount()
    {
        var s = new QueryStoreServerFilterState();
        Assert.True(s.IsEmpty);
        Assert.Empty(s.GetActiveChips());

        s.SetMinExecutions(1);
        Assert.False(s.IsEmpty);
        Assert.NotEmpty(s.GetActiveChips());

        s.SetMinExecutions(null);
        Assert.True(s.IsEmpty);
        Assert.Empty(s.GetActiveChips());
    }

    // ---- Scalar setters: normalize + clear ----

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(null)]
    public void SetMinExecutions_NonPositiveOrNull_Clears(long? v)
    {
        var s = new QueryStoreServerFilterState();
        s.SetMinExecutions(v);
        Assert.True(s.IsEmpty);
        Assert.Null(s.BuildFilter());
    }

    [Fact]
    public void SetMinExecutions_Positive_Sets()
    {
        var s = new QueryStoreServerFilterState();
        s.SetMinExecutions(100);
        Assert.False(s.IsEmpty);
        Assert.Equal(100L, s.BuildFilter()!.MinExecutions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetQueryTextSearch_NullOrWhitespace_Clears(string? v)
    {
        var s = new QueryStoreServerFilterState();
        s.SetQueryTextSearch(v);
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void SetQueryTextSearch_TrimsStoredValue()
    {
        var s = new QueryStoreServerFilterState();
        s.SetQueryTextSearch("  x  ");
        Assert.Equal("x", s.BuildFilter()!.QueryTextSearch);
        Assert.Contains("x", Assert.Single(s.GetActiveChips()).Label);
    }

    [Fact]
    public void SetExecutionType_NullOrEmpty_Clears()
    {
        var s = new QueryStoreServerFilterState();

        s.SetExecutionType(new[] { "Regular" });
        Assert.False(s.IsEmpty);

        s.SetExecutionType(Array.Empty<string>());
        Assert.True(s.IsEmpty);

        s.SetExecutionType(new[] { "Regular" });
        s.SetExecutionType(null);
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Setter_LastWriteWins()
    {
        var s = new QueryStoreServerFilterState();
        s.SetMinExecutions(100);
        s.SetMinExecutions(200);

        Assert.Equal(200L, s.BuildFilter()!.MinExecutions);
        Assert.Contains("200", Assert.Single(s.GetActiveChips()).Label);
    }

    // ---- Remove / Clear ----

    [Fact]
    public void Remove_ClearsExactlyThatCriterion()
    {
        var s = new QueryStoreServerFilterState();
        s.SetMinExecutions(100);
        s.SetQueryTextSearch("Votes");

        s.Remove(ServerFilterKind.MinExecutions);

        var chip = Assert.Single(s.GetActiveChips());
        Assert.Equal(ServerFilterKind.QueryTextSearch, chip.Kind);
        Assert.False(s.IsEmpty);
    }

    [Fact]
    public void Remove_InactiveOrUnknownKind_IsNoOp()
    {
        var s = new QueryStoreServerFilterState();
        s.SetMinExecutions(100);

        s.Remove(ServerFilterKind.PlanId);   // inactive criterion
        s.Remove((ServerFilterKind)999);     // outside the enum

        var chip = Assert.Single(s.GetActiveChips());
        Assert.Equal(ServerFilterKind.MinExecutions, chip.Kind);
    }

    [Fact]
    public void Clear_ResetsEverythingIncludingEscapeBrackets()
    {
        var s = new QueryStoreServerFilterState();
        s.SetMinExecutions(100);
        s.SetQueryTextSearch("Votes");
        s.SetEscapeBrackets(true);

        s.Clear();

        Assert.True(s.IsEmpty);
        Assert.Empty(s.GetActiveChips());
        Assert.Null(s.BuildFilter());

        // The EscapeBrackets modifier was reset too: a fresh text criterion now
        // builds a filter with escape off.
        s.SetQueryTextSearch("Votes");
        Assert.False(s.BuildFilter()!.EscapeBrackets);
    }

    // ---- Array setters ----

    [Fact]
    public void SetArrayCriteria_NullOrEmpty_Clears()
    {
        var s = new QueryStoreServerFilterState();

        s.SetIncludeQueryIds(null);
        s.SetIncludeQueryIds(Array.Empty<long>());
        s.SetIgnoreQueryIds(Array.Empty<long>());
        s.SetIncludePlanIds(Array.Empty<long>());
        s.SetIgnorePlanIds(Array.Empty<long>());

        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void SetIncludeQueryIds_ProducesChipAndFilter()
    {
        var s = new QueryStoreServerFilterState();
        s.SetIncludeQueryIds(new long[] { 1, 2 });

        var chip = Assert.Single(s.GetActiveChips());
        Assert.Equal(ServerFilterKind.IncludeQueryIds, chip.Kind);
        Assert.Contains("1,2", chip.Label);
        Assert.Equal(new long[] { 1, 2 }, s.BuildFilter()!.IncludeQueryIds);
    }

    [Fact]
    public void SetIgnoreQueryIds_ProducesChipAndFilter()
    {
        var s = new QueryStoreServerFilterState();
        s.SetIgnoreQueryIds(new long[] { 1, 2 });

        Assert.Equal(ServerFilterKind.IgnoreQueryIds, Assert.Single(s.GetActiveChips()).Kind);
        Assert.Equal(new long[] { 1, 2 }, s.BuildFilter()!.IgnoreQueryIds);
    }

    [Fact]
    public void SetIncludePlanIds_ProducesChipAndFilter()
    {
        var s = new QueryStoreServerFilterState();
        s.SetIncludePlanIds(new long[] { 1, 2 });

        Assert.Equal(ServerFilterKind.IncludePlanIds, Assert.Single(s.GetActiveChips()).Kind);
        Assert.Equal(new long[] { 1, 2 }, s.BuildFilter()!.IncludePlanIds);
    }

    [Fact]
    public void SetIgnorePlanIds_ProducesChipAndFilter()
    {
        var s = new QueryStoreServerFilterState();
        s.SetIgnorePlanIds(new long[] { 1, 2 });

        Assert.Equal(ServerFilterKind.IgnorePlanIds, Assert.Single(s.GetActiveChips()).Kind);
        Assert.Equal(new long[] { 1, 2 }, s.BuildFilter()!.IgnorePlanIds);
    }

    // ---- EscapeBrackets modifier ----

    [Fact]
    public void SetEscapeBrackets_Alone_DoesNotActivateFilter()
    {
        var s = new QueryStoreServerFilterState();
        s.SetEscapeBrackets(true);

        Assert.True(s.IsEmpty);
        Assert.Empty(s.GetActiveChips());
        Assert.Null(s.BuildFilter());
    }

    [Fact]
    public void EscapeBrackets_WithTextSearch_ShowsLiteralModeInChip_AndOnFilter()
    {
        var s = new QueryStoreServerFilterState();
        s.SetQueryTextSearch("x");
        s.SetEscapeBrackets(true);

        var chip = Assert.Single(s.GetActiveChips());
        Assert.Equal(ServerFilterKind.QueryTextSearch, chip.Kind);
        Assert.Contains("x", chip.Label);
        Assert.Contains("literal", chip.Label);   // literal-mode hint token

        Assert.True(s.BuildFilter()!.EscapeBrackets);
    }

    [Fact]
    public void EscapeBrackets_Off_TextChipHasNoLiteralHint()
    {
        var s = new QueryStoreServerFilterState();
        s.SetQueryTextSearch("x");

        Assert.DoesNotContain("literal", Assert.Single(s.GetActiveChips()).Label);
        Assert.False(s.BuildFilter()!.EscapeBrackets);
    }

    // ---- BuildFilter mapping ----

    [Fact]
    public void BuildFilter_NullWhenEmpty()
    {
        Assert.Null(new QueryStoreServerFilterState().BuildFilter());
    }

    [Fact]
    public void BuildFilter_MapsEveryField()
    {
        var s = new QueryStoreServerFilterState();
        s.SetMinExecutions(100);
        s.SetMinCpuMs(20);
        s.SetMinDurationMs(30);
        s.SetQueryTextSearch("Votes");
        s.SetQueryTextSearchNot("#temp");
        s.SetIncludeQueryIds(new long[] { 1, 2 });
        s.SetIgnoreQueryIds(new long[] { 3 });
        s.SetIncludePlanIds(new long[] { 4 });
        s.SetIgnorePlanIds(new long[] { 5 });
        s.SetQueryId(6);
        s.SetPlanId(7);
        s.SetQueryHash("0xAA");
        s.SetQueryPlanHash("0xBB");
        s.SetModule("dbo.p");
        s.SetExecutionType(new[] { "Regular" });
        s.SetEscapeBrackets(true);

        var f = s.BuildFilter()!;

        Assert.Equal(100L, f.MinExecutions);
        Assert.Equal(20L, f.MinCpuMs);
        Assert.Equal(30L, f.MinDurationMs);
        Assert.Equal("Votes", f.QueryTextSearch);
        Assert.Equal("#temp", f.QueryTextSearchNot);
        Assert.Equal(new long[] { 1, 2 }, f.IncludeQueryIds);
        Assert.Equal(new long[] { 3 }, f.IgnoreQueryIds);
        Assert.Equal(new long[] { 4 }, f.IncludePlanIds);
        Assert.Equal(new long[] { 5 }, f.IgnorePlanIds);
        Assert.Equal(6L, f.QueryId);
        Assert.Equal(7L, f.PlanId);
        Assert.Equal("0xAA", f.QueryHash);
        Assert.Equal("0xBB", f.QueryPlanHash);
        Assert.Equal("dbo.p", f.ModuleName);                    // Module -> ModuleName
        Assert.Equal(new[] { "Regular" }, f.ExecutionTypeDescs); // ExecutionType -> ExecutionTypeDescs
        Assert.True(f.EscapeBrackets);
    }

    [Fact]
    public void BuildFilter_QueryIdAndIncludeQueryIds_Coexist()
    {
        var s = new QueryStoreServerFilterState();
        s.SetQueryId(5);
        s.SetIncludeQueryIds(new long[] { 1, 2 });

        var f = s.BuildFilter()!;
        Assert.Equal(5L, f.QueryId);
        Assert.Equal(new long[] { 1, 2 }, f.IncludeQueryIds);
    }
}
