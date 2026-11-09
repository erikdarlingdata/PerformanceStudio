using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PlanViewer.App.Controls;
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
    public void ExchangeSpill_OneWrite_ReadsSingular()
    {
        // No checked-in plan spills a single write, so the fixture's ExchangeSpillDetails is
        // rewritten to one write before it is parsed; everything else in the plan is as recorded.
        var xml = Regex.Replace(ExchangeSpillPlanXml(), "(<ExchangeSpillDetails WritesToTempDb=\")\\d+(\")", "${1}1$2");
        var warnings = PlanTestHelper.WarningsOfType(ShowPlanParser.Parse(xml), "Exchange Spill");

        Assert.NotEmpty(warnings);
        Assert.All(warnings, w => Assert.StartsWith("Exchange spill: 1 write to TempDB.", w.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ExchangeSpill_ManyWrites_ReadsPlural()
    {
        var warnings = PlanTestHelper.WarningsOfType(ShowPlanParser.Parse(ExchangeSpillPlanXml()), "Exchange Spill");

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.StartsWith("Exchange spill: 5,000,000 writes to TempDB.", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryStatus_OneOfEach_ReadsSingular()
    {
        var at = new DateTime(2026, 11, 9, 9, 0, 0);

        Assert.Equal(
            "1 interval, 1 plan, 1 total execution | 11/09 09:00 to 11/09 09:00",
            QueryStoreHistoryControl.FormatHistoryStatus(1, 1, 1, at, at));
    }

    [Fact]
    public void HistoryStatus_ManyOfEach_ReadsPlural()
    {
        var first = new DateTime(2026, 11, 9, 9, 0, 0);
        var last = new DateTime(2026, 11, 9, 11, 0, 0);

        Assert.Equal(
            "3 intervals, 2 plans, 1,250 total executions | 11/09 09:00 to 11/09 11:00",
            QueryStoreHistoryControl.FormatHistoryStatus(3, 2, 1250, first, last));
    }

    private static string ExchangeSpillPlanXml()
    {
        var path = Path.Combine("Plans", "exchange_spill_plan.sqlplan");
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        return File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
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
