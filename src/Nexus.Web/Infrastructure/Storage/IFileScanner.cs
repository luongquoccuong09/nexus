namespace Nexus.Web.Infrastructure.Storage;

public enum FileScanVerdict
{
    Clean,
    Infected,
    SkippedPrototype,
    FailedClosed
}

public sealed record FileScanResult(FileScanVerdict Verdict, string Engine, string? Detail = null)
{
    public string StatusCode => Verdict switch
    {
        FileScanVerdict.Clean => "clean",
        FileScanVerdict.Infected => "infected",
        FileScanVerdict.SkippedPrototype => "skipped_prototype",
        FileScanVerdict.FailedClosed => "failed_closed",
        _ => "failed_closed"
    };
}

public interface IFileScanner
{
    Task<FileScanResult> ScanAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// M0.9 prototype stub returning skipped_prototype.
/// Must be replaced by fail-closed ClamAV scanning in M3.6 before accepting real data.
/// </summary>
public sealed class PrototypeFileScannerStub : IFileScanner
{
    public Task<FileScanResult> ScanAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Task.FromResult(new FileScanResult(
            FileScanVerdict.SkippedPrototype,
            "prototype_stub",
            "Prototype scanner stub; ClamAV fail-closed scanning is required before pilot (M3.6)."));
    }
}
