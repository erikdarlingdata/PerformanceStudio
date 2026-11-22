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

    private const string GlobalLimitMessage = "Plan sharing has reached today's overall limit. Please try again tomorrow.";

    [Fact]
    public async Task Share_OverTheGlobalBudget_Returns503WithTheOverallMessage_EvenForAFreshKey()
    {
        var size = Encoding.UTF8.GetByteCount(SmallShare);
        using var app = new PlanShareApp(globalDailyUploadBytes: size * 2);
        var http = app.CreateClient();
        await ShareOne(http, caller: "203.0.113.1");
        await ShareOne(http, caller: "203.0.113.2");

        var (status, reply) = await Send(http, Post, "/api/share", "203.0.113.3", SmallShare);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(GlobalLimitMessage, ErrorText(reply));
        Assert.Equal(2, app.Count("plans"));
    }

    [Fact]
    public async Task Share_RefusedByThePerKeyBudget_DoesNotSpendGlobalAllowance()
    {
        var size = Encoding.UTF8.GetByteCount(SmallShare);
        using var app = new PlanShareApp(dailyUploadBytes: size, globalDailyUploadBytes: size * 2);
        var http = app.CreateClient();
        await ShareOne(http, caller: "203.0.113.1");
        for (var i = 0; i < 3; i++)
        {
            var (refused, _) = await Send(http, Post, "/api/share", "203.0.113.1", SmallShare);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused);
        }

        // One global share is left: the refusals above took none of it
        await ShareOne(http, caller: "203.0.113.2");
        var (status, reply) = await Send(http, Post, "/api/share", "203.0.113.3", SmallShare);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(GlobalLimitMessage, ErrorText(reply));
    }

    [Fact]
    public async Task Share_RefusedByTheGlobalBudget_DoesNotSpendThePerKeyAllowance()
    {
        var size = Encoding.UTF8.GetByteCount(SmallShare);
        using var app = new PlanShareApp(dailyUploadBytes: size, globalDailyUploadBytes: size);
        var http = app.CreateClient();
        await ShareOne(http, caller: "203.0.113.1");

        // The second caller is refused by the global budget twice. Had the first refusal kept its
        // per-key charge, the second would be a per-key 429 instead.
        var (first, _) = await Send(http, Post, "/api/share", "203.0.113.2", SmallShare);
        var (second, reply) = await Send(http, Post, "/api/share", "203.0.113.2", SmallShare);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second);
        Assert.Equal(GlobalLimitMessage, ErrorText(reply));
    }

    [Fact]
    public async Task Share_BodyRefusedByTheGlobalBudgetAfterTheRead_DoesNotSpendThePerKeyAllowance()
    {
        var size = Encoding.UTF8.GetByteCount(SmallShare);
        // No declared length, so only the charge after the read can refuse and refund
        using var app = new PlanShareApp(dailyUploadBytes: size, globalDailyUploadBytes: size, dropContentLength: true);
        var http = app.CreateClient();
        var body = Encoding.UTF8.GetBytes(SmallShare);
        var (accepted, _) = await SendBytes(http, "/api/share", "203.0.113.1", body);

        var (first, _) = await SendBytes(http, "/api/share", "203.0.113.2", body);
        var (second, reply) = await SendBytes(http, "/api/share", "203.0.113.2", body);

        Assert.Equal(HttpStatusCode.OK, accepted);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second);
        Assert.Equal(GlobalLimitMessage, ErrorText(reply));
    }

    [Fact]
    public async Task Share_WhenTheDeclaredLengthIsOverTheGlobalBudget_IsRefusedBeforeTheBodyIsParsed()
    {
        using var app = new PlanShareApp(globalDailyUploadBytes: 10);

        // Not JSON: a 400 would mean the body was parsed before the budget check
        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, "not json at all");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(GlobalLimitMessage, ErrorText(reply));
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
    public async Task Event_PathOf128Characters_IsAccepted_AndOf129IsRefused()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();

        var (accepted, _) = await Send(http, Post, "/api/event", Caller, $$"""{"path":"/{{new string('a', 127)}}"}""");
        var (refused, reply) = await Send(http, Post, "/api/event", Caller, $$"""{"path":"/{{new string('a', 128)}}"}""");

        Assert.Equal(HttpStatusCode.OK, accepted);
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Equal("path must be 128 characters or fewer.", ErrorText(reply));
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

    // --- Request size limits ---

    private static async Task<(HttpStatusCode Status, string Body)> SendBytes(
        HttpClient http, string url, string caller, byte[] body)
    {
        using var request = new HttpRequestMessage(Post, url);
        request.Headers.Add("X-Forwarded-For", caller);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new("application/json");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Event_BodyOver16KB_IsRefusedWith413_AndStoresNothing()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        var big = Encoding.UTF8.GetBytes($$"""{"path":"/","referrer":"https://example.com/{{new string('a', 17 * 1024)}}"}""");

        var (status, reply) = await SendBytes(http, "/api/event", Caller, big);
        using var noLength = new PlanShareApp(dropContentLength: true);
        var (chunkedStatus, _) = await SendBytes(noLength.CreateClient(), "/api/event", Caller, big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("Request body too large.", ErrorText(reply));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedStatus);
        Assert.Equal(0, app.Count("page_views"));
        Assert.Equal(0, noLength.Count("page_views"));
    }

    [Fact]
    public async Task Event_LongestAllowedPathWithEveryCharacterEscaped_FitsTheBodyLimit()
    {
        using var app = new PlanShareApp();
        // 128 characters at 6 bytes each, the worst case the limit has to allow
        var escaped = string.Concat(Enumerable.Repeat("\\u0061", 128));

        var (status, _) = await Send(app.CreateClient(), Post, "/api/event", Caller, $$"""{"path":"{{escaped}}"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, app.Count("page_views"));
    }

    [Fact]
    public async Task Share_WhenTheStoreIsFull_IsRefusedBeforeTheBodyIsParsed()
    {
        using var app = new PlanShareApp(maxDatabaseBytes: 1);
        var http = app.CreateClient();

        // Not JSON: a 400 would mean the body was parsed before the store check
        var (status, _) = await Send(http, Post, "/api/share", Caller, "not json");
        using var noLength = new PlanShareApp(maxDatabaseBytes: 1, dropContentLength: true);
        var (chunked, _) = await SendBytes(noLength.CreateClient(), "/api/share", Caller, "not json"u8.ToArray());

        Assert.Equal(HttpStatusCode.InsufficientStorage, status);
        Assert.Equal(HttpStatusCode.InsufficientStorage, chunked);
    }

    [Fact]
    public async Task Share_WhenTheDeclaredLengthIsOverTheBudget_IsRefusedBeforeTheBodyIsParsed()
    {
        using var app = new PlanShareApp(dailyUploadBytes: 10);

        // Not JSON: a 400 would mean the body was parsed before the budget check
        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, "not json at all");

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("Daily sharing limit reached for your network. Please try again tomorrow.", ErrorText(reply));
    }

    [Fact]
    public async Task Share_WithNoDeclaredLength_IsStillChargedAfterTheRead()
    {
        using var app = new PlanShareApp(dailyUploadBytes: 10, dropContentLength: true);

        var (status, _) = await SendBytes(app.CreateClient(), "/api/share", Caller, Encoding.UTF8.GetBytes(SmallShare));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal(0, app.Count("plans"));
    }

    [Fact]
    public async Task Share_BodyOver10MB_IsRefusedWith413()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        var big = new byte[10 * 1024 * 1024 + 1];
        Array.Fill(big, (byte)' ');

        var (status, _) = await SendBytes(http, "/api/share", Caller, big);
        using var noLength = new PlanShareApp(dropContentLength: true);
        var (chunked, _) = await SendBytes(noLength.CreateClient(), "/api/share", Caller, big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked);
        Assert.Equal(0, app.Count("plans"));
        Assert.Equal(0, noLength.Count("plans"));
    }

    // --- /api/share: what the reader-based check must keep doing ---

    private static string SharedExpiry(string reply)
    {
        using var doc = JsonDocument.Parse(reply);
        return doc.RootElement.GetProperty("expires_at").GetString()!;
    }

    [Theory]
    [InlineData("""{"ttl_days":3}""", 3)]
    [InlineData("""{"ttl_days":0}""", 1)]
    [InlineData("""{"ttl_days":-5}""", 1)]
    [InlineData("""{"ttl_days":1000}""", 365)]
    [InlineData("""{"ttl_days":99999999999}""", 7)]
    [InlineData("""{"ttl_days":2.0}""", 7)]
    [InlineData("""{"ttl_days":1e1}""", 7)]
    [InlineData("""{"ttl_days":2,"ttl_days":9}""", 9)]
    [InlineData("""{"ttl_days":9,"ttl_days":null}""", 7)]
    [InlineData("""{"ttl_days":4}""", 4)]
    [InlineData("""{"result":{"ttl_days":"x"},"list":[{"ttl_days":[1]}],"ttl_days":5}""", 5)]
    [InlineData("""{"result":{"ttl_days":2}}""", 7)]
    public async Task Share_TtlDays_IsReadFromTheTopLevelOnly_AndClamped(string body, int expectedDays)
    {
        using var app = new PlanShareApp();
        var before = DateTime.UtcNow;

        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, body);

        Assert.Equal(HttpStatusCode.OK, status);
        var after = DateTime.UtcNow;
        Assert.Contains(SharedExpiry(reply), new[]
        {
            before.AddDays(expectedDays).ToString("yyyy-MM-dd"),
            after.AddDays(expectedDays).ToString("yyyy-MM-dd")
        });
    }

    [Fact]
    public async Task Share_TheLastTtlDaysWins_SoABadOneAfterAGoodOneIsRefused_AndAGoodOneAfterABadOneIsNot()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();

        var (bad, _) = await Send(http, Post, "/api/share", Caller, """{"ttl_days":3,"ttl_days":"x"}""");
        var (good, _) = await Send(http, Post, "/api/share", Caller, """{"ttl_days":"x","ttl_days":3}""");

        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Equal(HttpStatusCode.OK, good);
    }

    [Fact]
    public async Task Share_InvalidJsonAfterTheTtl_StillBeatsTheTtlError()
    {
        using var app = new PlanShareApp();

        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, """{"ttl_days":"x","text":""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid JSON", ErrorText(reply));
    }

    [Theory]
    [InlineData("""{"a":1} {"b":2}""")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a":1} // note""")]
    [InlineData("[1,2] x")]
    public async Task Share_TrailingContentCommasAndComments_AreInvalidJson(string body)
    {
        using var app = new PlanShareApp();

        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid JSON", ErrorText(reply));
    }

    [Fact]
    public async Task Share_WhitespaceOnlyBody_IsEmpty()
    {
        using var app = new PlanShareApp();

        var (status, reply) = await Send(app.CreateClient(), Post, "/api/share", Caller, " \n\t ");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Empty body", ErrorText(reply));
    }

    [Fact]
    public async Task Share_NestingToTheDepthLimit_IsAccepted_AndOneDeeperIsRefused()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        // The root object is level 1, so 1023 arrays inside it reach level 1024
        string Nested(int arrays) => "{\"a\":" + new string('[', arrays) + new string(']', arrays) + "}";

        var (accepted, _) = await Send(http, Post, "/api/share", Caller, Nested(1023));
        var (refused, reply) = await Send(http, Post, "/api/share", Caller, Nested(1024));

        Assert.Equal(HttpStatusCode.OK, accepted);
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Equal("Invalid JSON", ErrorText(reply));
    }

    [Fact]
    public async Task Share_StoresTheBodyWithoutAByteOrderMark_AndChargesItsUtf8Length()
    {
        var raw = Encoding.UTF8.GetBytes("{\"text\":\"café 😀\"}");
        var withBom = Encoding.UTF8.GetPreamble().Concat(raw).ToArray();
        // The budget allows exactly the body without its mark
        using var app = new PlanShareApp(dailyUploadBytes: raw.Length, dropContentLength: true);
        var http = app.CreateClient();

        var (status, reply) = await SendBytes(http, "/api/share", Caller, withBom);
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(reply);
        var (getStatus, stored) = await Send(http, HttpMethod.Get, $"/api/plans/{doc.RootElement.GetProperty("id").GetString()}", Caller);

        Assert.Equal(HttpStatusCode.OK, getStatus);
        Assert.Equal(Encoding.UTF8.GetString(raw), stored);
        var (second, _) = await SendBytes(http, "/api/share", Caller, raw);
        Assert.Equal(HttpStatusCode.TooManyRequests, second);
    }

    [Fact]
    public async Task Share_ABadByteInsideAString_IsStoredAsAReplacementCharacter()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        var body = Encoding.UTF8.GetBytes("{\"text\":\"a?b\"}");
        body[10] = 0xFF;

        var (status, reply) = await SendBytes(http, "/api/share", Caller, body);

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(reply);
        var (_, stored) = await Send(http, HttpMethod.Get, $"/api/plans/{doc.RootElement.GetProperty("id").GetString()}", Caller);
        Assert.Equal("{\"text\":\"a�b\"}", stored);
    }

    // --- Read limit on /api/stats and DELETE ---

    [Fact]
    public async Task Stats_IsRateLimited_AfterTheReadLimit()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        for (var i = 0; i < 120; i++)
        {
            var (ok, _) = await Send(http, HttpMethod.Get, "/api/stats", Caller);
            Assert.Equal(HttpStatusCode.OK, ok);
        }

        var (status, _) = await Send(http, HttpMethod.Get, "/api/stats", Caller);
        var (otherCaller, _) = await Send(http, HttpMethod.Get, "/api/stats", "203.0.113.99");

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal(HttpStatusCode.OK, otherCaller);
    }

    [Fact]
    public async Task Delete_IsRateLimited_AfterTheReadLimit_EvenWithAWrongToken()
    {
        using var app = new PlanShareApp();
        var http = app.CreateClient();
        for (var i = 0; i < 120; i++)
        {
            using var request = PlanShareApp.Request(HttpMethod.Delete, "/api/plans/abc12345", Caller);
            request.Headers.Add("X-Delete-Token", "wrong");
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var over = PlanShareApp.Request(HttpMethod.Delete, "/api/plans/abc12345", Caller);
        over.Headers.Add("X-Delete-Token", "wrong");
        using var overResponse = await http.SendAsync(over, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, overResponse.StatusCode);
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
