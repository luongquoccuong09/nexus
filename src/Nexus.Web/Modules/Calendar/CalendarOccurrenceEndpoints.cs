using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Meetings;

namespace Nexus.Web.Modules.Calendar;

internal static partial class CalendarEndpoints
{
    private static async Task IncludeRecurringAsync(NexusDbContext db, IQueryable<CalendarEventEntity> scope,
        SortedSet<EventListItem> entries, DateTimeOffset from, DateTimeOffset to, EventCursor? after, int capacity,
        CancellationToken ct)
    {
        var series = scope.Where(item => item.RecurrenceFrequency != null && item.CanceledAt == null &&
            (item.StartUtc < to || db.CalendarOccurrenceOverrides.Any(change => change.EventId == item.Id &&
                change.CanceledAt == null && change.StartUtc < to && change.EndUtc > from)));
        var seriesCursor = Guid.Empty;
        while (true)
        {
            var batch = await Project(db, series.Where(item => item.Id.CompareTo(seriesCursor) > 0)
                .OrderBy(item => item.Id).Take(PaginationGuard.MaxPageSize)).ToListAsync(ct);
            if (batch.Count == 0) return;
            seriesCursor = batch[^1].Item.Id;

            var ids = batch.Select(row => row.Item.Id).ToArray();
            var bound = batch.Min(row => LowerOriginalBound(row.Item, from, after));
            var changes = await db.CalendarOccurrenceOverrides.AsNoTracking().Where(entry => ids.Contains(entry.EventId) &&
                (entry.OriginalStartUtc >= bound && entry.OriginalStartUtc < to ||
                 entry.CanceledAt == null && entry.StartUtc < to && entry.EndUtc > from))
                .ToListAsync(ct);
            var changesByEvent = changes.ToLookup(entry => entry.EventId);

            foreach (var row in batch)
            {
                ct.ThrowIfCancellationRequested();
                var overrides = changesByEvent[row.Item.Id].ToDictionary(entry => entry.OriginalStartUtc);
                foreach (var change in overrides.Values.Where(entry => entry.StartUtc is not null && entry.CanceledAt is null))
                    Add(row, CalendarRecurrences.Resolve(row.Item, change.OriginalStartUtc, change));

                foreach (var occurrence in CalendarRecurrences.Expand(row.Item, LowerOriginalBound(row.Item, from, after), to))
                {
                    if (entries.Count == capacity && occurrence.StartUtc > entries.Max!.Cursor.StartUtc) break;
                    if (!overrides.ContainsKey(occurrence.OriginalStartUtc)) Add(row, occurrence);
                }
            }
            if (batch.Count < PaginationGuard.MaxPageSize) return;
        }

        void Add(EventRow row, CalendarOccurrence occurrence)
        {
            if (occurrence.StartUtc >= to || occurrence.EndUtc <= from) return;
            var entry = new EventListItem(row, occurrence);
            if (after is not null && CompareCursor(entry.Cursor, after) <= 0) return;
            entries.Add(entry);
            if (entries.Count > capacity) entries.Remove(entries.Max!);
        }
    }

    private static DateTimeOffset LowerOriginalBound(CalendarEventEntity item, DateTimeOffset from, EventCursor? after)
    {
        var overlappedStart = CalendarRecurrences.Shift(from, item.StartUtc - item.EndUtc);
        return after is not null && after.StartUtc > overlappedStart ? after.StartUtc.ToUniversalTime() : overlappedStart;
    }

    private static int CompareCursor(EventCursor left, EventCursor right)
    {
        var comparison = left.StartUtc.CompareTo(right.StartUtc);
        if (comparison != 0) return comparison;
        comparison = left.Id.CompareTo(right.Id);
        return comparison != 0 ? comparison : Nullable.Compare(left.OccurrenceStartUtc, right.OccurrenceStartUtc);
    }

    private static async Task<IResult> ExportAsync(Guid eventId, [FromServices] NexusDbContext db,
        IAuthorizationService authz, CancellationToken ct)
    {
        var row = await FindAsync(db, eventId, ct);
        if (row is null || !await CanReadAsync(db, authz, row.Item, ct)) return Results.NotFound();
        var changes = await db.CalendarOccurrenceOverrides.AsNoTracking().Where(entry => entry.EventId == eventId)
            .OrderBy(entry => entry.OriginalStartUtc).ToListAsync(ct);
        var content = CalendarRecurrences.Serialize(row.Item, changes);
        return Results.File(Encoding.UTF8.GetBytes(content), "text/calendar; charset=utf-8", $"event-{eventId:N}.ics");
    }

    private static Task<IResult> UpdateOccurrenceAsync(Guid eventId, OccurrenceUpdateRequest request,
        [FromServices] NexusDbContext db, IAuthorizationService authz, MeetingRevocations meetings, CancellationToken ct)
    {
        var title = request.Title?.Trim();
        var description = request.Description?.Trim();
        if (request.Version < 1 || request.OccurrenceStartUtc == default || title is null ||
            title.Length is < 1 or > 160 || title.Contains('\0') || description?.Length > 4000 ||
            description?.Contains('\0') == true || request.StartUtc == default || request.EndUtc <= request.StartUtc)
            return Task.FromResult(Invalid("invalid_calendar_occurrence", "Check the occurrence time, title, and current version."));

        var update = new OccurrenceFields(title, string.IsNullOrEmpty(description) ? null : description,
            request.StartUtc.ToUniversalTime(), request.EndUtc.ToUniversalTime());
        return ChangeOccurrenceAsync(eventId, request.OccurrenceStartUtc, request.Version, update, db, authz, meetings, ct);
    }

    private static Task<IResult> CancelOccurrenceAsync(Guid eventId, OccurrenceCancelRequest request,
        [FromServices] NexusDbContext db, IAuthorizationService authz, MeetingRevocations meetings, CancellationToken ct) =>
        request.Version < 1 || request.OccurrenceStartUtc == default
            ? Task.FromResult(Invalid("invalid_calendar_occurrence", "Provide an occurrence and its current version."))
            : ChangeOccurrenceAsync(eventId, request.OccurrenceStartUtc, request.Version, null, db, authz, meetings, ct);

    private static async Task<IResult> ChangeOccurrenceAsync(Guid eventId, DateTimeOffset original, int version,
        OccurrenceFields? update, NexusDbContext db, IAuthorizationService authz, MeetingRevocations meetings, CancellationToken ct)
    {
        original = original.ToUniversalTime();
        var actor = db.OrganizationActor!;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var item = await LockVisibleAsync(db, eventId, ct);
        if (item is null) return Results.NotFound();
        var access = await CalendarAccess.ResolveScopeAsync(db, actor, item.CommunityId, ct);
        if (!authz.Can(actor.ToActorContext(), ResourceAction.CalendarEdit, access with { TargetIsOwner = item.OrganizerId == actor.Id }))
            return Results.Forbid();
        if (item.Version != version || item.CanceledAt is not null) return Conflict();
        if (!CalendarRecurrences.ContainsOriginal(item, original)) return Results.NotFound();

        var change = await db.CalendarOccurrenceOverrides.SingleOrDefaultAsync(entry => entry.EventId == eventId &&
            entry.OriginalStartUtc == original, ct);
        if (change?.CanceledAt is not null)
            return update is null ? await ReadOccurrenceAsync(db, eventId, original, ct) : Conflict();
        var current = CalendarRecurrences.Resolve(item, original, change);
        if (update is not null && current.Title == update.Title && current.Description == update.Description &&
            current.StartUtc == update.StartUtc && current.EndUtc == update.EndUtc)
            return await ReadOccurrenceAsync(db, eventId, original, ct);

        if (change is null)
        {
            change = new CalendarOccurrenceOverride { EventId = eventId, OriginalStartUtc = original };
            db.CalendarOccurrenceOverrides.Add(change);
        }
        var now = DateTimeOffset.UtcNow;
        if (update is null) change.CanceledAt = now;
        else
        {
            change.StartUtc = update.StartUtc;
            change.EndUtc = update.EndUtc;
            change.Title = update.Title;
            change.Description = update.Description;
        }

        item.UpdatedAt = now;
        item.Version++;
        var attendeeIds = await db.CalendarAttendees.Where(attendee => attendee.EventId == eventId)
            .Select(attendee => attendee.UserId).ToArrayAsync(ct);
        CalendarOutbox.Enqueue(db, item, CalendarOutbox.Updated, attendeeIds, now);
        await db.SaveChangesAsync(ct);
        await CalendarOutbox.ScheduleReminderAsync(db, item, now, ct, preserveSeriesGeneration: true);
        await db.SaveChangesAsync(ct);
        var ended = update is null ? await meetings.EndCalendarOccurrenceAsync(eventId, original, now, ct) : null;
        await transaction.CommitAsync(ct);
        if (ended is { } room) await meetings.CloseProviderRoomAsync(room, ct);
        return await ReadOccurrenceAsync(db, eventId, original, ct);
    }

    private static async Task<IResult> ReadOccurrenceAsync(NexusDbContext db, Guid eventId, DateTimeOffset original,
        CancellationToken ct)
    {
        var row = await FindAsync(db, eventId, ct);
        if (row is null) return Results.NotFound();
        var occurrence = await CalendarRecurrences.FindAsync(db, row.Item, original, ct);
        return occurrence is null ? Results.NotFound() : Results.Ok(row.ToDto(db.OrganizationActor!.Id, occurrence));
    }

    private sealed record EventListItem(EventRow Row, CalendarOccurrence? Occurrence)
    {
        public EventCursor Cursor => new(Occurrence?.StartUtc ?? Row.Item.StartUtc, Row.Item.Id, Occurrence?.OriginalStartUtc);
    }

    private sealed record OccurrenceUpdateRequest(int Version, DateTimeOffset OccurrenceStartUtc, string? Title, string? Description,
        DateTimeOffset StartUtc, DateTimeOffset EndUtc);
    private sealed record OccurrenceCancelRequest(int Version, DateTimeOffset OccurrenceStartUtc);
    private sealed record OccurrenceFields(string Title, string? Description, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}
