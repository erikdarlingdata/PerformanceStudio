using PlanViewer.Cli.Commands;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// "planview query-store" used to report a plan that failed to analyze on stderr and in
/// summary.txt, then exit 0, so a script or a CI job could not tell the run was incomplete. The
/// command hands the fetched plans to <see cref="QueryStoreCommand.AnalyzePlansAsync"/>, which
/// returns how many failed; the command turns a nonzero count into exit code 1, as
/// "analyze --server" does. The fetch needs a live SQL Server, so these tests start from the plans.
/// </summary>
public class QueryStoreAnalyzePlansTests
{
    private static string GoodPlanXml() =>
        File.ReadAllText(Path.Combine("Plans", "row_goal_plan.sqlplan"))
            .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

    private static QueryStorePlan Plan(long queryId, long planId, string planXml) => new()
    {
        QueryId = queryId,
        PlanId = planId,
        QueryHash = "0x1AB2C3D4",
        QueryText = "SELECT 1;",
        PlanXml = planXml,
        CountExecutions = 5,
        TotalCpuTimeUs = 5000
    };

    private static Task<int> Analyze(List<QueryStorePlan> plans, DirectoryInfo directory, StringWriter log) =>
        QueryStoreCommand.AnalyzePlansAsync(
            plans, "TestDb", "cpu", hoursBack: 24, directory.FullName, outputFormat: "both",
            compact: false, warningsOnly: false, AnalyzerConfig.Default, serverMetadata: null, log);

    [Fact]
    public async Task FailedPlansAreCounted_AndEveryOtherPlanStillGetsItsFiles()
    {
        var directory = Directory.CreateTempSubdirectory("planview-qs-");
        try
        {
            var log = new StringWriter();
            var goodXml = GoodPlanXml();
            var plans = new List<QueryStorePlan>
            {
                Plan(1, 10, goodXml),
                Plan(2, 20, "<ShowPlanXML"),        // not well formed XML
                Plan(3, 30, "<root><a/></root>"),   // parses, but has no statement
                Plan(4, 40, goodXml),               // after two failures, still analyzed
            };

            var failed = await Analyze(plans, directory, log);

            Assert.Equal(2, failed);

            foreach (var worked in new[] { "query_1_plan_10", "query_4_plan_40" })
            {
                Assert.True(File.Exists(Path.Combine(directory.FullName, $"{worked}.sqlplan")), worked);
                Assert.True(File.Exists(Path.Combine(directory.FullName, $"{worked}.analysis.json")), worked);
                Assert.True(File.Exists(Path.Combine(directory.FullName, $"{worked}.analysis.txt")), worked);
            }

            foreach (var broke in new[] { "query_2_plan_20", "query_3_plan_30" })
            {
                Assert.False(File.Exists(Path.Combine(directory.FullName, $"{broke}.analysis.json")), broke);
                Assert.False(File.Exists(Path.Combine(directory.FullName, $"{broke}.analysis.txt")), broke);
            }

            // The summary keeps one row per plan, and says which ones failed and why.
            var rows = File.ReadAllLines(Path.Combine(directory.FullName, QueryStoreCommand.SummaryFileName))
                .Where(line => line.TrimStart().Length > 0 && char.IsDigit(line.TrimStart()[0]))
                .ToList();
            Assert.Equal(4, rows.Count);
            var errorRows = rows.Where(row => row.Contains("ERROR:")).ToList();
            Assert.Equal(2, errorRows.Count);
            Assert.Contains("Could not parse the plan XML", errorRows[0]);
            Assert.Contains("Could not parse any statements from the plan XML", errorRows[1]);

            // And it says so on the log, which is stderr for the command.
            Assert.Equal(2, log.ToString().Split("ERROR:").Length - 1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WhenEveryPlanWorks_NothingIsCountedAsFailed()
    {
        var directory = Directory.CreateTempSubdirectory("planview-qs-");
        try
        {
            var goodXml = GoodPlanXml();
            var plans = new List<QueryStorePlan> { Plan(1, 10, goodXml), Plan(4, 40, goodXml) };

            var failed = await Analyze(plans, directory, new StringWriter());

            Assert.Equal(0, failed);
            Assert.True(File.Exists(Path.Combine(directory.FullName, QueryStoreCommand.SummaryFileName)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
