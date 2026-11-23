namespace Nexus.Web.Infrastructure.Security;

public sealed class NexusSecurityOptions
{
    public const string SectionName = "NexusSecurity";
    public const int HardMaxPageSize = 50;

    public string[] AllowedOrigins { get; set; } = [];
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxPageSize { get; set; } = HardMaxPageSize;
    public int RateLimitPermitsPerMinute { get; set; } = 120;
    public string HubPathPrefix { get; set; } = "/hubs";
    public bool TrustForwardedHeaders { get; set; }

    public void Validate()
    {
        if (MaxRequestBodyBytes <= 0)
        {
            throw new InvalidOperationException("NexusSecurity:MaxRequestBodyBytes must be positive.");
        }

        if (MaxPageSize is <= 0 or > HardMaxPageSize)
        {
            throw new InvalidOperationException($"NexusSecurity:MaxPageSize must be between 1 and {HardMaxPageSize}.");
        }

        if (RateLimitPermitsPerMinute <= 0)
        {
            throw new InvalidOperationException("NexusSecurity:RateLimitPermitsPerMinute must be positive.");
        }

        foreach (var origin in AllowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(origin) || origin.Trim() == "*")
            {
                throw new InvalidOperationException("Wildcard '*' or empty origins are forbidden in NexusSecurity:AllowedOrigins.");
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.PathAndQuery.TrimStart('/')))
            {
                throw new InvalidOperationException($"Invalid origin '{origin}' in NexusSecurity:AllowedOrigins. Expected scheme://host[:port].");
            }
        }
    }

    public bool IsOriginAllowed(string origin, string requestScheme, HostString requestHost)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Trim() == "*")
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin))
        {
            return false;
        }

        var normalizedOrigin = parsedOrigin.GetLeftPart(UriPartial.Authority);

        if (requestHost.HasValue)
        {
            var sameOrigin = $"{requestScheme}://{requestHost.Value}";
            if (Uri.TryCreate(sameOrigin, UriKind.Absolute, out var parsedSameOrigin) &&
                string.Equals(normalizedOrigin, parsedSameOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var allowed in AllowedOrigins)
        {
            if (Uri.TryCreate(allowed, UriKind.Absolute, out var parsedAllowed) &&
                string.Equals(normalizedOrigin, parsedAllowed.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
