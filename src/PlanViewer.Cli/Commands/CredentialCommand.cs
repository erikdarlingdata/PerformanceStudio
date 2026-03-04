using System.CommandLine;
using System.Text;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Services;

namespace PlanViewer.Cli.Commands;

public static class CredentialCommand
{
    public static Command Create(ICredentialService credentialService)
    {
        var cmd = new Command("credential", "Manage stored server credentials");

        cmd.AddCommand(CreateAddCommand(credentialService));
        cmd.AddCommand(CreateListCommand(credentialService));
        cmd.AddCommand(CreateRemoveCommand(credentialService));

        return cmd;
    }

    private static Command CreateAddCommand(ICredentialService credentialService)
    {
        var serverArg = new Argument<string>("server-name", "Server name to store credentials for");
        var userOption = new Option<string>("--user", "Username") { IsRequired = true };
        userOption.AddAlias("-u");
        var passwordOption = new Option<string?>("--password", "Password (if omitted, prompts interactively)");
        passwordOption.AddAlias("-p");

        var cmd = new Command("add", "Add or update credentials for a server")
        {
            serverArg, userOption, passwordOption
        };

        cmd.SetHandler((string server, string user, string? passwordArg) =>
        {
            string password;
            if (!string.IsNullOrEmpty(passwordArg))
            {
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
        }, serverArg, userOption, passwordOption);

        return cmd;
    }

    private static Command CreateListCommand(ICredentialService credentialService)
    {
        var cmd = new Command("list", "List stored credentials");

        cmd.SetHandler(() =>
        {
            IReadOnlyList<(string ServerName, string Username)>? creds = credentialService switch
            {
                WindowsCredentialService win => win.ListAll(),
                KeychainCredentialService mac => mac.ListAll(),
                _ => null
            };

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
        var serverArg = new Argument<string>("server-name", "Server name to remove credentials for");

        var cmd = new Command("remove", "Remove stored credentials for a server")
        {
            serverArg
        };

        cmd.SetHandler((string server) =>
        {
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
        }, serverArg);

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
