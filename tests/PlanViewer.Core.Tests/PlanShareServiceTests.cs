using System.Net;
using System.Text;
using PlanViewer.Core.Output;
using PlanViewer.Web.Services;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The web client's share service, run against a stub HTTP handler, so nothing goes to the real
/// server. The service source is compiled into this project (see the csproj) because the Blazor
/// WASM project is not referenced here.
/// </summary>
public class PlanShareServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public Dictionary<string, string> Headers { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Copied here because the service disposes the request when the call returns
            Method = request.Method;
            Uri = request.RequestUri;
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(",", header.Value);
            return Task.FromResult(_respond());
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
    }

    private static Task<PlanShareResult> Share(StubHandler handler)
    {
        var service = new PlanShareService(new HttpClient(handler));
        return service.ShareAsync(new AnalysisResult(), "text report", ttlDays: 7);
    }

    [Fact]
    public async Task Share_ReturnsTheIdAndDeleteToken()
    {
        var handler = new StubHandler(() => Reply(HttpStatusCode.OK, """{"id":"abc12345","delete_token":"0123abcd","expires_at":"2026-10-05"}"""));

        var result = await Share(handler);

        Assert.Equal("abc12345", result.Id);
        Assert.Equal("0123abcd", result.DeleteToken);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"{PlanShareService.ApiBase}/api/share", handler.Uri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InsufficientStorage, "Plan sharing is full right now. Please try again later.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Daily sharing limit reached for your network. Please try again tomorrow.")]
    [InlineData(HttpStatusCode.BadRequest, "The request body must be a JSON object.")]
    // Server text wins over the built-in 413 message below
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Plans this large can't be shared.")]
    public async Task Share_ShowsTheErrorTextTheServerSent(HttpStatusCode status, string error)
    {
        var handler = new StubHandler(() => Reply(status, $$"""{"error":"{{error}}"}"""));

        var ex = await Assert.ThrowsAsync<PlanShareException>(() => Share(handler));

        Assert.Equal(error, ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>", "text/html")]
    [InlineData(HttpStatusCode.TooManyRequests, "", "application/json")]
    [InlineData(HttpStatusCode.InsufficientStorage, """{"message":"no error property"}""", "application/json")]
    [InlineData(HttpStatusCode.InsufficientStorage, """{"error":5}""", "application/json")]
    [InlineData(HttpStatusCode.InsufficientStorage, """{"error":"  "}""", "application/json")]
    [InlineData(HttpStatusCode.BadRequest, """["error"]""", "application/json")]
    public async Task Share_FallsBackToTheStatusCode_WhenTheReplyHasNoErrorText(HttpStatusCode status, string body, string contentType)
    {
        var handler = new StubHandler(() => Reply(status, body, contentType));

        var ex = await Assert.ThrowsAsync<PlanShareException>(() => Share(handler));

        Assert.Equal($"Share failed: server returned {(int)status}", ex.Message);
    }

    // nginx refuses an oversized upload with its own HTML page, and Kestrel with an empty body
    [Theory]
    [InlineData("<html><head><title>413 Request Entity Too Large</title></head><body><center><h1>413 Request Entity Too Large</h1></center><hr><center>nginx</center></body></html>", "text/html")]
    [InlineData("", "application/json")]
    public async Task Share_ExplainsTheSizeLimit_WhenTheUploadIsTooLarge(string body, string contentType)
    {
        var handler = new StubHandler(() => Reply(HttpStatusCode.RequestEntityTooLarge, body, contentType));

        var ex = await Assert.ThrowsAsync<PlanShareException>(() => Share(handler));

        Assert.Equal("This plan is too large to share. The limit is 10 MB.", ex.Message);
    }

    [Fact]
    public async Task Delete_SendsTheTokenInAHeader_NotInTheUrl()
    {
        var handler = new StubHandler(() => Reply(HttpStatusCode.OK));
        var service = new PlanShareService(new HttpClient(handler));

        await service.DeleteAsync("abc12345", "0123456789abcdef0123456789abcdef");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal($"{PlanShareService.ApiBase}/api/plans/abc12345", handler.Uri!.ToString());
        Assert.Equal("0123456789abcdef0123456789abcdef", handler.Headers["X-Delete-Token"]);
    }

    [Fact]
    public async Task Delete_ThrowsTheUserFacingMessage_WhenTheServerRefuses()
    {
        var handler = new StubHandler(() => Reply(HttpStatusCode.NotFound));
        var service = new PlanShareService(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<PlanShareException>(() => service.DeleteAsync("abc12345", "token"));

        Assert.Equal("Failed to delete shared plan.", ex.Message);
    }
}
