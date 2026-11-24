using System.Net;

namespace Nexus.LoadRunner;

internal static class RunnerHttp
{
    public static HttpClient Client(Uri baseUri, TimeSpan timeout, CookieContainer? cookies = null) =>
        new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true,
            UseCookies = cookies is not null,
            CookieContainer = cookies ?? new CookieContainer(),
            UseProxy = !baseUri.IsLoopback
        }) { BaseAddress = baseUri, Timeout = timeout };

    public static async Task<bool> ReadyAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync("/readyz", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            return false;
        }
    }

    public static bool IsTransient(Exception error, CancellationToken ct) =>
        error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested;
}
