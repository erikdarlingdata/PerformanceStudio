using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PlanViewer.Core.Tests;

public sealed class HistoricalCliContractTests
{
    /* Rolled twice now, both times additively and both times deliberately: #436 added "source" to
       every warning so a consumer can tell SQL Server's own warnings from Performance Studio's
       inferences, and #440 added "origin_node_ids" so a consumer can get from a finding to the
       operator that produced it. Nothing has been removed or renamed either time, so a consumer
       reading fields by name is unaffected — but anything diffing or hashing whole output sees
       different bytes, which is exactly what this constant exists to make somebody decide on rather
       than discover. Verified for #440 that the only new key on a warning is origin_node_ids. */
    private const string ExpectedCompactOutputSha256 =
        "779a85036b74dc7cc7d26d4bad3d7e2b327e4ff89a5ed79b00b78cfae3717cab";

    [Fact]
    public async Task AnalyzeCompact_PreservesHistoricalOutputBytes()
    {
        var solutionRoot = FindSolutionRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var cliAssembly = Path.Combine(
            solutionRoot,
            "src",
            "PlanViewer.Cli",
            "bin",
            configuration,
            "net10.0",
            "planview.dll");
        Assert.True(File.Exists(cliAssembly), $"CLI assembly not found: {cliAssembly}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = solutionRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliAssembly);
        startInfo.ArgumentList.Add("analyze");
        startInfo.ArgumentList.Add("tests/PlanViewer.Core.Tests/Plans/row_goal_plan.sqlplan");
        startInfo.ArgumentList.Add("--compact");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        try
        {
            await using var stdout = new MemoryStream();
            var copyOutput = process.StandardOutput.BaseStream.CopyToAsync(stdout, timeout.Token);
            var readError = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token);
            await copyOutput;
            var standardError = await readError;

            Assert.True(
                process.ExitCode == 0,
                $"Historical analyze command exited with {process.ExitCode}: {standardError}");
            // Normalize line endings before hashing. Console.WriteLine emits CRLF on
            // Windows and LF elsewhere, so hashing raw stdout pins the contract to
            // whichever OS produced the baseline — this hash was generated on the
            // Linux CI runner and therefore failed on every Windows machine.
            var text = Encoding.UTF8.GetString(stdout.ToArray()).Replace("\r\n", "\n");
            var actualHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            Assert.Equal(ExpectedCompactOutputSha256, actualHash);
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
