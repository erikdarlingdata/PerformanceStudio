using System.CommandLine;
using PlanViewer.Cli;
using PlanViewer.Cli.Commands;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Issue 645: without a credential store (Linux), `planview credential` must say why and name the
/// alternatives. Before, Linux got the in-memory store: `credential add` printed "Credential saved"
/// and the next planview run had nothing. Dropping the command instead would turn every
/// `credential ...` line into the parser's "Unrecognized command" error.
/// </summary>
public sealed class CredentialCommandUnavailableTests
{
    [Theory]
    [InlineData("credential")]
    [InlineData("credential", "list")]
    [InlineData("credential", "add", "sql2022", "--user", "sa", "-p", "hunter2")]
    [InlineData("credential", "remove", "sql2022")]
    public async Task WithoutStore_CredentialExitsOneAndNamesTheAlternatives(params string[] args)
    {
        var realError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var code = await CliRoot.Create(null).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, code);
            Assert.Equal(CredentialCommand.NoStoreMessage, captured.ToString().Trim());
        }
        finally
        {
            Console.SetError(realError);
        }
    }

    /// <summary>
    /// The real process, not the parsed tree: Program.cs maps the factory's in-memory store (what
    /// Linux gets) to no store. Skipped where a credential store exists, so CI on ubuntu-latest is
    /// where this runs.
    /// </summary>
    [Fact]
    public async Task OnLinux_TheRealProcessHasNoStore()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "only Linux has no credential store");

        var run = await CliProcess.RunAsync(
            Path.GetTempPath(), TestContext.Current.CancellationToken,
            "credential", "add", "sql2022", "--user", "sa", "-p", "hunter2");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(CredentialCommand.NoStoreMessage, run.StandardError.Trim());
    }

    [Fact]
    public void WithoutStore_CredentialStaysInHelpAndSaysSo()
    {
        var root = CliRoot.Create(null);

        var credential = Assert.Single(root.Subcommands, c => c.Name == "credential");
        Assert.Contains("not available", credential.Description);
    }
}
