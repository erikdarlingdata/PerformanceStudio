using System.Net.Http;
using Velopack.Sources;

namespace PlanViewer.App.Services;

/// <summary>
/// Velopack downloader that routes through the user-configured proxy. The default
/// <see cref="HttpClientFileDownloader"/> creates a vanilla <see cref="HttpClientHandler"/>
/// whose proxy never sees Windows credentials, which breaks "Check for Updates" on
/// most corporate networks (GitHub issue #314).
/// </summary>
internal sealed class ProxyAwareDownloader : HttpClientFileDownloader
{
    // Snapshot at construction so a long-running Velopack flow (retries, redirects,
    // delta + full downloads) doesn't keep re-reading the credential manager.
    private readonly ProxySettings _settings = ProxySettings.Load();

    protected override HttpClientHandler CreateHttpClientHandler()
    {
        return ProxyHttpHandlerFactory.Create(_settings);
    }
}
