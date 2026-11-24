using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;

namespace Nexus.Web.Modules.Notifications;

public interface IOutboxHandler
{
    // Stage database changes using the processor's scoped DbContext and transaction.
    Task HandleAsync(OutboxMessageEntity message, CancellationToken ct);
}

public sealed class OutboxProcessor(
    NexusDbContext db,
    IServiceProvider services,
    ILogger<OutboxProcessor> logger)
{
    public const int MaxAttempts = 6;

    public async Task<bool> ProcessNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var messages = await db.OutboxMessages.FromSqlInterpolated($"""
            SELECT * FROM outbox_messages
            WHERE completed_at IS NULL AND dead_lettered_at IS NULL AND available_at <= {now}
            ORDER BY available_at, id
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        var message = messages.SingleOrDefault();
        if (message is null)
        {
            return false;
        }

        var handler = services.GetKeyedService<IOutboxHandler>(message.Kind);
        message.Attempts++;
        if (handler is null)
        {
            message.DeadLetteredAt = now;
            message.LastFailure = "unknown_event_kind";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            LogFailure(message);
            db.ChangeTracker.Clear();
            return true;
        }

        const string savepoint = "before_outbox_handler";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            await handler.HandleAsync(message, ct);
            if (message.AvailableAt > now)
            {
                message.Attempts--;
            }
            else
            {
                message.CompletedAt = now;
            }
            message.LastFailure = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await tx.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            message.CompletedAt = null;
            message.LastFailure = ex.GetType().Name;
            if (message.Attempts >= MaxAttempts)
            {
                message.DeadLetteredAt = now;
            }
            else
            {
                message.AvailableAt = now.AddSeconds(5 * (1 << (message.Attempts - 1)));
            }

            db.OutboxMessages.Update(message);
            await db.SaveChangesAsync(ct);
            LogFailure(message);
        }

        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return true;
    }

    private void LogFailure(OutboxMessageEntity message) => logger.LogWarning(
        "Outbox message {OutboxMessageId} failed at attempt {OutboxAttempt}. Dead-lettered: {OutboxDeadLettered}. Failure: {OutboxFailure}",
        message.Id, message.Attempts, message.DeadLetteredAt is not null, message.LastFailure);
}

internal sealed class OutboxBackgroundService(
    IServiceScopeFactory scopes,
    ILogger<OutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (scope.ServiceProvider.GetService<NexusDbContext>() is null)
                    {
                        return;
                    }

                    var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                    for (var processed = 0; processed < 50; processed++)
                    {
                        if (!await processor.ProcessNextAsync(DateTimeOffset.UtcNow, stoppingToken))
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Outbox polling failed with {OutboxFailureType}.", ex.GetType().Name);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown leaves any unfinished transaction available for the next worker.
        }
    }
}
