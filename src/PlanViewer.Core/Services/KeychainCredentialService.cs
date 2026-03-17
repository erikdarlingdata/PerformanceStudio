using System.Diagnostics;
using System.Text.RegularExpressions;
using PlanViewer.Core.Interfaces;

namespace PlanViewer.Core.Services;

/// <summary>
/// macOS Keychain implementation of ICredentialService.
/// Shells out to /usr/bin/security for generic-password operations.
/// </summary>
public class KeychainCredentialService : ICredentialService
{
    private const string ServicePrefix = "PlanViewer";

    private static string ServiceName(string serverId) => $"{ServicePrefix}:{serverId}";

    public bool SaveCredential(string serverId, string username, string password)
    {
        var (exitCode, _) = RunSecurity(
            "add-generic-password",
            "-s", ServiceName(serverId),
            "-a", username,
            "-w", password,
            "-U");
        return exitCode == 0;
    }

    public (string Username, string Password)? GetCredential(string serverId)
    {
        var service = ServiceName(serverId);

        var (exitCode, output) = RunSecurity("find-generic-password", "-s", service);
        if (exitCode != 0) return null;

        var username = ParseAccount(output);
        if (username == null) return null;

        var (pwExit, password) = RunSecurity("find-generic-password", "-s", service, "-w");
        if (pwExit != 0) return null;

        return (username, password.Trim());
    }

    public bool DeleteCredential(string serverId)
    {
        var (exitCode, _) = RunSecurity("delete-generic-password", "-s", ServiceName(serverId));
        return exitCode == 0;
    }

    public bool CredentialExists(string serverId)
    {
        var (exitCode, _) = RunSecurity("find-generic-password", "-s", ServiceName(serverId));
        return exitCode == 0;
    }

    public bool UpdateCredential(string serverId, string username, string password) =>
        SaveCredential(serverId, username, password);

    /// <summary>
    /// Enumerates all PlanViewer credentials in the macOS Keychain.
    /// </summary>
    public IReadOnlyList<(string ServerName, string Username)> ListAll()
    {
        var (exitCode, output) = RunSecurity("dump-keychain");
        if (exitCode != 0) return [];

        var results = new List<(string, string)>();
        var entries = output.Split("keychain:", StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in entries)
        {
            var svcMatch = Regex.Match(entry, @"""svce""<blob>=""" + Regex.Escape(ServicePrefix) + @":(.+?)""");
            if (!svcMatch.Success) continue;

            var acctMatch = Regex.Match(entry, @"""acct""<blob>=""(.+?)""");
            if (!acctMatch.Success) continue;

            results.Add((svcMatch.Groups[1].Value, acctMatch.Groups[1].Value));
        }

        return results;
    }

    private static string? ParseAccount(string output)
    {
        var match = Regex.Match(output, @"""acct""<blob>=""(.+?)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static (int ExitCode, string Output) RunSecurity(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null) return (-1, string.Empty);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var stdout = stdoutTask.Result;

        return (process.ExitCode, stdout + stderr);
    }
}
