using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using ModelContextProtocol.Client;
using PlanViewer.App.Controls;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The text a person reads from plan analysis and the plan viewer holds no em dash. One rule, ported
/// from PerformanceMonitor's sweep (PM #7536, PR #8072): a pair of dashes around a short aside is
/// parentheses; a single dash is a comma when the clause continues, when the sentence already holds
/// a colon, or inside a parenthesis; a colon otherwise. A spill warning's lead ends in a full stop
/// before its "Granted:" figures, since those carry colons of their own. Code comments, SQL comments
/// in the generated repro script, the empty-cell placeholder, the "[low — high]" range and the
/// "Name — Detail" label separators keep their dashes: none of them is a clause.
/// </summary>
public class AnalysisTextPunctuationTests
{
    private const string EmDash = "—";

    [Fact]
    public void EveryFixture_AnalyzerAndParserWarningTextHoldsNoEmDash()
    {
        var plansDir = Path.Combine(AppContext.BaseDirectory, "Plans");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(plansDir, "*.sqlplan").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var plan = PlanTestHelper.LoadAndAnalyze(name);
            foreach (var w in PlanTestHelper.AllWarnings(plan))
            {
                if ((w.Message ?? string.Empty).Contains(EmDash, StringComparison.Ordinal))
                    offenders.Add($"{name}: {w.WarningType}: {w.Message}");
            }
        }

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("key_lookup_plan.sqlplan", "Key Lookup", "Key Lookup: SQL Server found rows via a nonclustered index")]
    [InlineData("spill_plan.sqlplan", "Sort Spill", "thread(s). Granted:")]
    [InlineData("row-count-spool-slow.sqlplan", "Hash Spill", "thread(s). Granted:")]
    [InlineData("exchange_spill_plan.sqlplan", "Exchange Spill", "Exchange spill: ")]
    public void WarningText_ReadsWithPlainPunctuation(string fixture, string warningType, string expected)
    {
        var warnings = PlanTestHelper.WarningsOfType(PlanTestHelper.LoadAndAnalyze(fixture), warningType);

        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void WaitLabels_ReadWithAColon()
    {
        Assert.Equal("I/O: reading data from disk", PlanAnalyzer.GetWaitLabel("PAGEIOLATCH_SH"));
        Assert.Equal("parallelism: thread skew", PlanAnalyzer.GetWaitLabel("CXPACKET"));
        Assert.Equal("network: client not consuming results", PlanAnalyzer.GetWaitLabel("ASYNC_NETWORK_IO"));

        foreach (var waitType in new[]
                 {
                     "PAGEIOLATCH_SH", "IO_COMPLETION", "SOS_SCHEDULER_YIELD", "CXPACKET", "CXCONSUMER", "CXSYNC_PORT",
                     "HTBUILD", "HTDELETE", "HTREPARTITION", "PAGELATCH_EX", "ASYNC_NETWORK_IO"
                 })
        {
            Assert.DoesNotContain(EmDash, PlanAnalyzer.GetWaitLabel(waitType), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReproScriptWarnings_ReadWithAColon_AndTheSqlCommentKeepsItsDash()
    {
        var sql = ReproScriptBuilder.BuildReproScript("SELECT * FROM #stage WHERE Id = @id", "db", planXml: null, isolationLevel: null);

        var headerEnd = sql.IndexOf("*/", StringComparison.Ordinal);
        Assert.True(headerEnd > 0, sql);
        var header = sql[..headerEnd];

        Assert.Contains(" - Plan XML not available: parameters could not be extracted", header, StringComparison.Ordinal);
        Assert.Contains(" - Query references temp table(s): reproduction may require creating the temp table first", header, StringComparison.Ordinal);
        Assert.Contains("These may be local variables: fill in values before executing.", header, StringComparison.Ordinal);
        Assert.DoesNotContain(EmDash, header, StringComparison.Ordinal);

        // The comment inside the generated T-SQL is a SQL comment, which the rule leaves alone.
        Assert.Contains("/* Parameters unknown — plan XML not available */", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlExport_WarningTextAndLegacyBadge_ReadWithPlainPunctuation()
    {
        var keyLookup = Export("key_lookup_plan.sqlplan");
        Assert.Contains("Key Lookup: SQL Server found rows via a nonclustered index", keyLookup, StringComparison.Ordinal);

        // local_variable_plan carries a legacy-rule warning, so the export renders the legacy badge.
        var localVariables = Export("local_variable_plan.sqlplan");
        Assert.Contains("title=\"Legacy rule: predates the benefit-scoring framework\"", localVariables, StringComparison.Ordinal);

        // The page <title> is a "Plan Analysis — source" label and keeps its separator; everything after it is prose.
        foreach (var html in new[] { keyLookup, localVariables })
        {
            var titleEnd = html.IndexOf("</title>", StringComparison.Ordinal);
            Assert.True(titleEnd > 0);
            Assert.DoesNotContain(EmDash, html[titleEnd..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ParametersPanel_AnnotationsAndSniffedTooltip_ReadWithPlainPunctuation()
    {
        HeadlessUi.Run(() =>
        {
            // optimize_for_unknown_plan has no parameter list and one @variable in its text.
            var localVariables = Annotations(Show(Load(FixtureXml("optimize_for_unknown_plan.sqlplan"))));
            Assert.Contains("Local variables detected (@rep): values not captured in plan XML", localVariables);

            /* The other annotations need a parameter list in a particular state, which no committed fixture is in.
               Each variant below is convert_implicit_parameter_side_plan with one thing changed: its one parameter
               @0 carries ParameterCompiledValue="(1)" ParameterRuntimeValue="(1)" and its statement text has no hint. */
            var xml = FixtureXml("convert_implicit_parameter_side_plan.sqlplan");
            const string values = " ParameterCompiledValue=\"(1)\" ParameterRuntimeValue=\"(1)\"";
            const string statementText = "StatementText=\"select * from dbo . IB03_GARANZ IB03 where UNIV_SEZIONI = @0";
            Assert.Contains(values, xml, StringComparison.Ordinal);
            Assert.Contains(statementText, xml, StringComparison.Ordinal);

            // A runtime value that differs from the compiled one: the sniffing tooltip on the runtime cell.
            var sniffed = Show(Load(xml.Replace(values, " ParameterCompiledValue=\"(1)\" ParameterRuntimeValue=\"(2)\"")));
            var tooltips = ParametersContent(sniffed).GetLogicalDescendants().OfType<TextBlock>()
                .Select(ToolTip.GetTip).OfType<string>().ToList();
            Assert.Contains("Runtime value differs from compiled: possible parameter sniffing", tooltips);

            // No compiled values at all, and no hint in the text: the plan was captured without them.
            var notCaptured = Annotations(Show(Load(xml.Replace(values, ""))));
            Assert.Contains("Parameter values not captured in the plan: no compiled values, so the values the optimizer sniffed are not known", notCaptured);

            // No compiled values and OPTION (RECOMPILE) in the text.
            var recompile = Annotations(Show(Load(xml.Replace(values, "")
                .Replace(statementText, statementText + " OPTION (RECOMPILE)"))));
            Assert.Contains("OPTION(RECOMPILE): parameter values embedded as literals, not sniffed", recompile);

            // No compiled values and the hint in the text.
            var optimizeForUnknown = Annotations(Show(Load(xml.Replace(values, "")
                .Replace(statementText, statementText + " OPTION (OPTIMIZE FOR UNKNOWN)"))));
            Assert.Contains("OPTIMIZE FOR UNKNOWN: optimizer used average density estimates instead of sniffed values", optimizeForUnknown);

            // A variable in the text that the parameter list does not carry.
            var unresolved = Annotations(Show(Load(xml.Replace(statementText, statementText + " and ALTRO = @extra"))));
            Assert.Contains("Unresolved variables: @extra, not in parameter list", unresolved);

            foreach (var text in localVariables.Concat(tooltips).Concat(notCaptured).Concat(recompile).Concat(optimizeForUnknown).Concat(unresolved))
                Assert.DoesNotContain(EmDash, text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RuntimeSummaryEmptyText_AndTheEmptyPlanError_ReadWithAColon()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = Show(Load(FixtureXml("key_lookup_plan.sqlplan")));
            var empty = viewer.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "RuntimeSummaryEmpty");
            Assert.Equal("Estimated plan: no runtime stats", empty.Text);

            var blank = new PlanViewerControl();
            Assert.False(blank.LoadPlan("   ", "empty.sqlplan"));
            Assert.Equal("The plan is empty: this source has no stored query plan XML.", blank.LastLoadError);
        });
    }

    [Fact]
    public async Task McpInstructionsAndToolDescriptions_ReadWithPlainPunctuation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await McpHostServiceTests.RunningServer.StartAsync(cancellationToken);

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = server.Address,
                TransportMode = HttpTransportMode.StreamableHttp
            }),
            cancellationToken: cancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var connections = tools.Single(tool => tool.Name == "get_connections");
        Assert.Contains("authentication types only: credentials are never exposed", connections.Description, StringComparison.Ordinal);
        foreach (var tool in tools)
            Assert.DoesNotContain(EmDash, tool.Description ?? string.Empty, StringComparison.Ordinal);

        var instructions = client.ServerInstructions;
        Assert.NotNull(instructions);
        Assert.Contains("1. `list_plans`: see what plans are loaded in the application", instructions, StringComparison.Ordinal);
        Assert.Contains("2. `analyze_plan` with the target session_id: get full analysis", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(EmDash, instructions, StringComparison.Ordinal);
    }

    private static string Export(string fixture)
    {
        var plan = PlanTestHelper.LoadAndAnalyze(fixture);
        foreach (var batch in plan.Batches)
            foreach (var stmt in batch.Statements)
                PlanLayoutEngine.Layout(stmt);

        var result = ResultMapper.Map(plan, fixture);
        return HtmlExporter.Export(result, TextFormatter.Format(result));
    }

    private static string FixtureXml(string fixture)
    {
        var path = Path.Combine("Plans", fixture);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        return File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    private static PlanViewerControl Load(string xml)
    {
        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, "punctuation.sqlplan"), $"Plan failed to load: {viewer.LastLoadError}");
        return viewer;
    }

    private static PlanViewerControl Show(PlanViewerControl viewer)
    {
        var window = new Window { Content = viewer, Width = 1400, Height = 900 };
        window.Show();
        window.UpdateLayout();
        return viewer;
    }

    private static StackPanel ParametersContent(PlanViewerControl viewer) =>
        viewer.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Name == "ParametersContent");

    /// <summary>The italic lines under the parameter grid: what the panel says about the parameters as a whole.</summary>
    private static List<string> Annotations(PlanViewerControl viewer) =>
        ParametersContent(viewer).Children.OfType<TextBlock>()
            .Where(t => t.FontStyle == FontStyle.Italic)
            .Select(t => t.Text ?? string.Empty)
            .ToList();
}
