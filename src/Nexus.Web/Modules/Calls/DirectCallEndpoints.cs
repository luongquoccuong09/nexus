using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Meetings;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Calls;

internal static class DirectCallEndpoints
{
    private static readonly TimeSpan RingDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(1);
    private const int StartsPerWindow = 5;

    internal static IEndpointRouteBuilder MapDirectCalls(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/calls/active", ListActiveAsync);
        app.MapGet("/api/calls/history", DirectCallHistory.ListAsync);
        foreach (var scope in new[] { OrganizationScope.PersonalApi, OrganizationScope.Api })
        {
            var routes = app.MapGroup(scope);
            routes.MapPost("/conversations/{conversationId:guid}/calls", StartAsync)
                .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
            routes.MapGet("/calls/{callId:guid}", GetAsync);
            routes.MapPost("/calls/{callId:guid}/{action:regex(^(accept|decline|cancel|end)$)}", TransitionAsync)
                .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        }
        return app;
    }

    private static async Task<IResult> StartAsync(
        Guid conversationId, StartRequest request, HttpContext http, [FromServices] NexusDbContext db,
        MeetingsAccess access, DirectCallLifecycle lifecycle, IMeetingProvider provider, CancellationToken ct)
    {
        var kind = request.Kind switch
        {
            nameof(DirectCallKind.Voice) => DirectCallKind.Voice,
            nameof(DirectCallKind.Video) => DirectCallKind.Video,
            _ => (DirectCallKind?)null
        };
        if (kind is null || request.ClientCallId == Guid.Empty)
            return Results.BadRequest(new { code = "invalid_direct_call", message = "Choose Voice or Video and provide a request ID." });

        var actor = db.OrganizationActor!;
        http.Response.Headers.CacheControl = "no-store";
        var orgId = db.ConversationOrganizationId;
        if (http.User.GetSessionId() is not { } sessionId ||
            !await access.IsActiveSessionAsync(actor.Id, sessionId, ct) ||
            await FindPeerAsync(db, access, actor, conversationId, ct) is null)
            return Results.NotFound();
        if (!provider.Enabled) return Unavailable();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lockKey = $"nexus:direct-call-start:{actor.Id:N}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        var conversation = await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM conversations WHERE id = {conversationId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == orgId, ct);
        if (conversation is null || await access.FindActorAsync(actor.Id, orgId, ct) is not { } currentActor ||
            !await access.IsActiveSessionAsync(actor.Id, sessionId, ct) ||
            await FindPeerAsync(db, access, currentActor, conversationId, ct) is not { } targetUserId)
            return Results.NotFound();

        var replay = await db.DirectCalls.AsNoTracking().SingleOrDefaultAsync(call =>
            call.CallerId == actor.Id && call.ClientCallId == request.ClientCallId, ct);
        if (replay is not null)
        {
            if (replay.OrganizationId != orgId || replay.ConversationId != conversationId || replay.Kind != kind)
                return Conflict("direct_call_request_conflict", "This request ID already started a different call.");
            return await FindAsync(db, replay.Id, actor.Id, orgId, ct) is { } previous
                ? Results.Ok(previous.ToDto(actor.Id, sessionId))
                : Results.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var windowStart = now.Subtract(StartWindow);
        if (await db.DirectCalls.CountAsync(call => call.CallerId == actor.Id && call.CreatedAt > windowStart, ct) >= StartsPerWindow)
        {
            http.Response.Headers.RetryAfter = "60";
            return Results.Json(new { code = "direct_call_rate_limited", message = "Wait before starting another call." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var activeId = await DirectCallAccess.Running(db.DirectCalls.AsNoTracking())
            .Where(call => call.ConversationId == conversationId)
            .Select(call => (Guid?)call.Id).SingleOrDefaultAsync(ct);
        DirectCallEntity? expired = null;
        if (activeId is { } activeCallId &&
            await DirectCallAccess.Running(DirectCallLifecycle.Lock(db, activeCallId)).SingleOrDefaultAsync(ct) is { } active)
        {
            if (active.State != DirectCallState.Ringing || active.ExpiresAt > now)
                return Conflict("direct_call_in_progress", "This conversation already has an active call.");
            DirectCallLifecycle.Transition(active, DirectCallState.Missed, now);
            await db.SaveChangesAsync(ct);
            expired = active;
        }

        var call = new DirectCallEntity
        {
            Id = Guid.CreateVersion7(), ClientCallId = request.ClientCallId, OrganizationId = orgId,
            ConversationId = conversationId, CallerId = actor.Id, CalleeId = targetUserId, CallerSessionId = sessionId,
            Kind = kind.Value, State = DirectCallState.Ringing, CreatedAt = now, CheckedAt = now, ExpiresAt = now.Add(RingDuration)
        };
        db.DirectCalls.Add(call);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (expired is not null) await lifecycle.PublishAsync(expired, CancellationToken.None);
        await lifecycle.PublishAsync(call, CancellationToken.None);
        var row = await FindAsync(db, call.Id, actor.Id, orgId, ct);
        return row is null ? Results.NotFound() :
            Results.Created($"{OrganizationScope.ApiPath(orgId)}/calls/{call.Id}", row.ToDto(actor.Id, sessionId));
    }

    private static async Task<IResult> ListActiveAsync(
        HttpContext http, string? cursor, int? limit, [FromServices] NexusDbContext db,
        MeetingsAccess access, IMeetingProvider provider, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.User.TryGetUserId(out var userId) || http.User.GetSessionId() is not { } sessionId ||
            !await access.IsActiveSessionAsync(userId, sessionId, ct))
            return Results.Unauthorized();
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var error))
            return Results.BadRequest(new { code = "invalid_page_size", message = error });
        if (!PaginationGuard.TryDecodeCursor<CallCursor>(cursor, out var after) ||
            after is not null && (after.Id == Guid.Empty || after.CreatedAt == default))
            return PaginationGuard.InvalidCursor();
        if (!provider.Enabled) return Results.Ok(new { items = Array.Empty<CallDto>(), nextCursor = (string?)null, available = false });

        var now = DateTimeOffset.UtcNow;
        var calls = DirectCallAccess.Running(DirectCallAccess.Eligible(db))
            .Where(call => (call.CallerId == userId || call.CalleeId == userId) &&
                (call.State != DirectCallState.Ringing || call.ExpiresAt > now));
        if (after is not null)
        {
            var cutoff = after.CreatedAt.ToUniversalTime();
            calls = calls.Where(call => call.CreatedAt < cutoff || call.CreatedAt == cutoff && call.Id.CompareTo(after.Id) < 0);
        }
        var rows = await Project(db, calls.OrderByDescending(call => call.CreatedAt).ThenByDescending(call => call.Id)
            .Take(pageSize + 1)).ToListAsync(ct);
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);
        return Results.Ok(new
        {
            items = rows.Select(row => row.ToDto(userId, sessionId)),
            nextCursor = hasMore ? PaginationGuard.EncodeCursor(new CallCursor(rows[^1].Call.CreatedAt, rows[^1].Call.Id)) : null,
            available = true
        });
    }

    private static async Task<IResult> GetAsync(Guid callId, HttpContext http, [FromServices] NexusDbContext db,
        MeetingsAccess access, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actor = db.OrganizationActor!;
        if (http.User.GetSessionId() is not { } sessionId || !await access.IsActiveSessionAsync(actor.Id, sessionId, ct))
            return Results.NotFound();
        var row = await FindAsync(db, callId, actor.Id, db.ConversationOrganizationId, ct);
        return row is null ? Results.NotFound() : Results.Ok(row.ToDto(actor.Id, sessionId));
    }

    private static async Task<IResult> TransitionAsync(Guid callId, string action, HttpContext http, [FromServices] NexusDbContext db,
        MeetingsAccess access, DirectCallLifecycle lifecycle, IMeetingProvider provider, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var actorId = db.OrganizationActor!.Id;
        var orgId = db.ConversationOrganizationId;
        if (http.User.GetSessionId() is not { } sessionId || !await access.IsActiveSessionAsync(actorId, sessionId, ct))
            return Results.NotFound();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var call = await DirectCallLifecycle.Lock(db, callId).AsNoTracking().SingleOrDefaultAsync(item =>
            item.OrganizationId == orgId && (item.CallerId == actorId || item.CalleeId == actorId), ct);
        if (call is null || !await DirectCallAccess.Eligible(db).AnyAsync(item => item.Id == callId, ct) ||
            await access.FindActorAsync(actorId, orgId, ct) is not { } currentActor ||
            await access.FindConversationAsync(call.ConversationId, currentActor, true, ct) is null)
            return Results.NotFound();
        db.Attach(call);

        var next = action.ToLowerInvariant() switch
        {
            "accept" when call.CalleeId == actorId => DirectCallState.Accepted,
            "decline" when call.CalleeId == actorId => DirectCallState.Declined,
            "cancel" when call.CallerId == actorId => DirectCallState.Canceled,
            "end" => DirectCallState.Ended,
            _ => (DirectCallState?)null
        };
        if (next is null) return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        if (call.State == DirectCallState.Ringing && call.ExpiresAt <= now)
        {
            DirectCallLifecycle.Transition(call, DirectCallState.Missed, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await lifecycle.PublishAsync(call, CancellationToken.None);
            return Conflict("direct_call_expired", "The call has already stopped ringing.");
        }
        if (call.State == next)
        {
            var existing = await FindAsync(db, callId, actorId, orgId, ct);
            return existing is null ? Results.NotFound() : Results.Ok(existing.ToDto(actorId, sessionId));
        }
        if (call.State != (next == DirectCallState.Ended ? DirectCallState.Accepted : DirectCallState.Ringing))
            return Conflict("direct_call_state_conflict", "Another device has already changed this call.");

        Guid? closedRoom = null;
        if (next == DirectCallState.Accepted)
        {
            if (!provider.Enabled) return Unavailable();
            if (call.CallerSessionId is not { } ownerSession || !await access.IsActiveSessionAsync(call.CallerId, ownerSession, ct))
            {
                DirectCallLifecycle.Transition(call, DirectCallState.Canceled, now);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                await lifecycle.PublishAsync(call, CancellationToken.None);
                return Conflict("direct_call_not_available", "The caller is no longer available.");
            }

            call.CalleeSessionId = sessionId;
            db.Meetings.Add(new MeetingEntity
            {
                Id = Guid.CreateVersion7(), OrganizationId = orgId, DirectCallId = call.Id, HostId = call.CallerId,
                CreatedAt = now, Title = call.Kind == DirectCallKind.Video ? "Video call" : "Voice call"
            });
        }
        else if (next == DirectCallState.Ended)
        {
            closedRoom = await lifecycle.StageRoomEndAsync(call, now, ct);
        }

        DirectCallLifecycle.Transition(call, next.Value, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await lifecycle.AfterTransitionAsync(call, closedRoom, ct);
        var row = await FindAsync(db, callId, actorId, orgId, ct);
        return row is null ? Results.NotFound() : Results.Ok(row.ToDto(actorId, sessionId));
    }

    private static async Task<Guid?> FindPeerAsync(NexusDbContext db, MeetingsAccess access, OrganizationMembership actor,
        Guid conversationId, CancellationToken ct)
    {
        var conversation = await access.FindConversationAsync(conversationId, actor, true, ct);
        if (conversation?.Conversation.Kind != ConversationKind.Dm) return null;
        var peers = await db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == conversationId && member.UserId != actor.Id && member.LeftAt == null)
            .Select(member => member.UserId).Take(2).ToListAsync(ct);
        return peers.Count == 1 && await access.FindActorAsync(peers[0], conversation.Conversation.OrganizationId, ct) is not null
            ? peers[0]
            : null;
    }

    private static Task<CallRow?> FindAsync(NexusDbContext db, Guid callId, Guid userId, Guid? orgId, CancellationToken ct) =>
        Project(db, DirectCallAccess.Eligible(db).Where(call => call.Id == callId && call.OrganizationId == orgId &&
            (call.CallerId == userId || call.CalleeId == userId))).SingleOrDefaultAsync(ct);

    private static IQueryable<CallRow> Project(NexusDbContext db, IQueryable<DirectCallEntity> calls) =>
        from call in calls
        join caller in db.Users.AsNoTracking() on call.CallerId equals caller.Id
        join callee in db.Users.AsNoTracking() on call.CalleeId equals callee.Id
        join meeting in db.Meetings.AsNoTracking() on call.Id equals meeting.DirectCallId into rooms
        from room in rooms.DefaultIfEmpty()
        select new CallRow(call, caller.DisplayName, callee.DisplayName, room == null ? null : room.Id);

    private static IResult Conflict(string code, string message) => Results.Conflict(new { code, message });
    private static IResult Unavailable() => Results.Json(new
    {
        code = "direct_calls_unavailable", message = "Direct calls are unavailable on this server."
    }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private sealed record StartRequest(string? Kind, Guid ClientCallId);
    private sealed record CallCursor(DateTimeOffset CreatedAt, Guid Id);
    private sealed record CallRow(DirectCallEntity Call, string CallerName, string CalleeName, Guid? MeetingId)
    {
        internal CallDto ToDto(Guid userId, string sessionId) => new(Call.Id, Call.OrganizationId, Call.ConversationId,
            Call.CallerId, CallerName, Call.CalleeId, CalleeName, Call.Kind.ToString(), DirectCallLifecycle.WireState(Call.State),
            MeetingId, Call.CreatedAt, Call.ExpiresAt, Call.AcceptedAt, Call.EndedAt, Call.Version,
            MeetingId.HasValue && Call.State == DirectCallState.Accepted && DirectCallAccess.MatchesSession(Call, userId, sessionId));
    }

    private sealed record CallDto(Guid Id, Guid? OrganizationId, Guid ConversationId, Guid CallerId, string CallerName,
        Guid CalleeId, string CalleeName, string Kind, string State, Guid? MeetingId, DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset? EndedAt, int Version, bool CanJoinMedia);
}
