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
        var payload = JsonSerializer.Serialize(new
        {
            result = result,
            text = text,
            ttl_days = ttlDays
        });
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
        var response = await _http.DeleteAsync($"{ApiBase}/api/plans/{shareId}?token={deleteToken}");
        if (!response.IsSuccessStatusCode)
            throw new PlanShareException("Failed to delete shared plan.");
    }

    public async Task<SharedPlan> LoadAsync(string id)
    {
        var response = await _http.GetAsync($"{ApiBase}/api/plans/{id}");
        if (!response.IsSuccessStatusCode)
        {
            throw new PlanShareException(response.StatusCode == HttpStatusCode.NotFound
                ? "This shared plan has expired or does not exist."
                : $"Failed to load shared plan: {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = JsonSerializer.Deserialize<AnalysisResult>(root.GetProperty("result").GetRawText());
        var text = root.GetProperty("text").GetString();
        return new SharedPlan(result, text);
    }
}
