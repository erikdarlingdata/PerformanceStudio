using System.CommandLine;
using PlanViewer.Cli;
using PlanViewer.Cli.Commands;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// --output accepts json, text, or both. A value outside that set used to get through: the
/// query-store command and "analyze --server" wrote no files for it and exited 0, and
/// "analyze &lt;file&gt;" printed json. Now the command line is refused while it is parsed, before
/// the command does any work, and the file writer refuses it too.
/// </summary>
public class OutputOptionTests
{
    /// <summary>Every command in the tree Program.cs runs that declares --output.</summary>
    private static List<Command> CommandsWithOutputOption()
    {
        var found = new List<Command>();
        void Walk(Command command)
        {
            if (command.Options.Any(option => option.Name == "--output"))
                found.Add(command);
            foreach (var subcommand in command.Subcommands)
                Walk(subcommand);
        }

        Walk(CliRoot.Create(new InMemoryCredentialService()));
        return found;
    }

    /// <summary>The arguments that satisfy a command's required options, so the only error left is the one under test.</summary>
    private static List<string> RequiredArguments(Command command)
    {
        var args = new List<string>();
        foreach (var option in command.Options.Where(option => option.Required && option.Name != "--output"))
        {
            args.Add(option.Name);
            args.Add("x");
        }
        return args;
    }

    [Fact]
    public void TheSweepFindsBothCommandsThatTakeTheOption()
    {
        // Guards the two tests below against passing because they found nothing to check.
        var names = CommandsWithOutputOption().Select(command => command.Name).ToList();

        Assert.Contains("analyze", names);
        Assert.Contains("query-store", names);
    }

    [Fact]
    public void EveryCommandThatTakesOutput_RefusesAValueOutsideTheAcceptedSet()
    {
        foreach (var command in CommandsWithOutputOption())
        {
            foreach (var shortName in new[] { "--output", "-o" })
            {
                var args = RequiredArguments(command);
                args.AddRange(new[] { shortName, "bogus" });

                var errors = command.Parse(args.ToArray()).Errors.Select(error => error.Message).ToList();

                Assert.True(errors.Count > 0, $"{command.Name} {shortName} bogus was not refused");
                var message = string.Join("\n", errors);
                // The value that was refused, and every value that is allowed.
                Assert.Contains("bogus", message);
                foreach (var allowed in PlanAnalysisRunner.OutputFormats)
                    Assert.Contains(allowed, message);
            }
        }
    }

    [Fact]
    public void EveryCommandThatTakesOutput_StillAcceptsJsonTextAndBoth()
    {
        foreach (var command in CommandsWithOutputOption())
        {
            foreach (var value in new[] { "json", "text", "both" })
            {
                var args = RequiredArguments(command);
                args.AddRange(new[] { "--output", value });

                var parse = command.Parse(args.ToArray());

                Assert.True(parse.Errors.Count == 0,
                    $"{command.Name} --output {value}: {string.Join("; ", parse.Errors.Select(error => error.Message))}");
            }
        }
    }

    [Fact]
    public async Task AnalyzeOnAFile_WithAnUnknownOutput_ExitsNonzeroAndNamesTheAllowedValues()
    {
        // The exit code is Program.cs's, so run the built command the way a script would.
        var directory = Directory.CreateTempSubdirectory("planview-output-");
        try
        {
            var plan = Path.Combine(AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan");

            var run = await CliProcess.RunAsync(
                directory.FullName, TestContext.Current.CancellationToken, "analyze", plan, "--output", "bogus");

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("bogus", run.StandardError);
            foreach (var allowed in PlanAnalysisRunner.OutputFormats)
                Assert.Contains(allowed, run.StandardError);
            // It refused before doing the analysis, so nothing was printed as if it had worked.
            Assert.DoesNotContain("plan_source", run.StandardOutput);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task QueryStore_WithAnUnknownOutput_ExitsNonzeroBeforeItConnectsAnywhere()
    {
        var directory = Directory.CreateTempSubdirectory("planview-output-");
        try
        {
            // The server name is never contacted: the command line is refused while it is parsed.
            var run = await CliProcess.RunAsync(
                directory.FullName, TestContext.Current.CancellationToken,
                "query-store", "--server", "localhost", "--database", "x", "--output", "bogus");

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("bogus", run.StandardError);
            foreach (var allowed in PlanAnalysisRunner.OutputFormats)
                Assert.Contains(allowed, run.StandardError);
            Assert.DoesNotContain("Checking Query Store", run.StandardError);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static AnalysisResult MappedResult() =>
        ResultMapper.Map(PlanTestHelper.LoadAndAnalyze("row_goal_plan.sqlplan"), "row_goal_plan.sqlplan");

    [Theory]
    [InlineData("json", true, false)]
    [InlineData("text", false, true)]
    [InlineData("both", true, true)]
    public async Task TheFileWriter_WritesTheFilesEachAcceptedValueNames(string format, bool json, bool text)
    {
        var directory = Directory.CreateTempSubdirectory("planview-output-");
        try
        {
            await PlanAnalysisRunner.WriteResultFilesAsync(
                MappedResult(), directory.FullName, "plan", format, AnalysisJson.Indented, warningsOnly: false);

            Assert.Equal(json, File.Exists(Path.Combine(directory.FullName, "plan.analysis.json")));
            Assert.Equal(text, File.Exists(Path.Combine(directory.FullName, "plan.analysis.txt")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("JSON")]
    [InlineData("")]
    public async Task TheFileWriter_RefusesAnyOtherValue_InsteadOfWritingNothing(string format)
    {
        var directory = Directory.CreateTempSubdirectory("planview-output-");
        try
        {
            var result = MappedResult();
            Assert.Contains(result.Statements, statement => statement.OperatorTree != null);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                PlanAnalysisRunner.WriteResultFilesAsync(
                    result, directory.FullName, "plan", format, AnalysisJson.Indented, warningsOnly: true));

            Assert.Contains("json, text, both", exception.Message);
            Assert.Empty(directory.GetFiles());
            // It refused before it did the warnings-only trim, so the caller's result is unchanged.
            Assert.Contains(result.Statements, statement => statement.OperatorTree != null);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task QueryStore_IfAnUnknownFormatGetsPastTheOption_CountsEveryPlanAsFailed()
    {
        // The backstop: the file writer's refusal is a per-plan failure, and a failed plan is a nonzero exit.
        var directory = Directory.CreateTempSubdirectory("planview-output-");
        try
        {
            var xml = File.ReadAllText(Path.Combine("Plans", "row_goal_plan.sqlplan"))
                .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
            var plans = new List<QueryStorePlan>
            {
                new() { QueryId = 1, PlanId = 10, PlanXml = xml },
                new() { QueryId = 2, PlanId = 20, PlanXml = xml },
            };

            var failed = await QueryStoreCommand.AnalyzePlansAsync(
                plans, "TestDb", "cpu", hoursBack: 24, directory.FullName, outputFormat: "bogus",
                compact: false, warningsOnly: false, AnalyzerConfig.Default, serverMetadata: null, new StringWriter());

            Assert.Equal(2, failed);
            Assert.Empty(directory.GetFiles("*.analysis.*"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
