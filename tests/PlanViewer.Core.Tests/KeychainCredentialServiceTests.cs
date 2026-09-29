using System.Diagnostics;
using System.Text;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The macOS keychain store against the real /usr/bin/security, in a throwaway keychain file
/// per test so the developer's login keychain is never touched. Every call goes through a
/// shim that logs its arguments and then execs /usr/bin/security, so the process that reads
/// and writes the keychain is the same one the app runs. macOS only; skipped elsewhere.
/// </summary>
public sealed class KeychainCredentialServiceTests : IDisposable
{
    private const string SecurityPath = "/usr/bin/security";

    private readonly string _dir = "";
    private readonly string _keychainPath = "";
    private readonly string _argsLog = "";
    private readonly KeychainCredentialService? _service;

    public KeychainCredentialServiceTests()
    {
        if (!OperatingSystem.IsMacOS()) return;

        _dir = Directory.CreateTempSubdirectory("keychain-test-").FullName;
        _keychainPath = Path.Combine(_dir, "test.keychain-db");
        _argsLog = Path.Combine(_dir, "args.log");

        // A throwaway keychain's own password, not a credential.
        RunSecurity("create-keychain", "-p", "throwaway", _keychainPath);
        // No auto-lock: a locked keychain puts up an unlock dialog and the test would hang.
        RunSecurity("set-keychain-settings", _keychainPath);

        var shim = Path.Combine(_dir, "security");
        File.WriteAllText(shim, $"#!/bin/sh\nprintf '%s\\n' \"$@\" >> '{_argsLog}'\nexec {SecurityPath} \"$@\"\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _service = new KeychainCredentialService(_keychainPath, shim);
    }

    public void Dispose()
    {
        if (_dir.Length == 0) return;
        RunSecurity("delete-keychain", _keychainPath);
        Directory.Delete(_dir, recursive: true);
    }

    public static TheoryData<string> AwkwardPasswords => new()
    {
        "plain",
        "",
        "has spaces in it",
        "  leading and trailing  ",
        "double\"quote and 'single'",
        @"back\slash\\ then \""",
        "héllo wörld",
        "密码🔑",
        "tab\there",
        "line\nbreak",
        "68656c6c6f",
        "-U",
    };

    [Theory]
    [MemberData(nameof(AwkwardPasswords))]
    public void SaveReadUpdateAndDeleteKeepThePasswordExactly(string password)
    {
        SkipUnlessMac();
        var service = _service!;

        Assert.True(service.SaveCredential("server1", "sa", password));
        Assert.Equal(("sa", password), service.GetCredential("server1"));

        Assert.True(service.UpdateCredential("server1", "sa", password + " v2"));
        Assert.Equal(("sa", password + " v2"), service.GetCredential("server1"));

        Assert.True(service.CredentialExists("server1"));
        Assert.True(service.DeleteCredential("server1"));
        Assert.False(service.CredentialExists("server1"));
        Assert.Null(service.GetCredential("server1"));
    }

    [Fact]
    public void ThePasswordIsNeverInSecurityArguments()
    {
        SkipUnlessMac();
        var service = _service!;
        const string password = "never on the command line 7f3a";
        var hex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(password));

        Assert.True(service.SaveCredential("server1", "sa", password));
        Assert.Equal(("sa", password), service.GetCredential("server1"));
        Assert.True(service.UpdateCredential("server1", "sa", password + "!"));
        Assert.True(service.DeleteCredential("server1"));

        var logged = File.ReadAllText(_argsLog);
        Assert.DoesNotContain(password, logged);
        Assert.DoesNotContain(hex, logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-i", logged.Split('\n'));
    }

    /// <summary>
    /// Items saved before this change were written with the password on the command line.
    /// They must still read, update in place (one item, not a second), and delete.
    /// </summary>
    [Theory]
    [MemberData(nameof(AwkwardPasswords))]
    public void AnItemSavedByTheCurrentReleaseStillWorks(string password)
    {
        SkipUnlessMac();
        var service = _service!;

        // The exact command the current release runs to save.
        RunSecurity("add-generic-password", "-s", "PlanViewer:server1", "-a", "sa", "-w", password, "-U", _keychainPath);

        Assert.Equal(("sa", password), service.GetCredential("server1"));

        Assert.True(service.SaveCredential("server1", "sa", "replaced"));
        Assert.Equal(("sa", "replaced"), service.GetCredential("server1"));
        Assert.Single(service.ListAll(), c => c.ServerName == "server1");

        Assert.True(service.DeleteCredential("server1"));
        Assert.False(service.CredentialExists("server1"));
    }

    [Fact]
    public void AwkwardServerNamesAndUsernamesRoundTripAndList()
    {
        SkipUnlessMac();
        var service = _service!;
        (string Server, string User)[] credentials =
        [
            (@"SQL01\PROD", @"CORP\svc_sql"),
            ("server with spaces", "o'brien \"the dba\""),
            ("sérveur,1433", "utilisateur"),
        ];

        foreach (var (server, user) in credentials)
            Assert.True(service.SaveCredential(server, user, "pw"));

        foreach (var (server, user) in credentials)
            Assert.Equal((user, "pw"), service.GetCredential(server));

        Assert.Equal(
            credentials.OrderBy(c => c.Server, StringComparer.Ordinal),
            service.ListAll().OrderBy(c => c.ServerName, StringComparer.Ordinal));
    }

    [Fact]
    public void APasswordTooLongToSendWholeIsRefusedAndNothingIsSaved()
    {
        SkipUnlessMac();
        var service = _service!;

        Assert.True(service.SaveCredential("server1", "sa", new string('x', 1000)));
        Assert.Equal(("sa", new string('x', 1000)), service.GetCredential("server1"));

        Assert.False(service.SaveCredential("server2", "sa", new string('x', 3000)));
        Assert.False(service.CredentialExists("server2"));
    }

    [Fact]
    public void AServerNameWithANewlineIsRefusedAndNothingIsSaved()
    {
        SkipUnlessMac();
        var service = _service!;

        Assert.False(service.SaveCredential("server1\ndelete-keychain", "sa", "pw"));
        Assert.Empty(service.ListAll());
        Assert.True(File.Exists(_keychainPath));
    }

    private static void SkipUnlessMac() =>
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the keychain store runs only on macOS");

    private static void RunSecurity(params string[] args)
    {
        var psi = new ProcessStartInfo(SecurityPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"security {args[0]} failed: {stderr.Result}{stdout.Result}");
    }
}
