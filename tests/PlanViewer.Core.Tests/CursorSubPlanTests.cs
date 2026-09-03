using System.Linq;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #491: #456 taught the parser to descend into StoredProc/UDF sub-plans, but that descent lives
/// in ParseStatement — and a StmtCursor's operation statements never go through ParseStatement.
/// They are built in ParseStatementAndChildren's cursor branch, straight from
/// CursorPlan &gt; Operation &gt; QueryPlan, so a function called by the cursor's query carried
/// its whole body in the XML (the Operation element holds the UDF sub-plan beside its QueryPlan)
/// and the parser dropped every statement of it. Same failure mode as #455: the output stayed
/// well-formed and plausible — the cursor's own query analyzed, the function body silently
/// contributed nothing.
///
/// <para>The plan XML here is generated rather than captured, on the depth-limit tests'
/// precedent: a cursor wrapping a UDF sub-plan is three nested element shapes, and a minimal
/// document states that more clearly than a 40KB fixture could. The element shapes mirror the
/// real showplan schema — StmtCursor &gt; CursorPlan &gt; Operation &gt; QueryPlan, with the UDF
/// element and its Statements sitting beside the QueryPlan under Operation — matching the
/// namespace and attributes the parser reads from committed fixtures.</para>
/// </summary>
public sealed class CursorSubPlanTests
{
    /* One cursor operation whose query calls dbo.CursorFn; the function body carries two
       statements — one bare (RETURN, no QueryPlan of its own, like the EXEC in #455) and one
       with a plan — so the descent is proven for both statement shapes. */
    private const string CursorOverUdfPlan =
        "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\" Version=\"1.564\" Build=\"16.0.4135.4\">" +
        "<BatchSequence><Batch><Statements>" +
        "<StmtCursor StatementText=\"DECLARE cur CURSOR FAST_FORWARD FOR SELECT dbo.CursorFn(o.Id) FROM dbo.Orders AS o\" StatementId=\"1\" StatementCompId=\"1\">" +
        "<CursorPlan CursorName=\"cur\" CursorActualType=\"FastForward\" CursorRequestedType=\"FastForward\" CursorConcurrency=\"Read Only\" ForwardOnly=\"true\">" +
        "<Operation OperationType=\"FetchQuery\">" +
        "<QueryPlan>" +
        "<RelOp NodeId=\"0\" PhysicalOp=\"Clustered Index Scan\" LogicalOp=\"Clustered Index Scan\" EstimateRows=\"10\" EstimatedTotalSubtreeCost=\"0.005\" />" +
        "</QueryPlan>" +
        "<UDF ProcName=\"dbo.CursorFn\" IsNativelyCompiled=\"false\">" +
        "<Statements>" +
        "<StmtSimple StatementText=\"RETURN @Id * 2\" StatementId=\"2\" StatementCompId=\"2\" />" +
        "<StmtSimple StatementText=\"SELECT @Total = COUNT(*) FROM dbo.Numbers AS n\" StatementId=\"3\" StatementCompId=\"3\">" +
        "<QueryPlan>" +
        "<RelOp NodeId=\"0\" PhysicalOp=\"Clustered Index Scan\" LogicalOp=\"Clustered Index Scan\" EstimateRows=\"100\" EstimatedTotalSubtreeCost=\"0.02\" />" +
        "</QueryPlan>" +
        "</StmtSimple>" +
        "</Statements>" +
        "</UDF>" +
        "</Operation>" +
        "</CursorPlan>" +
        "</StmtCursor>" +
        "</Statements></Batch></BatchSequence></ShowPlanXML>";

    /// <summary>
    /// The attach point, pinned the way #455 pinned the EXEC's: the cursor operation's statement
    /// is the one that must carry the function body, because it is the statement the operation's
    /// query belongs to — and it is built by a code path ParseStatement's descent never touches.
    /// </summary>
    [Fact]
    public void ACursorOperationStatementCarriesItsFunctionBody()
    {
        var plan = ShowPlanParser.Parse(CursorOverUdfPlan);

        Assert.Null(plan.ParseError);
        var operation = Assert.Single(Assert.Single(plan.Batches).Statements);

        // Proves this went down the cursor branch, not some fallback path.
        Assert.Equal("cur", operation.CursorName);

        var udf = Assert.Single(operation.UdfPlans);
        Assert.Equal("dbo.CursorFn", udf.ProcName);
        Assert.Equal(2, udf.Statements.Count);
        Assert.Equal("RETURN @Id * 2", udf.Statements[0].StatementText);
        Assert.StartsWith("SELECT @Total", udf.Statements[1].StatementText);
    }

    /// <summary>
    /// The shared traversal surfaces the body — which is the whole fix. Every consumer (#486)
    /// reads PlanStatements.EnumerateAll rather than walking batch.Statements itself, so once the
    /// bodies are here, the analyzer, the scorer, the mapper, and the statements grid all see
    /// them without any change of their own. Order matters like it did for #455: the operation
    /// comes first, its body follows in source order.
    /// </summary>
    [Fact]
    public void EnumerateAllSurfacesCursorFunctionBodyStatements()
    {
        var plan = ShowPlanParser.Parse(CursorOverUdfPlan);

        var all = PlanStatements.EnumerateAll(plan).ToList();
        var operation = Assert.Single(plan.Batches.SelectMany(b => b.Statements));

        Assert.Equal(3, all.Count);
        Assert.Same(operation, all[0]);
        Assert.Equal("RETURN @Id * 2", all[1].StatementText);
        Assert.StartsWith("SELECT @Total", all[2].StatementText);

        /* The context-carrying walk names the module, so a grid row for a body statement says
           where it came from instead of showing a bare SELECT beside the cursor's. */
        var entries = PlanStatements.EnumerateAllWithContainer(plan).ToList();
        Assert.Null(entries[0].ContainerPath);
        Assert.All(entries.Skip(1), e => Assert.Equal("dbo.CursorFn", e.ContainerPath));
    }

    /// <summary>
    /// Downstream of the traversal: the same document through analyze + score + map counts all
    /// three statements. TotalStatements is the number a report reader trusts first, and before
    /// this fix a cursor-over-UDF plan reported 1 — the #455 signature all over again, one
    /// container type later.
    /// </summary>
    [Fact]
    public void CursorFunctionBodyStatementsAreAnalyzedAndCounted()
    {
        var plan = ShowPlanParser.Parse(CursorOverUdfPlan);
        PlanAnalyzer.Analyze(plan);
        BenefitScorer.Score(plan);

        var result = ResultMapper.Map(plan, "cursor_over_udf");

        Assert.Equal(3, result.Summary.TotalStatements);
    }

    /// <summary>
    /// The StoredProc half of the same read. The published XSD only puts UDF under a cursor
    /// Operation, but the parser reads UDF and StoredProc as a pair everywhere else it descends
    /// ("XSD gap" reads — plans have carried elements the schema omits before), so the cursor
    /// branch reads both on purpose. Pinned so the symmetric half cannot rot into untested code.
    /// </summary>
    [Fact]
    public void ACursorOperationStoredProcSubPlanIsReadToo()
    {
        const string xml =
            "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\">" +
            "<BatchSequence><Batch><Statements>" +
            "<StmtCursor StatementText=\"DECLARE cur CURSOR FOR SELECT 1\">" +
            "<CursorPlan CursorName=\"cur\"><Operation OperationType=\"FetchQuery\">" +
            "<QueryPlan><RelOp NodeId=\"0\" /></QueryPlan>" +
            "<StoredProc ProcName=\"dbo.CursorProc\"><Statements>" +
            "<StmtSimple StatementText=\"SELECT 2\" />" +
            "</Statements></StoredProc>" +
            "</Operation></CursorPlan></StmtCursor>" +
            "</Statements></Batch></BatchSequence></ShowPlanXML>";

        var plan = ShowPlanParser.Parse(xml);

        Assert.Null(plan.ParseError);
        var operation = Assert.Single(Assert.Single(plan.Batches).Statements);
        Assert.NotNull(operation.StoredProcPlan);
        Assert.Equal("dbo.CursorProc", operation.StoredProcPlan!.ProcName);
        Assert.Equal("SELECT 2", Assert.Single(operation.StoredProcPlan.Statements).StatementText);
        Assert.Equal(2, PlanStatements.EnumerateAll(plan).Count());
    }
}
