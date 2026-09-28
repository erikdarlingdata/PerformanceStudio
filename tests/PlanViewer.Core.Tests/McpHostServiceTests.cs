using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PlanViewer.App.Mcp;
using PlanViewer.App.Services;
using PlanViewer.Core.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The MCP server as the app runs it: Kestrel on a loopback port, reached over HTTP. The host
/// is built by hand from an empty builder, so these pin that it still serves MCP, including a
/// tool call that needs its registered services, and that its guards refuse what they should.
/// </summary>
public class McpHostServiceTests
{
    [Fact]
    public async Task AClientOnThisMachineCanListAndCallTools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await RunningServer.StartAsync(cancellationToken);

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = server.Address,
                TransportMode = HttpTransportMode.StreamableHttp
            }),
            cancellationToken: cancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        Assert.Contains(tools, tool => tool.Name == "list_plans");

        var result = await client.CallToolAsync(
            "list_plans",
            new Dictionary<string, object?>(),
            cancellationToken: cancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.False(string.IsNullOrEmpty(Assert.IsType<TextContentBlock>(result.Content[0]).Text));
    }

    [Fact]
    public async Task ARequestNamingAnotherHostIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await RunningServer.StartAsync(cancellationToken);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Address)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Host = "attacker.example";

        using var response = await http.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("::ffff:10.0.0.5", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("fe80::1", false)]
    public void OnlyLoopbackAddressesMayConnect(string address, bool allowed)
    {
        Assert.Equal(allowed, McpHostService.IsLoopbackAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void AConnectionWithNoAddressIsRefused()
    {
        Assert.False(McpHostService.IsLoopbackAddress(null));
    }

    /// <summary>
    /// One McpHostService on a free loopback port, stopped on dispose. It never saves the
    /// connection store and keeps credentials in memory, so no test touches the user's files.
    /// </summary>
    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly McpHostService _service;

        private RunningServer(McpHostService service, int port)
        {
            _service = service;
            Address = new Uri($"http://localhost:{port}/");
        }

        public Uri Address { get; }

        public static async Task<RunningServer> StartAsync(CancellationToken cancellationToken)
        {
            var port = FreePort();
            var service = new McpHostService(
                new PlanSessionManager(), new ConnectionStore(), new InMemoryCredentialService(), port);
            await service.StartAsync(cancellationToken);

            var server = new RunningServer(service, port);
            try
            {
                await WaitUntilListeningAsync(port, cancellationToken);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }

            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>
        /// The service starts its host in the background and reports a failure to start only
        /// to the debugger, so the port is the one sign that it came up.
        /// </summary>
        private static async Task WaitUntilListeningAsync(int port, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                    return;
                }
                catch (SocketException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }
        }
    }
}
