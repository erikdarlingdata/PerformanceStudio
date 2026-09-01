using System.IO;
using PlanViewer.App;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #471: the same startup crash #459 cured in ShowFileError, still living in ShowError.
///
/// <para>Both the command-line open and the restore of the previous session's tabs run from
/// the MainWindow constructor, before the window has been shown. A plan file that fails XML
/// validation on that path reaches ShowError with nothing to be modal over, and ShowDialog
/// against a window that has not been shown throws InvalidOperationException: "Cannot show
/// window with non-visible owner" — so the app dies at launch instead of reporting the file
/// it was called about.</para>
///
/// <para>These drive LoadPlanFile against a never-shown window, which is exactly the startup
/// shape, and assert only that nothing escapes. What the dialog says is not the point; that
/// it can be raised at all is.</para>
/// </summary>
public class ShowErrorBeforeVisibleTests
{
    [Fact]
    public void AMalformedPlanFileIsReportedRatherThanThrownAtStartup()
    {
        HeadlessUi.Run(() =>
        {
            /* A .sql routed through LoadPlanFile is how this reproduced during #463's red run:
               XDocument.Parse throws, ValidatePlanXml calls ShowError. */
            var path = TempPlan("SELECT 1 AS not_a_plan;");
            try
            {
                var window = new MainWindow();
                Assert.False(window.IsVisible);

                Assert.Null(Record.Exception(() => window.LoadPlanFile(path)));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void WellFormedXmlThatIsNotAPlanIsAlsoReportedRatherThanThrown()
    {
        HeadlessUi.Run(() =>
        {
            /* The other arm of ValidatePlanXml: parses cleanly, no ShowPlanXML anywhere in it.
               A corrupt or truncated file can land on either arm, so both have to survive
               being reported before the window exists. */
            var path = TempPlan("<Nonsense><Inner/></Nonsense>");
            try
            {
                var window = new MainWindow();
                Assert.False(window.IsVisible);

                Assert.Null(Record.Exception(() => window.LoadPlanFile(path)));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    private static string TempPlan(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.sqlplan");
        File.WriteAllText(path, text);
        return path;
    }
}
