using System.CommandLine;
using PlanViewer.Cli;
using PlanViewer.Core.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Cli.ReplSurface;
using Repl;

if (CliRouting.ShouldUseRepl(args))
{
    // The REPL builds its operations, and so reads .planview.json, while it is created.
    ReplApp repl;
    try
    {
        repl = PlanReplApp.Create();
    }
    catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.InvalidDataException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    return await repl.RunAsync(CliRouting.GetReplArgs(args));
}

// Create credential service (platform-specific). Windows and macOS have a credential store.
// Linux gets the in-memory service, which forgets everything when the process exits, so a
// credential saved by one planview run could never reach the next. Treat that as no store:
// analyze and query-store then ask for --login with --password-stdin or a .env file, and
// `planview credential` explains why it cannot help (issue 645).
ICredentialService? credentialService = null;
try
{
    credentialService = CredentialServiceFactory.Create();
    if (credentialService is InMemoryCredentialService)
        credentialService = null;
}
catch (PlatformNotSupportedException)
{
    // Credential storage not available — analyze-only mode still works
}

var root = CliRoot.Create(credentialService);

// System.CommandLine's InvokeAsync returns 0 for successful dispatch even when a
// handler set Environment.ExitCode = 1 to signal a validation error. Honor either
// signal so scripts can tell success from failure.
var code = await root.Parse(args).InvokeAsync();
return code != 0 ? code : Environment.ExitCode;
