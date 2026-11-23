using Nexus.Web.Infrastructure.Storage;

namespace Nexus.Web.Modules.Files;

/// <summary>
/// Configurable file upload limits, quotas, and malware-scanning policy (M3.2, M3.5, M3.6, P0-D08).
/// </summary>
public sealed class FilePolicyOptions
{
    public const string SectionName = "NexusFiles";

    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10 MB default (aligned with Kestrel limit until D08 finalized)
    public long PerUserQuotaBytes { get; set; } = 200 * 1024 * 1024; // 200 MB
    public long PerConversationQuotaBytes { get; set; } = 500 * 1024 * 1024; // 500 MB
    public double QuotaWarningRatio { get; set; } = 0.80;
    public int MaxAttachmentsPerMessage { get; set; } = 10;
    public int OrphanAttachmentTtlHours { get; set; } = 24;
    public int OrphanCleanupIntervalMinutes { get; set; } = 60;
    public int DefaultSignedUrlTtlSeconds { get; set; } = FileStorageOptions.HardMaxSignedUrlTtlSeconds;

    /// <summary>
    /// When true (required before pilot / real company data per M3.6), only FileScanVerdict.Clean
    /// is released. SkippedPrototype, Infected, and FailedClosed are rejected fail-closed.
    /// </summary>
    public bool FailClosedScanning { get; set; }

    public string? ClamAvHost { get; set; }
    public int ClamAvPort { get; set; } = 3310;
    public int ClamAvTimeoutSeconds { get; set; } = 15;

    public void Validate()
    {
        if (MaxFileSizeBytes <= 0)
        {
            throw new InvalidOperationException("NexusFiles:MaxFileSizeBytes must be positive.");
        }

        if (PerUserQuotaBytes <= 0 || PerConversationQuotaBytes <= 0)
        {
            throw new InvalidOperationException("NexusFiles quotas must be positive.");
        }

        if (DefaultSignedUrlTtlSeconds is < 1 or > FileStorageOptions.HardMaxSignedUrlTtlSeconds)
        {
            throw new InvalidOperationException(
                $"NexusFiles:DefaultSignedUrlTtlSeconds must be between 1 and {FileStorageOptions.HardMaxSignedUrlTtlSeconds}.");
        }

        if (ClamAvPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("NexusFiles:ClamAvPort must be a valid TCP port.");
        }
    }
}
