using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Storage;

namespace Nexus.Web.Modules.Files;

/// <summary>
/// Native ClamAV clamd TCP scanner using the standard zINSTREAM protocol (M3.6).
/// Fails closed on any connection, timeout, or protocol error so unscanned files are never released.
/// </summary>
public sealed class ClamAvFileScanner(IOptions<FilePolicyOptions> options) : IFileScanner
{
    private const int ChunkSize = 64 * 1024;
    private static readonly byte[] InstreamCommand = "zINSTREAM\0"u8.ToArray();

    public async Task<FileScanResult> ScanAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.ClamAvHost))
        {
            return new FileScanResult(FileScanVerdict.FailedClosed, "clamav", "ClamAV host is not configured.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, opts.ClamAvTimeoutSeconds)));
        var ct = timeoutCts.Token;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(opts.ClamAvHost, opts.ClamAvPort, ct);
            await using var netStream = client.GetStream();

            await netStream.WriteAsync(InstreamCommand, ct);

            var chunkBuffer = new byte[ChunkSize];
            var lenPrefix = new byte[4];
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            int read;
            while ((read = await content.ReadAsync(chunkBuffer, ct)) > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(lenPrefix, (uint)read);
                await netStream.WriteAsync(lenPrefix, ct);
                await netStream.WriteAsync(chunkBuffer.AsMemory(0, read), ct);
            }

            // Terminate INSTREAM with a zero-length chunk
            BinaryPrimitives.WriteUInt32BigEndian(lenPrefix, 0);
            await netStream.WriteAsync(lenPrefix, ct);
            await netStream.FlushAsync(ct);

            var respBuf = new byte[512];
            var totalRead = 0;
            while (totalRead < respBuf.Length)
            {
                var n = await netStream.ReadAsync(respBuf.AsMemory(totalRead), ct);
                if (n <= 0) break;
                totalRead += n;
                if (Array.IndexOf(respBuf, (byte)0, 0, totalRead) >= 0) break;
            }

            var response = Encoding.ASCII.GetString(respBuf, 0, totalRead).Trim('\0', '\r', '\n', ' ');
            if (response.EndsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                return new FileScanResult(FileScanVerdict.Clean, "clamav", "clean");
            }

            if (response.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
            {
                return new FileScanResult(FileScanVerdict.Infected, "clamav", response);
            }

            return new FileScanResult(FileScanVerdict.FailedClosed, "clamav", $"Unexpected clamd response: {response}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new FileScanResult(FileScanVerdict.FailedClosed, "clamav", $"ClamAV unavailable: {ex.Message}");
        }
    }
}
