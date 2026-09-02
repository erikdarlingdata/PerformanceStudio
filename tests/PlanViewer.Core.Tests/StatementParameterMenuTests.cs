using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
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
/// <para>They also open the real menu on a real control in a real window. That used to be
/// impossible — a <see cref="PlanViewerControl"/> in a headless <see cref="Window"/> took the
/// shared Avalonia session's font manager down with it, so these tests called the menu's Opening
/// work directly instead. #474 fixed the session, so the right-click the reporter performed is now
/// the right-click the test performs.</para>
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
    /// Right-clicks the statements grid and hands back the grid the menu hangs off.
    ///
    /// <para>The window and the layout pass are what make the right-click possible: a ContextMenu
    /// needs a TopLevel to open into, and the grid has no rows to select from until it has been
    /// laid out. The request is raised rather than <c>ContextMenu.Open</c> being called, because
    /// <c>Open</c> skips the Opening event and Opening is where the labels are decided — calling it
    /// would test the menu with nothing having configured it.</para>
    /// </summary>
    private static DataGrid OpenStatementMenu(PlanViewerControl viewer)
    {
        var window = new Window { Content = viewer, Width = 1400, Height = 900 };
        window.Show();
        window.UpdateLayout();

        var grid = viewer.GetLogicalDescendants().OfType<DataGrid>().First(g => g.Name == "StatementsGrid");
        grid.RaiseEvent(new ContextRequestedEventArgs());

        return grid;
    }

    private static MenuItem MenuItemNamed(DataGrid grid, string name) =>
        grid.ContextMenu!.Items.OfType<MenuItem>().First(i => i.Name == name);
}
