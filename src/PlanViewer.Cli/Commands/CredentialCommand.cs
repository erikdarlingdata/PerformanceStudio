using System.CommandLine;
using System.Text;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Services;

namespace PlanViewer.Cli.Commands;

public static class CredentialCommand
{
    /// <summary>
    /// What analyze, query-store and credential print when there is no credential store (Linux).
    /// </summary>
    public const string NoStoreMessage =
        "Credential storage is not available on this operating system. Only Windows and macOS have a credential store. " +
        "Pass --login and --password-stdin, or put PLANVIEW_LOGIN and PLANVIEW_PASSWORD in a .env file.";

    public static Command Create(ICredentialService credentialService)
    {
        var cmd = new Command("credential", "Manage stored server credentials");

        cmd.Subcommands.Add(CreateAddCommand(credentialService));
        cmd.Subcommands.Add(CreateListCommand(credentialService));
        cmd.Subcommands.Add(CreateRemoveCommand(credentialService));

        return cmd;
    }

    /// <summary>
    /// The credential command on an operating system with no credential store (Linux, issue 645).
    /// It stays in --help, accepts whatever subcommand and options the user typed, and exits 1 with
    /// <see cref="NoStoreMessage"/> instead of the parser's "Unrecognized command" line. Before
    /// this, Linux got the in-memory store: `credential add` printed "Credential saved" and the
    /// next planview run had nothing.
    /// </summary>
    public static Command CreateUnavailable()
    {
        var cmd = new Command("credential", "Manage stored server credentials (not available on this operating system)")
        {
            TreatUnmatchedTokensAsErrors = false
        };
        cmd.Arguments.Add(new Argument<string[]>("arguments")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Hidden = true
        });

        cmd.SetAction(_ =>
        {
            Console.Error.WriteLine(NoStoreMessage);
            return 1;
        });

        return cmd;
    }

    private static Command CreateAddCommand(ICredentialService credentialService)
    {
        var serverArg = new Argument<string>("server-name")
        {
            Description = "Server name to store credentials for"
        };
        var userOption = new Option<string>("--user", "-u")
        {
            Description = "Username",
            Required = true
        };
        var passwordOption = new Option<string?>("--password", "-p")
        {
            Description = "Password (if omitted, prompts interactively)"
        };

        var cmd = new Command("add", "Add or update credentials for a server")
        {
            serverArg, userOption, passwordOption
        };

        cmd.SetAction(parseResult =>
        {
            var server = parseResult.GetValue(serverArg)!;
            var user = parseResult.GetValue(userOption)!;
            var passwordArg = parseResult.GetValue(passwordOption);

            string password;
            if (!string.IsNullOrEmpty(passwordArg))
            {
                Console.Error.WriteLine(
                    "Warning: --password is visible in process listings and shell history. " +
                    "Prefer piping the password into stdin (e.g. `echo hunter2 | planview credential add ...`).");
                password = passwordArg;
            }
            else if (Console.IsInputRedirected)
            {
                password = Console.In.ReadLine() ?? "";
            }
            else
            {
                Console.Write("Password: ");
                password = ReadPasswordMasked();
                Console.WriteLine();
            }

            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("Password cannot be empty");
                Environment.ExitCode = 1;
                return;
            }

            if (credentialService.SaveCredential(server, user, password))
                Console.WriteLine($"Credential saved for {server}");
            else
            {
                Console.Error.WriteLine($"Failed to save credential for {server}");
                Environment.ExitCode = 1;
            }
        });

        return cmd;
    }

    private static Command CreateListCommand(ICredentialService credentialService)
    {
        var cmd = new Command("list", "List stored credentials");

        cmd.SetAction(_ =>
        {
            IReadOnlyList<(string ServerName, string Username)>? creds = null;
            // CA1416: WindowsCredentialService is gated on OperatingSystem.IsWindows().
            // .NET 10 won't run below Windows 10, so the underlying "windows5.1.2600" requirement is always met.
#pragma warning disable CA1416
            if (OperatingSystem.IsWindows() && credentialService is WindowsCredentialService win)
                creds = win.ListAll();
#pragma warning restore CA1416
            if (OperatingSystem.IsMacOS() && credentialService is KeychainCredentialService mac)
                creds = mac.ListAll();

            if (creds == null)
            {
                Console.Error.WriteLine("Credential listing not supported on this platform");
                return;
            }

            if (creds.Count == 0)
            {
                Console.WriteLine("No stored credentials");
                return;
            }

            Console.WriteLine($"{"Server",-40} {"Username",-30}");
            Console.WriteLine(new string('-', 70));
            foreach (var (server, username) in creds)
                Console.WriteLine($"{server,-40} {username,-30}");
        });

        return cmd;
    }

    private static Command CreateRemoveCommand(ICredentialService credentialService)
    {
        var serverArg = new Argument<string>("server-name")
        {
            Description = "Server name to remove credentials for"
        };

        var cmd = new Command("remove", "Remove stored credentials for a server")
        {
            serverArg
        };

        cmd.SetAction(parseResult =>
        {
            var server = parseResult.GetValue(serverArg)!;
            if (credentialService.CredentialExists(server))
            {
                credentialService.DeleteCredential(server);
                Console.WriteLine($"Credential removed for {server}");
            }
            else
            {
                Console.Error.WriteLine($"No credential found for {server}");
                Environment.ExitCode = 1;
            }
        });

        return cmd;
    }

    private static string ReadPasswordMasked()
    {
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        return password.ToString();
    }
}
