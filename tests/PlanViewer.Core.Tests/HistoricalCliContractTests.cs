using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PlanViewer.Core.Tests;

public sealed class HistoricalCliContractTests
{
    private const string ExpectedCompactOutputSha256 =
        "0c609fed8e250d9366eb9a6cd5eaf40b661ee30d7ba2546bd7726960592e9d87";

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
