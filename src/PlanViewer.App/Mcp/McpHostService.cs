using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
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

    /// <summary>
    /// Resolves once <see cref="ExecuteAsync"/> knows whether Kestrel came up: null for success,
    /// otherwise a short reason a menu item can show. RunContinuationsAsynchronously so a slow
    /// UI-thread continuation (see MainWindow.ReportMcpStartResultAsync) never runs inline on
    /// this service's own async state machine.
    /// </summary>
    private readonly TaskCompletionSource<string?> _startResult =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>
    /// How the start went: null once Kestrel is actually listening, a short reason if it never
    /// came up. Cancelled instead of completed if the host stopped before either happened — the
    /// window closing mid-start is neither outcome, and MainWindow treats that the same as "no
    /// longer anyone to tell".
    /// </summary>
    internal Task<string?> Started => _startResult.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            /* An empty builder, not CreateBuilder. CreateBuilder also reads appsettings files
               from the working directory and the process's environment variables, and a
               Kestrel section in either one adds endpoints beside the loopback one set below.
               This server takes its whole setup from this code. */
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore();
            builder.Services.AddRoutingCore();

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
                /* Only this machine may connect. Kestrel is bound to loopback, so this
                   check is the second of two; it keeps holding if the binding ever changes. */
                if (!IsLoopbackAddress(context.Connection.RemoteIpAddress))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

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

            /* Split from the single RunAsync the rest of this class used to call: StartAsync
               is the half that can fail to bind (another process already on _port, most often),
               and it has to be awaited on its own so that failure reaches _startResult instead
               of vanishing into RunAsync's combined start-then-wait Task. WaitForShutdownAsync
               is the old wait-forever half, unchanged. */
            await _app.StartAsync(stoppingToken);
            _startResult.TrySetResult(null);

            await _app.WaitForShutdownAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* Normal shutdown. If this fired before StartAsync returned, the finally block
               below resolves Started as cancelled rather than failed — the window closing
               mid-bind isn't a start failure, it's just nobody left to tell either way. */
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP server failed to start: {ex.Message}");
            _startResult.TrySetResult(DescribeStartFailure(ex, _port));
        }
        finally
        {
            /* A safety net, not the normal path: TrySetResult above already settled Started for
               the two outcomes MainWindow shows. This only fires if neither did — a start still
               in flight when the token was cancelled — and TrySetCanceled is a no-op once a
               result is already set, so it never overwrites a real success or failure. */
            _startResult.TrySetCanceled();
        }
    }

    /// <summary>
    /// A short reason for the MCP status menu item. Kestrel wraps a taken port as an IOException
    /// whose InnerException is AddressInUseException, but the socket layer beneath it can also
    /// surface a bare SocketException(AddressAlreadyInUse) — seen on some platform/transport
    /// combinations without the Kestrel wrapper — so the whole chain is walked rather than just
    /// the outermost exception or its immediate InnerException. Anything else reports the
    /// exception's own message, whatever that turns out to be.
    /// </summary>
    private static string DescribeStartFailure(Exception ex, int port)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is AddressInUseException
                || current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return $"port {port} is in use";
            }
        }

        return ex.Message;
    }

    internal static bool IsLoopbackAddress(IPAddress? address)
    {
        /* No address (a transport other than TCP) is refused, not trusted. */
        if (address == null)
            return false;

        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
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
