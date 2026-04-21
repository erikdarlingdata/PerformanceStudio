using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// CORS — allow all origins for WASM client
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Database path — data/ subdirectory relative to the binary
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "plans.db");
var connectionString = $"Data Source={dbPath}";

// Initialize database
using (var conn = new SqliteConnection(connectionString))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS plans (
            id TEXT PRIMARY KEY,
            data TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            delete_token TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS page_views (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT NOT NULL,
            referrer TEXT,
            visitor_hash TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_pv_created ON page_views(created_at);
        """;
    cmd.ExecuteNonQuery();
}

// --- Rate limiters (in-memory) ---
// Created before Build() so they can be DI-registered and swept by CleanupService.
var rateLimiter = new RateLimiter(maxRequests: 10, windowSeconds: 60);
var analyticsRateLimiter = new RateLimiter(maxRequests: 30, windowSeconds: 60);

// Register the cleanup background service
builder.Services.AddSingleton(new PlanDbConfig(connectionString));
builder.Services.AddSingleton(new RateLimiters(rateLimiter, analyticsRateLimiter));
builder.Services.AddHostedService<CleanupService>();

// Request size limit (10 MB)
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 10 * 1024 * 1024);

var app = builder.Build();
app.UseCors();

const int MaxTtlDays = 365;

// --- Endpoints ---

app.MapGet("/health", () => Results.Content("OK", "text/plain"));

app.MapPost("/api/share", async (HttpContext ctx) =>
{
    // Rate limit by IP
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!rateLimiter.IsAllowed(ip))
    {
        return Results.StatusCode(429);
    }

    // Read raw body
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(body))
    {
        return Results.BadRequest("Empty body");
    }

    // Parse and extract ttl_days from the JSON
    int ttlDays = 7;
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("ttl_days", out var ttlProp) && ttlProp.TryGetInt32(out var t))
            ttlDays = Math.Clamp(t, 1, MaxTtlDays);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON");
    }

    var id = GenerateId();
    var deleteToken = GenerateDeleteToken();
    var now = DateTime.UtcNow;
    var expiresAt = now.AddDays(ttlDays);

    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO plans (id, data, created_at, expires_at, delete_token) VALUES (@id, @data, @created_at, @expires_at, @delete_token)";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.Parameters.AddWithValue("@data", body);
    cmd.Parameters.AddWithValue("@created_at", now.ToString("o"));
    cmd.Parameters.AddWithValue("@expires_at", expiresAt.ToString("o"));
    cmd.Parameters.AddWithValue("@delete_token", deleteToken);
    cmd.ExecuteNonQuery();

    return Results.Content(
        $"{{\"id\":\"{id}\",\"delete_token\":\"{deleteToken}\",\"expires_at\":\"{expiresAt:yyyy-MM-dd}\"}}",
        "application/json");
});

app.MapGet("/api/plans/{id}", (string id) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT data FROM plans WHERE id = @id AND expires_at > @now";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));

    var result = cmd.ExecuteScalar() as string;
    if (result is null)
    {
        return Results.NotFound();
    }

    return Results.Content(result, "application/json");
});

// --- Analytics: page view tracking ---

app.MapPost("/api/event", async (HttpContext ctx) =>
{
    // Rate limit: 30 events/min per IP (generous — covers page nav + shares)
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!analyticsRateLimiter.IsAllowed(ip))
        return Results.StatusCode(429);

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    string path = "/";
    string? referrer = null;
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("path", out var p))
            path = p.GetString() ?? "/";
        if (doc.RootElement.TryGetProperty("referrer", out var r))
            referrer = r.GetString();
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON");
    }

    // Strip referrer to domain only (no full URLs with query params).
    // If it doesn't parse as an absolute URL, drop it — never persist raw
    // client-supplied strings, since the dashboard renders referrers in HTML.
    if (!string.IsNullOrEmpty(referrer))
    {
        referrer = Uri.TryCreate(referrer, UriKind.Absolute, out var refUri)
            ? refUri.Host
            : null;
    }

    // Visitor hash: SHA256(IP + User-Agent + date) — unique per day, no PII stored
    var ua = ctx.Request.Headers.UserAgent.FirstOrDefault() ?? "";
    var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
    var visitorHash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{ip}|{ua}|{day}"))).ToLower()[..16];

    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO page_views (path, referrer, visitor_hash, created_at) VALUES (@path, @referrer, @hash, @now)";
    cmd.Parameters.AddWithValue("@path", path);
    cmd.Parameters.AddWithValue("@referrer", (object?)referrer ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@hash", visitorHash);
    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();

    return Results.Ok();
});

app.MapGet("/api/stats", () =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var now = DateTime.UtcNow.ToString("o");
    var cutoff30 = DateTime.UtcNow.AddDays(-30).ToString("o");
    var cutoff7 = DateTime.UtcNow.AddDays(-7).ToString("o");
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    // --- Plan sharing stats ---
    long totalShared;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM plans";
        totalShared = (long)cmd.ExecuteScalar()!;
    }

    long activePlans;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM plans WHERE expires_at > @now";
        cmd.Parameters.AddWithValue("@now", now);
        activePlans = (long)cmd.ExecuteScalar()!;
    }

    var dailyShares = new List<object>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            SELECT DATE(created_at) as day, COUNT(*) as count
            FROM plans WHERE created_at > @cutoff
            GROUP BY DATE(created_at) ORDER BY day
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff30);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dailyShares.Add(new { day = reader.GetString(0), count = reader.GetInt64(1) });
    }

    // --- Page view stats ---
    long viewsToday;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM page_views WHERE DATE(created_at) = @today";
        cmd.Parameters.AddWithValue("@today", today);
        viewsToday = (long)cmd.ExecuteScalar()!;
    }

    long visitorsToday;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(DISTINCT visitor_hash) FROM page_views WHERE DATE(created_at) = @today";
        cmd.Parameters.AddWithValue("@today", today);
        visitorsToday = (long)cmd.ExecuteScalar()!;
    }

    long views7d;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM page_views WHERE created_at > @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff7);
        views7d = (long)cmd.ExecuteScalar()!;
    }

    long visitors7d;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(DISTINCT visitor_hash) FROM page_views WHERE created_at > @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff7);
        visitors7d = (long)cmd.ExecuteScalar()!;
    }

    long views30d;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM page_views WHERE created_at > @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff30);
        views30d = (long)cmd.ExecuteScalar()!;
    }

    long visitors30d;
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(DISTINCT visitor_hash) FROM page_views WHERE created_at > @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff30);
        visitors30d = (long)cmd.ExecuteScalar()!;
    }

    var dailyViews = new List<object>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            SELECT DATE(created_at) as day, COUNT(*) as views, COUNT(DISTINCT visitor_hash) as visitors
            FROM page_views WHERE created_at > @cutoff
            GROUP BY DATE(created_at) ORDER BY day
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff30);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dailyViews.Add(new { day = reader.GetString(0), views = reader.GetInt64(1), visitors = reader.GetInt64(2) });
    }

    var topReferrers = new List<object>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            SELECT referrer, COUNT(*) as count
            FROM page_views WHERE created_at > @cutoff AND referrer IS NOT NULL AND referrer != ''
            GROUP BY referrer ORDER BY count DESC LIMIT 10
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff30);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            topReferrers.Add(new { referrer = reader.GetString(0), count = reader.GetInt64(1) });
    }

    return Results.Json(new
    {
        sharing = new { total_shared = totalShared, active_plans = activePlans, daily = dailyShares },
        traffic = new
        {
            today = new { views = viewsToday, visitors = visitorsToday },
            last_7d = new { views = views7d, visitors = visitors7d },
            last_30d = new { views = views30d, visitors = visitors30d },
            daily = dailyViews,
            top_referrers = topReferrers
        }
    });
});

app.MapDelete("/api/plans/{id}", (string id, HttpContext ctx) =>
{
    var token = ctx.Request.Query["token"].FirstOrDefault();
    if (string.IsNullOrEmpty(token))
    {
        return Results.BadRequest("Missing delete token");
    }

    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM plans WHERE id = @id AND delete_token = @token";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.Parameters.AddWithValue("@token", token);
    var deleted = cmd.ExecuteNonQuery();

    return deleted > 0 ? Results.Ok() : Results.NotFound();
});

app.Run();

// --- Helpers ---

static string GenerateId()
{
    const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    return new string(Random.Shared.GetItems<char>(chars.AsSpan(), 8));
}

static string GenerateDeleteToken()
{
    return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLower();
}

// --- Supporting types ---

record PlanDbConfig(string ConnectionString);

record RateLimiters(RateLimiter Share, RateLimiter Analytics);

sealed class CleanupService : BackgroundService
{
    private readonly PlanDbConfig _config;
    private readonly RateLimiters _rateLimiters;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(PlanDbConfig config, RateLimiters rateLimiters, ILogger<CleanupService> logger)
    {
        _config = config;
        _rateLimiters = rateLimiters;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Cleanup();

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        try
        {
            using var conn = new SqliteConnection(_config.ConnectionString);
            conn.Open();

            // Delete expired plans
            var now = DateTime.UtcNow.ToString("o");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM plans WHERE expires_at < @now";
                cmd.Parameters.AddWithValue("@now", now);
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                    _logger.LogInformation("Cleaned up {Count} expired plans", deleted);
            }

            // Delete page views older than 90 days
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM page_views WHERE created_at < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-90).ToString("o"));
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                    _logger.LogInformation("Cleaned up {Count} old page views", deleted);
            }

            // Evict stale rate-limiter keys so the dictionary doesn't grow forever.
            var shareEvicted = _rateLimiters.Share.Sweep();
            var analyticsEvicted = _rateLimiters.Analytics.Sweep();
            if (shareEvicted + analyticsEvicted > 0)
                _logger.LogInformation(
                    "Evicted {Share} share + {Analytics} analytics rate-limit keys",
                    shareEvicted, analyticsEvicted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cleanup");
        }
    }
}

sealed class RateLimiter
{
    private readonly int _maxRequests;
    private readonly int _windowSeconds;
    private readonly ConcurrentDictionary<string, List<DateTime>> _requests = new();

    public RateLimiter(int maxRequests, int windowSeconds)
    {
        _maxRequests = maxRequests;
        _windowSeconds = windowSeconds;
    }

    public bool IsAllowed(string key)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-_windowSeconds);

        var timestamps = _requests.GetOrAdd(key, _ => new List<DateTime>());

        lock (timestamps)
        {
            timestamps.RemoveAll(t => t < cutoff);

            if (timestamps.Count >= _maxRequests)
            {
                return false;
            }

            timestamps.Add(now);
            return true;
        }
    }

    /// <summary>
    /// Evicts keys whose timestamp lists have gone empty. Call periodically
    /// so the dictionary doesn't grow forever across unique IPs.
    /// Returns the number of keys evicted.
    /// </summary>
    public int Sweep()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_windowSeconds);
        var evicted = 0;
        foreach (var kvp in _requests)
        {
            lock (kvp.Value)
            {
                kvp.Value.RemoveAll(t => t < cutoff);
                if (kvp.Value.Count == 0 && _requests.TryRemove(kvp))
                    evicted++;
            }
        }
        return evicted;
    }
}
