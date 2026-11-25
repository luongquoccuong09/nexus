using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Meetings;
using Nexus.Web.Modules.Messaging;

namespace Nexus.Web.Modules.Calls;

internal sealed class DirectCallLifecycle(
    NexusDbContext db, MeetingsAccess access, MeetingRevocations rooms, IHubContext<MessagingHub> hubs)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    internal Task PublishAsync(DirectCallEntity call, CancellationToken ct)
    {
        var orgId = call.OrganizationId ?? Guid.Empty;
        return hubs.Clients.Groups(
            [MessagingHub.UserGroupName(orgId, call.CallerId), MessagingHub.UserGroupName(orgId, call.CalleeId)])
            .SendAsync("DirectCallChanged", new
            {
                call.Id, call.OrganizationId, call.ConversationId, State = WireState(call.State), call.Version
            }, ct);
    }

    internal static string WireState(DirectCallState state) => state.ToString().ToLowerInvariant();

    internal static void Transition(DirectCallEntity call, DirectCallState state, DateTimeOffset now)
    {
        call.State = state;
        call.Version++;
        call.CheckedAt = now;
        if (state == DirectCallState.Accepted) call.AcceptedAt = now;
        else call.EndedAt = now;
    }

    internal static IQueryable<DirectCallEntity> Lock(NexusDbContext db, Guid id) =>
        db.DirectCalls.FromSqlInterpolated($"SELECT * FROM direct_calls WHERE id = {id} FOR UPDATE");

    internal async Task<Guid?> StageRoomEndAsync(DirectCallEntity call, DateTimeOffset now, CancellationToken ct)
    {
        var roomId = await db.Meetings.Where(room => room.DirectCallId == call.Id && room.EndedAt == null)
            .Select(room => (Guid?)room.Id).SingleOrDefaultAsync(ct);
        if (roomId.HasValue) await rooms.MarkEndedAsync(roomId.Value, now, ct);
        return roomId;
    }

    internal async Task AfterTransitionAsync(DirectCallEntity call, Guid? closedRoomId, CancellationToken ct)
    {
        if (closedRoomId is { } roomId)
        {
            await Task.WhenAll(PublishAsync(call, CancellationToken.None), rooms.CloseProviderRoomAsync(roomId, ct));
            return;
        }
        await PublishAsync(call, CancellationToken.None);
    }

    internal async Task RecheckUserAsync(Guid userId, CancellationToken ct)
    {
        var calls = await DirectCallAccess.Running(db.DirectCalls.AsNoTracking())
            .Where(call => call.CallerId == userId || call.CalleeId == userId).Select(call => call.Id).ToListAsync(ct);
        foreach (var id in calls) await RecheckAsync(id, ct);
    }

    internal async Task RecheckSessionAsync(string sessionId, CancellationToken ct)
    {
        var calls = await DirectCallAccess.Running(db.DirectCalls.AsNoTracking())
            .Where(call => call.CallerSessionId == sessionId || call.CalleeSessionId == sessionId).Select(call => call.Id).ToListAsync(ct);
        foreach (var id in calls) await RecheckAsync(id, ct);
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db.DirectCalls.Where(call => call.State == DirectCallState.Ringing && call.ExpiresAt <= now)
            .OrderBy(call => call.ExpiresAt).ThenBy(call => call.Id).Select(call => call.Id)
            .Take(PaginationGuard.MaxPageSize).ToListAsync(ct);
        foreach (var id in expired) await RecheckAsync(id, ct);

        var checkedBefore = now.Subtract(CheckInterval);
        var active = await DirectCallAccess.Running(db.DirectCalls.AsNoTracking())
            .Where(call => call.CheckedAt <= checkedBefore)
            .OrderBy(call => call.CheckedAt).ThenBy(call => call.Id).Select(call => call.Id)
            .Take(PaginationGuard.MaxPageSize).ToListAsync(ct);
        foreach (var id in active) await RecheckAsync(id, ct);
    }

    private async Task RecheckAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var call = await DirectCallAccess.Running(Lock(db, id)).AsNoTracking().SingleOrDefaultAsync(ct);
        if (call is null) return;
        db.Attach(call);
        var now = DateTimeOffset.UtcNow;
        var eligible = await DirectCallAccess.Eligible(db).AnyAsync(item => item.Id == call.Id, ct);
        var ownerPresent = call.CallerSessionId is { } caller && await access.IsActiveSessionAsync(call.CallerId, caller, ct);
        var peerPresent = call.State == DirectCallState.Ringing ||
            call.CalleeSessionId is { } callee && await access.IsActiveSessionAsync(call.CalleeId, callee, ct);
        var next = !eligible || !ownerPresent || !peerPresent
            ? (call.State == DirectCallState.Ringing ? DirectCallState.Canceled : DirectCallState.Ended)
            : call.State == DirectCallState.Ringing && call.ExpiresAt <= now
                ? DirectCallState.Missed
                : call.State;
        if (next == call.State)
        {
            call.CheckedAt = now;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            db.Entry(call).State = EntityState.Detached;
            return;
        }

        Transition(call, next, now);
        var room = next == DirectCallState.Ended ? await StageRoomEndAsync(call, now, ct) : null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.Entry(call).State = EntityState.Detached;
        await AfterTransitionAsync(call, room, ct);
    }
}

internal sealed class DirectCallReconciliationService(IServiceScopeFactory scopes, ILogger<DirectCallReconciliationService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await tick.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<NexusDbContext>() is null) return;
                await scope.ServiceProvider.GetRequiredService<DirectCallLifecycle>().ReconcileAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Direct call reconciliation failed ({ErrorType}).", error.GetType().Name);
            }
        }
    }
}
