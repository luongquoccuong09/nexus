using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Calendar;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Meetings;

internal static class MeetingEndpoints
{
    internal static IEndpointRouteBuilder MapMeetings(this IEndpointRouteBuilder app, string scope)
    {
        var endpoints = app.MapGroup(scope);
        endpoints.MapPost("/calendar/events/{eventId:guid}/meeting/join", JoinScheduledAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        endpoints.MapPost("/conversations/{conversationId:guid}/meetings", StartAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        endpoints.MapGet("/conversations/{conversationId:guid}/meetings/current", CurrentAsync);
        endpoints.MapPost("/meetings/{meetingId:guid}/join", JoinAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        endpoints.MapPost("/meetings/{meetingId:guid}/leave", LeaveAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        endpoints.MapPost("/meetings/{meetingId:guid}/end", EndAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        endpoints.MapDelete("/meetings/{meetingId:guid}/participants/{grantId:guid}", KickAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        return app;
    }

    private static async Task<IResult> StartAsync(Guid conversationId, [FromServices] NexusDbContext db,
        MeetingsAccess access, IMeetingProvider provider, CancellationToken ct)
    {
        var actor = db.OrganizationActor!;
        if (await access.FindConversationAsync(conversationId, actor, true, ct) is null) return Results.NotFound();
        if (!provider.Enabled) return Unavailable();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var organizationId = db.ConversationOrganizationId;
        var locked = await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM conversations WHERE id = {conversationId} AND organization_id IS NOT DISTINCT FROM {organizationId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        if (locked is null || await access.FindActorAsync(actor.Id, organizationId, ct) is not { } currentActor ||
            await access.FindConversationAsync(conversationId, currentActor, true, ct) is not { } conversation)
            return Results.NotFound();

        var room = await FindCurrentAsync(db, conversationId, ct);
        if (room is not null)
            return await access.CanJoinAsync(room, currentActor, ct) ? Results.Ok(Summary(room)) : Results.NotFound();

        room = new MeetingEntity
        {
            Id = Guid.CreateVersion7(), OrganizationId = organizationId, ConversationId = conversationId,
            HostId = currentActor.Id, Title = conversation.Conversation.Title ?? conversation.Community?.Name ?? "Meeting",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Meetings.Add(room);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(Summary(room));
    }

    private static async Task<IResult> CurrentAsync(Guid conversationId, HttpContext http, [FromServices] NexusDbContext db,
        MeetingsAccess access, IMeetingProvider provider, CancellationToken ct)
    {
        var actor = db.OrganizationActor!;
        if (await access.FindConversationAsync(conversationId, actor, false, ct) is null) return Results.NotFound();
        if (!provider.Enabled)
        {
            http.Response.Headers["Nexus-Meetings-Available"] = "false";
            return Results.NoContent();
        }
        var room = await FindCurrentAsync(db, conversationId, ct);
        if (room is null) return Results.NoContent();
        if (await access.CanJoinAsync(room, actor, ct)) return Results.Ok(Summary(room));
        http.Response.Headers["Nexus-Meetings-Available"] = "false";
        return Results.NoContent();
    }

    private static async Task<IResult> JoinScheduledAsync(Guid eventId, DateTimeOffset? occurrenceStartUtc, HttpContext http, [FromServices] NexusDbContext db,
        MeetingsAccess access, IMeetingProvider provider, CancellationToken ct)
    {
        var actor = db.OrganizationActor!;
        var orgId = db.ConversationOrganizationId;
        var allowed = CalendarAccess.VisibleTo(db, actor.Id).Where(item =>
            item.Id == eventId && item.OrganizationId == orgId && item.IsOnlineMeeting && item.CanceledAt == null);
        if (!await allowed.AnyAsync(ct)) return Results.NotFound();
        if (!provider.Enabled) return Unavailable();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var item = await db.CalendarEvents.FromSqlInterpolated($"""
            SELECT * FROM calendar_events WHERE id = {eventId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        if (item is null || !await allowed.AnyAsync(ct)) return Results.NotFound();

        var originalStart = occurrenceStartUtc?.ToUniversalTime();
        if (CalendarRecurrences.IsRecurring(item) != originalStart.HasValue) return Results.NotFound();
        var title = item.Title;
        if (originalStart is { } original)
        {
            if (await CalendarRecurrences.FindAsync(db, item, original, ct) is not { CanceledAt: null } occurrence)
                return Results.NotFound();
            title = occurrence.Title;
        }

        var room = await db.Meetings.AsNoTracking().SingleOrDefaultAsync(entry =>
            entry.CalendarEventId == eventId && entry.CalendarOccurrenceUtc == originalStart, ct);
        if (room is not null)
        {
            room = await LockAsync(db, room.Id, ct);
            if (room is null) return Results.NotFound();
        }
        else
        {
            room = new MeetingEntity
            {
                Id = Guid.CreateVersion7(), OrganizationId = item.OrganizationId, CalendarEventId = item.Id,
                CalendarOccurrenceUtc = originalStart, HostId = item.OrganizerId, Title = title, CreatedAt = DateTimeOffset.UtcNow
            };
            db.Meetings.Add(room);
        }

        var admission = await StageAdmissionAsync(db, http, access, room, ct);
        if (admission is null) return Results.NotFound();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Credentials(http, provider, access, room, admission, title);
    }

    private static async Task<IResult> JoinAsync(Guid meetingId, HttpContext http, [FromServices] NexusDbContext db,
        MeetingsAccess access, IMeetingProvider provider, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var room = await LockAsync(db, meetingId, ct);
        if (room is null) return Results.NotFound();
        var admission = await StageAdmissionAsync(db, http, access, room, ct);
        if (admission is null) return Results.NotFound();
        if (!provider.Enabled) return Unavailable();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Credentials(http, provider, access, room, admission, room.Title);
    }

    private static async Task<IResult> LeaveAsync(Guid meetingId, GrantRequest request, HttpContext http,
        [FromServices] NexusDbContext db, MeetingRevocations revocations, CancellationToken ct)
    {
        var actor = db.OrganizationActor!;
        var room = await FindScopedAsync(db, meetingId, ct);
        if (room is null || http.User.GetSessionId() is not { } sessionId)
            return Results.NotFound();
        var grant = db.MeetingGrants.Where(item => item.MeetingId == meetingId && item.Id == request.GrantId &&
            item.UserId == actor.Id && item.SessionId == sessionId);
        if (!await grant.AnyAsync(ct)) return Results.NotFound();
        var now = DateTimeOffset.UtcNow;
        if (await grant.Where(item => item.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.RevokedAt, now), ct) != 0)
            await revocations.RemoveProviderGrantAsync(meetingId, request.GrantId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> EndAsync(Guid meetingId, [FromServices] NexusDbContext db,
        MeetingsAccess access, MeetingRevocations revocations, CancellationToken ct)
    {
        var requestActor = db.OrganizationActor!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var room = await LockAsync(db, meetingId, ct);
        if (room is null || await access.FindActorAsync(requestActor.Id, room.OrganizationId, ct) is not { } actor ||
            !await access.CanJoinAsync(room, actor, ct))
            return Results.NotFound();
        if (!access.CanManage(room, actor)) return Results.Forbid();

        await revocations.MarkEndedAsync(room.Id, DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);
        await revocations.CloseProviderRoomAsync(room.Id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> KickAsync(Guid meetingId, Guid grantId, [FromServices] NexusDbContext db,
        MeetingsAccess access, MeetingRevocations revocations, CancellationToken ct)
    {
        var requestActor = db.OrganizationActor!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var room = await LockAsync(db, meetingId, ct);
        if (room is null || await access.FindActorAsync(requestActor.Id, room.OrganizationId, ct) is not { } actor ||
            !await access.CanJoinAsync(room, actor, ct))
            return Results.NotFound();
        if (!access.CanManage(room, actor)) return Results.Forbid();

        var target = await db.MeetingGrants.AsNoTracking().Where(grant => grant.Id == grantId && grant.MeetingId == meetingId)
            .Select(grant => (Guid?)grant.UserId).SingleOrDefaultAsync(ct);
        if (target is null) return Results.NotFound();
        if (target == actor.Id)
            return Results.BadRequest(new { code = "meeting_cannot_remove_host", message = "The host can leave or end this meeting." });

        var now = DateTimeOffset.UtcNow;
        if (!await db.MeetingBans.AnyAsync(ban => ban.MeetingId == meetingId && ban.UserId == target, ct))
            db.MeetingBans.Add(new MeetingBanEntity { MeetingId = meetingId, UserId = target.Value, CreatedAt = now });
        var active = db.MeetingGrants.Where(grant => grant.MeetingId == meetingId && grant.UserId == target && grant.RevokedAt == null);
        var grantIds = await active.Select(grant => grant.Id).ToListAsync(ct);
        await active.ExecuteUpdateAsync(update => update.SetProperty(grant => grant.RevokedAt, now), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        foreach (var id in grantIds)
            await revocations.RemoveProviderGrantAsync(meetingId, id, ct);
        return Results.NoContent();
    }

    private static async Task<Admission?> StageAdmissionAsync(NexusDbContext db, HttpContext http,
        MeetingsAccess access, MeetingEntity room, CancellationToken ct)
    {
        var actorId = db.OrganizationActor!.Id;
        var sessionId = http.User.GetSessionId();
        if (sessionId is null || !await access.IsActiveSessionAsync(actorId, sessionId, ct) ||
            await access.FindActorAsync(actorId, room.OrganizationId, ct) is not { } actor ||
            !await access.CanJoinAsync(room, actor, ct, sessionId))
            return null;

        var grant = await db.MeetingGrants.AsNoTracking().SingleOrDefaultAsync(item =>
            item.MeetingId == room.Id && item.UserId == actorId && item.SessionId == sessionId && item.RevokedAt == null, ct);
        if (grant is null)
        {
            var now = DateTimeOffset.UtcNow;
            grant = new MeetingGrantEntity
            {
                Id = Guid.CreateVersion7(), MeetingId = room.Id, UserId = actorId, SessionId = sessionId,
                CreatedAt = now, CheckedAt = now
            };
            db.MeetingGrants.Add(grant);
        }
        return new Admission(actor, grant);
    }

    private static IResult Credentials(HttpContext http, IMeetingProvider provider, MeetingsAccess access, MeetingEntity room,
        Admission admission, string title)
    {
        var credentials = provider.Issue(room.RoomName, admission.Grant.Id, admission.Actor.DisplayName);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            MeetingId = room.Id, room.OrganizationId, GrantId = admission.Grant.Id,
            credentials.SignalPath, credentials.Token, CanManage = access.CanManage(room, admission.Actor), Title = title
        });
    }

    private static object Summary(MeetingEntity room) => new { MeetingId = room.Id, room.OrganizationId, room.Title };

    private static IResult Unavailable() => Results.Json(
        new { code = "meetings_unavailable", message = "Meetings are unavailable on this server." },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static Task<MeetingEntity?> FindCurrentAsync(NexusDbContext db, Guid conversationId, CancellationToken ct) =>
        db.Meetings.AsNoTracking().SingleOrDefaultAsync(room => room.ConversationId == conversationId &&
            room.OrganizationId == db.ConversationOrganizationId && room.EndedAt == null, ct);

    private static Task<MeetingEntity?> FindScopedAsync(NexusDbContext db, Guid meetingId, CancellationToken ct) =>
        db.Meetings.AsNoTracking().SingleOrDefaultAsync(room => room.Id == meetingId && room.OrganizationId == db.ConversationOrganizationId, ct);

    private static Task<MeetingEntity?> LockAsync(NexusDbContext db, Guid meetingId, CancellationToken ct) =>
        db.Meetings.FromSqlInterpolated($"SELECT * FROM meetings WHERE id = {meetingId} FOR UPDATE").AsNoTracking()
            .SingleOrDefaultAsync(room => room.OrganizationId == db.ConversationOrganizationId, ct);

    private sealed record Admission(OrganizationMembership Actor, MeetingGrantEntity Grant);
    private sealed record GrantRequest(Guid GrantId);
}
