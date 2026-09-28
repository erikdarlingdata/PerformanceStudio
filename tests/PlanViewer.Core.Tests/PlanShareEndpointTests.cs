using System.Net;
using System.Text;
using System.Text.Json;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The share server's endpoints, through the real pipeline (forwarded headers, CORS, the limiters) with
/// its database in a temp folder. Each test starts its own server, so the in-memory limits start fresh.
/// A caller is named the way nginx names it, with X-Forwarded-For.
/// </summary>
public class PlanShareEndpointTests
{
    private const string Caller = "203.0.113.10";
    private const string SmallShare = """{"result":{},"text":"report","ttl_days":7}""";

    private static readonly HttpMethod Post = HttpMethod.Post;

    private static async Task<(HttpStatusCode Status, string Body)> Send(
        HttpClient http, HttpMethod method, string url, string caller, string? body = null)
    {
        using var request = PlanShareApp.Request(method, url, caller, body);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static string ErrorText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    private static async Task<(string Id, string Token)> ShareOne(HttpClient http, string caller = Caller)
    {
        var (status, body) = await Send(http, Post, "/api/share", caller, SmallShare);
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("delete_token").GetString()!);
    }

    // --- /api/share ---

    [Fact]
    public async Task Share_StoresThePlan_AndReturnsItBack()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();

        var (id, token) = await ShareOne(http);

        Assert.Equal(8, id.Length);
        Assert.Equal(32, token.Length);
        Assert.Equal(1, app.Count("plans"));
        var (status, body) = await Send(http, HttpMethod.Get, $"/api/plans/{id}", Caller);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(SmallShare, body);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task Share_RootThatIsNotAnObject_Returns400(string body)
    {
        using var app = new PlanShareApp();

        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("The request body must be a JSON object.", ErrorText(reply));
        Assert.Equal(0, app.Count("plans"));
    }

    [Theory]
    [InlineData("""{"ttl_days":"7"}""")]
    [InlineData("""{"ttl_days":true}""")]
    [InlineData("""{"ttl_days":{"days":7}}""")]
    [InlineData("""{"ttl_days":[7]}""")]
    public async Task Share_TtlDaysOfTheWrongType_Returns400(string body)
    {
        using var app = new PlanShareApp();

        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("ttl_days must be a number.", ErrorText(reply));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    public async Task Share_BodyThatIsNotJson_Returns400(string body)
    {
        using var app = new PlanShareApp();

        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(string.IsNullOrEmpty(ErrorText(reply)));
    }

    [Theory]
    [InlineData("""{"ttl_days":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"ttl_days":1.5}""")]
    public async Task Share_TtlDaysThatIsMissingNullOrNotAWholeNumber_UsesTheDefault(string body)
    {
        using var app = new PlanShareApp();

        var (status, _) = await Send(app.CreateClient(), Post, "/api/share", Caller, body);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Share_WhenTheStoreIsFull_Returns507_AndStoresNothing()
    {
        using var app = new PlanShareApp(maxDatabaseBytes: 1);

        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, SmallShare);

        Assert.Equal(HttpStatusCode.InsufficientStorage, status);
        Assert.Equal("Plan sharing is full right now. Please try again later.", ErrorText(reply));
        Assert.Equal(0, app.Count("plans"));
    }

    [Fact]
    public async Task Share_OverTheDailyBudget_Returns429_ForThatCallerOnly()
    {
        var size = Encoding.UTF8.GetByteCount(SmallShare);
        using var app = new PlanShareApp(dailyUploadBytes: size * 2);
        var http = app.CreateClient();
        await ShareOne(http);
        await ShareOne(http);

        var (status, reply) = await Send(http, Post, "/api/share", Caller, SmallShare);

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("Daily sharing limit reached for your network. Please try again tomorrow.", ErrorText(reply));
        Assert.Equal(2, app.Count("plans"));
        await ShareOne(http, caller: "203.0.113.99");
        Assert.Equal(3, app.Count("plans"));
    }

    [Fact]
    public async Task Share_BudgetIsCountedPerClientKey_SoAnIPv4AndItsMappedFormShareOne()
    {
        var size = Encoding.UTF8.GetByteCount(SmallShare);
        using var app = new PlanShareApp(dailyUploadBytes: size);
        var http = app.CreateClient();
        await ShareOne(http, caller: "203.0.113.10");

        var (status, _) = await Send(http, Post, "/api/share", "::ffff:203.0.113.10", SmallShare);

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    [Fact]
    public async Task Share_BudgetIsCountedPerSlash64_SoAddressesInOneShareItAndOtherPrefixesDoNot()
    {
        var size = Encoding.UTF8.GetByteCount(SmallShare);
        using var app = new PlanShareApp(dailyUploadBytes: size);
        var http = app.CreateClient();
        await ShareOne(http, caller: "2001:db8:1:2::1");

        var (sameSlash64, _) = await Send(http, Post, "/api/share", "2001:db8:1:2:aaaa:bbbb:cccc:dddd", SmallShare);
        var (otherSlash64, _) = await Send(http, Post, "/api/share", "2001:db8:1:3::1", SmallShare);

        Assert.Equal(HttpStatusCode.TooManyRequests, sameSlash64);
        Assert.Equal(HttpStatusCode.OK, otherSlash64);
    }

    [Fact]
    public async Task Share_RateLimitIsPerClientKey_AndTheRefusalHasAnErrorText()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        for (var i = 0; i < 10; i++)
            await ShareOne(http, caller: $"2001:db8:5:5::{i + 1}");

        // All ten came from one /64, so an eleventh from that /64 is over the 10 per minute limit
        var (status, reply) = await Send(http, Post, "/api/share", "2001:db8:5:5:ffff::1", SmallShare);

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("Too many shares from your network. Please wait a minute and try again.", ErrorText(reply));
        await ShareOne(http, caller: "2001:db8:5:6::1");
    }

    // --- /api/event ---

    [Fact]
    public async Task Event_StoresAPageView()
    {
        using var app = new PlanShareApp();

        var (status, _) = await Send(app.CreateClient(), Post, "/api/event", Caller, """{"path":"/","referrer":"https://example.com/page?x=1"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, app.Count("page_views"));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"x\"")]
    [InlineData("7")]
    [InlineData("null")]
    public async Task Event_RootThatIsNotAnObject_Returns400(string body)
    {
        using var app = new PlanShareApp();

        var (status, _) = await Send(app.CreateClient(), Post, "/api/event", Caller, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(0, app.Count("page_views"));
    }

    [Theory]
    [InlineData("""{"path":5}""")]
    [InlineData("""{"path":["/"]}""")]
    [InlineData("""{"path":true}""")]
    [InlineData("""{"path":{}}""")]
    [InlineData("""{"referrer":5}""")]
    [InlineData("""{"referrer":{"host":"x"}}""")]
    public async Task Event_FieldOfTheWrongType_Returns400(string body)
    {
        using var app = new PlanShareApp();

        var (status, _) = await Send(app.CreateClient(), Post, "/api/event", Caller, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(0, app.Count("page_views"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("""{"path":"\ud800"}""")]
    public async Task Event_BodyThatIsNotUsableJson_Returns400(string body)
    {
        using var app = new PlanShareApp();

        var (status, _) = await Send(app.CreateClient(), Post, "/api/event", Caller, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Event_NullFields_AreTreatedAsNotSent()
    {
        using var app = new PlanShareApp();

        var (status, _) = await Send(app.CreateClient(), Post, "/api/event", Caller, """{"path":null,"referrer":null}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, app.Count("page_views"));
    }

    [Fact]
    public async Task Event_PathOf512Characters_IsAccepted_AndOf513IsRefused()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();

        var (accepted, _) = await Send(http, Post, "/api/event", Caller, $$"""{"path":"/{{new string('a', 511)}}"}""");
        var (refused, reply) = await Send(http, Post, "/api/event", Caller, $$"""{"path":"/{{new string('a', 512)}}"}""");

        Assert.Equal(HttpStatusCode.OK, accepted);
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Equal("path must be 512 characters or fewer.", ErrorText(reply));
        Assert.Equal(1, app.Count("page_views"));
    }

    [Fact]
    public async Task Event_WhenTheStoreIsFull_StoresNothing_AndStillAnswers200()
    {
        using var app = new PlanShareApp(maxDatabaseBytes: 1);

        var (status, _) = await Send(app.CreateClient(), Post, "/api/event", Caller, """{"path":"/"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, app.Count("page_views"));
    }

    // --- DELETE /api/plans/{id} ---

    [Fact]
    public async Task Delete_WithTheTokenInTheHeader_DeletesThePlan()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        var (id, token) = await ShareOne(http);

        using var request = PlanShareApp.Request(HttpMethod.Delete, $"/api/plans/{id}", Caller);
        request.Headers.Add("X-Delete-Token", token);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, app.Count("plans"));
    }

    [Fact]
    public async Task Delete_WithTheTokenInTheQueryString_StillDeletesThePlan_ForOlderClients()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        var (id, token) = await ShareOne(http);

        var (status, _) = await Send(http, HttpMethod.Delete, $"/api/plans/{id}?token={token}", Caller);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, app.Count("plans"));
    }

    [Fact]
    public async Task Delete_UsesTheHeaderToken_WhenTheQueryStringHasAnother()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        var (id, token) = await ShareOne(http);

        using var request = PlanShareApp.Request(HttpMethod.Delete, $"/api/plans/{id}?token=wrong", Caller);
        request.Headers.Add("X-Delete-Token", token);
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutAToken_Returns400()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        var (id, _) = await ShareOne(http);

        var (status, reply) = await Send(http, HttpMethod.Delete, $"/api/plans/{id}", Caller);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Missing delete token", ErrorText(reply));
        Assert.Equal(1, app.Count("plans"));
    }

    [Fact]
    public async Task Delete_WithAWrongToken_Returns404_AndKeepsThePlan()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        var (id, _) = await ShareOne(http);

        using var request = PlanShareApp.Request(HttpMethod.Delete, $"/api/plans/{id}", Caller);
        request.Headers.Add("X-Delete-Token", "0000");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, app.Count("plans"));
    }

    // --- CORS: the web app calls the server from another origin ---

    [Fact]
    public async Task Cors_PreflightForADeleteWithTheTokenHeader_IsAllowed()
    {
        using var app = new PlanShareApp();
        using var request = PlanShareApp.Request(HttpMethod.Options, "/api/plans/abc12345", Caller);
        request.Headers.Add("Origin", "https://example.com");
        request.Headers.Add("Access-Control-Request-Method", "DELETE");
        request.Headers.Add("Access-Control-Request-Headers", "x-delete-token");

        using var response = await app.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.True(
            allowedHeaders == "*" || allowedHeaders.Contains("x-delete-token", StringComparison.OrdinalIgnoreCase),
            $"Access-Control-Allow-Headers was '{allowedHeaders}'");
        var allowedMethods = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.True(
            allowedMethods == "*" || allowedMethods.Contains("DELETE"),
            $"Access-Control-Allow-Methods was '{allowedMethods}'");
    }
}
