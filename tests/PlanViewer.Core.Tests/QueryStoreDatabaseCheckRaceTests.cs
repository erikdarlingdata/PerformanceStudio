using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using PlanViewer.App.Controls;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The grid's database picker checks Query Store is enabled before switching (QsDatabase_
/// SelectionChanged), and used to let whichever check happened to finish last apply — even for a
/// database the user had already clicked past. Picking twice in a row, before the first check can
/// land, has to leave only the newest pick's check able to run to completion (E6).
///
/// <para>Proven the same way the Overview's own load-generation race is (SessionViewLifecycleTests.
/// AskingForTheOverviewAgainReloadsTheSameControl): reflect out the CancellationTokenSource each
/// pick is running on and check the older one was cancelled by the newer, rather than waiting on
/// the pretend server's connection attempts to actually resolve.</para>
/// </summary>
public class QueryStoreDatabaseCheckRaceTests
{
    [Fact]
    public void PickingASecondDatabaseCancelsTheFirstOnesCheck()
    {
        HeadlessUi.Run(() =>
        {
            var grid = new QueryStoreGridControl(
                new ServerConnection { ServerName = "tcp:127.0.0.1,1", DisplayName = "unit test" },
                new NoCredentials(),
                initialDatabase: "master",
                databases: new List<string> { "master", "A", "B" });

            var databaseBox = grid.FindControl<ComboBox>("QsDatabaseBox")!;

            databaseBox.SelectedItem = "A";
            var firstCheck = (CancellationTokenSource?)GetField(grid, "_databaseCheckCts");
            Assert.NotNull(firstCheck);
            Assert.False(firstCheck!.IsCancellationRequested);

            databaseBox.SelectedItem = "B";
            var secondCheck = (CancellationTokenSource?)GetField(grid, "_databaseCheckCts");

            Assert.NotSame(firstCheck, secondCheck);
            Assert.True(firstCheck.IsCancellationRequested,
                "picking a second database left the first one's Query Store check running alongside it");

            // The superseded check is still awaiting, and reads its own token when it wakes. Token
            // on a disposed source throws, and nothing in that handler would be catching it, so the
            // newer pick must cancel the older source and leave it alone.
            var readingItAgain = Record.Exception(() => firstCheck.Token);
            Assert.True(readingItAgain == null,
                "the newer pick disposed the older check's token source, which the older check still reads");
        });
    }

    /// <summary>
    /// Picking the database the grid is already on is a no-op — nothing to race, and nothing
    /// should touch the check field at all.
    /// </summary>
    [Fact]
    public void ReselectingTheCurrentDatabaseStartsNoCheck()
    {
        HeadlessUi.Run(() =>
        {
            var grid = new QueryStoreGridControl(
                new ServerConnection { ServerName = "tcp:127.0.0.1,1", DisplayName = "unit test" },
                new NoCredentials(),
                initialDatabase: "master",
                databases: new List<string> { "master", "A" });

            var databaseBox = grid.FindControl<ComboBox>("QsDatabaseBox")!;
            databaseBox.SelectedItem = "master";

            Assert.Null(GetField(grid, "_databaseCheckCts"));
        });
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

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
