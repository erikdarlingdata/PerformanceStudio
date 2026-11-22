using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PlanShare;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The share server, started in memory with its database in a temp folder. The limits are passed the
/// way an operator would set them, as PlanShare:* settings, so a test can use a 1 KB budget instead of
/// uploading 100 MB. The type argument is only a way to name the server's assembly.
/// </summary>
internal sealed class PlanShareApp : WebApplicationFactory<UploadBudget>
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "planshare-app-" + Guid.NewGuid().ToString("N"));
    private readonly long? _maxDatabaseBytes;
    private readonly long? _dailyUploadBytes;
    private readonly long? _globalDailyUploadBytes;
    private readonly bool _dropContentLength;

    /// <param name="dropContentLength">
    /// Hides the request's Content-Length from the server, as for a chunked upload. The test client
    /// works out a length for every body it sends, so this is the only way to send one without.
    /// </param>
    public PlanShareApp(long? maxDatabaseBytes = null, long? dailyUploadBytes = null, bool dropContentLength = false, long? globalDailyUploadBytes = null)
    {
        _dropContentLength = dropContentLength;
        _maxDatabaseBytes = maxDatabaseBytes;
        _dailyUploadBytes = dailyUploadBytes;
        _globalDailyUploadBytes = globalDailyUploadBytes;
    }

    public string DbPath => Path.Combine(_dataDir, "plans.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PlanShare:DataDir", _dataDir);
        if (_maxDatabaseBytes is { } max)
            builder.UseSetting("PlanShare:MaxDatabaseBytes", max.ToString());
        if (_dailyUploadBytes is { } budget)
            builder.UseSetting("PlanShare:DailyUploadBytes", budget.ToString());
        if (_globalDailyUploadBytes is { } global)
            builder.UseSetting("PlanShare:GlobalDailyUploadBytes", global.ToString());

        // The in-memory server has no remote address. Production sits behind nginx on loopback, and the
        // server only believes X-Forwarded-For from loopback, so give every request a loopback peer.
        // Tests then pick their client address the way nginx does, with X-Forwarded-For.
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new LoopbackPeer(_dropContentLength)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        // The server's pooled connections keep plans.db open, which stops the delete on Windows
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A request as nginx would forward it: from loopback, with the caller in X-Forwarded-For.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string url, string clientAddress, string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Forwarded-For", clientAddress);
        if (body is not null)
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    public long Count(string table)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    private sealed class LoopbackPeer(bool dropContentLength) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                if (dropContentLength)
                    context.Request.ContentLength = null;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
