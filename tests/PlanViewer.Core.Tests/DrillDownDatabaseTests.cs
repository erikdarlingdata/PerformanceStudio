using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using PlanViewer.App.Controls;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The Overview's drill-down is meant to switch the session to the database it drilled into —
/// not just open a Query Store tab against it while the toolbar keeps showing whatever database
/// the session was on before (E1). <see cref="QuerySessionControl.TrySelectDrilledDatabase"/> is
/// the seam: it does exactly what a user picking that database in DatabaseBox would do, and
/// nothing more when the drilled database is not one the picker knows about.
///
/// <para>A plan opened from a Query Store grid carries the same gap one step further: the grid
/// has its own database picker, independent of the toolbar's, so Get Actual Plan on such a plan
/// used to run it wherever the toolbar happened to be pointed rather than where the plan came
/// from. <see cref="QuerySessionControl.ResolveExecutionTarget"/> is pinned directly here, per
/// the ruling, rather than through a real execution.</para>
/// </summary>
public class DrillDownDatabaseTests
{
    [Fact]
    public void DrillingDownSwitchesTheSessionToTheDrilledDatabase()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session, database: "master");
                var databaseBox = session.FindControl<ComboBox>("DatabaseBox")!;
                databaseBox.ItemsSource = new List<string> { "master", "Sales", "Ops" };
                databaseBox.SelectedItem = "master";

                var found = session.TrySelectDrilledDatabase("Sales");

                Assert.True(found);
                Assert.Equal("Sales", databaseBox.SelectedItem);
                Assert.Equal("Sales", SessionHarness.SelectedDatabase(session));
                Assert.Contains("Sales", SessionHarness.ConnectionString(session) ?? "");
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A database created after connect is not in the picker's list. Nothing names it, so
    /// nothing is selected — the session stays exactly as it was; only the Query Store tab the
    /// caller opens afterward (QuerySessionControl.Views.cs) actually reaches the new database.
    /// </summary>
    [Fact]
    public void DrillingDownToADatabaseMissingFromThePickerLeavesTheSessionAlone()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session, database: "master");
                var databaseBox = session.FindControl<ComboBox>("DatabaseBox")!;
                databaseBox.ItemsSource = new List<string> { "master", "Sales" };
                databaseBox.SelectedItem = "master";

                var databaseBefore = SessionHarness.SelectedDatabase(session);
                var connectionStringBefore = SessionHarness.ConnectionString(session);

                var found = session.TrySelectDrilledDatabase("CreatedAfterConnect");

                Assert.False(found);
                Assert.Equal("master", databaseBox.SelectedItem);
                Assert.Equal(databaseBefore, SessionHarness.SelectedDatabase(session));
                Assert.Equal(connectionStringBefore, SessionHarness.ConnectionString(session));
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>
    /// A plan pulled out of a Query Store grid resolves Get Actual Plan to that grid's own
    /// database, even though the toolbar is pointed somewhere else — and a plan with no grid of
    /// its own (every other plan tab: executed, pasted, opened from History) resolves to the
    /// toolbar's, unchanged.
    /// </summary>
    [Fact]
    public void GetActualPlanResolvesAGridPlanToItsGridsDatabaseAndOthersToTheToolbars()
    {
        HeadlessUi.Run(() =>
        {
            var (window, session) = SessionHarness.NewSession();
            try
            {
                SessionHarness.PretendConnected(session, database: "master");

                var grid = new QueryStoreGridControl(
                    new ServerConnection { ServerName = "tcp:127.0.0.1,1", DisplayName = "unit test" },
                    new NoCredentials(),
                    initialDatabase: "Sales",
                    databases: new List<string> { "master", "Sales" });

                session.OnQueryStorePlansSelected(grid, new List<QueryStorePlan>
                {
                    new()
                    {
                        QueryId = 1,
                        PlanId = 1,
                        QueryText = "select 1;",
                        PlanXml = SessionHarness.SamplePlanXml(),
                    },
                });

                var gridViewer = (PlanViewerControl)SessionHarness.Documents(session).Single().Content!;
                Assert.Equal("Sales", gridViewer.SourceDatabase);

                var (gridDatabase, gridConnectionString) = session.ResolveExecutionTarget(gridViewer);
                Assert.Equal("Sales", gridDatabase);
                Assert.Contains("Sales", gridConnectionString ?? "");

                // A plan with no grid of its own — the same path History and a pasted plan use.
                // The grid plan opened above is still in the strip, so take the newest tab
                // (OpenPlanDocuments always selects the one it just added) rather than the only one.
                var pastedTab = SessionHarness.OpenPlanDocuments(session).Last();
                var pastedViewer = (PlanViewerControl)pastedTab.Content!;
                Assert.Null(pastedViewer.SourceDatabase);

                var (pastedDatabase, pastedConnectionString) = session.ResolveExecutionTarget(pastedViewer);
                Assert.Equal("master", pastedDatabase);
                Assert.Contains("master", pastedConnectionString ?? "");
            }
            finally
            {
                ChromeTestCleanup.PutAway(window);
            }
        });
    }

    /// <summary>Windows-auth credentials so the grid's connection-string build asks for nothing.</summary>
    private sealed class NoCredentials : ICredentialService
    {
        public bool SaveCredential(string serverId, string username, string password) => false;
        public (string Username, string Password)? GetCredential(string serverId) => null;
        public bool DeleteCredential(string serverId) => false;
        public bool CredentialExists(string serverId) => false;
        public bool UpdateCredential(string serverId, string username, string password) => false;
    }
}
