using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PlanViewer.App.Mcp;
using PlanViewer.Cli.Commands;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #589: the parser walked the plan tree on the caller's thread. On a 1 MB thread, the size of the
/// app's UI thread and the CLI's main thread, that walk overflowed the stack at about 450-480
/// nested operators and ended the process, long before MaxParseDepth (1,000) could refuse the
/// plan. The walk now runs on its own large-stack thread.
///
/// <para>The depth tests run on real threads with small stacks: 1 MB, or less where a step
/// should need less. A regression there does not fail an assert: it crashes the test host with a
/// stack overflow, which is loud. The tests were run against the unfixed code to confirm that.</para>
/// </summary>
public sealed class DeepPlanStackTests
{
    private const int OneMegabyte = 1024 * 1024;

    /// <summary>
    /// Nodes on the longest path of a plan at the depth limit: operator levels 0 through
    /// MaxParseDepth, plus the statement's own node above them.
    /// </summary>
    private const int LevelsAtTheLimit = ShowPlanParser.MaxParseDepth + 2;

    [Fact]
    public void APlanAtTheDepthLimitParsesOnAOneMegabyteThread()
    {
        var xml = NestedLoopsPlan(ShowPlanParser.MaxParseDepth);

        var plan = OnOneMegabyteThread(() => ShowPlanParser.Parse(xml));

        Assert.Null(plan.ParseError);
        Assert.Equal(LevelsAtTheLimit, TreeDepth(RootOf(plan)));
    }

    [Fact]
    public void OneLevelPastTheDepthLimitIsRefusedWithAParseError()
    {
        var xml = NestedLoopsPlan(ShowPlanParser.MaxParseDepth + 1);

        var plan = OnOneMegabyteThread(() => ShowPlanParser.Parse(xml));

        Assert.NotNull(plan.ParseError);
        Assert.Contains("depth limit", plan.ParseError);
    }

    [Fact]
    public void TheAsyncParseAlsoWalksOnItsOwnStack()
    {
        /* ParseAsync is what the analysis pipeline's async path calls. Without the parse thread,
           its walk ran on whichever thread the XML load finished on: this one, or a thread-pool
           thread, and neither holds 1,000 levels. */
        var xml = NestedLoopsPlan(ShowPlanParser.MaxParseDepth);

        var plan = OnOneMegabyteThread(
            () => ShowPlanParser.ParseAsync(xml, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Null(plan.ParseError);
        Assert.Equal(LevelsAtTheLimit, TreeDepth(RootOf(plan)));
    }

    [Fact]
    public void TheSearchInsideAnOperatorKeepsItsOwnStack()
    {
        /* No depth guard counts the elements inside an operator, and the recursive iterator that
           searched them used stack for every level: 150,000 levels overflowed even the parse
           thread's 32 MB. The search now keeps its own stack, so 20,000 levels, which needed
           several MB before, fit in 256 KB. The tree is built directly, because XML that deep is
           slow to load. */
        XNamespace showplan = Showplan;
        var nested = new XElement(showplan + "W", new XElement(showplan + "Object"));
        for (var level = 0; level < 20_000; level++)
            nested = new XElement(showplan + "W", nested);
        var hash = new XElement(showplan + "Hash", nested);

        var found = OnThread(256 * 1024, () => ShowPlanParser.ScopedDescendants(hash, showplan + "Object").ToList());

        Assert.Single(found);
    }

    [Fact]
    public void EveryStepAfterTheParseRunsOnAOneMegabyteThread()
    {
        /* The parse thread covers only the parse. The steps after it walk the same 1,000-level
           tree on the caller's thread, and the app and the CLI both call them on a 1 MB thread.
           Measured at 1,000 levels in a child process, each step now needs 384 KB or less. Before
           #589 changed them, the result mapper needed most of 1 MB and the HTML exporter 512 KB. */
        var xml = NestedLoopsPlan(ShowPlanParser.MaxParseDepth);

        OnOneMegabyteThread(() =>
        {
            var plan = PlanAnalysisPipeline.Analyze(xml, new AnalyzerConfig());
            Assert.Null(plan.ParseError);
            foreach (var statement in plan.Batches.SelectMany(batch => batch.Statements))
                PlanLayoutEngine.Layout(statement);

            var result = ResultMapper.Map(plan, "deep.sqlplan");
            Assert.Equal(LevelsAtTheLimit, TreeDepth(result.Statements.Single().OperatorTree!));
            var text = TextFormatter.Format(result);
            Assert.NotEmpty(HtmlExporter.Export(result, text));

            /* JSON stops at AnalysisJson.MaxDepth, about 500 operator levels, with an exception that
               every caller turns into AnalysisJson.TooDeepMessage. */
            Assert.Throws<JsonException>(() => JsonSerializer.Serialize(result, AnalysisJson.Indented));
            return plan;
        });
    }

    [Fact]
    public void TheResultMapperKeepsItsOwnStack()
    {
        /* The mapper recursed once per operator level and needed more than 768 KB at 1,000
           levels. It now keeps its own stack, so the whole tree maps on a 256 KB thread. */
        var xml = NestedLoopsPlan(ShowPlanParser.MaxParseDepth);

        var result = OnThread(256 * 1024, () => ResultMapper.Map(ShowPlanParser.Parse(xml), "deep.sqlplan"));

        Assert.Equal(LevelsAtTheLimit, TreeDepth(result.Statements.Single().OperatorTree!));
    }

    [Fact]
    public void TheHtmlExportersRecursionKeepsASmallFrame()
    {
        /* Each operator's line is written in a separate method, so the recursive method's frame
           stays small. Measured at 1,000 levels in a child process: the exporter now runs in
           256 KB in Release and 320 KB in Debug. Before #589 it needed more than 384 KB in
           Release and more than 640 KB in Debug, so this crashes if the split is undone. */
        var xml = NestedLoopsPlan(ShowPlanParser.MaxParseDepth);
        var (result, text) = OnOneMegabyteThread(() =>
        {
            var mapped = ResultMapper.Map(ShowPlanParser.Parse(xml), "deep.sqlplan");
            return (mapped, TextFormatter.Format(mapped));
        });

        var html = OnThread(384 * 1024, () => HtmlExporter.Export(result, text));

        var operatorsWritten = html.Split("<div class=\"op-node").Length - 1;
        Assert.Equal(NodeCount(result.Statements.Single().OperatorTree!), operatorsWritten);
    }

    [Fact]
    public void TheSearchInsideAnOperatorKeepsDocumentOrderAndSkipsChildOperators()
    {
        /* The loop must return what the recursive iterator did: every match in document order,
           including matches inside a match, and nothing inside a nested RelOp, which belongs to
           another operator. */
        XNamespace showplan = Showplan;
        var hash = new XElement(showplan + "Hash",
            new XElement(showplan + "W",
                ObjectElement("1"),
                new XElement(showplan + "W", ObjectElement("2"))),
            new XElement(showplan + "RelOp", ObjectElement("inside another operator")),
            new XElement(showplan + "Object", new XAttribute("Id", "3"), ObjectElement("4")));

        var found = ShowPlanParser.ScopedDescendants(hash, showplan + "Object")
            .Select(element => element.Attribute("Id")!.Value);

        Assert.Equal(new[] { "1", "2", "3", "4" }, found);
    }

    [Fact]
    public void TheCliStopsWithTheParseError()
    {
        /* Before #589, the CLI's live path and query-store command wrote an empty analysis for a
           plan that did not parse, and reported OK. */
        var refused = ShowPlanParser.Parse(NestedLoopsPlan(ShowPlanParser.MaxParseDepth + 1));
        var parsed = ShowPlanParser.Parse(NestedLoopsPlan(3));

        Assert.Equal($"Could not parse the plan XML: {refused.ParseError}", PlanAnalysisRunner.ParseFailure(refused));
        Assert.Null(PlanAnalysisRunner.ParseFailure(parsed));
    }

    [Fact]
    public void JsonPastTheDepthLimitReportsTheRealCause()
    {
        /* Past about 500 operator levels the serializer's own message blames "a possible object
           cycle". The CLI and the MCP tools say what is really wrong. */
        var xml = NestedLoopsPlan(600);

        OnOneMegabyteThread(() =>
        {
            var result = ResultMapper.Map(ShowPlanParser.Parse(xml), "deep.sqlplan");

            var cli = Assert.Throws<InvalidOperationException>(
                () => PlanAnalysisRunner.SerializeResult(result, AnalysisJson.Indented));
            Assert.StartsWith(AnalysisJson.TooDeepMessage, cli.Message);
            Assert.IsType<JsonException>(cli.InnerException);

            var tooDeep = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(result, AnalysisJson.Indented));
            Assert.Equal($"Error during analyze_plan: {AnalysisJson.TooDeepMessage}", McpHelpers.FormatError("analyze_plan", tooDeep));
            Assert.Equal("Error during analyze_plan: boom", McpHelpers.FormatError("analyze_plan", new InvalidOperationException("boom")));
            return result;
        });
    }

    [Fact]
    public void TheParseThreadUsesTheCallersCulture()
    {
        /* The parser writes some warning text itself, such as a spill's granted memory, in the
           current culture. The caller's culture reaches the parse thread with the execution
           context; this fails if the thread is ever started without it. */
        var xml = File.ReadAllText(Path.Combine("Plans", "spill_plan.sqlplan"));

        var plan = OnOneMegabyteThread(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            return ShowPlanParser.Parse(xml);
        });

        Assert.Null(plan.ParseError);
        var spill = PlanTestHelper.AllWarnings(plan).First(warning => warning.Message.Contains("Granted:"));
        Assert.Contains("Granted: 10.815.552 KB", spill.Message);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a new thread with the given stack and rethrows anything it
    /// throws, so an assert inside it fails the test instead of ending the process.
    /// </summary>
    private static T OnOneMegabyteThread<T>(Func<T> work) => OnThread(OneMegabyte, work);

    private static T OnThread<T>(int stackBytes, Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }, stackBytes);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static PlanNode RootOf(ParsedPlan plan) =>
        Assert.Single(Assert.Single(plan.Batches).Statements).RootNode!;

    private static int TreeDepth(PlanNode root) =>
        Deepest(root, node => node.Children);

    private static int TreeDepth(OperatorResult root) =>
        Deepest(root, node => node.Children);

    private static int Deepest<TNode>(TNode root, Func<TNode, IEnumerable<TNode>> children)
    {
        var deepest = 0;
        var pending = new Stack<(TNode Node, int Depth)>();
        pending.Push((root, 1));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            deepest = Math.Max(deepest, depth);
            foreach (var child in children(node))
                pending.Push((child, depth + 1));
        }
        return deepest;
    }

    private static int NodeCount(OperatorResult root)
    {
        var count = 0;
        var pending = new Stack<OperatorResult>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            count++;
            foreach (var child in pending.Pop().Children)
                pending.Push(child);
        }
        return count;
    }

    private static XElement ObjectElement(string id) =>
        new(XName.Get("Object", Showplan), new XAttribute("Id", id));

    private const string Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>
    /// An actual plan with <paramref name="levels"/> nested Nested Loops, each with a Constant
    /// Scan on its inner side, above a Constant Scan. Runtime counters on every operator make the
    /// analyzer's timing rules walk the whole tree too.
    /// </summary>
    private static string NestedLoopsPlan(int levels)
    {
        var xml = new StringBuilder();
        xml.Append($"<ShowPlanXML xmlns=\"{Showplan}\" Version=\"1.6\" Build=\"17.0.1000.7\"><BatchSequence><Batch><Statements>");
        xml.Append("<StmtSimple StatementText=\"SELECT 1\" StatementId=\"1\" StatementCompId=\"1\" StatementType=\"SELECT\" StatementSubTreeCost=\"10\" StatementEstRows=\"1\">");
        xml.Append("<QueryPlan DegreeOfParallelism=\"1\" CachedPlanSize=\"16\"><QueryTimeStats CpuTime=\"5000\" ElapsedTime=\"5000\" />");
        var nodeId = 0;
        for (var level = 0; level < levels; level++)
        {
            var elapsed = 5000 - level * 4;
            xml.Append($"<RelOp NodeId=\"{nodeId++}\" PhysicalOp=\"Nested Loops\" LogicalOp=\"Inner Join\" EstimateRows=\"10\" EstimatedTotalSubtreeCost=\"1\" EstimateCPU=\"0.001\" EstimateIO=\"0\" AvgRowSize=\"11\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\"><OutputList />");
            xml.Append($"<RunTimeInformation><RunTimeCountersPerThread Thread=\"0\" ActualRows=\"{1000 + level}\" ActualEndOfScans=\"1\" ActualExecutions=\"1\" ActualElapsedms=\"{elapsed}\" ActualCPUms=\"{elapsed}\" /></RunTimeInformation>");
            xml.Append("<NestedLoops Optimized=\"0\">");
        }
        xml.Append(ConstantScan(nodeId++));
        for (var level = 0; level < levels; level++)
            xml.Append(ConstantScan(nodeId++)).Append("</NestedLoops></RelOp>");
        xml.Append("</QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>");
        return xml.ToString();
    }

    private static string ConstantScan(int nodeId) =>
        $"<RelOp NodeId=\"{nodeId}\" PhysicalOp=\"Constant Scan\" LogicalOp=\"Constant Scan\" EstimateRows=\"1\" EstimatedTotalSubtreeCost=\"0.001\" EstimateCPU=\"0.001\" EstimateIO=\"0\" AvgRowSize=\"9\" Parallel=\"0\" EstimateRebinds=\"0\" EstimateRewinds=\"0\"><OutputList />"
        + "<RunTimeInformation><RunTimeCountersPerThread Thread=\"0\" ActualRows=\"1\" ActualEndOfScans=\"1\" ActualExecutions=\"1\" ActualElapsedms=\"0\" ActualCPUms=\"0\" /></RunTimeInformation>"
        + "<ConstantScan /></RelOp>";
}
