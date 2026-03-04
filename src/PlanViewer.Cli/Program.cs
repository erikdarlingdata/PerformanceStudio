using System.CommandLine;
using PlanViewer.Cli.Commands;
using PlanViewer.Core.Services;
using PlanViewer.Core.Interfaces;

// Create credential service (platform-specific)
ICredentialService? credentialService = null;
try
{
    credentialService = CredentialServiceFactory.Create();
}
catch (PlatformNotSupportedException)
{
    // Credential storage not available — analyze-only mode still works
}

var root = new RootCommand("SQL Server execution plan analyzer")
{
    AnalyzeCommand.Create(credentialService),
    QueryStoreCommand.Create(credentialService),
};

if (credentialService != null)
    root.Add(CredentialCommand.Create(credentialService));

return await root.InvokeAsync(args);
