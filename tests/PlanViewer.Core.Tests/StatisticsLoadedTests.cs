using System.Text.Json;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #681: each statement of an analysis lists the statistics the optimizer loaded while compiling it
/// (the plan's OptimizerStatsUsage; the plan does not say which of them drove the estimates), with a
/// status that tells a plan that recorded none apart from a plan that does not say and from a statement
/// with no plan at all. Ported from PerformanceMonitor #8147 (PR #8163). The plans are built per
/// assertion with invented names, and every expected value is a fact of the fixture.
/// </summary>
public class StatisticsLoadedTests
{
    private static readonly string[] RowFields =
    {
        "database", "schema", "table", "statistic", "modification_count", "table_rows", "modified_percent", "stale_at_compile", "last_update", "sampling_percent"
    };

    private const string TempRaw = "#orders_______________________________________________________________________________________________000000000012";

    private sealed record Stat(string Table, string Name, long Mods, string Db = "SalesDb", string Schema = "dbo", string? LastUpdate = "2026-10-30T02:14:07.53", string Sampling = "100");

    private sealed record Scan(string Table, string Cardinality, string Db = "SalesDb", string Schema = "dbo");

    private static string StatsXml(IEnumerable<Stat> stats) =>
        "<OptimizerStatsUsage>" + string.Concat(stats.Select(s =>
            $"<StatisticsInfo Database=\"{s.Db}\" Schema=\"{s.Schema}\" Table=\"{s.Table}\" Statistics=\"{s.Name}\" ModificationCount=\"{s.Mods}\" SamplingPercent=\"{s.Sampling}\"" +
            (s.LastUpdate == null ? "" : $" LastUpdate=\"{s.LastUpdate}\"") + "/>")) + "</OptimizerStatsUsage>";

    private static string ScanRelOp(int id, Scan s) =>
        $"<RelOp NodeId=\"{id}\" PhysicalOp=\"Table Scan\" LogicalOp=\"Table Scan\" EstimateRows=\"1\" EstimateIO=\"0.003\" EstimateCPU=\"0.0001\" AvgRowSize=\"9\" EstimatedTotalSubtreeCost=\"0.0032\" TableCardinality=\"{s.Cardinality}\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\" EstimatedExecutionMode=\"Row\">" +
        "<OutputList/><TableScan Ordered=\"0\" ForcedIndex=\"0\" ForceScan=\"0\" NoExpandHint=\"0\" Storage=\"RowStore\"><DefinedValues/>" +
        $"<Object Database=\"[{s.Db}]\" Schema=\"[{s.Schema}]\" Table=\"[{s.Table}]\" IndexKind=\"Heap\" Storage=\"RowStore\"/></TableScan></RelOp>";

    private static string QueryPlan(string? statsXml, IReadOnlyList<Scan> scans)
    {
        var body = scans.Count == 1
            ? ScanRelOp(0, scans[0])
            : "<RelOp NodeId=\"0\" PhysicalOp=\"Nested Loops\" LogicalOp=\"Inner Join\" EstimateRows=\"1\" EstimateIO=\"0\" EstimateCPU=\"0.1\" AvgRowSize=\"9\" EstimatedTotalSubtreeCost=\"0.5\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\" EstimatedExecutionMode=\"Row\"><OutputList/><NestedLoops Optimized=\"0\">" +
              string.Concat(scans.Select((s, i) => ScanRelOp(i + 1, s))) + "</NestedLoops></RelOp>";
        return "<QueryPlan CachedPlanSize=\"16\" CompileTime=\"1\" CompileCPU=\"1\" CompileMemory=\"64\">" + statsXml + body + "</QueryPlan>";
    }

    private static string SelectStmt(string? statsXml, params Scan[] scans) =>
        "<StmtSimple StatementText=\"SELECT * FROM dbo.Orders\" StatementId=\"1\" StatementType=\"SELECT\" StatementSubTreeCost=\"1\" StatementEstRows=\"1\">" +
        QueryPlan(statsXml, scans.Length == 0 ? new[] { new Scan("Orders", "1000") } : scans) + "</StmtSimple>";

    private static string IfStmt(string? statsXml) =>
        "<StmtCond StatementText=\"IF EXISTS (SELECT 1 FROM dbo.Orders)\" StatementId=\"1\" StatementType=\"COND WITH QUERY\" StatementSubTreeCost=\"0.0032\">" +
        "<Condition>" + QueryPlan(statsXml, new[] { new Scan("Orders", "1000") }) + "</Condition><Then><Statements/></Then></StmtCond>";

    private static string CursorStmt(string? statsXml) =>
        "<StmtCursor StatementText=\"declare c cursor for select * from dbo.Orders\" StatementId=\"1\" StatementType=\"DECLARE CURSOR\">" +
        "<CursorPlan CursorName=\"c\" CursorActualType=\"FastForward\" CursorRequestedType=\"FastForward\" CursorConcurrency=\"Read Only\" ForwardOnly=\"true\">" +
        "<Operation OperationType=\"FetchQuery\">" + QueryPlan(statsXml, new[] { new Scan("Orders", "1000") }) + "</Operation></CursorPlan></StmtCursor>";

    private static string Wrap(string statements) =>
        "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\" Version=\"1.539\" Build=\"16.0.1000.6\">" +
        "<BatchSequence><Batch><Statements>" + statements + "</Statements></Batch></BatchSequence></ShowPlanXML>";

    private static JsonDocument Analyze(string xml)
    {
        var plan = ShowPlanParser.Parse(xml);
        Assert.Null(plan.ParseError);
        var result = ResultMapper.Map(plan, "xml");
        return JsonDocument.Parse(JsonSerializer.Serialize(result, AnalysisJson.Indented));
    }

    private static JsonDocument Run(string? statsXml, params Scan[] scans) => Analyze(Wrap(SelectStmt(statsXml, scans)));

    private static JsonElement OnlyStatement(JsonDocument doc) => Assert.Single(doc.RootElement.GetProperty("statements").EnumerateArray());

    private static List<JsonElement> Rows(JsonElement stmt) => stmt.GetProperty("statistics_loaded").EnumerateArray().ToList();

    [Fact]
    public void TwelveStatistics_AreOrderedCappedAndTruncationIsObserved()
    {
        var counts = new long[] { 7, 900, 3, 450, 12, 5000, 1, 88, 640, 20, 2, 130 };
        var stats = counts.Select((c, i) => new Stat("Orders", $"ST_{i:00}", c));
        using var doc = Run(StatsXml(stats));
        var stmt = OnlyStatement(doc);

        Assert.Equal("recorded", stmt.GetProperty("statistics_status").GetString());
        Assert.Equal(10, stmt.GetProperty("statistics_cap").GetInt32());
        Assert.Equal(10, stmt.GetProperty("statistics_returned").GetInt32());
        Assert.Equal(12, stmt.GetProperty("total_statistics").GetInt32());
        Assert.True(stmt.GetProperty("statistics_truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, stmt.GetProperty("statistics_note").ValueKind);

        var rows = Rows(stmt);
        Assert.Equal(10, rows.Count);
        var mods = rows.Select(r => r.GetProperty("modification_count").GetInt64()).ToList();
        Assert.Equal(counts.OrderByDescending(c => c).Take(10).ToList(), mods);
    }

    [Fact]
    public void TenStatistics_AreNotTruncated()
    {
        using var doc = Run(StatsXml(Enumerable.Range(0, 10).Select(i => new Stat("Orders", $"ST_{i:00}", i))));
        var stmt = OnlyStatement(doc);

        Assert.Equal(10, stmt.GetProperty("statistics_returned").GetInt32());
        Assert.Equal(10, stmt.GetProperty("total_statistics").GetInt32());
        Assert.False(stmt.GetProperty("statistics_truncated").GetBoolean());
        Assert.Equal(10, Rows(stmt).Count);
    }

    [Fact]
    public void EqualCounts_AreOrderedByName()
    {
        var stats = new[]
        {
            new Stat("Orders", "ST_b", 5),
            new Stat("Orders", "ST_a", 5),
            new Stat("Orders", "ST_a", 5, Schema: "arch"),
            new Stat("Customers", "ST_z", 5),
            new Stat("Orders", "ST_a", 5, Db: "AuditDb"),
        };
        using var doc = Run(StatsXml(stats));
        var rows = Rows(OnlyStatement(doc));

        var keys = rows.Select(r => string.Join("|", r.GetProperty("database").GetString(), r.GetProperty("schema").GetString(), r.GetProperty("table").GetString(), r.GetProperty("statistic").GetString())).ToList();
        Assert.Equal(new[]
        {
            "AuditDb|dbo|Orders|ST_a",
            "SalesDb|arch|Orders|ST_a",
            "SalesDb|dbo|Customers|ST_z",
            "SalesDb|dbo|Orders|ST_a",
            "SalesDb|dbo|Orders|ST_b",
        }, keys);
    }

    [Fact]
    public void RowShare_ComesFromTheScansTableCardinality()
    {
        using var doc = Run(StatsXml(new[] { new Stat("Orders", "IX_Orders_CustomerId", 250) }), new Scan("Orders", "1000"));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal(1000, row.GetProperty("table_rows").GetDouble());
        Assert.Equal(25.0, row.GetProperty("modified_percent").GetDouble());
        Assert.Equal(250, row.GetProperty("modification_count").GetInt64());
        Assert.Equal(100, row.GetProperty("sampling_percent").GetDouble());
    }

    [Fact]
    public void TableNoOperatorReads_HasNullRowsPercentAndStaleness()
    {
        using var doc = Run(StatsXml(new[] { new Stat("Customers", "ST_c", 900000) }), new Scan("Orders", "1000"));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal(JsonValueKind.Null, row.GetProperty("table_rows").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("modified_percent").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("stale_at_compile").ValueKind);
        Assert.Equal(900000, row.GetProperty("modification_count").GetInt64());
    }

    [Fact]
    public void ThreeOperatorsOnOneTable_TakeTheLargestCardinality()
    {
        using var doc = Run(StatsXml(new[] { new Stat("Orders", "ST_o", 100) }), new Scan("Orders", "400"), new Scan("Orders", "800"), new Scan("Orders", "600"));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal(800, row.GetProperty("table_rows").GetDouble());
        Assert.Equal(12.5, row.GetProperty("modified_percent").GetDouble());
    }

    [Fact]
    public void OperatorWithoutTableCardinality_GivesNullRowsNotZero()
    {
        var update =
            "<StmtSimple StatementText=\"UPDATE dbo.Orders SET Status = 2 WHERE OrderId = 7\" StatementId=\"1\" StatementType=\"UPDATE\" StatementSubTreeCost=\"0.01\" StatementEstRows=\"1\">" +
            "<QueryPlan CachedPlanSize=\"16\" CompileTime=\"1\" CompileCPU=\"1\" CompileMemory=\"64\">" +
            StatsXml(new[] { new Stat("Orders", "PK_Orders", 300) }) +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Clustered Index Update\" LogicalOp=\"Clustered Index Update\" EstimateRows=\"1\" EstimateIO=\"0.01\" EstimateCPU=\"0.0001\" AvgRowSize=\"9\" EstimatedTotalSubtreeCost=\"0.01\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\" EstimatedExecutionMode=\"Row\">" +
            "<OutputList/><Update DMLRequestSort=\"0\" ActionColumn=\"[Action1004]\"><Object Database=\"[SalesDb]\" Schema=\"[dbo]\" Table=\"[Orders]\" Index=\"[PK_Orders]\" IndexKind=\"Clustered\" Storage=\"RowStore\"/></Update></RelOp>" +
            "</QueryPlan></StmtSimple>";
        using var doc = Analyze(Wrap(update));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal(JsonValueKind.Null, row.GetProperty("table_rows").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("modified_percent").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("stale_at_compile").ValueKind);
        Assert.Equal(300, row.GetProperty("modification_count").GetInt64());
    }

    [Fact]
    public void ZeroTableRows_GivesNullPercent()
    {
        using var doc = Run(StatsXml(new[] { new Stat("Orders", "ST_o", 100) }), new Scan("Orders", "0"));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal(0, row.GetProperty("table_rows").GetDouble());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("modified_percent").ValueKind);
    }

    [Fact]
    public void TempTable_ShowsItsShortNameAndGetsItsRowCount()
    {
        using var doc = Run(StatsXml(new[] { new Stat(TempRaw, "_WA_Sys_00000001_0", 40, Db: "tempdb") }), new Scan(TempRaw, "200", Db: "tempdb"));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal("#orders", row.GetProperty("table").GetString());
        Assert.Equal(200, row.GetProperty("table_rows").GetDouble());
        Assert.Equal(20.0, row.GetProperty("modified_percent").GetDouble());
    }

    [Fact]
    public void NamesMatchIgnoringCaseAndBrackets()
    {
        using var doc = Run(
            StatsXml(new[] { new Stat("[orders]", "[ST_o]", 50, Db: "[salesdb]", Schema: "[DBO]") }),
            new Scan("ORDERS", "500", Db: "SALESDB", Schema: "dbo"));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal(500, row.GetProperty("table_rows").GetDouble());
        Assert.Equal(10.0, row.GetProperty("modified_percent").GetDouble());
        Assert.Equal("orders", row.GetProperty("table").GetString());
        Assert.Equal("ST_o", row.GetProperty("statistic").GetString());
    }

    [Fact]
    public void MissingLastUpdate_IsNull()
    {
        using var doc = Run(StatsXml(new[] { new Stat("Orders", "ST_o", 1, LastUpdate: null) }));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal(JsonValueKind.Null, row.GetProperty("last_update").ValueKind);
    }

    [Fact]
    public void LastUpdate_IsVerbatim()
    {
        using var doc = Run(StatsXml(new[] { new Stat("Orders", "ST_o", 1, LastUpdate: "2026-10-30T02:14:07.53") }));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal("2026-10-30T02:14:07.53", row.GetProperty("last_update").GetString());
    }

    [Fact]
    public void PlanWithoutStatsUsage_IsNotInPlanWithTheNote()
    {
        using var doc = Run(null);
        var stmt = OnlyStatement(doc);

        Assert.Equal("not_in_plan", stmt.GetProperty("statistics_status").GetString());
        Assert.Contains("state at compile is unknown", stmt.GetProperty("statistics_note").GetString());
        Assert.Empty(Rows(stmt));
        Assert.Equal(0, stmt.GetProperty("statistics_returned").GetInt32());
        Assert.Equal(0, stmt.GetProperty("total_statistics").GetInt32());
        Assert.False(stmt.GetProperty("statistics_truncated").GetBoolean());
        Assert.Equal(10, stmt.GetProperty("statistics_cap").GetInt32());
    }

    [Fact]
    public void EmptyRecordedList_IsRecordedWithNoNote()
    {
        using var doc = Run("<OptimizerStatsUsage/>");
        var stmt = OnlyStatement(doc);

        Assert.Equal("recorded", stmt.GetProperty("statistics_status").GetString());
        Assert.Equal(JsonValueKind.Null, stmt.GetProperty("statistics_note").ValueKind);
        Assert.Empty(Rows(stmt));
        Assert.Equal(0, stmt.GetProperty("total_statistics").GetInt32());
        Assert.False(stmt.GetProperty("statistics_truncated").GetBoolean());
    }

    [Fact]
    public void IfConditionWithRecordedStats_IsRecorded()
    {
        using var doc = Analyze(Wrap(IfStmt(StatsXml(new[] { new Stat("Orders", "IX_Orders_CustomerId", 250) }))));
        var stmt = OnlyStatement(doc);
        var row = Assert.Single(Rows(stmt));

        Assert.Equal("recorded", stmt.GetProperty("statistics_status").GetString());
        Assert.Equal("IX_Orders_CustomerId", row.GetProperty("statistic").GetString());
        Assert.Equal(1000, row.GetProperty("table_rows").GetDouble());
        Assert.Equal(25.0, row.GetProperty("modified_percent").GetDouble());
    }

    [Fact]
    public void IfConditionWithoutStatsUsage_IsNotInPlan()
    {
        using var doc = Analyze(Wrap(IfStmt(null)));
        var stmt = OnlyStatement(doc);

        Assert.Equal("not_in_plan", stmt.GetProperty("statistics_status").GetString());
        Assert.Contains("state at compile is unknown", stmt.GetProperty("statistics_note").GetString());
        Assert.Empty(Rows(stmt));
    }

    [Fact]
    public void CursorFetchQuery_IsRecorded()
    {
        using var doc = Analyze(Wrap(CursorStmt(StatsXml(new[] { new Stat("Orders", "IX_Orders_CustomerId", 250) }))));
        var stmt = Assert.Single(doc.RootElement.GetProperty("statements").EnumerateArray(), s => s.GetProperty("statistics_status").GetString() == "recorded");

        Assert.Equal("IX_Orders_CustomerId", Assert.Single(Rows(stmt)).GetProperty("statistic").GetString());
    }

    [Fact]
    public void ProcedurePlan_DeclareIsNoQueryPlan_InnerStatementIsRecorded()
    {
        var inner = SelectStmt(StatsXml(new[] { new Stat("Orders", "ST_o", 9) }));
        var xml = Wrap(
            "<StmtSimple StatementText=\"EXEC dbo.GetOrders\" StatementId=\"1\" StatementType=\"EXEC PROC\">" +
            $"<StoredProc ProcName=\"[dbo].[GetOrders]\"><Statements><StmtSimple StatementText=\"DECLARE @i int\" StatementId=\"2\" StatementType=\"DECLARE\"/>{inner}</Statements></StoredProc>" +
            "</StmtSimple>");
        using var doc = Analyze(xml);
        var statements = doc.RootElement.GetProperty("statements").EnumerateArray().ToList();

        var recorded = Assert.Single(statements, s => s.GetProperty("statistics_status").GetString() == "recorded");
        Assert.Equal("ST_o", Assert.Single(Rows(recorded)).GetProperty("statistic").GetString());

        var planless = statements.Where(s => s.GetProperty("statistics_status").GetString() != "recorded").ToList();
        Assert.NotEmpty(planless);
        Assert.Contains(planless, s => s.GetProperty("statement_type").GetString() == "DECLARE");
        Assert.All(planless, s =>
        {
            Assert.Equal("no_query_plan", s.GetProperty("statistics_status").GetString());
            Assert.Equal(JsonValueKind.Null, s.GetProperty("statistics_note").ValueKind);
            Assert.Empty(Rows(s));
            Assert.Equal(10, s.GetProperty("statistics_cap").GetInt32());
            Assert.Equal(0, s.GetProperty("statistics_returned").GetInt32());
            Assert.Equal(0, s.GetProperty("total_statistics").GetInt32());
            Assert.False(s.GetProperty("statistics_truncated").GetBoolean());
        });
    }

    [Theory]
    [InlineData("10000", 2500, false)]
    [InlineData("10000", 2501, true)]
    [InlineData("100", 500, false)]
    [InlineData("100", 501, true)]
    [InlineData("0", 500, false)]
    [InlineData("0", 501, true)]
    [InlineData("500", 500, false)]
    [InlineData("500", 501, true)]
    [InlineData("501", 600, false)]
    [InlineData("501", 601, true)]
    [InlineData("19000", 4300, false)]
    [InlineData("19000", 4301, true)]
    [InlineData("20000", 4472, false)]
    [InlineData("20000", 4473, true)]
    [InlineData("100000", 10000, false)]
    [InlineData("100000", 10001, true)]
    public void StaleAtCompile_UsesTheFloorOf500ThenTheSmallerOfTwentyPercentAndSqrtTerms(string rows, int modifications, bool expected)
    {
        using var doc = Run(StatsXml(new[] { new Stat("Orders", "ST_o", modifications) }), new Scan("Orders", rows));

        Assert.Equal(expected, Assert.Single(Rows(OnlyStatement(doc))).GetProperty("stale_at_compile").GetBoolean());
    }

    [Fact]
    public void RowsCarryOnlyTheSpecifiedFields()
    {
        using var doc = Run(StatsXml(new[] { new Stat("Orders", "ST_o", 9) }));
        var row = Assert.Single(Rows(OnlyStatement(doc)));

        Assert.Equal(RowFields.OrderBy(n => n, StringComparer.Ordinal), row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void LeavesWarningsAndCountsUnchanged()
    {
        var stats = StatsXml(new[] { new Stat("Orders", "ST_o", 900) });
        using var with = Run(stats);
        using var without = Run(null);

        Assert.Equal(OnlyStatement(without).GetProperty("warnings").GetRawText(), OnlyStatement(with).GetProperty("warnings").GetRawText());
        Assert.Equal(without.RootElement.GetProperty("summary").GetRawText(), with.RootElement.GetProperty("summary").GetRawText());
    }

    [Fact]
    public void TheParserCleansATempTableStatisticsNameLikeTheOperator()
    {
        var plan = ShowPlanParser.Parse(Wrap(SelectStmt(
            StatsXml(new[] { new Stat(TempRaw, "_WA_Sys_00000001_0", 3, Db: "tempdb") }),
            new Scan(TempRaw, "200", Db: "tempdb"))));
        var stmt = Assert.Single(plan.Batches.SelectMany(b => b.Statements));
        var scan = Assert.Single(stmt.RootNode!.Children);

        Assert.Equal("#orders", Assert.Single(stmt.StatsUsage).TableName);
        Assert.Equal("dbo.#orders", scan.ObjectName);
    }
}
