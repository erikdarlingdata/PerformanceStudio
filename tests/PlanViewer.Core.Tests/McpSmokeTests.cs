using ModelContextProtocol.Client;
using PlanViewer.Cli;

namespace PlanViewer.Core.Tests;

public sealed class McpSmokeTests
{
    [Fact]
    public async Task GeneratedMcpServer_ListsReadOnlyPlanToolsOverStdio()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var assemblyPath = typeof(CliRouting).Assembly.Location;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "PerformanceStudio test",
            Command = "dotnet",
            Arguments = [assemblyPath, "mcp", "serve"],
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)
        });

        await using var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var names = tools.Select(tool => tool.Name).ToArray();

        Assert.Contains(names, name => name.Contains("summary", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, name => name.Contains("warnings", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, name => name.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, name => name.Contains("operators", StringComparison.OrdinalIgnoreCase));
    }
}
