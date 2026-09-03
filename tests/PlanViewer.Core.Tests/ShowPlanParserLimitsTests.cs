using System.Text;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #456's stored-procedure descent called ParseStatementAndChildren without passing the depth
/// argument, so the parser's recursion depth silently reset to zero at every StoredProc/UDF
/// boundary. The MaxParseDepth guard — which exists precisely so a maliciously deep plan throws
/// a catchable error instead of an uncatchable StackOverflowException — could therefore never
/// fire across procedure nesting, and roughly sixty bytes of XML per level bought an attacker
/// one more stack frame pair on every plan-open route that feeds the parser.
///
/// <para>These tests pin both parser input ceilings: recursion depth across procedure bodies,
/// and the synchronous Parse path's document-size cap. ParseAsync always had the cap through
/// XmlReaderSettings.MaxCharactersInDocument; Parse — the path used by the app's
/// PlanViewerControl, the web viewer, and the analysis pipeline — had none.</para>
///
/// <para>The plan XML here is generated rather than loaded from a fixture: a depth bomb is the
/// same three elements repeated a thousand-odd times, and a loop states that more honestly than
/// a 70KB fixture file could.</para>
/// </summary>
public sealed class ShowPlanParserLimitsTests
{
    /// <summary>
    /// Nesting chosen to fail safely in both directions. With the guard carrying through, it
    /// fires at depth 1,001 — about two thousand frames, nowhere near exhaustion. If the depth
    /// reset ever regresses, this nesting is shallow enough to parse to completion on the
    /// big-stack thread below, so the test fails on the ParseError assert (it stays null)
    /// instead of killing the test host — verified by running it against the unfixed parser.
    /// </summary>
    private const int BombDepth = ShowPlanParser.MaxParseDepth + 100;

    [Fact]
    public void ProcedureNestingPastTheDepthLimitFailsWithACatchableError()
    {
        var xml = NestedProcedurePlan(BombDepth);

        /* A dedicated thread with a deliberately generous stack, so the test's two outcomes
           stay deterministic regardless of build config or future frame-size drift: guard
           working -> depth error long before the stack matters; guard regressed -> the parse
           COMPLETES in the headroom (instead of gambling on where overflow lands) and the
           assert below reports the miss. */
        ParsedPlan? plan = null;
        var thread = new Thread(() => plan = ShowPlanParser.Parse(xml), 8 * 1024 * 1024);
        thread.Start();
        thread.Join();

        Assert.NotNull(plan);
        Assert.NotNull(plan!.ParseError);
        Assert.Contains("depth limit", plan.ParseError);
    }

    /// <summary>
    /// The other direction: carrying depth through procedure bodies must not reject legitimate
    /// nesting. Every level below the limit still parses, in order, all the way down.
    /// </summary>
    [Fact]
    public void ProcedureNestingBelowTheDepthLimitStillParsesEveryLevel()
    {
        const int depth = 50;

        var plan = ShowPlanParser.Parse(NestedProcedurePlan(depth));

        Assert.Null(plan.ParseError);
        var stmt = Assert.Single(Assert.Single(plan.Batches).Statements);
        var levels = 0;
        while (stmt.StoredProcPlan is not null)
        {
            stmt = Assert.Single(stmt.StoredProcPlan.Statements);
            levels++;
        }
        Assert.Equal(depth, levels);
    }

    [Fact]
    public void SynchronousParseRejectsOversizedInput()
    {
        /* Well-formed XML on purpose: if the size cap regressed, this input would parse
           cleanly and ParseError would stay null, so the test fails on the assert instead of
           passing by accident on a syntax error. Whitespace inside the root pads it just past
           the limit; the cap is in characters, matching MaxCharactersInDocument's unit on the
           async path. */
        var xml = "<ShowPlanXML>"
            + new string(' ', ShowPlanParser.MaxParseCharacters)
            + "</ShowPlanXML>";

        var plan = ShowPlanParser.Parse(xml);

        Assert.NotNull(plan.ParseError);
        Assert.Contains("size limit", plan.ParseError);
    }

    /// <summary>
    /// StmtSimple > StoredProc > Statements repeated <paramref name="levels"/> times around one
    /// innermost bare statement — the exact shape whose depth #456's descent stopped counting.
    /// </summary>
    private static string NestedProcedurePlan(int levels)
    {
        var xml = new StringBuilder(
            "<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>");
        for (var level = 0; level < levels; level++)
            xml.Append("<StmtSimple StatementText=\"EXEC p\"><StoredProc ProcName=\"p\"><Statements>");
        xml.Append("<StmtSimple StatementText=\"SELECT 1\" />");
        for (var level = 0; level < levels; level++)
            xml.Append("</Statements></StoredProc></StmtSimple>");
        xml.Append("</Statements></Batch></BatchSequence></ShowPlanXML>");
        return xml.ToString();
    }
}
