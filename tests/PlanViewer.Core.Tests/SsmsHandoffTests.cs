using System.IO;
using System.Linq;
using Avalonia.Controls;
using PlanViewer.App;
using PlanViewer.App.Controls;
using PlanViewer.App.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The SSMS extension hands a plan to the app as a temp file, ssms_plan_*.sqlplan, holding
/// the query text and any parameter values. The app deletes that file once it has read it and
/// treats the tab like a pasted plan: no file behind it, not on the recent list, not restored
/// at the next start. A file of the user's own is never deleted.
/// </summary>
public class SsmsHandoffTests
{
    [Fact]
    public void APlanFromSsmsIsDeletedOnceReadAndNotRemembered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the SSMS extension runs only on Windows");

        HeadlessUi.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"ssms_plan_{Path.GetRandomFileName()}.sqlplan");
            File.Copy(PlanPath("key_lookup_plan.sqlplan"), path);
            try
            {
                var window = new MainWindow();
                window.LoadPlanFile(path);

                Assert.False(File.Exists(path), "the handoff file is deleted once read");
                var viewer = OpenedViewer(window);
                Assert.Null(viewer.SourceFilePath);
                Assert.DoesNotContain(path, window.RecentPlans);
                Assert.DoesNotContain(path, window.CollectOpenTabEntries());
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void AFileWithTheSameNameOutsideTheTempFolderIsKept()
    {
        HeadlessUi.Run(() =>
        {
            var folder = Directory.CreateTempSubdirectory("ssms-handoff-test-");
            var path = Path.Combine(folder.FullName, "ssms_plan_mine.sqlplan");
            File.Copy(PlanPath("key_lookup_plan.sqlplan"), path);
            try
            {
                var window = new MainWindow();
                window.LoadPlanFile(path);

                Assert.True(File.Exists(path));
                var viewer = OpenedViewer(window);
                Assert.Equal(path, viewer.SourceFilePath);
                Assert.Contains(path, window.RecentPlans);
                Assert.Contains(path, window.CollectOpenTabEntries());
            }
            finally
            {
                var settings = AppSettingsService.Load();
                AppSettingsService.RemoveRecentPlan(settings, path);
                AppSettingsService.Save(settings);
                folder.Delete(recursive: true);
            }
        });
    }

    [Fact]
    public void OnlyTheExtensionsFileNameDirectlyInTheTempFolderCounts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the SSMS extension runs only on Windows");
        var temp = Path.GetTempPath();

        Assert.True(MainWindow.IsSsmsHandoffFile(Path.Combine(temp, "ssms_plan_ab12cd34.sqlplan")));
        Assert.True(MainWindow.IsSsmsHandoffFile(Path.Combine(temp, "SSMS_PLAN_AB12CD34.SQLPLAN")));
        Assert.False(MainWindow.IsSsmsHandoffFile(Path.Combine(temp, "sub", "ssms_plan_ab12cd34.sqlplan")));
        Assert.False(MainWindow.IsSsmsHandoffFile(Path.Combine(temp, "my_plan.sqlplan")));
        Assert.False(MainWindow.IsSsmsHandoffFile(Path.Combine(temp, "ssms_plan_ab12cd34.sql")));
        Assert.False(MainWindow.IsSsmsHandoffFile(@"C:\Plans\ssms_plan_ab12cd34.sqlplan"));
    }

    [Fact]
    public void OffWindowsNoFileCounts()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "pins the contract where the extension never runs");

        Assert.False(MainWindow.IsSsmsHandoffFile(
            Path.Combine(Path.GetTempPath(), "ssms_plan_ab12cd34.sqlplan")));
    }

    private static string PlanPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Plans", name);

    /// <summary>The viewer in the tab that LoadPlanFile just opened and selected.</summary>
    private static PlanViewerControl OpenedViewer(MainWindow window)
    {
        var tab = Assert.IsType<TabItem>(window.MainTabControl.SelectedItem);
        var dock = Assert.IsType<DockPanel>(tab.Content);
        return Assert.Single(dock.Children.OfType<PlanViewerControl>());
    }
}
