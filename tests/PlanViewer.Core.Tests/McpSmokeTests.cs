using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PlanViewer.Cli;

namespace PlanViewer.Core.Tests;

public sealed class McpSmokeTests
{
    [Fact]
    public async Task GeneratedMcpServer_ExposesOnlyBoundedPlanToolsOverStdio()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;
        var assemblyPath = GetCliAssemblyPath();
        var plansRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Plans"));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "PerformanceStudio test",
            Command = "dotnet",
            Arguments = [assemblyPath, "mcp", "serve"],
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)
        });
        var clientOptions = new McpClientOptions
        {
            Capabilities = new ClientCapabilities
            {
                Roots = new RootsCapability { ListChanged = true }
            },
            Handlers = new McpClientHandlers
            {
                RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
                {
                    Roots = [new Root { Uri = new Uri(plansRoot + Path.DirectorySeparatorChar).AbsoluteUri, Name = "plans" }]
                })
            }
        };

        await using var client = await McpClient.CreateAsync(
            transport,
            clientOptions,
            cancellationToken: cancellationToken);
        Assert.Equal("planview", client.ServerInfo.Name);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var names = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "plan_close",
                "plan_expensive-operators",
                "plan_list",
                "plan_missing-indexes",
                "plan_open",
                "plan_summary",
                "plan_warnings"
            ],
            names);
        Assert.NotEqual(true, tools.Single(tool => tool.Name == "plan_open").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools.Single(tool => tool.Name == "plan_summary").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Empty(await client.ListResourcesAsync(cancellationToken: cancellationToken));

        var outsidePath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.sqlplan");
        try
        {
            File.Copy(Path.Combine(plansRoot, "top_above_scan_plan.sqlplan"), outsidePath);
            var denied = await client.CallToolAsync(
                "plan_open",
                new Dictionary<string, object?> { ["path"] = outsidePath },
                cancellationToken: cancellationToken);
            Assert.True(denied.IsError);
            var deniedText = string.Join(
                "\n",
                denied.Content.OfType<TextContentBlock>().Select(block => block.Text));
            Assert.DoesNotContain(outsidePath, deniedText, StringComparison.Ordinal);

            var linkPath = Path.Combine(plansRoot, $"outside-link-{Guid.NewGuid():N}.sqlplan");
            try
            {
                var linkCreated = false;
                try
                {
                    File.CreateSymbolicLink(linkPath, outsidePath);
                    linkCreated = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Some platforms do not permit unprivileged symbolic-link creation.
                }

                if (linkCreated)
                {
                    var linked = await client.CallToolAsync(
                        "plan_open",
                        new Dictionary<string, object?> { ["path"] = linkPath },
                        cancellationToken: cancellationToken);
                    Assert.True(linked.IsError);
                }
            }
            finally
            {
                File.Delete(linkPath);
            }

            var opened = await client.CallToolAsync(
                "plan_open",
                new Dictionary<string, object?> { ["path"] = "top_above_scan_plan.sqlplan" },
                cancellationToken: cancellationToken);
            Assert.False(opened.IsError);

            var openedText = string.Join(
                "\n",
                opened.Content.OfType<TextContentBlock>().Select(block => block.Text));
            var id = System.Text.RegularExpressions.Regex.Match(
                openedText,
                "top_above_scan_plan-[0-9a-f]{12}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
            Assert.NotEmpty(id);
            var summary = await client.CallToolAsync(
                "plan_summary",
                new Dictionary<string, object?> { ["id"] = id },
                cancellationToken: cancellationToken);
            Assert.False(summary.IsError);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task GeneratedMcpServer_DeniesEmptyAdvertisedRoots()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;
        var assemblyPath = GetCliAssemblyPath();
        var workingDirectory = Path.GetDirectoryName(assemblyPath)!;
        var fileName = $"empty-roots-{Guid.NewGuid():N}.sqlplan";
        var probePath = Path.Combine(workingDirectory, fileName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Plans", "row_goal_plan.sqlplan"), probePath);
        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "PerformanceStudio empty-roots test",
                Command = "dotnet",
                Arguments = [assemblyPath, "mcp", "serve"],
                WorkingDirectory = workingDirectory
            });
            var clientOptions = new McpClientOptions
            {
                Capabilities = new ClientCapabilities
                {
                    Roots = new RootsCapability { ListChanged = true }
                },
                Handlers = new McpClientHandlers
                {
                    RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult { Roots = [] })
                }
            };

            await using var client = await McpClient.CreateAsync(
                transport,
                clientOptions,
                cancellationToken: cancellationToken);
            var denied = await client.CallToolAsync(
                "plan_open",
                new Dictionary<string, object?> { ["path"] = fileName },
                cancellationToken: cancellationToken);

            Assert.True(denied.IsError);
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private static string GetCliAssemblyPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the build configuration.");
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PlanViewer.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var assemblyPath = Path.Combine(
            directory.FullName,
            "src",
            "PlanViewer.Cli",
            "bin",
            configuration,
            "net10.0",
            "planview.dll");
        Assert.True(File.Exists(assemblyPath), $"CLI assembly not found: {assemblyPath}");
        return assemblyPath;
    }
}
