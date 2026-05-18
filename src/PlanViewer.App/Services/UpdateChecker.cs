using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace PlanViewer.App.Services;

public record UpdateCheckResult(bool UpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? Error);

public static class UpdateChecker
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/erikdarlingdata/PerformanceStudio/releases/latest";

    public static async Task<UpdateCheckResult> CheckAsync(Version currentVersion)
    {
        // Build a fresh client per call so proxy-setting changes take effect without
        // requiring an app restart. The check is rare and the handler is cheap.
        using var handler = ProxyHttpHandlerFactory.Create(ProxySettings.Load());
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Add("User-Agent", "PerformanceStudio-UpdateCheck");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        try
        {
            var json = await http.GetStringAsync(ReleasesApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            var htmlUrl = root.GetProperty("html_url").GetString();

            if (string.IsNullOrEmpty(tagName))
                return new UpdateCheckResult(false, null, null, "No release tag found");

            // Strip leading 'v' from tag (e.g. "v0.9.0" -> "0.9.0")
            var versionStr = tagName.StartsWith('v') ? tagName[1..] : tagName;

            if (!Version.TryParse(versionStr, out var latestVersion))
                return new UpdateCheckResult(false, tagName, htmlUrl, $"Could not parse version: {tagName}");

            var updateAvailable = latestVersion > currentVersion;
            return new UpdateCheckResult(updateAvailable, tagName, htmlUrl, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, null, ex.Message);
        }
    }
}
