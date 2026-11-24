using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Notifications;

namespace Nexus.IntegrationTests;

public sealed class OutboxM4Tests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task DispatchSkipsLockedRowsAndRollsBackPartialHandlersBeforeRetrying()
    {
        const string failingKind = "test.outbox.failure";
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var first = new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(), Kind = failingKind, ResourceId = Guid.NewGuid(), CreatedAt = now,
            AvailableAt = now.AddSeconds(-1)
        };
        var second = new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(), Kind = "test.outbox.unknown", ResourceId = Guid.NewGuid(), CreatedAt = now,
            AvailableAt = now
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NexusDbContext>(options => options.UseNpgsql(postgres.RuntimeConnectionString));
        services.AddScoped<OutboxProcessor>();
        services.AddKeyedScoped<IOutboxHandler, FailingOutboxHandler>(failingKind);
        await using var provider = services.BuildServiceProvider();
        await using var lockedScope = provider.CreateAsyncScope();
        var lockingDb = lockedScope.ServiceProvider.GetRequiredService<NexusDbContext>();
        lockingDb.OutboxMessages.AddRange(first, second);
        await lockingDb.SaveChangesAsync();

        await using var dispatchScope = provider.CreateAsyncScope();
        var processor = dispatchScope.ServiceProvider.GetRequiredService<OutboxProcessor>();
        var db = dispatchScope.ServiceProvider.GetRequiredService<NexusDbContext>();

        await using (var tx = await lockingDb.Database.BeginTransactionAsync())
        {
            await lockingDb.OutboxMessages.FromSqlInterpolated($"""
                SELECT * FROM outbox_messages WHERE id = {first.Id} FOR UPDATE
                """).ToListAsync();
            Assert.True(await processor.ProcessNextAsync(now, CancellationToken.None));
            Assert.NotNull(await db.OutboxMessages.Where(message => message.Id == second.Id)
                .Select(message => message.DeadLetteredAt).SingleAsync());
            Assert.Equal(0, await db.OutboxMessages.Where(message => message.Id == first.Id)
                .Select(message => message.Attempts).SingleAsync());
            await tx.CommitAsync();
        }

        Assert.True(await processor.ProcessNextAsync(now, CancellationToken.None));
        var retry = await db.OutboxMessages.AsNoTracking().SingleAsync(message => message.Id == first.Id);
        Assert.Equal(1, retry.Attempts);
        Assert.Equal(now.AddSeconds(5), retry.AvailableAt);
        Assert.Equal(nameof(InvalidOperationException), retry.LastFailure);
        Assert.Null(retry.CompletedAt);
        Assert.False(await db.OutboxMessages.AnyAsync(message => message.Id == first.ResourceId));
        Assert.False(await processor.ProcessNextAsync(now, CancellationToken.None));

        while (retry.Attempts < OutboxProcessor.MaxAttempts)
        {
            Assert.True(await processor.ProcessNextAsync(retry.AvailableAt, CancellationToken.None));
            retry = await db.OutboxMessages.AsNoTracking().SingleAsync(message => message.Id == first.Id);
        }

        Assert.NotNull(retry.DeadLetteredAt);
        Assert.False(await db.OutboxMessages.AnyAsync(message => message.Id == first.ResourceId));
        Assert.False(await processor.ProcessNextAsync(now.AddHours(1), CancellationToken.None));
    }

    private sealed class FailingOutboxHandler(NexusDbContext db) : IOutboxHandler
    {
        public async Task HandleAsync(OutboxMessageEntity message, CancellationToken ct)
        {
            db.OutboxMessages.Add(new OutboxMessageEntity
            {
                Id = message.ResourceId, Kind = "test.outbox.nested", ResourceId = Guid.NewGuid(),
                CreatedAt = message.CreatedAt, AvailableAt = message.CreatedAt
            });
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Test rollback after saving partial work.");
        }
    }
}
