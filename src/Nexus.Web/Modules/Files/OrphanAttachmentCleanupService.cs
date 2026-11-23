using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Storage;

namespace Nexus.Web.Modules.Files;

/// <summary>
/// Periodically deletes orphaned Pending/Rejected/Quarantined attachments older than the configured TTL (M3.1).
/// </summary>
public sealed class OrphanAttachmentCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<FilePolicyOptions> options,
    ILogger<OrphanAttachmentCleanupService> logger) : BackgroundService
{
    public async Task<int> CleanupOrphansAsync(DateTimeOffset olderThanUtc, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<NexusDbContext>();
        if (db is null)
        {
            return 0;
        }

        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var orphans = await db.Attachments
            .Where(a => a.State != AttachmentState.Attached && a.CreatedAt < olderThanUtc)
            .OrderBy(a => a.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        if (orphans.Count == 0)
        {
            return 0;
        }

        foreach (var item in orphans)
        {
            if (!string.IsNullOrWhiteSpace(item.ObjectKey))
            {
                await storage.DeleteAsync(item.ObjectKey, ct);
            }

            if (!string.IsNullOrWhiteSpace(item.ThumbnailObjectKey))
            {
                await storage.DeleteAsync(item.ThumbnailObjectKey, ct);
            }
        }

        db.Attachments.RemoveRange(orphans);
        await db.SaveChangesAsync(ct);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Cleaned up {Count} orphaned attachment objects.", orphans.Count);
        }
        return orphans.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(5, options.Value.OrphanCleanupIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, options.Value.OrphanAttachmentTtlHours));
                await CleanupOrphansAsync(cutoff, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Orphan attachment cleanup iteration failed.");
            }
        }
    }
}
