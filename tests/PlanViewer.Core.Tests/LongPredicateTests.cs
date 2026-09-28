using System.Linq;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// A predicate or statement in plan XML from an unknown source can be millions of characters
/// long. The analyzer read each of these inputs again from many starting points, so the time it
/// took grew with the square of the length, and each one took half a minute or more. Now each
/// takes well under a second. The time limit is loose on purpose, so a slow machine does not
/// fail these tests.
/// </summary>
public class LongPredicateTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    // An unaliased scan of table t, so [t].[c] is its column and [@p] is not.
    private static readonly ScanIdentity Scan = new(null, "t", false, new HashSet<string>());

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    private static Task<T> Within<T>(Func<T> analyze) =>
        Task.Run(analyze, TestContext.Current.CancellationToken)
            .WaitAsync(Limit, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ARunOfOpeningBrackets()
    {
        var predicate = "[t].[c]=abs([@p])+" + new string('[', 150_000);

        Assert.Null(await Within(() => PlanAnalyzer.DetectNonSargablePattern(predicate, Scan)));
    }

    [Fact]
    public async Task ManyComparisonsWithAFunction()
    {
        // Only the last function wraps the column.
        var predicate = Repeat("[t].[c]=abs([@p]) AND ", 10_000) + "abs([t].[c])=(1)";

        Assert.Equal("Function call (ABS) on column",
            await Within(() => PlanAnalyzer.DetectNonSargablePattern(predicate, Scan)));
    }

    [Fact]
    public async Task ManyFunctionsInOneComparison()
    {
        var predicate = "[t].[c]=" + Repeat("abs([@p])+", 50_000) + "(1)";

        Assert.Null(await Within(() => PlanAnalyzer.DetectNonSargablePattern(predicate, Scan)));
    }

    [Fact]
    public async Task DeeplyNestedConversions()
    {
        var predicate = Repeat("CONVERT_IMPLICIT(int,", 60_000) + "[@p]" + Repeat(",0)", 60_000) + "=[t].[c]";

        Assert.Null(await Within(() => PlanAnalyzer.DetectNonSargablePattern(predicate, Scan)));
    }

    [Fact]
    public async Task ManyLikesWithNoPattern()
    {
        var predicate = Repeat("like ", 150_000);

        Assert.Null(await Within(() => PlanAnalyzer.DetectNonSargablePattern(predicate, Scan)));
    }

    [Fact]
    public async Task ManyCursorDeclarationsWithNoFor()
    {
        var statement = new PlanStatement { StatementText = Repeat("DECLARE c CURSOR ", 20_000) };

        await Within(() =>
        {
            PlanAnalyzer.Analyze(new ParsedPlan { Batches = [new PlanBatch { Statements = [statement] }] });
            return statement.PlanWarnings.Count;
        });
    }
}
