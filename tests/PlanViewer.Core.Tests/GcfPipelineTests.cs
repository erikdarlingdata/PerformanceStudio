using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlanViewer.App.Mcp;
using Xunit;

namespace PlanViewer.Core.Tests;

// End-to-end proof that the GCF filter actually fires in the MCP request pipeline when
// registered the way McpHostService registers it — AddMcpServer(...).WithTools(...)
// .WithRequestFilters(f => f.AddCallToolFilter(GcfCallToolFilter.Instance)) — rather than
// only that Transform()/TryEncode() work in isolation. A real MCP client calls a tool over
// an in-process stream transport and the response text is asserted.
[Collection("PlanViewerOutputFormatEnv")]
public class GcfPipelineTests : IDisposable
{
    public GcfPipelineTests() => Environment.SetEnvironmentVariable("PLANVIEWER_OUTPUT_FORMAT", null);

    public void Dispose() => Environment.SetEnvironmentVariable("PLANVIEWER_OUTPUT_FORMAT", null);

    [McpServerToolType]
    public class PipelineTools
    {
        // Returns the record-array envelope shape the real Query Store / listing tools
        // return, so the filter has something it can shrink.
        [McpServerTool(Name = "records")]
        public static string Records() =>
            JsonSerializer.Serialize(new
            {
                server = "SQLPROD01",
                total = 20,
                rows = Enumerable
                    .Range(0, 20)
                    .Select(i => new { id = 1000 + i, name = $"row{i}", value = i * 10 })
                    .ToList(),
            });
    }

    private static async Task<string> CallRecordsAsync()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<PipelineTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(GcfCallToolFilter.Instance));

        await using var provider = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(cts.Token);

        try
        {
            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream());
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);

            var result = await client.CallToolAsync(
                "records",
                new Dictionary<string, object?>(),
                cancellationToken: cts.Token);

            return ((TextContentBlock)result.Content[0]).Text;
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Filter_Rewrites_Tool_Result_As_Gcf_When_Enabled()
    {
        Environment.SetEnvironmentVariable("PLANVIEWER_OUTPUT_FORMAT", "gcf");

        var text = await CallRecordsAsync();

        Assert.StartsWith("GCF profile=generic", text);
    }

    [Fact]
    public async Task Tool_Result_Stays_Json_When_Disabled()
    {
        var text = await CallRecordsAsync();

        Assert.DoesNotContain("GCF profile=generic", text);
        Assert.Contains("\"server\"", text); // still the JSON the tool returned
    }
}
