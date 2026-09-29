using PlanViewer.Cli.Commands;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The web page's guard against a plan with nothing to show. The Blazor page is not part of this
/// test project, so the decision lives in <see cref="PlanStatements.NoStatementsMessage(PlanViewer.Core.Models.ParsedPlan)"/>,
/// which the page calls, and these tests pin that decision against the inputs that used to get
/// past the page. The page's own lines (show the message, keep the landing view) are too small to
/// need a test host.
/// </summary>
public class NoStatementsGuardTests
{
    private const string ShowPlanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private const string ExpectedMessage = "Could not parse any statements from the plan XML";

    public static TheoryData<string> PlansWithNoStatements => new()
    {
        // Well formed XML that is not a showplan at all.
        "<root><a/></root>",
        // A showplan whose batch holds no statements.
        $"<ShowPlanXML xmlns=\"{ShowPlanNamespace}\" Version=\"1.5\" Build=\"16.0.1\"><BatchSequence><Batch><Statements/></Batch></BatchSequence></ShowPlanXML>",
        // A showplan with no batch sequence.
        $"<ShowPlanXML xmlns=\"{ShowPlanNamespace}\" Version=\"1.5\" Build=\"16.0.1\"/>",
    };

    [Theory]
    [MemberData(nameof(PlansWithNoStatements))]
    public void PlanWithNoStatements_ParsesWithoutError_AndIsRefusedWithAMessage(string xml)
    {
        var plan = ShowPlanParser.Parse(xml);

        /* The premise of the bug: this is not a parse failure, so the page's ParseError check let it
           through, and the result it mapped had no statement for the view to draw. */
        Assert.Null(plan.ParseError);
        var result = ResultMapper.Map(plan, "pasted plan");
        Assert.Empty(result.Statements);

        Assert.Equal(ExpectedMessage, PlanStatements.NoStatementsMessage(plan));
        Assert.Equal(ExpectedMessage, PlanStatements.NoStatementsMessage(result));
    }

    [Fact]
    public void PlanWithNoStatements_GetsThroughEveryStepBeforeTheResultView()
    {
        /* Nothing the page runs between parsing and drawing throws for this input, so its catch
           blocks never saw it. Only the check this fix adds stops it. If one of these calls starts
           throwing for an empty plan, the page would show "Analysis failed" instead, which is
           still an error message rather than a crash, so this test is documentation, not a rule. */
        var plan = ShowPlanParser.Parse("<root><a/></root>");

        PlanAnalyzer.Analyze(plan);
        BenefitScorer.Score(plan);
        var result = ResultMapper.Map(plan, "pasted plan");
        var text = TextFormatter.Format(result);

        Assert.Empty(result.Statements);
        Assert.NotNull(text);
    }

    [Fact]
    public void PlanWithStatements_IsNotRefused()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("row_goal_plan.sqlplan");
        var result = ResultMapper.Map(plan, "pasted plan");

        Assert.NotEmpty(result.Statements);
        Assert.Null(PlanStatements.NoStatementsMessage(plan));
        Assert.Null(PlanStatements.NoStatementsMessage(result));
    }

    [Fact]
    public void ResultWithNoStatements_ReadBackFromTheShareServer_IsRefused()
    {
        // A stored result comes back as JSON, with no parsed plan to look at.
        var stored = new AnalysisResult();

        Assert.Empty(stored.Statements);
        Assert.Equal(ExpectedMessage, PlanStatements.NoStatementsMessage(stored));
    }

    [Theory]
    [MemberData(nameof(PlansWithNoStatements))]
    public void TheCliRefusesAPlanWithNoStatements(string xml)
    {
        /* "analyze <file>" always said this. "analyze --server" and "query-store" call the same
           ParseFailure, but it only looked at ParseError, so for a plan with no statements they
           wrote an empty analysis and reported OK. */
        Assert.Equal(ExpectedMessage, PlanAnalysisRunner.ParseFailure(ShowPlanParser.Parse(xml)));
    }

    [Fact]
    public void TheCliDoesNotRefuseAPlanWithStatements()
    {
        Assert.Null(PlanAnalysisRunner.ParseFailure(PlanTestHelper.LoadAndAnalyze("row_goal_plan.sqlplan")));
    }

    [Fact]
    public async Task AnalyzeOnAFileWithNoStatements_ExitsNonzeroWithTheSameMessage()
    {
        /* The single-file path had its own check, which now lives in ParseFailure with the others.
           This pins what a script sees: exit code 1, the message on stderr, no analysis on stdout. */
        var directory = Directory.CreateTempSubdirectory("planview-nostatements-");
        try
        {
            var file = Path.Combine(directory.FullName, "notaplan.xml");
            await File.WriteAllTextAsync(file, "<root><a/></root>", TestContext.Current.CancellationToken);

            var run = await CliProcess.RunAsync(
                directory.FullName, TestContext.Current.CancellationToken, "analyze", file);

            Assert.Equal(1, run.ExitCode);
            Assert.Contains(ExpectedMessage, run.StandardError);
            Assert.Equal("", run.StandardOutput.Trim());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
