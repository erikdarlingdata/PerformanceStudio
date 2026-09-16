using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The toolbar buttons that show and hide a panel say which they are doing.
///
/// <para>They used to be plain buttons with nothing to distinguish "this panel is open" from "the
/// pointer is here" — half of finding U7, which was about the accent meaning four different things.
/// The other half was fixed by bounding the hover and pressed states; this is the half that adds
/// the one meaning worth keeping back, deliberately, as an "on" class.</para>
///
/// <para>What matters is <b>where</b> the class is set. It follows the panel's visibility, not the
/// click, because the click is not the only way a panel closes: both panels have their own close
/// button, and clearing the plan closes them too. Wired to the click, a panel dismissed any other
/// way would leave its button still lit.</para>
/// </summary>
public class PanelToggleStateTests
{
    [Fact]
    public void TheStatementsButtonFollowsTheStatementsPanel()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("isnull_plan.sqlplan");
            var window = new Window { Content = viewer, Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();

            var button = Named<Button>(viewer, "StatementsButton");
            var panel = Named<Control>(viewer, "StatementsPanel");

            // A plan with statements opens the panel, so the button starts lit.
            Assert.True(panel.IsVisible);
            Assert.Contains("on", button.Classes);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.False(panel.IsVisible);
            Assert.DoesNotContain("on", button.Classes);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(panel.IsVisible);
            Assert.Contains("on", button.Classes);

            window.Close();
        });
    }

    [Fact]
    public void TheMinimapButtonGoesOutWhenThePanelClosesItself()
    {
        /* The case that fails if the class is hung off the toggle's click: the minimap's own close
           button never touches the toggle, so the button would stay lit over a panel that is gone. */
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("isnull_plan.sqlplan");
            var window = new Window { Content = viewer, Width = 1400, Height = 900 };
            window.Show();
            window.UpdateLayout();

            var toggle = Named<Button>(viewer, "MinimapToggleButton");
            var panel = Named<Control>(viewer, "MinimapPanel");

            Assert.False(panel.IsVisible);
            Assert.DoesNotContain("on", toggle.Classes);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(panel.IsVisible);
            Assert.Contains("on", toggle.Classes);

            // Close it the other way — the panel's own header button.
            var closeButton = panel.GetLogicalDescendants().OfType<Button>()
                .First(b => ToolTip.GetTip(b) as string == "Close minimap");
            closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();

            Assert.False(panel.IsVisible);
            Assert.DoesNotContain("on", toggle.Classes);

            window.Close();
        });
    }

    private static T Named<T>(PlanViewerControl viewer, string name) where T : Control =>
        viewer.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);

    private static PlanViewerControl LoadPlan(string planFileName)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, planFileName, null), $"Plan failed to load: {viewer.LastLoadError}");

        return viewer;
    }
}
