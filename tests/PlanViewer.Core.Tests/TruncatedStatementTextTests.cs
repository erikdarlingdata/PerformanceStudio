using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;
using PlanViewer.Core.Models;
using PlanViewer.Core.Output;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #502: SQL Server caps StatementText at 4,000 characters when it writes showplan XML, so for a
/// long query every consumer of the plan — advice, Copy Query Text, Open in Query Editor — was
/// handing back a query that stops mid-statement. The reporter's symptom was the second-order one:
/// the shortened text is not valid T-SQL, so re-running or formatting it failed with a syntax error
/// that pointed nowhere near the truncation.
///
/// <para>Two halves. The plan says so (Rule 39), and where the full query was captured alongside the
/// plan it is handed out instead of the plan's short copy.</para>
/// </summary>
public class TruncatedStatementTextTests
{
    private const string CapturedQuery = "SELECT 'the full query the session actually ran';";

    // ---------------------------------------------------------------
    // Rule 39: say so on the plan
    // ---------------------------------------------------------------

    [Fact]
    public void Rule39_FlagsAStatementThatHitTheCap()
    {
        var plan = AnalyzeStatementOfLength(PlanStatement.TruncationLengthThreshold);

        var warning = Assert.Single(PlanTestHelper.WarningsOfType(plan, "Truncated Query Text"));
        Assert.Equal(PlanWarningSeverity.Info, warning.Severity);
        Assert.Contains("4,000 characters", warning.Message);
    }

    [Fact]
    public void Rule39_SaysNothingAboutAQueryThatFits()
    {
        var plan = AnalyzeStatementOfLength(PlanStatement.TruncationLengthThreshold - 1);

        Assert.Empty(PlanTestHelper.WarningsOfType(plan, "Truncated Query Text"));
    }

    [Fact]
    public void Rule39_CanBeTurnedOff()
    {
        var stmt = StatementOfLength(PlanStatement.TruncationLengthThreshold);
        var plan = new ParsedPlan();
        plan.Batches.Add(new PlanBatch { Statements = { stmt } });

        PlanAnalyzer.Analyze(plan, new AnalyzerConfig { Rules = new RulesConfig { Disabled = { 39 } } });

        Assert.Empty(PlanTestHelper.WarningsOfType(plan, "Truncated Query Text"));
    }

    // ---------------------------------------------------------------
    // Hand back the captured query instead of the plan's short copy
    // ---------------------------------------------------------------

    [Fact]
    public void SingleStatement_OpenInQueryEditor_HandsBackTheCapturedQuery()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("isnull_plan.sqlplan", Truncated, CapturedQuery);

            Assert.Equal(CapturedQuery, OpenInEditorText(viewer));
        });
    }

    /// <summary>
    /// A plan opened from a file has no captured text, and nothing can invent it — the short copy is
    /// all there is. Rule 39 is what tells the user why.
    /// </summary>
    [Fact]
    public void SingleStatement_WithNothingCaptured_StillHandsBackThePlanText()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("isnull_plan.sqlplan", Truncated, queryText: null);

            var text = OpenInEditorText(viewer);
            Assert.NotEqual(CapturedQuery, text);
            Assert.True(text!.Length >= PlanStatement.TruncationLengthThreshold);
        });
    }

    /// <summary>
    /// The captured text is the whole batch and showplan records no statement offsets, so for a
    /// multi-statement plan there is no way to tell which slice belongs to the selected row. Handing
    /// back a confidently wrong statement would be worse than handing back a short one.
    /// </summary>
    [Fact]
    public void MultiStatement_KeepsThePlanTextEvenThoughItIsTruncated()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("eager_table_spool_plan.sqlplan", Truncated, CapturedQuery);

            Assert.NotEqual(CapturedQuery, OpenInEditorText(viewer));
        });
    }

    /// <summary>
    /// A plan captured around EXEC has one statement in its batch and several rows in the grid, since
    /// the grid descends into the procedure body. Counting batch statements would call that
    /// single-statement and hand the outer EXEC back for a truncated body statement — a confidently
    /// wrong answer, which is worse than a short one. The count has to match what the grid lists.
    /// </summary>
    [Fact]
    public void StoredProcedureBody_IsNotTreatedAsASingleStatementPlan()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("exec_stored_procedure_plan.sqlplan", Truncated, CapturedQuery);

            // Pins that this fixture actually exercises the bug: counting batches calls it
            // single-statement, so a test that passes here is passing for the right reason.
            Assert.Equal(1, viewer.CurrentPlan!.Batches.Sum(b => b.Statements.Count));

            Assert.NotEqual(CapturedQuery, OpenInEditorText(viewer));
        });
    }

    [Fact]
    public void UntruncatedPlan_IsUnaffectedByAnyOfThis()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("isnull_plan.sqlplan", xml => xml, CapturedQuery);

            Assert.NotEqual(CapturedQuery, OpenInEditorText(viewer));
        });
    }

    // ---------------------------------------------------------------
    // The advice reads the captured query too (#502 follow-up)
    // ---------------------------------------------------------------
    //
    // The first cut recovered the full query for Copy Query Text and Open in Query Editor but left
    // Human Advice, Robot Advice, and the MCP tools analyzing the plan's short copy — which is the
    // surface the reporter actually screenshotted, so the fix read as no fix at all. The mapping is
    // where every one of those surfaces gets its statement text, so the recovery lands there, under
    // the same single-statement guard.

    [Fact]
    public void Advice_SingleTruncatedStatement_ShowsTheCapturedQuery()
    {
        var result = ResultMapper.Map(
            SingleTruncatedPlan(), "query editor", capturedQueryText: CapturedQuery);

        Assert.Equal(CapturedQuery, Assert.Single(result.Statements).StatementText);

        // The reported symptom, end to end: the Advice for Humans text carries the whole query.
        Assert.Contains(CapturedQuery, TextFormatter.Format(result));
    }

    /// <summary>
    /// Showing the complete query next to a warning that says the text "stops early" would read as a
    /// contradiction, so the mapped warning says what actually happened. The model's own warning is
    /// untouched — the properties panel keeps reporting what the plan records.
    /// </summary>
    [Fact]
    public void Advice_WhenTheQueryWasRecovered_TheTruncationWarningSaysSo()
    {
        var plan = SingleTruncatedPlan();

        var recovered = ResultMapper.Map(plan, "query editor", capturedQueryText: CapturedQuery);
        var warning = Assert.Single(recovered.Statements[0].Warnings, w => w.Type == "Truncated Query Text");
        Assert.Contains("complete query", warning.Message);

        var modelWarning = Assert.Single(PlanTestHelper.WarningsOfType(plan, "Truncated Query Text"));
        Assert.DoesNotContain("complete query", modelWarning.Message);
    }

    [Fact]
    public void Advice_MultiStatementPlan_KeepsThePlanTextEvenThoughItIsTruncated()
    {
        var plan = new ParsedPlan();
        plan.Batches.Add(new PlanBatch
        {
            Statements =
            {
                StatementOfLength(PlanStatement.TruncationLengthThreshold),
                StatementOfLength(PlanStatement.TruncationLengthThreshold)
            }
        });
        PlanAnalyzer.Analyze(plan);

        var result = ResultMapper.Map(plan, "query editor", capturedQueryText: CapturedQuery);

        Assert.All(result.Statements, s => Assert.NotEqual(CapturedQuery, s.StatementText));

        // Rule 39 fires per statement; none of them may claim the text was recovered.
        var warnings = result.Statements.SelectMany(s => s.Warnings)
            .Where(w => w.Type == "Truncated Query Text").ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.DoesNotContain("complete query", w.Message));
    }

    [Fact]
    public void Advice_WithNothingCaptured_StillShowsThePlanText()
    {
        var result = ResultMapper.Map(SingleTruncatedPlan(), "file");

        Assert.True(Assert.Single(result.Statements).StatementText.Length
            >= PlanStatement.TruncationLengthThreshold);
    }

    [Fact]
    public void Advice_UntruncatedStatement_IgnoresTheCapturedText()
    {
        var plan = new ParsedPlan();
        plan.Batches.Add(new PlanBatch
        {
            Statements = { StatementOfLength(PlanStatement.TruncationLengthThreshold - 1) }
        });
        PlanAnalyzer.Analyze(plan);

        var result = ResultMapper.Map(plan, "query editor", capturedQueryText: CapturedQuery);

        Assert.NotEqual(CapturedQuery, Assert.Single(result.Statements).StatementText);
    }

    /// <summary>
    /// The recovered text goes through the same parameter substitution as the plan's copy, and the
    /// parameterized form the result carries alongside is then the recovered text too — the pairing
    /// get_repro_script depends on.
    /// </summary>
    [Fact]
    public void Advice_RecoveredQuery_StillGetsParameterValuesSubstituted()
    {
        var stmt = StatementOfLength(PlanStatement.TruncationLengthThreshold);
        stmt.Parameters.Add(new PlanParameter
        {
            Name = "@p", CompiledValue = "(5)"
        });
        var plan = new ParsedPlan();
        plan.Batches.Add(new PlanBatch { Statements = { stmt } });
        PlanAnalyzer.Analyze(plan);

        var captured = "SELECT c FROM t WHERE id = @p;";
        var result = ResultMapper.Map(plan, "query editor", capturedQueryText: captured);

        var mapped = Assert.Single(result.Statements);
        // ParameterSubstitution unwraps showplan's "(5)" to the bare literal.
        Assert.Equal("SELECT c FROM t WHERE id = 5;", mapped.StatementText);
        Assert.Equal(captured, mapped.ParameterizedStatementText);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static ParsedPlan SingleTruncatedPlan()
    {
        var plan = new ParsedPlan();
        plan.Batches.Add(new PlanBatch
        {
            Statements = { StatementOfLength(PlanStatement.TruncationLengthThreshold) }
        });
        PlanAnalyzer.Analyze(plan);
        return plan;
    }

    private static PlanStatement StatementOfLength(int length) =>
        new() { StatementText = new string('x', length), StatementType = "SELECT" };

    private static ParsedPlan AnalyzeStatementOfLength(int length)
    {
        var plan = new ParsedPlan();
        plan.Batches.Add(new PlanBatch { Statements = { StatementOfLength(length) } });
        PlanAnalyzer.Analyze(plan);
        return plan;
    }

    /// <summary>
    /// Pads the plan's first recorded statement past the cap, so the fixture looks the way a real
    /// capped plan looks. Letters and a space only — this goes back into an XML attribute.
    /// </summary>
    private static string Truncated(string xml)
    {
        const string marker = "StatementText=\"";
        var start = xml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "fixture has no StatementText to pad");

        var valueEnd = xml.IndexOf('"', start + marker.Length);
        Assert.True(valueEnd > start, "fixture's StatementText is not terminated");

        return xml[..valueEnd]
            + " " + new string('x', PlanStatement.TruncationLengthThreshold)
            + xml[valueEnd..];
    }

    private static PlanViewerControl LoadPlan(string planFileName, Func<string, string> shape, string? queryText)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = shape(File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\""));

        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, planFileName, queryText), $"Plan failed to load: {viewer.LastLoadError}");

        return viewer;
    }

    /// <summary>
    /// Drives the menu entry the reporter used and returns the text it handed over. Goes through the
    /// real right-click so the Opening event configures the menu first, the way #467 established.
    /// </summary>
    private static string? OpenInEditorText(PlanViewerControl viewer)
    {
        var window = new Window { Content = viewer, Width = 1400, Height = 900 };
        window.Show();
        window.UpdateLayout();

        var grid = viewer.GetLogicalDescendants().OfType<DataGrid>().First(g => g.Name == "StatementsGrid");
        grid.RaiseEvent(new ContextRequestedEventArgs());

        string? opened = null;
        viewer.OpenInEditorRequested += (_, text) => opened = text;

        grid.ContextMenu!.Items.OfType<MenuItem>().First(i => i.Name == "OpenStatementInEditorItem")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        return opened;
    }
}
