using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nexus.Web.Infrastructure.Storage;

/// <summary>
/// Server-side adapter for private Supabase Storage buckets.
/// Privileged service-role credentials stay strictly on the server.
/// </summary>
public sealed class SupabaseFileStorage : IFileStorage
{
    private readonly HttpClient _httpClient;
    private readonly FileStorageOptions _options;

    public SupabaseFileStorage(HttpClient httpClient, IOptions<FileStorageOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<StoredObjectDescriptor> SaveAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var objectKey = StorageKeyHelper.GenerateRandomObjectKey();
        var effectiveContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        using var bufferStream = new MemoryStream();
        await content.CopyToAsync(bufferStream, cancellationToken);
        var bytes = bufferStream.ToArray();
        var sha256Hex = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/storage/v1/object/{Uri.EscapeDataString(_options.SupabaseBucket!)}/{objectKey}");
        request.Headers.Add("x-upsert", "false");
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(effectiveContentType);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return new StoredObjectDescriptor(objectKey, bytes.LongLength, sha256Hex, effectiveContentType);
    }

    public async Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        StorageKeyHelper.ValidateObjectKey(objectKey);

        var encodedBucket = Uri.EscapeDataString(_options.SupabaseBucket!);
        var primaryPath = $"/storage/v1/object/{encodedBucket}/{objectKey}";
        using var primaryRequest = CreateAuthorizedRequest(HttpMethod.Get, primaryPath);
        var response = await _httpClient.SendAsync(primaryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Fallback to /object/authenticated/ for older Supabase gateways that require the authenticated route
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            using var fallbackRequest = CreateAuthorizedRequest(
                HttpMethod.Get,
                $"/storage/v1/object/authenticated/{encodedBucket}/{objectKey}");
            response = await _httpClient.SendAsync(fallbackRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new ResponseBoundStream(stream, response);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        StorageKeyHelper.ValidateObjectKey(objectKey);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/storage/v1/object/{Uri.EscapeDataString(_options.SupabaseBucket!)}/{objectKey}");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<Uri?> TryCreateSignedDownloadUrlAsync(
        string objectKey,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        StorageKeyHelper.ValidateObjectKey(objectKey);
        StorageKeyHelper.ValidateSignedUrlTtl(ttl);

        var expiresIn = Math.Min((int)Math.Ceiling(ttl.TotalSeconds), _options.SignedUrlTtlSeconds);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/storage/v1/object/sign/{Uri.EscapeDataString(_options.SupabaseBucket!)}/{objectKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { expiresIn }),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("signedURL", out var signedPathElem))
        {
            return null;
        }

        var signedPath = signedPathElem.GetString();
        if (string.IsNullOrWhiteSpace(signedPath))
        {
            return null;
        }

        var baseUri = new Uri(_options.SupabaseUrl!.TrimEnd('/') + "/storage/v1/");
        return new Uri(baseUri, signedPath.TrimStart('/'));
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string relativePath)
    {
        var baseUrl = _options.SupabaseUrl!.TrimEnd('/');
        var request = new HttpRequestMessage(method, new Uri(baseUrl + relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SupabaseServiceRoleKey);
        request.Headers.Add("apikey", _options.SupabaseServiceRoleKey);
        return request;
    }

    private sealed class ResponseBoundStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }
}
