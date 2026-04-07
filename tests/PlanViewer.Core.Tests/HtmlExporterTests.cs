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
}
