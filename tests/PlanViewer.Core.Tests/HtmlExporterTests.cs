using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

public class HtmlExporterTests
{
    [Fact]
    public void Export_ProducesValidHtml_WithWarnings()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("key_lookup_plan.sqlplan");
        foreach (var batch in plan.Batches)
            foreach (var stmt in batch.Statements)
                PlanLayoutEngine.Layout(stmt);

        var result = ResultMapper.Map(plan, "test-plan.sqlplan");
        var textOutput = TextFormatter.Format(result);

        var html = HtmlExporter.Export(result, textOutput);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("Performance Studio", html);
        Assert.Contains("plan-type", html);
        Assert.Contains("Full Text Analysis", html);
        // Should contain operator tree
        Assert.Contains("op-node", html);
        // Should contain the text analysis output
        Assert.Contains("=== Summary ===", html);
    }

    [Fact]
    public void Export_HandlesMultipleStatements()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("excellent-parallel-spill.sqlplan");
        foreach (var batch in plan.Batches)
            foreach (var stmt in batch.Statements)
                PlanLayoutEngine.Layout(stmt);

        var result = ResultMapper.Map(plan, "multi-stmt.sqlplan");
        var textOutput = TextFormatter.Format(result);

        var html = HtmlExporter.Export(result, textOutput);

        Assert.Contains("<!DOCTYPE html>", html);
        // Should encode HTML entities properly
        Assert.DoesNotContain("<script", html.Replace("<script>", "").Replace("</script>", ""));
    }

    [Fact]
    public void Export_EscapesHtmlInQueryText()
    {
        var plan = PlanTestHelper.LoadAndAnalyze("convert_implicit_plan.sqlplan");
        foreach (var batch in plan.Batches)
            foreach (var stmt in batch.Statements)
                PlanLayoutEngine.Layout(stmt);

        var result = ResultMapper.Map(plan, "test.sqlplan");
        var textOutput = TextFormatter.Format(result);

        var html = HtmlExporter.Export(result, textOutput);

        // The HTML should be well-formed — no unescaped angle brackets in user content
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
    }

    [Theory]
    [InlineData("Critical", "critical")]
    [InlineData("Warning", "warning")]
    [InlineData("Info", "info")]
    public void Export_KnownSeverity_KeepsItsClass(string severity, string cssClass)
    {
        var html = ExportWithSeverity(severity);

        Assert.Contains($"<div class=\"warning-item {cssClass}\">", html);
        Assert.Contains($"<span class=\"sev sev-{cssClass}\">{severity}</span>", html);
    }

    [Fact]
    public void Export_CraftedSeverity_CannotLeaveTheClassAttribute()
    {
        // A shared plan's analysis is caller-supplied JSON, so severity can hold markup.
        var html = ExportWithSeverity("\"><script>alert(1)</script><div class=\"");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("<div class=\"warning-item info\">", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public void Export_NullSeverity_ExportsAsInfo()
    {
        // JSON can send "severity": null, and the export used to throw on it.
        var html = ExportWithSeverity(null);

        Assert.Contains("<div class=\"warning-item info\">", html);
    }

    private static string ExportWithSeverity(string? severity)
    {
        var result = new AnalysisResult
        {
            Statements =
            {
                new StatementResult
                {
                    StatementText = "SELECT 1",
                    Warnings = { new WarningResult { Severity = severity!, Type = "demo", Message = "demo" } }
                }
            }
        };
        return HtmlExporter.Export(result, "demo");
    }
}
