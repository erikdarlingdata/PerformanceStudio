using System.CommandLine;
using PlanViewer.Cli.Commands;
using PlanViewer.Core.Interfaces;

namespace PlanViewer.Cli;

public static class CliRoot
{
    /// <summary>
    /// The System.CommandLine commands: analyze, query-store, and credential where credentials can
    /// be stored. Program.cs runs this tree, and the tests parse it, so a command added here is seen
    /// by both.
    /// </summary>
    public static RootCommand Create(ICredentialService? credentialService)
    {
        var root = new RootCommand("SQL Server execution plan analyzer (use 'planview repl' for interactive plan exploration)")
        {
            AnalyzeCommand.Create(credentialService),
            QueryStoreCommand.Create(credentialService),
        };

        if (credentialService != null)
            root.Add(CredentialCommand.Create(credentialService));

        return root;
    }
}
