using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Toggling the Query Store grid's server-filter panel used to lose its own change the
/// next time MainWindow saved settings for an unrelated reason (opening a plan, closing a tab,
/// ...). ServerFilterExpander_StateChanged cloned AppSettingsService's cached instance, changed
/// the clone, and saved that — AppSettingsService cached the clone, but MainWindow went on
/// holding its own older _appSettings reference, and MainWindow's next save wrote that older
/// object straight back over the clone. The fix mutates the shared cached instance in place,
/// the way every other AppSettings save in this app already does.
/// </summary>
[Collection("SettingsFileStore serial")]
public class ServerFilterPanelPersistenceTests
{
    [Fact]
    public void ExpandingTheServerFilterPanelSurvivesAnUnrelatedMainWindowSave()
    {
        HeadlessUi.Run(() =>
        {
            AppSettingsService.Invalidate();
            var before = AppSettingsService.Load().QueryStoreFilterPanelExpanded;

            // Constructed first, exactly like the real app: MainWindow's own AppSettings
            // reference is whatever AppSettingsService.Load() hands back right now.
            var window = new MainWindow();

            var grid = new QueryStoreGridControl(
                new ServerConnection { ServerName = "tcp:127.0.0.1,1", DisplayName = "unit test" },
                CredentialServiceFactory.Create(),
                initialDatabase: "master",
                databases: new List<string> { "master" });

            // Fires ServerFilterExpander_StateChanged, which loads, changes and saves.
            grid.FindControl<Expander>("ServerFilterExpander")!.IsExpanded = !before;
            Dispatcher.UIThread.RunJobs();

            // An ordinary MainWindow save for an unrelated reason — opening a plan tracks it in
            // Recent Plans and saves through MainWindow's own _appSettings.
            var planPath = Path.Combine(System.AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan");
            window.LoadPlanFile(planPath);
            Dispatcher.UIThread.RunJobs();

            AppSettingsService.Invalidate();
            Assert.Equal(!before, AppSettingsService.Load().QueryStoreFilterPanelExpanded);
        });
    }
}
