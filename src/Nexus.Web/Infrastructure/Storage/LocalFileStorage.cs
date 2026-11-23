using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Nexus.Web.Infrastructure.Storage;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootDirectory;
    private readonly string _rootWithSeparator;

    public LocalFileStorage(IOptions<FileStorageOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.LocalRootPath;
        var resolved = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);

        _rootDirectory = Path.GetFullPath(resolved);
        _rootWithSeparator = _rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _rootDirectory
            : _rootDirectory + Path.DirectorySeparatorChar;
    }

    public async Task<StoredObjectDescriptor> SaveAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var objectKey = StorageKeyHelper.GenerateRandomObjectKey();
        var targetPath = ResolveSafePath(objectKey);
        var directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        long totalBytes = 0;
        string sha256Hex;

        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalBytes += read;
                }

                await fs.FlushAsync(cancellationToken);
            }

            sha256Hex = Convert.ToHexStringLower(hasher.GetHashAndReset());
            File.Move(tempPath, targetPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }

        return new StoredObjectDescriptor(
            objectKey,
            totalBytes,
            sha256Hex,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
    }

    public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var targetPath = ResolveSafePath(objectKey);
        if (!File.Exists(targetPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var targetPath = ResolveSafePath(objectKey);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        return Task.CompletedTask;
    }

    public Task<Uri?> TryCreateSignedDownloadUrlAsync(
        string objectKey,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        StorageKeyHelper.ValidateObjectKey(objectKey);
        StorageKeyHelper.ValidateSignedUrlTtl(ttl);
        // Local storage streams authorized downloads directly through Nexus.Web.
        return Task.FromResult<Uri?>(null);
    }

    private string ResolveSafePath(string objectKey)
    {
        StorageKeyHelper.ValidateObjectKey(objectKey);
        var relativeOsPath = objectKey.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, relativeOsPath));

        if (!fullPath.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved storage path escapes configured root directory.");
        }

        return fullPath;
    }
}
