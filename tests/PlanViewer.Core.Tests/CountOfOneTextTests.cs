using System;
using System.Linq;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A count in the analysis text agrees with its noun: one thread reads "1 thread", four read
/// "4 threads", and the index script's header reads "1 index" or "2 indexes". No message hands
/// the reader a "(s)" to resolve.
/// </summary>
public class CountOfOneTextTests
{
    [Theory]
    [InlineData("excellent-parallel-spill.sqlplan", "Spill to TempDb", "Spill level 0, 1 thread")]
    [InlineData("excellent-parallel-spill.sqlplan", "Spill to TempDb", "Spill level 0, 4 threads")]
    public void StandaloneSpill_CountsItsThreads(string fixture, string warningType, string expected)
    {
        // The standalone SpillToTempDb message carries nothing after the thread count when the
        // plan reports no grant and no writes, so the whole message is the pin.
        var warnings = PlanTestHelper.WarningsOfType(PlanTestHelper.LoadAndAnalyze(fixture), warningType);

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message == expected);
    }

    [Theory]
    [InlineData("param-sniffing-posttypeid2.sqlplan", "Sort Spill", "Sort spill level 8, 1 thread. Granted:")]
    [InlineData("memory_grant_wait_plan.sqlplan", "Sort Spill", "Sort spill level 1, 8 threads. Granted:")]
    [InlineData("row-count-spool-slow.sqlplan", "Hash Spill", "Hash spill level 1, 6 threads. Granted:")]
    public void SortAndHashSpill_CountTheirThreads(string fixture, string warningType, string expected)
    {
        var warnings = PlanTestHelper.WarningsOfType(PlanTestHelper.LoadAndAnalyze(fixture), warningType);

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.Contains(expected, StringComparison.Ordinal));
        Assert.All(warnings, w => Assert.DoesNotContain("(s)", w.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void IndexScript_OneIndex_ReadsSingular()
    {
        var sql = DdlScripter.FormatIndexes("dbo.T", new[] { Index("PK_T", 1) });

        Assert.Contains("-- 1 index, 1 row", sql);
        Assert.DoesNotContain("(es)", sql);
    }

    [Fact]
    public void IndexScript_TwoIndexes_ReadsPlural()
    {
        var sql = DdlScripter.FormatIndexes("dbo.T", new[] { Index("PK_T", 1000), Index("IX_T", 1000) });

        Assert.Contains("-- 2 indexes, 1,000 rows", sql);
        Assert.DoesNotContain("(es)", sql);
    }

    private static IndexInfo Index(string name, long rowCount) => new()
    {
        IndexName = name,
        IndexType = "NONCLUSTERED",
        IsUnique = false,
        IsPrimaryKey = false,
        KeyColumns = "Id",
        IncludeColumns = "",
        FilterDefinition = null,
        RowCount = rowCount,
        SizeMB = 1.5,
        DataCompression = "NONE"
    };
}
