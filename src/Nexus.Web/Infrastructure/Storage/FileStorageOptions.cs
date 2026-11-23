namespace Nexus.Web.Infrastructure.Storage;

public sealed class FileStorageOptions
{
    public const string SectionName = "NexusStorage";
    public const int HardMaxSignedUrlTtlSeconds = 60;

    public string Provider { get; set; } = "Local";
    public string LocalRootPath { get; set; } = "App_Data/storage";
    public int SignedUrlTtlSeconds { get; set; } = HardMaxSignedUrlTtlSeconds;

    // Server-side only; never exposed to Nexus.Client.
    public string? SupabaseUrl { get; set; }
    public string? SupabaseBucket { get; set; }
    public string? SupabaseServiceRoleKey { get; set; }

    public void Validate()
    {
        if (SignedUrlTtlSeconds is <= 0 or > HardMaxSignedUrlTtlSeconds)
        {
            throw new InvalidOperationException(
                $"NexusStorage:SignedUrlTtlSeconds must be between 1 and {HardMaxSignedUrlTtlSeconds}.");
        }

        if (string.Equals(Provider, "Local", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(LocalRootPath))
            {
                throw new InvalidOperationException("NexusStorage:LocalRootPath is required when Provider is Local.");
            }

            var normalized = LocalRootPath.Replace('\\', '/');
            if (normalized.Contains("wwwroot", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("NexusStorage:LocalRootPath must not be located under wwwroot.");
            }

            return;
        }

        if (string.Equals(Provider, "Supabase", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(SupabaseUrl) ||
                !Uri.TryCreate(SupabaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("NexusStorage:SupabaseUrl must be a valid HTTPS URL.");
            }

            if (string.IsNullOrWhiteSpace(SupabaseBucket))
            {
                throw new InvalidOperationException("NexusStorage:SupabaseBucket is required when Provider is Supabase.");
            }

            if (string.IsNullOrWhiteSpace(SupabaseServiceRoleKey))
            {
                throw new InvalidOperationException("NexusStorage:SupabaseServiceRoleKey is required when Provider is Supabase.");
            }

            return;
        }

        throw new InvalidOperationException($"Unsupported NexusStorage:Provider '{Provider}'. Expected 'Local' or 'Supabase'.");
    }
}
