using System;
using System.Threading.Tasks;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

namespace PlanViewer.Cli.Commands;

/// <summary>
/// Shared connection/credential resolution for the live-capture CLI commands
/// (analyze --server and querystore), which previously duplicated this logic.
/// </summary>
public static class CliConnectionResolver
{
    public static bool IsAzureSqlDb(string serverName) =>
        serverName.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) ||
        serverName.Contains(".database.azure.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a ServerConnection from the --auth flag and trust-cert setting,
    /// resolving the auth type and validating that a stored credential exists for
    /// SQL auth. On a missing credential it prints guidance, sets a failing exit
    /// code, and throws so the caller can abort.
    /// </summary>
    public static ServerConnection BuildServerConnection(
        string server, string? auth, bool trustCert, ICredentialService credentialService)
    {
        var authType = auth?.ToLowerInvariant() switch
        {
            "windows" => AuthenticationTypes.Windows,
            "sql" => AuthenticationTypes.SqlServer,
            "entra" => AuthenticationTypes.EntraMFA,
            null => credentialService.CredentialExists(server)
                ? AuthenticationTypes.SqlServer
                : AuthenticationTypes.Windows,
            _ => throw new ArgumentException($"Unknown auth type: {auth}. Use: windows, sql, entra")
        };

        if (authType == AuthenticationTypes.SqlServer && !credentialService.CredentialExists(server))
        {
            Console.Error.WriteLine($"No credential found for {server}. Run: planview credential add {server} --user <username>");
            Environment.ExitCode = 1;
            throw new InvalidOperationException("No credentials configured");
        }

        return new ServerConnection
        {
            Id = server,
            ServerName = server,
            DisplayName = server,
            AuthenticationType = authType,
            TrustServerCertificate = trustCert,
            EncryptMode = trustCert ? "Optional" : "Mandatory"
        };
    }

    /// <summary>
    /// Fetches server metadata for analysis (Rule 38 server context). Non-fatal:
    /// returns null if the fetch fails so analysis can continue without it.
    /// </summary>
    public static async Task<ServerMetadata?> FetchServerMetadataAsync(string connectionString, string server)
    {
        try
        {
            return await ServerMetadataService.FetchServerMetadataAsync(connectionString, IsAzureSqlDb(server));
        }
        catch
        {
            // Non-fatal: analysis continues without server context.
            return null;
        }
    }
}
