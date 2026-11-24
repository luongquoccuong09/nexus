using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Nexus.Web.Modules.Notifications;

public enum WebPushResult
{
    Accepted,
    Expired,
    Rejected
}

public sealed class WebPushTransport : IDisposable
{
    public const string ClientName = "Nexus.WebPush";
    public static readonly HttpRequestOptionsKey<bool> PrivateEndpoint = new("Nexus.PrivatePushEndpoint");

    private readonly IHttpClientFactory _clients;
    private readonly string? _subject;
    private readonly ECDsa? _signer;
    private readonly Dictionary<string, (string Token, DateTimeOffset ValidUntil)> _tokens = new(StringComparer.Ordinal);

    public WebPushTransport(IConfiguration configuration, IHttpClientFactory clients)
    {
        _clients = clients;
        var pem = configuration["NexusPush:VapidPrivateKeyPem"];
        if (string.IsNullOrWhiteSpace(pem)) return;

        _subject = configuration["NexusPush:VapidSubject"];
        if (!Uri.TryCreate(_subject, UriKind.Absolute, out var subject) || subject.Scheme is not ("mailto" or "https"))
        {
            throw new InvalidOperationException("Set NexusPush:VapidSubject to a mailto or HTTPS URL.");
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value || parameters.D?.Length != 32 ||
            parameters.Q.X?.Length != 32 || parameters.Q.Y?.Length != 32)
        {
            throw new InvalidOperationException("The VAPID key must be a P-256 private key.");
        }

        _signer = ECDsa.Create(parameters);
        PublicKey = WebEncoders.Base64UrlEncode([4, .. parameters.Q.X, .. parameters.Q.Y]);
    }

    public string? PublicKey { get; }

    public static bool TryParseEndpoint(string? value, out Uri? endpoint)
    {
        endpoint = null;
        if (value is not { Length: > 0 and <= 1600 } || !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort || parsed.UserInfo.Length != 0 ||
            parsed.Fragment.Length != 0 || parsed.AbsolutePath == "/" || parsed.AbsoluteUri.Length > 1600)
        {
            return false;
        }

        var host = parsed.IdnHost.ToLowerInvariant();
        if (host is not ("fcm.googleapis.com" or "updates.push.services.mozilla.com" or "web.push.apple.com" or "notify.windows.com") &&
            !host.EndsWith(".push.apple.com", StringComparison.Ordinal) && !host.EndsWith(".notify.windows.com", StringComparison.Ordinal))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    public async Task<WebPushResult> SendAsync(Uri endpoint, Guid conversationId, CancellationToken ct)
    {
        if (_signer is null || !TryParseEndpoint(endpoint.AbsoluteUri, out _)) return WebPushResult.Rejected;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent([])
        };
        request.Options.Set(PrivateEndpoint, true);
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={GetToken(endpoint)}, k={PublicKey}");
        request.Headers.TryAddWithoutValidation("TTL", "300");
        request.Headers.TryAddWithoutValidation("Urgency", "normal");
        var topic = SHA256.HashData(conversationId.ToByteArray());
        request.Headers.TryAddWithoutValidation("Topic", WebEncoders.Base64UrlEncode(topic.AsSpan(0, 18)));

        using var client = _clients.CreateClient(ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return WebPushResult.Accepted;
        if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound) return WebPushResult.Expired;
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            throw new HttpRequestException("The push service is temporarily unavailable.", null, response.StatusCode);
        }

        return WebPushResult.Rejected;
    }

    private string GetToken(Uri endpoint)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var now = DateTimeOffset.UtcNow;
        lock (_tokens)
        {
            if (_tokens.TryGetValue(audience, out var entry) && entry.ValidUntil > now.AddHours(1)) return entry.Token;

            var expires = now.AddHours(4);
            var header = WebEncoders.Base64UrlEncode("""{"typ":"JWT","alg":"ES256"}"""u8.ToArray());
            var claims = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                aud = audience, exp = expires.ToUnixTimeSeconds(), sub = _subject
            }));
            var unsigned = $"{header}.{claims}";
            var signature = _signer!.SignData(System.Text.Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var token = $"{unsigned}.{WebEncoders.Base64UrlEncode(signature)}";
            _tokens[audience] = (token, expires);
            return token;
        }
    }

    public void Dispose() => _signer?.Dispose();
}
