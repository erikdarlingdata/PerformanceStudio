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

        /* Interactive Entra auth goes through the Windows WAM broker, which needs a parent window handle to
           own its account picker (issue #425). A CLI has no window to give it, so the connection would fail
           deep inside MSAL with "0xwindow_handle_required" — a message that tells the user nothing about
           what to do. Say it here instead, up front, and name the modes that actually work headless.

           Deliberately not silently substituting another auth mode: picking a different identity than the
           one the operator asked for is worse than refusing. Whether the CLI should grow device-code flow is
           the open question on #425, not something to guess at from here. */
        if (authType == AuthenticationTypes.EntraMFA && !EntraInteractiveAuth.IsSupported)
        {
            Console.Error.WriteLine(
                "Interactive Microsoft Entra MFA (--auth entra) needs a desktop window for the Windows " +
                "account picker, so it cannot run from the CLI.");
            Console.Error.WriteLine(
                "Use the Studio app for interactive sign-in, or a non-interactive identity here: " +
                "--auth sql with a stored credential, or a service principal / managed identity.");
            Environment.ExitCode = 1;
            throw new InvalidOperationException("Interactive Entra authentication is not available headless");
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
