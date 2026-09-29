using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PlanViewer.Core.Interfaces;

namespace PlanViewer.Core.Services;

/// <summary>
/// macOS Keychain implementation of ICredentialService.
/// Shells out to /usr/bin/security for generic-password operations. A save sends its command
/// to <c>security -i</c> on stdin, so the password is never in a process's argument list.
///
/// <para>Calling the Security framework directly would also keep it off the command line, but
/// the keychain lets only the app that created an item read its password without asking. Every
/// item saved so far was created by /usr/bin/security, so reading them from this process would
/// show a keychain prompt for each one. Staying with /usr/bin/security keeps both old and new
/// items readable without a prompt.</para>
/// </summary>
public class KeychainCredentialService : ICredentialService
{
    private const string ServicePrefix = "PlanViewer";
    private const string DefaultSecurityPath = "/usr/bin/security";

    // `security -i` reads each line into a 4096-byte buffer and runs whatever doesn't fit as a
    // separate command. 4094 bytes plus the newline is the longest line it reads whole.
    private const int MaxInteractiveLineBytes = 4094;

    private readonly string? _keychainPath;
    private readonly string _securityPath = DefaultSecurityPath;

    public KeychainCredentialService() { }

    /// <summary>
    /// For tests: works in one keychain file instead of the user's default keychain, and runs
    /// <paramref name="securityPath"/> in place of /usr/bin/security.
    /// </summary>
    internal KeychainCredentialService(string keychainPath, string securityPath = DefaultSecurityPath)
    {
        _keychainPath = keychainPath;
        _securityPath = securityPath;
    }

    private static string ServiceName(string serverId) => $"{ServicePrefix}:{serverId}";

    public bool SaveCredential(string serverId, string username, string password)
    {
        // -X takes the password as hex, so it needs no quoting whatever characters it holds.
        var line = InteractiveLine(Command(
            "add-generic-password",
            "-s", ServiceName(serverId),
            "-a", username,
            "-X", Convert.ToHexStringLower(Encoding.UTF8.GetBytes(password)),
            "-U"));
        if (line == null) return false;

        var (exitCode, _, _) = RunSecurity(["-i"], stdin: line);
        return exitCode == 0;
    }

    public (string Username, string Password)? GetCredential(string serverId)
    {
        // -g prints the attributes on stdout and the password on stderr, in a form that
        // decodes exactly. -w prints hex for a password with any byte outside printable ASCII,
        // with nothing to tell it apart from a password that is itself hex digits.
        var (exitCode, stdout, stderr) = RunSecurity(Command("find-generic-password", "-g", "-s", ServiceName(serverId)));
        if (exitCode != 0) return null;

        var username = ReadAttribute(stdout, "acct");
        if (username == null) return null;

        var match = Regex.Match(stderr, "^password: (.*)$", RegexOptions.Multiline);
        var password = match.Success ? DecodePrintedValue(match.Groups[1].Value) : null;
        if (password == null) return null;

        return (username, password);
    }

    public bool DeleteCredential(string serverId)
    {
        var (exitCode, _, _) = RunSecurity(Command("delete-generic-password", "-s", ServiceName(serverId)));
        return exitCode == 0;
    }

    public bool CredentialExists(string serverId)
    {
        var (exitCode, _, _) = RunSecurity(Command("find-generic-password", "-s", ServiceName(serverId)));
        return exitCode == 0;
    }

    public bool UpdateCredential(string serverId, string username, string password) =>
        SaveCredential(serverId, username, password);

    /// <summary>
    /// Enumerates all PlanViewer credentials in the macOS Keychain.
    /// </summary>
    public IReadOnlyList<(string ServerName, string Username)> ListAll()
    {
        var (exitCode, output, _) = RunSecurity(Command("dump-keychain"));
        if (exitCode != 0) return [];

        var results = new List<(string, string)>();
        var entries = output.Split("keychain:", StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in entries)
        {
            var service = ReadAttribute(entry, "svce");
            if (service == null || !service.StartsWith(ServicePrefix + ":", StringComparison.Ordinal)) continue;

            var account = ReadAttribute(entry, "acct");
            if (account == null) continue;

            results.Add((service[(ServicePrefix.Length + 1)..], account));
        }

        return results;
    }

    /// <summary>The command's arguments, followed by the keychain file when there is one.</summary>
    private string[] Command(params string[] args) =>
        _keychainPath == null ? args : [.. args, _keychainPath];

    /// <summary>
    /// Joins a command into one line for <c>security -i</c>, or returns null when it can't be
    /// sent intact. Each argument is double-quoted with <c>\</c> before any backslash or quote,
    /// which is how <c>security</c> splits an interactive line. A newline would end the
    /// command early and a NUL would cut the argument short.
    /// </summary>
    private static string? InteractiveLine(IEnumerable<string> args)
    {
        var line = new StringBuilder();
        foreach (var arg in args)
        {
            if (arg.Contains('\n') || arg.Contains('\0')) return null;

            if (line.Length > 0) line.Append(' ');
            line.Append('"').Append(arg.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }

        var text = line.ToString();
        return Encoding.UTF8.GetByteCount(text) <= MaxInteractiveLineBytes ? text : null;
    }

    private static string? ReadAttribute(string output, string name)
    {
        var match = Regex.Match(output, $"^\\s*\"{name}\"<blob>=(.*)$", RegexOptions.Multiline);
        return match.Success ? DecodePrintedValue(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Decodes a value as <c>security</c> prints it: <c>"text"</c> when every byte is printable
    /// ASCII other than a backslash, otherwise <c>0x</c> and the bytes in hex, then an escaped
    /// copy that is ignored here. An empty value prints as nothing. Returns null for anything
    /// else, such as <c>&lt;NULL&gt;</c> for a missing attribute.
    /// </summary>
    private static string? DecodePrintedValue(string printed)
    {
        if (printed.Length == 0) return "";

        if (printed.StartsWith("0x", StringComparison.Ordinal))
        {
            var end = printed.IndexOf(' ');
            var hex = end < 0 ? printed[2..] : printed[2..end];
            try { return Encoding.UTF8.GetString(Convert.FromHexString(hex)); }
            catch (FormatException) { return null; }
        }

        if (printed.Length >= 2 && printed[0] == '"' && printed[^1] == '"')
            return printed[1..^1];

        return null;
    }

    private (int ExitCode, string StdOut, string StdErr) RunSecurity(string[] args, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _securityPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null) return (-1, string.Empty, string.Empty);

        // Read both streams concurrently. Doing stderr synchronously while stdout is
        // async can deadlock if stderr fills its pipe buffer before the process exits
        // (reproduces on `security dump-keychain` with large keychains).
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (stdin != null)
            process.StandardInput.Write(stdin + "\n");
        process.StandardInput.Close();

        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
