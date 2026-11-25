using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;

namespace Nexus.Web.Modules.Meetings;

internal sealed class MeetingRevocations(
    NexusDbContext db, MeetingsAccess access, IMeetingProvider provider,
    ILogger<MeetingRevocations> logger)
{
    internal async Task RecheckUserAsync(Guid userId, CancellationToken ct)
    {
        var pending = await GetActiveGrantsAsync(db.MeetingGrants.Where(grant => grant.UserId == userId), db.Meetings, ct);
        foreach (var candidate in pending)
            await RecheckAsync(candidate, ct);
    }

    internal async Task RecheckSessionAsync(string sessionId, CancellationToken ct)
    {
        var pending = await GetActiveGrantsAsync(db.MeetingGrants.Where(grant => grant.SessionId == sessionId), db.Meetings, ct);
        foreach (var candidate in pending)
            await RecheckAsync(candidate, ct);
    }

    internal async Task RecheckConversationAsync(Guid conversationId, CancellationToken ct)
    {
        var pending = await GetActiveGrantsAsync(
            db.MeetingGrants, db.Meetings.Where(room => room.ConversationId == conversationId), ct);
        foreach (var candidate in pending)
            await RecheckAsync(candidate, ct);
    }

    internal async Task RecheckCalendarAsync(Guid eventId, CancellationToken ct)
    {
        var pending = await GetActiveGrantsAsync(
            db.MeetingGrants, db.Meetings.Where(room => room.CalendarEventId == eventId), ct);
        foreach (var candidate in pending)
            await RecheckAsync(candidate, ct);
    }

    internal async Task<IReadOnlyList<Guid>> EndCalendarAsync(Guid eventId, DateTimeOffset now, CancellationToken ct)
    {
        var activeRooms = db.Meetings.Where(room => room.CalendarEventId == eventId && room.EndedAt == null);
        var immediate = await activeRooms.OrderBy(room => room.CreatedAt).ThenBy(room => room.Id)
            .Select(room => room.Id).Take(PaginationGuard.MaxPageSize).ToListAsync(ct);
        if (immediate.Count == 0) return immediate;

        await db.MeetingGrants.Where(grant => grant.RevokedAt == null && activeRooms.Any(room => room.Id == grant.MeetingId))
            .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.RevokedAt, now), ct);
        await activeRooms.ExecuteUpdateAsync(update => update.SetProperty(room => room.EndedAt, now), ct);
        return immediate;
    }

    internal async Task<Guid?> EndCalendarOccurrenceAsync(Guid eventId, DateTimeOffset occurrenceUtc, DateTimeOffset now, CancellationToken ct)
    {
        var meetingId = await db.Meetings.Where(room => room.CalendarEventId == eventId && room.CalendarOccurrenceUtc == occurrenceUtc && room.EndedAt == null)
            .Select(room => (Guid?)room.Id).SingleOrDefaultAsync(ct);
        if (meetingId is not null) await MarkEndedAsync(meetingId.Value, now, ct);
        return meetingId;
    }

    internal async Task MarkEndedAsync(Guid meetingId, DateTimeOffset now, CancellationToken ct)
    {
        await db.Meetings.Where(room => room.Id == meetingId && room.EndedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(room => room.EndedAt, now), ct);
        await db.MeetingGrants.Where(grant => grant.MeetingId == meetingId && grant.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.RevokedAt, now), ct);
    }

    internal async Task CloseProviderRoomAsync(Guid meetingId, CancellationToken ct)
    {
        var roomName = MeetingEntity.Room(meetingId);
        if (!provider.Enabled) return;
        try
        {
            await provider.EndRoomAsync(roomName, ct);
            var now = DateTimeOffset.UtcNow;
            await db.MeetingGrants.Where(grant => grant.MeetingId == meetingId &&
                grant.RevokedAt != null && grant.ProviderRemovedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.ProviderRemovedAt, now), ct);
            await db.Meetings.Where(room => room.Id == meetingId && room.EndedAt != null && room.ProviderEndedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(room => room.ProviderEndedAt, now), ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Media room {MeetingId} closure remains pending ({ErrorType}).", meetingId, error.GetType().Name);
        }
    }

    internal async Task RemoveProviderGrantAsync(Guid meetingId, Guid grantId, CancellationToken ct)
    {
        if (!provider.Enabled) return;
        try
        {
            await provider.RemoveParticipantAsync(MeetingEntity.Room(meetingId), grantId, ct);
            await db.MeetingGrants.Where(grant => grant.Id == grantId && grant.RevokedAt != null && grant.ProviderRemovedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.ProviderRemovedAt, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Media participant cleanup remains pending for room {MeetingId} ({ErrorType}).",
                meetingId, error.GetType().Name);
        }
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-5);
        var active = await GetActiveGrantsAsync(db.MeetingGrants.Where(grant => grant.CheckedAt <= cutoff),
            db.Meetings, ct, PaginationGuard.MaxPageSize);
        foreach (var candidate in active)
            await RecheckAsync(candidate, ct);
        if (!provider.Enabled) return;

        var rooms = await db.Meetings.Where(room => room.EndedAt != null && room.ProviderEndedAt == null)
            .OrderBy(room => room.EndedAt).ThenBy(room => room.Id).Select(room => room.Id)
            .Take(PaginationGuard.MaxPageSize).ToListAsync(ct);
        foreach (var room in rooms)
            await CloseProviderRoomAsync(room, ct);

        var pending = await (
            from grant in db.MeetingGrants.AsNoTracking()
            join room in db.Meetings.AsNoTracking() on grant.MeetingId equals room.Id
            where grant.RevokedAt != null && grant.ProviderRemovedAt == null && room.EndedAt == null
            orderby grant.RevokedAt, grant.Id
            select new { grant.MeetingId, grant.Id }).Take(PaginationGuard.MaxPageSize).ToListAsync(ct);
        foreach (var grant in pending)
            await RemoveProviderGrantAsync(grant.MeetingId, grant.Id, ct);
    }

    private static Task<List<GrantCheck>> GetActiveGrantsAsync(
        IQueryable<MeetingGrantEntity> grants, IQueryable<MeetingEntity> rooms, CancellationToken ct, int? limit = null)
    {
        var query =
            from grant in grants.AsNoTracking()
            join room in rooms.AsNoTracking() on grant.MeetingId equals room.Id
            where grant.RevokedAt == null && room.EndedAt == null
            orderby grant.CheckedAt, grant.Id
            select new GrantCheck(grant, room);
        return (limit is { } size ? query.Take(size) : query).ToListAsync(ct);
    }

    private async Task RecheckAsync(GrantCheck candidate, CancellationToken ct)
    {
        var active = db.MeetingGrants.Where(grant => grant.Id == candidate.Grant.Id && grant.RevokedAt == null);
        var now = DateTimeOffset.UtcNow;
        if (await access.CanUseGrantAsync(candidate.Grant, candidate.Room, ct))
        {
            await active.ExecuteUpdateAsync(update => update.SetProperty(grant => grant.CheckedAt, now), ct);
        }
        else if (await active.ExecuteUpdateAsync(update => update.SetProperty(grant => grant.RevokedAt, now), ct) != 0)
        {
            await RemoveProviderGrantAsync(candidate.Room.Id, candidate.Grant.Id, ct);
        }
    }

    private sealed record GrantCheck(MeetingGrantEntity Grant, MeetingEntity Room);
}

internal sealed class MeetingReconciliationService(IServiceScopeFactory scopes, ILogger<MeetingReconciliationService> logger)
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
                await scope.ServiceProvider.GetRequiredService<MeetingRevocations>().ReconcileAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Meeting access reconciliation failed ({ErrorType}).", error.GetType().Name);
            }
        }
    }
}
