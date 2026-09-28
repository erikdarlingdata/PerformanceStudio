using System.Diagnostics;

namespace PlanViewer.Core.Tests;

/// <summary>
/// Runs the built planview.dll as a child process, so a test sees what a script sees: the exit code
/// Program.cs returns and the text on stderr. Running the command in this process would set the
/// test host's own Environment.ExitCode and write to its console, which has aborted the whole test
/// session before (see CliConnectionResolverTests).
///
/// <para>The CLI project is a ProjectReference of this test project, so building the tests builds
/// planview.dll into its own bin folder, in the same configuration as the tests.</para>
/// </summary>
internal static class CliProcess
{
    public sealed record Outcome(int ExitCode, string StandardOutput, string StandardError);

    public static async Task<Outcome> RunAsync(
        string workingDirectory, CancellationToken cancellationToken, params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var cliAssembly = Path.Combine(
            FindSolutionRoot(), "src", "PlanViewer.Cli", "bin", configuration, "net10.0", "planview.dll");
        Assert.True(File.Exists(cliAssembly), $"CLI assembly not found: {cliAssembly}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        try
        {
            // Nothing for the command to read: close stdin so it cannot wait on a pipe it inherited.
            process.StandardInput.Close();
            var readOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var readError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new Outcome(process.ExitCode, await readOutput, await readError);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PlanViewer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
