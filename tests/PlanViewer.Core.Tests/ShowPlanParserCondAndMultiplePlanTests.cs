using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #580: the parser dropped two statement shapes that carry their own plan or hashes. A
/// <c>StmtCond</c> (<c>IF EXISTS (...)</c>) keeps its condition's own <c>QueryPlan</c> under
/// <c>Condition</c>, and the old code fed that bare <c>QueryPlan</c> to <c>ParseStatement</c> as if it
/// were a statement: an empty <c>STATEMENT</c> placeholder with no operators, no hashes and no missing
/// index. A <c>StmtSimple</c> with <c>StatementType="MULTIPLE PLAN"</c> carries <c>QueryHash</c> and
/// <c>QueryPlanHash</c> but no <c>QueryPlan</c>, and the hashes were read only after the no-plan return.
///
/// <para>The same two gaps were fixed in PerformanceMonitor's copy of this parser
/// (erikdarlingdata/PerformanceMonitor#4470); the first five tests and the repro XML are ported from
/// there. The last two are Studio's: a UDF sub-plan under <c>Condition</c> attaches to the condition's
/// statement (Studio hangs module bodies off their calling statement), and a plain <c>IF</c> with no
/// query of its own still adds no statement.</para>
/// </summary>
public sealed class ShowPlanParserCondAndMultiplePlanTests
{
    private const string TableScanRelOp = """<RelOp NodeId="0" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="1" EstimateIO="0.003" EstimateCPU="0.0001" AvgRowSize="9" EstimatedTotalSubtreeCost="0.0032" TableCardinality="100" Parallel="0" EstimateRebinds="0" EstimateRewinds="0" EstimatedExecutionMode="Row"><OutputList/><TableScan Ordered="0" ForcedIndex="0" ForceScan="0" NoExpandHint="0" Storage="RowStore"><DefinedValues/><Object Database="[db]" Schema="[dbo]" Table="[t]" IndexKind="Heap" Storage="RowStore"/></TableScan></RelOp>""";

    private const string ReproStatements = $"""
        <StmtCond StatementText="IF EXISTS (SELECT 1 FROM dbo.t WHERE id = @id)" StatementId="1" StatementCompId="1" StatementType="COND WITH QUERY" RetrievedFromCache="true" QueryHash="0x1111111111111111" QueryPlanHash="0x2222222222222222">
          <Condition><QueryPlan CachedPlanSize="16" CompileTime="1" CompileCPU="1" CompileMemory="104">
            <MissingIndexes><MissingIndexGroup Impact="90.5"><MissingIndex Database="[db]" Schema="[dbo]" Table="[t]"><ColumnGroup Usage="EQUALITY"><Column Name="[id]" ColumnId="1"/></ColumnGroup></MissingIndex></MissingIndexGroup></MissingIndexes>
            {TableScanRelOp}
          </QueryPlan></Condition>
          <Then><Statements><StmtSimple StatementText="RETURN" StatementId="2" StatementCompId="2" StatementType="RETURN NONE"/></Statements></Then>
        </StmtCond>
        <StmtSimple StatementText="SELECT c FROM dbo.u WHERE k = @k" StatementId="3" StatementCompId="3" StatementType="MULTIPLE PLAN" RetrievedFromCache="true" QueryHash="0x3333333333333333" QueryPlanHash="0x4444444444444444"/>
        """;

    private static string Wrap(string statements) => $"""
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.564" Build="16.0.4215.2"><BatchSequence><Batch><Statements>
        {statements}
        </Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    private static ParsedPlan ParseAndAnalyze(string statements)
    {
        var plan = ShowPlanParser.Parse(Wrap(statements));
        PlanAnalyzer.Analyze(plan);
        return plan;
    }

    private static PlanStatement Single(ParsedPlan plan, string statementType) =>
        plan.Batches.SelectMany(b => b.Statements).Single(s => s.StatementType == statementType);

    [Fact]
    public void StatementCount_IsThree_OneConditionOneThenOneMultiplePlan()
    {
        // The condition's own plan (1) + the Then branch's RETURN (1) + the MULTIPLE PLAN sibling (1).
        // Condition never nests a Stmt* element, so it contributes exactly one statement.
        var plan = ParseAndAnalyze(ReproStatements);
        Assert.Equal(3, plan.Batches.SelectMany(b => b.Statements).Count());
    }

    [Fact]
    public void CondWithQuery_KeepsItsOwnTextHashesAndRealOperatorRoot()
    {
        var stmt = Single(ParseAndAnalyze(ReproStatements), "COND WITH QUERY");

        Assert.Equal("IF EXISTS (SELECT 1 FROM dbo.t WHERE id = @id)", stmt.StatementText);
        Assert.Equal("0x1111111111111111", stmt.QueryHash);
        Assert.Equal("0x2222222222222222", stmt.QueryPlanHash);

        // The statement-type wrapper holds the real Table Scan, not a bare STATEMENT placeholder.
        Assert.NotNull(stmt.RootNode);
        var operatorChild = Assert.Single(stmt.RootNode!.Children);
        Assert.Equal("Table Scan", operatorChild.PhysicalOp);
    }

    [Fact]
    public void CondWithQuery_KeepsItsMissingIndexSuggestion()
    {
        var plan = ParseAndAnalyze(ReproStatements);
        var stmt = Single(plan, "COND WITH QUERY");

        var mi = Assert.Single(stmt.MissingIndexes);
        Assert.Equal("dbo", mi.Schema);
        Assert.Equal("t", mi.Table);
        Assert.Equal(90.5, mi.Impact);
        Assert.Equal("id", Assert.Single(mi.EqualityColumns));

        // It also surfaces through the plan-wide rollup, not only on the statement.
        Assert.Single(plan.AllMissingIndexes);
    }

    [Fact]
    public void ThenBranch_StillHasItsReturnStatement()
    {
        var stmt = Single(ParseAndAnalyze(ReproStatements), "RETURN NONE");
        Assert.Equal("RETURN", stmt.StatementText);
    }

    [Fact]
    public void MultiplePlan_KeepsItsHashesAndPlaceholderRoot()
    {
        var stmt = Single(ParseAndAnalyze(ReproStatements), "MULTIPLE PLAN");

        Assert.Equal("0x3333333333333333", stmt.QueryHash);
        Assert.Equal("0x4444444444444444", stmt.QueryPlanHash);

        // No QueryPlan child in the XML, so the placeholder root stays (no operator tree to show).
        Assert.NotNull(stmt.RootNode);
        Assert.Equal("MULTIPLE PLAN", stmt.RootNode!.PhysicalOp);
    }

    [Fact]
    public void CondWithQuery_UdfSubPlanUnderCondition_AttachesToTheConditionStatement()
    {
        // The XSD lets Condition carry UDF sub-plans beside its QueryPlan. They used to become one
        // empty STATEMENT placeholder each, with the function's body lost.
        var plan = ParseAndAnalyze($"""
            <StmtCond StatementText="IF EXISTS (SELECT 1 FROM dbo.t WHERE dbo.f(id) = 1)" StatementId="1" StatementCompId="1" StatementType="COND WITH QUERY" QueryHash="0x5555555555555555" QueryPlanHash="0x6666666666666666">
              <Condition><QueryPlan CachedPlanSize="16" CompileTime="1" CompileCPU="1" CompileMemory="104">{TableScanRelOp}</QueryPlan>
                <UDF ProcName="[db].[dbo].[f]"><Statements><StmtSimple StatementText="RETURN @x + 1" StatementId="2" StatementCompId="3" StatementType="RETURN"/></Statements></UDF>
              </Condition>
              <Then><Statements><StmtSimple StatementText="RETURN" StatementId="3" StatementCompId="4" StatementType="RETURN NONE"/></Statements></Then>
            </StmtCond>
            """);

        var statements = plan.Batches.SelectMany(b => b.Statements).ToList();
        Assert.Equal(2, statements.Count);
        Assert.DoesNotContain(statements, s => s.StatementType.Length == 0);

        var cond = Single(plan, "COND WITH QUERY");
        var udf = Assert.Single(cond.UdfPlans);
        Assert.Equal("[db].[dbo].[f]", udf.ProcName);
        Assert.Equal("RETURN", Assert.Single(udf.Statements).StatementType);
    }

    [Fact]
    public void PlainCondition_WithoutAQueryOfItsOwn_AddsNoStatement()
    {
        var plan = ParseAndAnalyze("""
            <StmtCond StatementText="IF @x = 1" StatementId="1" StatementCompId="1" StatementType="COND">
              <Condition/>
              <Then><Statements><StmtSimple StatementText="RETURN" StatementId="2" StatementCompId="2" StatementType="RETURN NONE"/></Statements></Then>
            </StmtCond>
            """);

        var stmt = Assert.Single(plan.Batches.SelectMany(b => b.Statements));
        Assert.Equal("RETURN NONE", stmt.StatementType);
    }
}
