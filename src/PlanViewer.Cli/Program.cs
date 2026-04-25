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

// System.CommandLine's InvokeAsync returns 0 for successful dispatch even when a
// handler set Environment.ExitCode = 1 to signal a validation error. Honor either
// signal so scripts can tell success from failure.
var code = await root.Parse(args).InvokeAsync();
return code != 0 ? code : Environment.ExitCode;
