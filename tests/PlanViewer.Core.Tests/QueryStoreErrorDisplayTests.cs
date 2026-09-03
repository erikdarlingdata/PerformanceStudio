using System.Collections.Generic;
using Avalonia.Controls;
using PlanViewer.App.Controls;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;

namespace PlanViewer.Core.Tests;

/// <summary>
/// #452's pattern on the Query Store grid: five catch sites cut exception text to 60-80
/// characters + "..." before handing it to a one-line status strip, throwing away the part of
/// a SQL error that says what actually went wrong (the interesting half of a login or firewall
/// message is rarely in its first 60 characters). The sites now pass the full message, and the
/// strip mirrors whatever it shows into its tooltip — trimming belongs to the display, and the
/// hover is the only way a one-line strip can ever surrender the rest.
///
/// <para>What is pinned here is the mirror, driven through StatusText the way every one of the
/// five sites drives it. The sites themselves are SQL failure paths, and a test that
/// manufactures a real connection failure inherits the machine's SQL state and SqlClient's
/// timeouts — the wrong trade for a display contract.</para>
/// </summary>
public class QueryStoreErrorDisplayTests
{
    [Fact]
    public void TheStatusStripMirrorsItsFullTextIntoTheTooltip()
    {
        HeadlessUi.Run(() =>
        {
            var grid = new QueryStoreGridControl(
                new ServerConnection { ServerName = "tcp:127.0.0.1,1", DisplayName = "unit test" },
                new NoCredentials(),
                initialDatabase: "master",
                databases: new List<string> { "master" });

            var status = grid.FindControl<TextBlock>("StatusText")!;

            /* Longer than either of the old cut-offs, so a reintroduced pre-truncation at any
               site would be visible as a tooltip that no longer matches what the site sent. */
            var fullError = "A network-related or instance-specific error occurred while "
                + "establishing a connection to SQL Server. The server was not found or was not "
                + "accessible. Verify that the instance name is correct and that SQL Server is "
                + "configured to allow remote connections. (provider: TCP Provider, error: 0)";

            status.Text = fullError;
            Assert.Equal(fullError, ToolTip.GetTip(status));

            /* And an empty status must not leave a stale error hovering over nothing. */
            status.Text = "";
            Assert.Null(ToolTip.GetTip(status));
        });
    }

    /// <summary>
    /// Windows-auth credentials so the constructor's connection-string build asks for nothing;
    /// no test here ever opens the connection.
    /// </summary>
    private sealed class NoCredentials : ICredentialService
    {
        public bool SaveCredential(string serverId, string username, string password) => false;
        public (string Username, string Password)? GetCredential(string serverId) => null;
        public bool DeleteCredential(string serverId) => false;
        public bool CredentialExists(string serverId) => false;
        public bool UpdateCredential(string serverId, string username, string password) => false;
    }
}
