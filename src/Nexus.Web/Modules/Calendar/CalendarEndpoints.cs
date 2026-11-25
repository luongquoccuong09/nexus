using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Idempotency;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Meetings;
using Nexus.Web.Modules.Org;
using Npgsql;

namespace Nexus.Web.Modules.Calendar;

internal static class CalendarEndpoints
{
    internal static IEndpointRouteBuilder MapCalendar(this IEndpointRouteBuilder app, string scope)
    {
        var group = app.MapGroup(scope + "/calendar/events");
        group.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        group.MapGet("", ListAsync);
        group.MapGet("/{eventId:guid}", GetAsync);
        group.MapGet("/{eventId:guid}/attendees", ListAttendeesAsync);
        group.MapPost("", CreateAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        group.MapPut("/{eventId:guid}", UpdateAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        group.MapPost("/{eventId:guid}/cancel", CancelAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        group.MapPut("/{eventId:guid}/rsvp", RespondAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, Guid? communityId, string? cursor, int? limit,
        [FromServices] NexusDbContext db, IAuthorizationService authz, CancellationToken ct)
    {
        if (!PaginationGuard.TryNormalize(limit, out var size, out var error))
            return Invalid("invalid_page_size", error!);
        if (fromUtc == default || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(62))
            return Invalid("invalid_calendar_range", "Choose a date range of up to 62 days.");
        if (!PaginationGuard.TryDecodeCursor<EventCursor>(cursor, out var after) ||
            (after is not null && (after.Id == Guid.Empty || after.StartUtc == default)))
            return PaginationGuard.InvalidCursor();

        var actor = db.OrganizationActor!;
        if (!authz.Can(actor.ToActorContext(), ResourceAction.CalendarRead,
                await CalendarAccess.ResolveScopeAsync(db, actor, communityId, ct)))
            return Results.NotFound();

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        var query = VisibleInScope(db).Where(item => item.StartUtc < to && item.EndUtc > from);
        if (communityId.HasValue) query = query.Where(item => item.CommunityId == communityId);
        if (after is not null)
        {
            var start = after.StartUtc.ToUniversalTime();
            query = query.Where(item => item.StartUtc > start || item.StartUtc == start && item.Id.CompareTo(after.Id) > 0);
        }

        var rows = await Project(db, query.OrderBy(item => item.StartUtc).ThenBy(item => item.Id).Take(size + 1))
            .ToListAsync(ct);
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        return Results.Ok(new
        {
            items = rows.Select(row => row.ToDto(actor.Id)),
            nextCursor = hasMore ? PaginationGuard.EncodeCursor(new EventCursor(rows[^1].Item.StartUtc, rows[^1].Item.Id)) : null
        });
    }

    private static async Task<IResult> GetAsync(
        Guid eventId, [FromServices] NexusDbContext db, IAuthorizationService authz, CancellationToken ct)
    {
        var row = await FindAsync(db, eventId, ct);
        return row is not null && await CanReadAsync(db, authz, row.Item, ct)
            ? Results.Ok(row.ToDto(db.OrganizationActor!.Id))
            : Results.NotFound();
    }

    private static async Task<IResult> ListAttendeesAsync(
        Guid eventId, string? cursor, int? limit, [FromServices] NexusDbContext db, IAuthorizationService authz, CancellationToken ct)
    {
        if (!PaginationGuard.TryNormalize(limit, out var size, out var error))
            return Invalid("invalid_page_size", error!);
        if (!PaginationGuard.TryDecodeCursor<AttendeeCursor>(cursor, out var after) ||
            after?.UserId == Guid.Empty)
            return PaginationGuard.InvalidCursor();

        var item = await VisibleInScope(db).SingleOrDefaultAsync(entry => entry.Id == eventId, ct);
        if (item is null || !await CanReadAsync(db, authz, item, ct)) return Results.NotFound();
        var eligible = CalendarAccess.EligibleInvitees(db, item.OrganizerId, item.OrganizationId, item.CommunityId);
        var query = db.CalendarAttendees.Where(attendee => attendee.EventId == eventId && eligible.Contains(attendee.UserId));
        if (after is not null) query = query.Where(attendee => attendee.UserId.CompareTo(after.UserId) > 0);
        var rows = await (
            from attendee in query
            join account in db.Users.AsNoTracking() on attendee.UserId equals account.Id
            orderby attendee.UserId
            select new { attendee.UserId, account.DisplayName, attendee.Status }).Take(size + 1).ToListAsync(ct);
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);
        return Results.Ok(new
        {
            items = rows.Select(row => new { row.UserId, row.DisplayName, Status = row.Status?.ToString() }),
            nextCursor = hasMore ? PaginationGuard.EncodeCursor(new AttendeeCursor(rows[^1].UserId)) : null
        });
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, EventRequest request, [FromServices] NexusDbContext db, IAuthorizationService authz, CancellationToken ct)
    {
        var actor = db.OrganizationActor!;
        if (!IdempotencyEndpointFilter.IsValidIdempotencyKey(
                http.Request.Headers[IdempotencyEndpointFilter.HeaderName].ToString(), out var creationKey))
            return Invalid("invalid_idempotency_key", "Header 'Idempotency-Key' must be 1 to 128 printable ASCII characters.");
        if (!TryReadFields(request, actor.Id, out var fields)) return InvalidFields();
        var resource = await CalendarAccess.ResolveScopeAsync(db, actor, request.CommunityId, ct);
        if (!authz.Can(actor.ToActorContext(), ResourceAction.CalendarCreate, resource))
            return Results.Forbid();

        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(creationKey)));
        var requestFields = new
        {
            request.CommunityId,
            Fields = new
            {
                fields.Title, fields.Description, fields.StartUtc, fields.EndUtc, fields.TimeZone, fields.ReminderMinutes,
                AttendeeIds = fields.AttendeeIds.Order().ToArray()
            }
        };
        var serialized = fields.IsOnlineMeeting == true
            ? JsonSerializer.SerializeToUtf8Bytes(new { Event = requestFields, IsOnlineMeeting = true })
            : JsonSerializer.SerializeToUtf8Bytes(requestFields);
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(serialized));
        var replay = await ReplayCreateAsync(http, db, authz, keyHash, requestHash, ct);
        if (replay is not null) return replay;

        var now = DateTimeOffset.UtcNow;
        var item = new CalendarEventEntity
        {
            Id = Guid.CreateVersion7(), OrganizationId = db.ConversationOrganizationId,
            CommunityId = request.CommunityId, OrganizerId = actor.Id,
            Title = fields.Title, TimeZone = fields.TimeZone, CreatedAt = now,
            CreationKeyHash = keyHash, CreationRequestHash = requestHash
        };
        if (!await CanInviteAsync(db, item, fields.AttendeeIds, ct)) return IneligibleInvitees();

        ApplyFields(item, fields, now);
        db.CalendarEvents.Add(item);
        db.CalendarAttendees.AddRange(fields.AttendeeIds.Select(userId => new CalendarAttendeeEntity { EventId = item.Id, UserId = userId }));
        CalendarOutbox.Enqueue(db, item, CalendarOutbox.Invitation, fields.AttendeeIds, now);
        CalendarOutbox.ScheduleReminder(db, item, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: CalendarEventConfiguration.CreationKeyIndex
            })
        {
            db.ChangeTracker.Clear();
            return await ReplayCreateAsync(http, db, authz, keyHash, requestHash, ct) ?? Conflict();
        }

        return Created(new EventRow(item, actor.DisplayName, false, null), actor.Id);
    }

    private static async Task<IResult?> ReplayCreateAsync(HttpContext http, NexusDbContext db, IAuthorizationService authz,
        string keyHash, string requestHash, CancellationToken ct)
    {
        var actorId = db.OrganizationActor!.Id;
        var existing = await Project(db, VisibleInScope(db)
            .Where(item => item.OrganizerId == actorId && item.CreationKeyHash == keyHash)).SingleOrDefaultAsync(ct);
        if (existing is null) return null;
        if (!await CanReadAsync(db, authz, existing.Item, ct)) return Results.NotFound();
        if (!string.Equals(existing.Item.CreationRequestHash, requestHash, StringComparison.Ordinal))
            return Results.Conflict(new { code = "idempotency_key_conflict", message = "This key already created a different event." });
        http.Response.Headers[IdempotencyEndpointFilter.ReplayedHeaderName] = "true";
        return Created(existing, actorId);
    }

    private static IResult Created(EventRow row, Guid actorId) =>
        Results.Created($"{OrganizationScope.ApiPath(row.Item.OrganizationId)}/calendar/events/{row.Item.Id}", row.ToDto(actorId));

    private static async Task<IResult> UpdateAsync(
        Guid eventId, EventRequest request, [FromServices] NexusDbContext db, IAuthorizationService authz,
        MeetingRevocations meetings, CancellationToken ct)
    {
        var actor = db.OrganizationActor!;
        if (request.Version is null or < 1 || !TryReadFields(request, actor.Id, out var fields)) return InvalidFields();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = await LockVisibleAsync(db, eventId, ct);
        if (item is null) return Results.NotFound();
        var resource = await CalendarAccess.ResolveScopeAsync(db, actor, item.CommunityId, ct);
        if (!authz.Can(actor.ToActorContext(), ResourceAction.CalendarEdit, resource with { TargetIsOwner = item.OrganizerId == actor.Id }))
            return Results.Forbid();
        if (item.Version != request.Version || item.CanceledAt is not null) return Conflict();
        if (item.CommunityId != request.CommunityId)
            return Invalid("calendar_scope_immutable", "The event's Community cannot be changed.");
        if (!await CanInviteAsync(db, item, fields.AttendeeIds, ct)) return IneligibleInvitees();

        var roster = await db.CalendarAttendees.Where(row => row.EventId == item.Id).ToListAsync(ct);
        var oldIds = roster.Select(row => row.UserId).ToHashSet();
        var newIds = fields.AttendeeIds.ToHashSet();
        var invited = fields.AttendeeIds.Where(id => !oldIds.Contains(id)).ToArray();
        var retained = fields.AttendeeIds.Where(oldIds.Contains).ToArray();
        db.CalendarAttendees.RemoveRange(roster.Where(row => !newIds.Contains(row.UserId)));
        db.CalendarAttendees.AddRange(invited.Select(userId => new CalendarAttendeeEntity { EventId = item.Id, UserId = userId }));

        var now = DateTimeOffset.UtcNow;
        var reminderChanged = item.StartUtc != fields.StartUtc || item.ReminderMinutes != fields.ReminderMinutes;
        var onlineChanged = fields.IsOnlineMeeting is { } online && online != item.IsOnlineMeeting;
        var detailsChanged = reminderChanged || item.EndUtc != fields.EndUtc || item.Title != fields.Title ||
            item.Description != fields.Description || item.TimeZone != fields.TimeZone || onlineChanged;
        if (!detailsChanged && oldIds.SetEquals(newIds))
            return await ReadUpdatedAsync(db, eventId, ct);

        ApplyFields(item, fields, now);
        item.Version++;
        CalendarOutbox.Enqueue(db, item, CalendarOutbox.Invitation, invited, now);
        if (detailsChanged) CalendarOutbox.Enqueue(db, item, CalendarOutbox.Updated, retained, now);
        if (reminderChanged) CalendarOutbox.ScheduleReminder(db, item, now);
        await db.SaveChangesAsync(ct);
        var roomClosed = onlineChanged && !item.IsOnlineMeeting ? await meetings.EndCalendarAsync(eventId, now, ct) : null;
        await transaction.CommitAsync(ct);
        if (roomClosed is { } closedId) await meetings.CloseProviderRoomAsync(closedId, ct);
        else if (oldIds.Except(newIds).Any()) await meetings.RecheckCalendarAsync(eventId, ct);
        return await ReadUpdatedAsync(db, eventId, ct);
    }

    private static async Task<IResult> CancelAsync(
        Guid eventId, CancelRequest request, [FromServices] NexusDbContext db, IAuthorizationService authz,
        MeetingRevocations meetings, CancellationToken ct)
    {
        if (request.Version < 1) return Invalid("invalid_calendar_version", "Provide the event's current version.");
        var actor = db.OrganizationActor!;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = await LockVisibleAsync(db, eventId, ct);
        if (item is null) return Results.NotFound();
        var resource = await CalendarAccess.ResolveScopeAsync(db, actor, item.CommunityId, ct);
        if (!authz.Can(actor.ToActorContext(), ResourceAction.CalendarEdit, resource with { TargetIsOwner = item.OrganizerId == actor.Id }))
            return Results.Forbid();
        if (item.Version != request.Version) return Conflict();
        if (item.CanceledAt is not null) return await ReadUpdatedAsync(db, eventId, ct);

        var now = DateTimeOffset.UtcNow;
        item.CanceledAt = now;
        item.UpdatedAt = now;
        item.ReminderOutboxId = null;
        item.Version++;
        var attendeeIds = await db.CalendarAttendees.Where(row => row.EventId == item.Id).Select(row => row.UserId).ToArrayAsync(ct);
        CalendarOutbox.Enqueue(db, item, CalendarOutbox.Canceled, attendeeIds, now);
        await db.SaveChangesAsync(ct);
        var roomClosed = await meetings.EndCalendarAsync(eventId, now, ct);
        await transaction.CommitAsync(ct);
        if (roomClosed is { } closedId) await meetings.CloseProviderRoomAsync(closedId, ct);
        return await ReadUpdatedAsync(db, eventId, ct);
    }

    private static async Task<IResult> RespondAsync(
        Guid eventId, RsvpRequest request, [FromServices] NexusDbContext db, IAuthorizationService authz, CancellationToken ct)
    {
        var status = request.Status switch
        {
            nameof(CalendarRsvp.Going) => CalendarRsvp.Going,
            nameof(CalendarRsvp.Tentative) => CalendarRsvp.Tentative,
            nameof(CalendarRsvp.Declined) => CalendarRsvp.Declined,
            _ => (CalendarRsvp?)null
        };
        if (status is null) return Invalid("invalid_calendar_rsvp", "Choose Going, Tentative, or Declined.");

        var actor = db.OrganizationActor!;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = await LockVisibleAsync(db, eventId, ct);
        if (item is null) return Results.NotFound();
        var resource = await CalendarAccess.ResolveScopeAsync(db, actor, item.CommunityId, ct);
        var attendee = await db.CalendarAttendees.SingleOrDefaultAsync(row => row.EventId == eventId && row.UserId == actor.Id, ct);
        if (attendee is null ||
            !authz.Can(actor.ToActorContext(), ResourceAction.CalendarRespond, resource with { TargetIsSelf = true }))
            return Results.Forbid();
        if (item.CanceledAt is not null) return Conflict();

        attendee.Status = status;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ReadUpdatedAsync(db, eventId, ct);
    }

    private static IQueryable<CalendarEventEntity> VisibleInScope(NexusDbContext db) =>
        CalendarAccess.VisibleTo(db, db.OrganizationActor!.Id)
            .Where(item => item.OrganizationId == db.ConversationOrganizationId);

    private static Task<EventRow?> FindAsync(NexusDbContext db, Guid eventId, CancellationToken ct) =>
        Project(db, VisibleInScope(db).Where(item => item.Id == eventId)).SingleOrDefaultAsync(ct);

    private static async Task<CalendarEventEntity?> LockVisibleAsync(NexusDbContext db, Guid eventId, CancellationToken ct)
    {
        var visible = VisibleInScope(db);
        var item = await db.CalendarEvents.FromSqlInterpolated($"SELECT * FROM calendar_events WHERE id = {eventId} FOR UPDATE")
            .Where(eventItem => visible.Any(candidate => candidate.Id == eventItem.Id)).SingleOrDefaultAsync(ct);
        // The visibility query also serves read-only routes. Attach the locked event for mutation.
        if (item is not null) db.Attach(item);
        return item;
    }

    private static IQueryable<EventRow> Project(NexusDbContext db, IQueryable<CalendarEventEntity> query)
    {
        var actorId = db.OrganizationActor!.Id;
        return from item in query
            join organizer in db.Users.AsNoTracking() on item.OrganizerId equals organizer.Id
            join attendee in db.CalendarAttendees.AsNoTracking().Where(row => row.UserId == actorId)
                on item.Id equals attendee.EventId into invites
            from invitation in invites.DefaultIfEmpty()
            select new EventRow(item, organizer.DisplayName,
                invitation != null, invitation == null ? null : invitation.Status);
    }

    private static async Task<IResult> ReadUpdatedAsync(NexusDbContext db, Guid eventId, CancellationToken ct) =>
        await FindAsync(db, eventId, ct) is { } row ? Results.Ok(row.ToDto(db.OrganizationActor!.Id)) : Results.NotFound();

    private static async Task<bool> CanReadAsync(
        NexusDbContext db, IAuthorizationService authz, CalendarEventEntity item, CancellationToken ct)
    {
        var actor = db.OrganizationActor!;
        return authz.Can(actor.ToActorContext(), ResourceAction.CalendarRead,
            await CalendarAccess.ResolveScopeAsync(db, actor, item.CommunityId, ct));
    }

    private static async Task<bool> CanInviteAsync(NexusDbContext db, CalendarEventEntity item, Guid[] ids, CancellationToken ct) =>
        ids.Length == 0 || await CalendarAccess.EligibleInvitees(db, item.OrganizerId, item.OrganizationId, item.CommunityId)
            .CountAsync(id => ids.Contains(id), ct) == ids.Length;

    private static bool TryReadFields(EventRequest request, Guid organizerId, out EventFields fields)
    {
        fields = null!;
        var title = request.Title?.Trim();
        var description = request.Description?.Trim();
        var zoneId = request.TimeZone?.Trim();
        var ids = request.AttendeeIds ?? [];
        if (title is null || title.Length is < 1 or > 160 || title.Contains('\0') ||
            description?.Length > 4000 || description?.Contains('\0') == true ||
            zoneId is null || zoneId.Length is < 1 or > 128 || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone) ||
            request.StartUtc is not { } start || start == default || request.EndUtc is not { } end || end <= start ||
            request.ReminderMinutes is < 0 or > 10080 || request.CommunityId == Guid.Empty ||
            ids.Any(id => id == Guid.Empty || id == organizerId) ||
            ids.Distinct().Count() != ids.Length)
        {
            return false;
        }

        if (zone.Id != "UTC" && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId)) zoneId = ianaId;
        fields = new(title, string.IsNullOrEmpty(description) ? null : description,
            start.ToUniversalTime(), end.ToUniversalTime(), zoneId, request.ReminderMinutes, request.IsOnlineMeeting, ids);
        return true;
    }

    private static void ApplyFields(CalendarEventEntity item, EventFields fields, DateTimeOffset now)
    {
        item.Title = fields.Title;
        item.Description = fields.Description;
        item.StartUtc = fields.StartUtc;
        item.EndUtc = fields.EndUtc;
        item.TimeZone = fields.TimeZone;
        item.ReminderMinutes = fields.ReminderMinutes;
        item.IsOnlineMeeting = fields.IsOnlineMeeting ?? item.IsOnlineMeeting;
        item.UpdatedAt = now;
    }

    private static IResult Invalid(string code, string message) => Results.BadRequest(new { code, message });
    private static IResult InvalidFields() => Invalid("invalid_calendar_event",
        "Check the title, time zone, start and end times, reminder, and attendee list.");
    private static IResult IneligibleInvitees() => Results.Json(
        new { code = "calendar_ineligible_attendees", message = "One or more invitees no longer have access." },
        statusCode: StatusCodes.Status403Forbidden);
    private static IResult Conflict() => Results.Conflict(
        new { code = "calendar_conflict", message = "The event changed or was canceled. Refresh the event and try again." });

    private sealed record EventRequest(string? Title, string? Description, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc,
        string? TimeZone, Guid? CommunityId, Guid[]? AttendeeIds, int? ReminderMinutes, int? Version, bool? IsOnlineMeeting);
    private sealed record CancelRequest(int Version);
    private sealed record RsvpRequest(string? Status);
    private sealed record EventCursor(DateTimeOffset StartUtc, Guid Id);
    private sealed record AttendeeCursor(Guid UserId);
    private sealed record EventFields(string Title, string? Description, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
        string TimeZone, int? ReminderMinutes, bool? IsOnlineMeeting, Guid[] AttendeeIds);
    private sealed record EventRow(CalendarEventEntity Item, string OrganizerName, bool IsAttendee, CalendarRsvp? Rsvp)
    {
        public object ToDto(Guid actorId) => new
        {
            Item.Id, Item.OrganizationId, Item.CommunityId, Item.OrganizerId, OrganizerName, Item.Title, Item.Description,
            Item.StartUtc, Item.EndUtc, Item.TimeZone, Item.ReminderMinutes, Item.IsOnlineMeeting, Item.CanceledAt, Item.Version,
            MyRsvp = Rsvp?.ToString(), CanEdit = Item.OrganizerId == actorId && Item.CanceledAt == null,
            CanRsvp = IsAttendee && Item.CanceledAt == null
        };
    }
}
