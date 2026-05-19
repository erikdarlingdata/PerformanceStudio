using System.Net;
using System.Net.Http;

namespace PlanViewer.App.Services;

/// <summary>
/// Builds an <see cref="HttpClientHandler"/> configured from <see cref="ProxySettings"/>.
/// Centralised so the update check and the Velopack downloader share one source of truth.
/// </summary>
internal static class ProxyHttpHandlerFactory
{
    public static HttpClientHandler Create(ProxySettings settings)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        if (settings.Mode == ProxyMode.Manual && !string.IsNullOrWhiteSpace(settings.Address))
        {
            var proxy = new WebProxy(settings.Address);
            proxy.Credentials = string.IsNullOrEmpty(settings.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(settings.Username, settings.Password);

            handler.UseProxy = true;
            handler.Proxy = proxy;
            handler.PreAuthenticate = true;
        }
        else
        {
            // System / auto-discovered proxy — most corporate setups need default
            // Windows credentials sent for NTLM/Negotiate. This is the cheap fix
            // that solves auto-detected proxies without any UI.
            handler.UseProxy = true;
            try
            {
                handler.Proxy = WebRequest.GetSystemWebProxy();
            }
            catch
            {
                // GetSystemWebProxy can throw on some Linux configs — fall back to
                // HttpClient's default proxy resolution.
            }
            handler.DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials;
            handler.PreAuthenticate = true;
        }

        return handler;
    }
}
