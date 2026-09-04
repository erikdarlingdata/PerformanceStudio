using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using PlanViewer.App.Services;
using PlanViewer.Core.Interfaces;
using PlanViewer.Core.Models;
using PlanViewer.Core.Services;

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
            builder.Services.AddSingleton<IPlanCatalog>(_sessionManager);
            builder.Services.AddSingleton(new PlanOperations(
                _sessionManager,
                AnalyzerConfig.Default,
                enforceQueryAdmission: false));
            builder.Services.AddSingleton(_connectionStore);
            builder.Services.AddSingleton(_credentialService);

            /* Register MCP server with all tool classes */
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new()
                    {
                        Name = "PerformanceStudio",
                        /* Derived from the product version in Directory.Build.props
                           so it never drifts from the actual release. */
                        Version = typeof(McpHostService).Assembly.GetName().Version?.ToString(3) ?? "1.11.0"
                    };
                    options.ServerInstructions = McpInstructions.Text;
                })
                .WithHttpTransport()
                .WithTools<McpPlanTools>()
                .WithTools<McpQueryStoreTools>()
                /* Opt-in GCF output (PLANVIEWER_OUTPUT_FORMAT=gcf): one call-tool filter
                   re-encodes each tool's JSON result as a smaller, lossless GCF wire. */
                .WithRequestFilters(filters => filters.AddCallToolFilter(GcfCallToolFilter.Instance));

            _app = builder.Build();

            /* DNS-rebinding guard. Kestrel only listens on loopback, but a malicious
               web page can point a DNS name it controls at 127.0.0.1 and reach this
               server same-origin (CORS never applies). Reject any request whose Host
               isn't a loopback name, and any browser request whose Origin isn't a
               loopback origin, before it can touch an MCP endpoint. */
            _app.Use(async (context, next) =>
            {
                if (!IsLoopbackHost(context.Request.Host.Host))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                var origin = context.Request.Headers.Origin;
                if (origin.Count > 0 && !IsLoopbackOrigin(origin[0]))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                await next();
            });

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

    private static bool IsLoopbackHost(string host)
    {
        /* HostString.Host keeps IPv6 brackets ([::1]); Uri.Host strips them. */
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "[::1]"
            || host == "::1";
    }

    private static bool IsLoopbackOrigin(string? origin)
    {
        return origin != null
            && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && IsLoopbackHost(uri.Host);
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
