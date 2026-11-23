using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Storage;

namespace Nexus.IntegrationTests;

public sealed class StorageAndScannerTests : IDisposable
{
    private readonly string _tempRoot;

    public StorageAndScannerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "nexus-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFileStorage_RoundTripsPrivatePdfWithSha256AndRandomKeys()
    {
        var storage = CreateLocalStorage();
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj <<>> endobj\n%%EOF");
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(pdfBytes));

        StoredObjectDescriptor saved1;
        StoredObjectDescriptor saved2;
        await using (var s1 = new MemoryStream(pdfBytes))
        {
            saved1 = await storage.SaveAsync(s1, "application/pdf");
        }
        await using (var s2 = new MemoryStream(pdfBytes))
        {
            saved2 = await storage.SaveAsync(s2, "application/pdf");
        }

        Assert.NotEqual(saved1.ObjectKey, saved2.ObjectKey);
        Assert.Equal(pdfBytes.LongLength, saved1.SizeBytes);
        Assert.Equal(expectedSha, saved1.Sha256Hex);
        Assert.Equal("application/pdf", saved1.ContentType);

        await using (var readStream = await storage.OpenReadAsync(saved1.ObjectKey))
        {
            Assert.NotNull(readStream);
            using var ms = new MemoryStream();
            await readStream!.CopyToAsync(ms);
            Assert.Equal(pdfBytes, ms.ToArray());
        }

        // Local storage streams directly through server (returns null signed URL)
        var signedUrl = await storage.TryCreateSignedDownloadUrlAsync(saved1.ObjectKey, TimeSpan.FromSeconds(60));
        Assert.Null(signedUrl);

        await storage.DeleteAsync(saved1.ObjectKey);
        Assert.Null(await storage.OpenReadAsync(saved1.ObjectKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape.pdf")]
    [InlineData("2026/11/../../escape.pdf")]
    [InlineData("/etc/passwd")]
    [InlineData("folder\\file.pdf")]
    public async Task LocalFileStorage_RejectsPathTraversalKeys(string badKey)
    {
        var storage = CreateLocalStorage();
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(badKey));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(badKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(61)]
    [InlineData(300)]
    public async Task Storage_RejectsSignedUrlTtlOutside1To60Seconds(int ttlSeconds)
    {
        var storage = CreateLocalStorage();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            storage.TryCreateSignedDownloadUrlAsync("2026/11/abc", TimeSpan.FromSeconds(ttlSeconds)));
    }

    [Fact]
    public void FileStorageOptions_RejectsUnsafeConfigurations()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FileStorageOptions { Provider = "Local", LocalRootPath = "wwwroot/uploads" }.Validate());

        Assert.Throws<InvalidOperationException>(() =>
            new FileStorageOptions { Provider = "Local", SignedUrlTtlSeconds = 61 }.Validate());

        Assert.Throws<InvalidOperationException>(() =>
            new FileStorageOptions
            {
                Provider = "Supabase",
                SupabaseUrl = "http://insecure.supabase.co",
                SupabaseBucket = "private",
                SupabaseServiceRoleKey = "key"
            }.Validate());

        Assert.Throws<InvalidOperationException>(() =>
            new FileStorageOptions
            {
                Provider = "Supabase",
                SupabaseUrl = "https://project.supabase.co",
                SupabaseBucket = "private",
                SupabaseServiceRoleKey = ""
            }.Validate());
    }

    [Fact]
    public async Task SupabaseFileStorage_SendsServerCredentialsAndCreatesShortLivedSignedUrl()
    {
        HttpRequestMessage? lastRequest = null;
        string? lastBody = null;
        var handler = new StubHttpMessageHandler(async req =>
        {
            lastRequest = req;
            lastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { signedURL = "/object/sign/nexus-private/2026/11/obj?token=short" }),
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var options = Options.Create(new FileStorageOptions
        {
            Provider = "Supabase",
            SupabaseUrl = "https://example.supabase.co",
            SupabaseBucket = "nexus-private",
            SupabaseServiceRoleKey = "server-only-service-role-key",
            SignedUrlTtlSeconds = 60
        });
        var storage = new SupabaseFileStorage(httpClient, options);

        var uri = await storage.TryCreateSignedDownloadUrlAsync("2026/11/obj", TimeSpan.FromSeconds(45));

        Assert.NotNull(uri);
        Assert.StartsWith("https://example.supabase.co/storage/v1/object/sign/nexus-private/", uri!.ToString());
        Assert.Equal("Bearer", lastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("server-only-service-role-key", lastRequest.Headers.Authorization?.Parameter);
        Assert.Contains("\"expiresIn\":45", lastBody);
    }

    [Fact]
    public async Task PrototypeFileScannerStub_ReturnsSkippedPrototype()
    {
        var scanner = new PrototypeFileScannerStub();
        await using var stream = new MemoryStream("%PDF-1.7"u8.ToArray());

        var result = await scanner.ScanAsync(stream, "report.pdf");

        Assert.Equal(FileScanVerdict.SkippedPrototype, result.Verdict);
        Assert.Equal("skipped_prototype", result.StatusCode);
    }

    [Fact]
    public void DownloadHeaders_EnforceAttachmentNosniffAndSanitizedFilename()
    {
        var context = new DefaultHttpContext();
        StorageKeyHelper.ApplySafeDownloadHeaders(context.Response, "../../evil\";name.pdf", "application/pdf");

        Assert.Equal("application/pdf", context.Response.ContentType);
        Assert.Equal("attachment; filename=\"evilname.pdf\"", context.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
    }

    private LocalFileStorage CreateLocalStorage()
    {
        var options = Options.Create(new FileStorageOptions
        {
            Provider = "Local",
            LocalRootPath = _tempRoot,
            SignedUrlTtlSeconds = 60
        });
        return new LocalFileStorage(options, new StubHostEnvironment(_tempRoot));
    }

    private sealed class StubHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Nexus.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
