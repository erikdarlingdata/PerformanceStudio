using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PlanViewer.Core.Output;

namespace PlanViewer.Web.Services;

/// <summary>Result of uploading a plan analysis to the share server.</summary>
public record PlanShareResult(string? Id, string? DeleteToken);

/// <summary>A shared plan analysis fetched back from the share server.</summary>
public record SharedPlan(AnalysisResult? Result, string? Text);

/// <summary>
/// Thrown when the share server returns a non-success response. The message is
/// already user-facing, so callers can surface it directly.
/// </summary>
public sealed class PlanShareException : Exception
{
    public PlanShareException(string message) : base(message) { }
}

/// <summary>
/// Talks to the public plan-share API (upload / fetch / delete). Pulled out of
/// Index.razor so the page no longer news up an <see cref="HttpClient"/> per call
/// and the HTTP/JSON plumbing is testable in isolation. UI concerns (clipboard,
/// state, error display) stay in the component.
/// </summary>
public interface IPlanShareService
{
    Task<PlanShareResult> ShareAsync(AnalysisResult result, string text, int ttlDays);
    Task DeleteAsync(string shareId, string deleteToken);
    Task<SharedPlan> LoadAsync(string id);
}

public sealed class PlanShareService : IPlanShareService
{
    public const string ApiBase = "https://stats.erikdarling.com";

    private readonly HttpClient _http;

    public PlanShareService(HttpClient http) => _http = http;

    public async Task<PlanShareResult> ShareAsync(AnalysisResult result, string text, int ttlDays)
    {
        /* AnalysisJson.Wire, not default options: this serializes an AnalysisResult, and #431's
           depth ceiling exists for "every writer of this object". Default MaxDepth is 64, an
           operator costs two JSON levels, so sharing a plan ~30 operators deep threw an "object
           cycle" JsonException here while the same analysis rendered fine everywhere else. */
        var payload = JsonSerializer.Serialize(new
        {
            result = result,
            text = text,
            ttl_days = ttlDays
        }, AnalysisJson.Wire);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{ApiBase}/api/share", content);

        if (!response.IsSuccessStatusCode)
            throw new PlanShareException($"Share failed: server returned {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString();
        var deleteToken = doc.RootElement.GetProperty("delete_token").GetString();
        return new PlanShareResult(id, deleteToken);
    }

    public async Task DeleteAsync(string shareId, string deleteToken)
    {
        var response = await _http.DeleteAsync($"{ApiBase}/api/plans/{Uri.EscapeDataString(shareId)}?token={Uri.EscapeDataString(deleteToken)}");
        if (!response.IsSuccessStatusCode)
            throw new PlanShareException("Failed to delete shared plan.");
    }

    public async Task<SharedPlan> LoadAsync(string id)
    {
        var response = await _http.GetAsync($"{ApiBase}/api/plans/{Uri.EscapeDataString(id)}");
        if (!response.IsSuccessStatusCode)
        {
            throw new PlanShareException(response.StatusCode == HttpStatusCode.NotFound
                ? "This shared plan has expired or does not exist."
                : $"Failed to load shared plan: {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync();
        /* Reading a share hits the default 64 ceiling twice — JsonDocumentOptions and
           JsonSerializerOptions each carry their own MaxDepth — so a deep plan that ShareAsync
           could now write would still fail to load without both raised. A share must never be
           writable but not readable. */
        using var doc = JsonDocument.Parse(json, AnalysisJson.Document);
        var root = doc.RootElement;
        var result = JsonSerializer.Deserialize<AnalysisResult>(root.GetProperty("result").GetRawText(), AnalysisJson.Wire);
        var text = root.GetProperty("text").GetString();
        return new SharedPlan(result, text);
    }
}
