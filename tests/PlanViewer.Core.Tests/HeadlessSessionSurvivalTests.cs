using System.IO;
using Avalonia.Controls;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #474: showing a <see cref="PlanViewerControl"/> inside a headless <see cref="Window"/> used to
/// leave the shared Avalonia session unable to construct any window at all, for the rest of the
/// process. Everything that ran afterwards died in <c>FontManager.SystemFonts</c>, the test that
/// did it passed, and which tests went red depended on the order the runner picked that day.
///
/// <para>The cause was a deferred render pass still sitting on the dispatcher queue when the
/// dispatch ended — see <see cref="HeadlessUi"/> for the mechanism and the fix. This test exists
/// because the fix lives in the harness, where nothing else would notice it being removed.</para>
/// </summary>
public class HeadlessSessionSurvivalTests
{
    /// <summary>
    /// Two dispatches in one test rather than two tests, because the damage was never done by the
    /// body — it was done by that dispatch's teardown, after the test's result was recorded. A pair
    /// of tests would only reproduce it in the orders where one happened to follow the other.
    /// </summary>
    [Fact]
    public void APlanViewerShownInAWindowLeavesTheSessionUsable()
    {
        HeadlessUi.Run(() =>
        {
            var window = new Window { Content = LoadedViewer(), Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();
        });

        HeadlessUi.Run(() =>
        {
            var window = new Window { Content = new TextBlock { Text = "still here" }, Width = 400, Height = 300 };
            window.Show();
            window.UpdateLayout();
        });
    }

    private static PlanViewerControl LoadedViewer()
    {
        var path = Path.Combine("Plans", "forced_parameterization_plan.sqlplan");
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, "forced_parameterization_plan.sqlplan"), viewer.LastLoadError);

        return viewer;
    }
}
