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
       than discover. Verified for #440 that the only new key on a warning is origin_node_ids.

       Rolled a third time for #651, again additively: every warning gains "suppressed" (always
       written) and "rule" (written when a numbered rule produced it), and the summary gains
       "suppressed_warnings". Verified by removing exactly those keys from the new output, which
       gives back the previous hash, 779a85036b74dc7cc7d26d4bad3d7e2b327e4ff89a5ed79b00b78cfae3717cab. */
    private const string ExpectedCompactOutputSha256 =
        "0eeb5da0bc69ceb162100f4326eee446e07234b4cc1b6660dda19221f951f01a";

    [Fact]
    public async Task AnalyzeCompact_PreservesHistoricalOutputBytes()
    {
        // An empty config pins the run to the defaults, so a developer's own ~/.planview.json
        // cannot change the bytes or fail the test.
        var configPath = WriteTempConfig("{}");
        try
        {
            var run = await RunCliAsync(
                "analyze",
                "tests/PlanViewer.Core.Tests/Plans/row_goal_plan.sqlplan",
                "--compact",
                "--config",
                configPath);

            Assert.True(
                run.ExitCode == 0,
                $"Historical analyze command exited with {run.ExitCode}: {run.StandardError}");
            // Normalize line endings before hashing. Console.WriteLine emits CRLF on
            // Windows and LF elsewhere, so hashing raw stdout pins the contract to
            // whichever OS produced the baseline — this hash was generated on the
            // Linux CI runner and therefore failed on every Windows machine.
            var text = run.StandardOutput.Replace("\r\n", "\n");
            var actualHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            Assert.Equal(ExpectedCompactOutputSha256, actualHash);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    /// <summary>
    /// #651: a suppression with no reason stops the command with one line on stderr that names
    /// the file, and exit code 1. No stack trace, and nothing on stdout.
    /// </summary>
    [Fact]
    public async Task Analyze_WithABadSuppression_PrintsOneLineAndExits1()
    {
        var configPath = WriteTempConfig("""{ "suppressions": [ { "query_hash": "0xAB", "rule": 3 } ] }""");
        try
        {
            var run = await RunCliAsync(
                "analyze",
                "tests/PlanViewer.Core.Tests/Plans/row_goal_plan.sqlplan",
                "--config",
                configPath);

            Assert.Equal(1, run.ExitCode);
            Assert.Equal($"Suppression 0 in {configPath} has no \"reason\".", run.StandardError.Trim());
            Assert.Equal("", run.StandardOutput.Trim());
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private static string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"planview-cli-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(
        params string[] arguments)
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
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

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

            return (process.ExitCode, Encoding.UTF8.GetString(stdout.ToArray()), standardError);
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
