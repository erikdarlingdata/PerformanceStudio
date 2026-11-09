using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The plan viewer shows its zoom percentage as the nearest whole number. Three Zoom Out steps
/// from 100% land on 0.5499... in double arithmetic, which a truncating writer shows as "54%";
/// the viewer shows "55%".
/// </summary>
public class PlanViewerZoomPercentageTests
{
    [Fact]
    public void ThreeZoomOutSteps_ReadFiftyFivePercent()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("key_lookup_plan.sqlplan");
            var window = new Window { Content = viewer, Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();
            try
            {
                var zoomOut = viewer.GetLogicalDescendants().OfType<Button>()
                    .Single(b => b.Content is string content && content == "−");
                var zoomText = viewer.FindControl<TextBlock>("ZoomLevelText")!;
                Assert.Equal("100%", zoomText.Text);

                Click(zoomOut);
                Click(zoomOut);
                Click(zoomOut);

                Assert.Equal("55%", zoomText.Text);
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static PlanViewerControl LoadPlan(string planFileName)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, planFileName), $"Plan failed to load: {viewer.LastLoadError}");

        return viewer;
    }
}
