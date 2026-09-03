using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #456 in the desktop app: analysis and Human/Robot Advice walk every statement through
/// PlanStatements.EnumerateAll, but the statements grid and the MCP session registration were
/// still built from the outer batch only. For an <c>EXEC &lt;procedure&gt;</c> plan that meant
/// one grid row — the EXEC, on its synthetic root node — and near-zero registered counts, while
/// the advice discussed warnings across the whole body that the grid could neither display nor
/// navigate to.
///
/// <para>Drives the real control headlessly rather than testing a list-building helper, because
/// the defect was precisely the distance between what the pipeline computed and what the control
/// chose to show.</para>
/// </summary>
public class ProcBodyStatementsGridTests
{
    [Fact]
    public void TheGridListsBodyStatementsAndRendersThem()
    {
        HeadlessUi.Run(() =>
        {
            var path = Path.Combine("Plans", "exec_stored_procedure_plan.sqlplan");
            Assert.True(File.Exists(path), $"Test plan not found: {path}");
            var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

            var viewer = new PlanViewerControl();
            Assert.True(
                viewer.LoadPlan(xml, "exec_stored_procedure_plan.sqlplan"),
                $"an EXEC plan must load, not refuse: {viewer.LastLoadError}");

            var window = new Window { Content = viewer, Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();

            var grid = viewer.GetLogicalDescendants()
                .OfType<DataGrid>().First(g => g.Name == "StatementsGrid");
            var rows = ((System.Collections.IEnumerable)grid.ItemsSource!)
                .Cast<StatementRow>().ToList();

            /* The grid must agree with the traversal the analysis used: every statement that
               carries a plan of its own, body statements included. (The parser synthesizes a
               root node even for the EXEC and SET statements, so for this fixture that is all
               six — and before this fix the grid showed exactly one row: the EXEC.) Counted
               independently from a fresh parse so the assertion cannot be satisfied by the
               control agreeing with itself. */
            var expected = PlanStatements.EnumerateAll(ShowPlanParser.Parse(xml))
                .Count(s => s.RootNode != null);

            Assert.True(expected > 1, "fixture must carry multiple renderable body statements");
            Assert.Equal(expected, rows.Count);

            /* The outer EXEC keeps its bare text — it isn't inside anything — while every body
               row names its module, which is the label a reader needs to tell five bare body
               statements apart. Copy paths hand out the raw statement text; only the display is
               prefixed. */
            Assert.StartsWith("EXEC dbo.ReproProc", rows[0].QueryText);
            Assert.All(rows.Skip(1), r => Assert.StartsWith("dbo.ReproProc > ", r.QueryText));

            /* Selecting a body statement must draw its plan on the canvas. Row 0 was selected by
               LoadPlan; move to the last row to force a re-render of a different body statement,
               and fail loudly if rendering threw or produced nothing. */
            var canvas = viewer.GetLogicalDescendants()
                .OfType<Canvas>().First(c => c.Name == "PlanCanvas");
            grid.SelectedIndex = rows.Count - 1;
            window.UpdateLayout();

            Assert.True(canvas.Children.Count > 0,
                "selecting a procedure-body statement must render its operators");
        });
    }
}
