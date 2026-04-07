using System.Collections.Concurrent;
using System.Security.Cryptography;
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
        )
        """;
    cmd.ExecuteNonQuery();
}

// Register the cleanup background service
builder.Services.AddSingleton(new PlanDbConfig(connectionString));
builder.Services.AddHostedService<CleanupService>();

// Request size limit (10 MB)
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 10 * 1024 * 1024);

var app = builder.Build();
app.UseCors();

// --- Rate limiter: 10 shares per minute per IP (in-memory) ---
var rateLimiter = new RateLimiter(maxRequests: 10, windowSeconds: 60);

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

sealed class CleanupService : BackgroundService
{
    private readonly PlanDbConfig _config;
    private readonly ILogger<CleanupService> _logger;

    public CleanupService(PlanDbConfig config, ILogger<CleanupService> logger)
    {
        _config = config;
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
            var now = DateTime.UtcNow.ToString("o");
            using var conn = new SqliteConnection(_config.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM plans WHERE expires_at < @now";
            cmd.Parameters.AddWithValue("@now", now);
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired plans", deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during plan cleanup");
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
}
