using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;

namespace PlanViewer.App.Mcp;

/// <summary>
/// Background service that hosts an MCP server over Streamable HTTP transport.
/// Allows LLM clients to discover and call plan analysis tools via http://localhost:{port}.
/// </summary>
public sealed class McpHostService : BackgroundService
{
    private readonly PlanSessionManager _sessionManager;
    private readonly ConnectionStore _connectionStore;
    private readonly ICredentialService _credentialService;
    private readonly int _port;
    private WebApplication? _app;

    public McpHostService(
        PlanSessionManager sessionManager,
        ConnectionStore connectionStore,
        ICredentialService credentialService,
        int port)
    {
        _sessionManager = sessionManager;
        _connectionStore = connectionStore;
        _credentialService = credentialService;
        _port = port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var builder = WebApplication.CreateBuilder();

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(_port);
            });

            /* Suppress ASP.NET Core console logging */
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            /* Register services that MCP tools need via dependency injection */
            builder.Services.AddSingleton(_sessionManager);
            builder.Services.AddSingleton(_connectionStore);
            builder.Services.AddSingleton(_credentialService);

            /* Register MCP server with all tool classes */
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new()
                    {
                        Name = "PerformanceStudio",
                        Version = "0.7.0"
                    };
                    options.ServerInstructions = McpInstructions.Text;
                })
                .WithHttpTransport()
                .WithTools<McpPlanTools>()
                .WithTools<McpQueryStoreTools>();

            _app = builder.Build();
            _app.MapMcp();

            await _app.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* Normal shutdown */
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP server failed to start: {ex.Message}");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app != null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }

        await base.StopAsync(cancellationToken);
    }
}
