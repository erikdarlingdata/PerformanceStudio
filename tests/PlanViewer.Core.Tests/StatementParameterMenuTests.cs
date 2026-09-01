using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PlanViewer.App.Controls;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #467: the substitution is only worth anything if it reaches the menu the reporter actually used,
/// and only when there is something to substitute.
///
/// <para>These drive the real control rather than the rewriter in isolation, because the failure in
/// #466 was not in producing text — it was in which text the two existing menu entries handed out.
/// A pure test of the rewriter would have passed all along.</para>
///
/// <para><b>No window, deliberately.</b> Putting a <see cref="PlanViewerControl"/> inside a
/// <see cref="Window"/> headlessly leaves the shared Avalonia session unable to construct any later
/// window — every UI test that runs afterwards dies in <c>FontManager.SystemFonts</c>. Without a
/// window the control still loads a plan and fills its statements grid, but a ContextMenu cannot be
/// opened, so the menu's Opening work is invoked directly.</para>
/// </summary>
public class StatementParameterMenuTests
{
    [Fact]
    public void WithParameters_TheMenuOffersValuesAndKeepsTheParameterizedFormReachable()
    {
        HeadlessUi.Run(() =>
        {
            var grid = OpenStatementMenu(LoadPlan("forced_parameterization_plan.sqlplan"));

            Assert.Equal("Copy Query Text (with values)", MenuItemNamed(grid, "CopyStatementTextItem").Header);
            Assert.Equal(
                "Open in Query Editor (with values)",
                MenuItemNamed(grid, "OpenStatementInEditorItem").Header);
            Assert.True(MenuItemNamed(grid, "CopyParameterizedStatementTextItem").IsVisible);
        });
    }

    [Fact]
    public void WithoutParameters_TheMenuIsTheTwoEntriesItAlwaysWas()
    {
        HeadlessUi.Run(() =>
        {
            var grid = OpenStatementMenu(LoadPlan("isnull_plan.sqlplan"));

            Assert.Equal("Copy Query Text", MenuItemNamed(grid, "CopyStatementTextItem").Header);
            Assert.Equal("Open in Query Editor", MenuItemNamed(grid, "OpenStatementInEditorItem").Header);
            Assert.False(MenuItemNamed(grid, "CopyParameterizedStatementTextItem").IsVisible);
        });
    }

    [Fact]
    public void OpenInQueryEditor_HandsOverTextThatWillActuallyRun()
    {
        HeadlessUi.Run(() =>
        {
            var viewer = LoadPlan("forced_parameterization_plan.sqlplan");
            var grid = OpenStatementMenu(viewer);

            string? opened = null;
            viewer.OpenInEditorRequested += (_, text) => opened = text;

            MenuItemNamed(grid, "OpenStatementInEditorItem")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.NotNull(opened);
            Assert.DoesNotContain("@", opened);
            Assert.Contains("[t].[StatusId] in (5,6,7,8,9)", opened);
        });
    }

    private static PlanViewerControl LoadPlan(string planFileName)
    {
        var path = Path.Combine("Plans", planFileName);
        Assert.True(File.Exists(path), $"Test plan not found: {path}");
        var xml = File.ReadAllText(path).Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");

        var viewer = new PlanViewerControl();
        Assert.True(viewer.LoadPlan(xml, planFileName), $"Plan failed to load: {viewer.LastLoadError}");

        return viewer;
    }

    /// <summary>
    /// Does what a right-click on the statements grid does: configures the menu for the selected
    /// statement, then hands back the grid the menu hangs off.
    /// </summary>
    private static DataGrid OpenStatementMenu(PlanViewerControl viewer)
    {
        viewer.UpdateStatementMenuForSelection();
        return viewer.GetLogicalDescendants().OfType<DataGrid>().First(g => g.Name == "StatementsGrid");
    }

    private static MenuItem MenuItemNamed(DataGrid grid, string name) =>
        grid.ContextMenu!.Items.OfType<MenuItem>().First(i => i.Name == name);
}
