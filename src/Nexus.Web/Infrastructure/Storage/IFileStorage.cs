using System.Security.Cryptography;

namespace Nexus.Web.Infrastructure.Storage;

public sealed record StoredObjectDescriptor(
    string ObjectKey,
    long SizeBytes,
    string Sha256Hex,
    string ContentType);

public interface IFileStorage
{
    Task<StoredObjectDescriptor> SaveAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<Uri?> TryCreateSignedDownloadUrlAsync(
        string objectKey,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}

public static class StorageKeyHelper
{
    public static string GenerateRandomObjectKey()
    {
        var now = DateTimeOffset.UtcNow;
        var randomBytes = RandomNumberGenerator.GetBytes(16);
        return $"{now:yyyy}/{now:MM}/{Convert.ToHexStringLower(randomBytes)}";
    }

    public static void ValidateObjectKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException("Object key must not be empty.", nameof(objectKey));
        }

        if (objectKey.StartsWith('/') ||
            objectKey.StartsWith('\\') ||
            objectKey.Contains("..", StringComparison.Ordinal) ||
            objectKey.Contains('\\') ||
            objectKey.Contains('\0'))
        {
            throw new ArgumentException("Object key contains forbidden path characters.", nameof(objectKey));
        }
    }

    public static void ValidateSignedUrlTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromSeconds(FileStorageOptions.HardMaxSignedUrlTtlSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                $"Signed download URL TTL must be between 1 second and {FileStorageOptions.HardMaxSignedUrlTtlSeconds} seconds.");
        }
    }

    public static void ApplySafeDownloadHeaders(HttpResponse response, string downloadFileName, string contentType)
    {
        response.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        response.Headers.ContentDisposition = BuildContentDisposition("attachment", downloadFileName);
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    public static string BuildContentDisposition(string dispositionType, string downloadFileName)
    {
        var safeName = SanitizeFileName(downloadFileName);
        var asciiChars = safeName
            .Select(c => c is >= ' ' and <= '~' && c is not '"' and not '\\' ? c : '_')
            .ToArray();
        var asciiFallback = new string(asciiChars).Trim().Trim('.');
        if (string.IsNullOrEmpty(asciiFallback))
        {
            asciiFallback = "download.bin";
        }

        if (string.Equals(asciiFallback, safeName, StringComparison.Ordinal))
        {
            return $"{dispositionType}; filename=\"{asciiFallback}\"";
        }

        var encodedUtf8 = Uri.EscapeDataString(safeName);
        return $"{dispositionType}; filename=\"{asciiFallback}\"; filename*=UTF-8''{encodedUtf8}";
    }

    public static string SanitizeFileName(string fileName)
    {
        var leaf = Path.GetFileName(fileName ?? string.Empty);
        var chars = leaf
            .Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ')
            .ToArray();
        var cleaned = new string(chars).Trim().Trim('.');
        return string.IsNullOrEmpty(cleaned) ? "download.bin" : cleaned;
    }
}
