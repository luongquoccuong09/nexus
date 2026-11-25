using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Notifications;

namespace Nexus.Web.Modules.Calendar;

internal sealed class CalendarOutbox(
    NexusDbContext db, IAuthorizationService authz, WebPushTransport push, IEmailDigestTransport digest) : IOutboxHandler
{
    internal const string Invitation = "calendar.invitation";
    internal const string Updated = "calendar.updated";
    internal const string Canceled = "calendar.canceled";
    internal const string Reminder = "calendar.reminder";

    internal static bool IsCalendarKind(string value) => value is Invitation or Updated or Canceled or Reminder;

    internal static void Enqueue(NexusDbContext db, CalendarEventEntity item, string kind, Guid[] recipientIds, DateTimeOffset now)
    {
        if (recipientIds.Length > 0)
            db.OutboxMessages.Add(NewMessage(item, kind, recipientIds, now, now));
    }

    internal static async Task ScheduleReminderAsync(NexusDbContext db, CalendarEventEntity item, DateTimeOffset now,
        CancellationToken ct, bool preserveSeriesGeneration = false)
    {
        if (CalendarRecurrences.IsRecurring(item) && item.ReminderMinutes is not null)
        {
            if (!preserveSeriesGeneration || item.ReminderGenerationId is null)
                item.ReminderGenerationId = Guid.CreateVersion7();
            await ScheduleNextAsync(db, item, now, null, now, ct);
            return;
        }

        item.ReminderGenerationId = null;
        item.ReminderOutboxId = null;
        if (item.ReminderMinutes is not { } minutes || item.StartUtc <= now) return;

        var message = CreateReminder(item, minutes, item.StartUtc, now);
        item.ReminderOutboxId = message.Id;
        db.OutboxMessages.Add(message);
    }

    private static async Task ScheduleNextAsync(NexusDbContext db, CalendarEventEntity item, DateTimeOffset after,
        DateTimeOffset? afterOriginal, DateTimeOffset now, CancellationToken ct)
    {
        if (item.ReminderMinutes is not { } minutes || item.ReminderGenerationId is not { } generation) return;

        while (await FindNextAsync(db, item, after, afterOriginal, ct) is { } next)
        {
            var matching = db.CalendarOccurrenceReminders.Where(reminder => reminder.EventId == item.Id &&
                reminder.OriginalStartUtc == next.OriginalStartUtc && reminder.EffectiveStartUtc == next.StartUtc &&
                reminder.ReminderMinutes == minutes);
            var processed = await matching.Where(reminder => reminder.ProcessedAt != null)
                .OrderByDescending(reminder => reminder.ProcessedAt).FirstOrDefaultAsync(ct);
            if (processed is not null)
            {
                processed.GenerationId = generation;
                after = next.StartUtc;
                afterOriginal = next.OriginalStartUtc;
                continue;
            }

            var pending = await matching.SingleOrDefaultAsync(reminder => reminder.OutboxMessageId == item.ReminderOutboxId, ct);
            if (pending is not null)
            {
                pending.GenerationId = generation;
                return;
            }

            var message = CreateReminder(item, minutes, next.StartUtc, now);
            item.ReminderOutboxId = message.Id;
            db.OutboxMessages.Add(message);
            db.CalendarOccurrenceReminders.Add(new CalendarOccurrenceReminderEntity
            {
                OutboxMessageId = message.Id, EventId = item.Id, GenerationId = generation, ReminderMinutes = minutes,
                OriginalStartUtc = next.OriginalStartUtc, EffectiveStartUtc = next.StartUtc
            });
            return;
        }

        item.ReminderOutboxId = null;
    }

    private static async Task<CalendarOccurrence?> FindNextAsync(NexusDbContext db, CalendarEventEntity item,
        DateTimeOffset after, DateTimeOffset? afterOriginal, CancellationToken ct)
    {
        var overrides = db.CalendarOccurrenceOverrides.AsNoTracking().Where(change => change.EventId == item.Id);
        var moved = await overrides.Where(change => change.CanceledAt == null &&
                (change.StartUtc > after || change.StartUtc == after && afterOriginal != null && change.OriginalStartUtc > afterOriginal))
            .OrderBy(change => change.StartUtc).ThenBy(change => change.OriginalStartUtc).FirstOrDefaultAsync(ct);
        var cutoff = moved?.StartUtc ?? DateTimeOffset.MaxValue;
        var originals = CalendarRecurrences.OriginalStarts(item, after)
            .Where(original => original > after || original == after && afterOriginal is { } previous && original > previous)
            .TakeWhile(original => original <= cutoff);
        foreach (var candidates in originals.Chunk(PaginationGuard.MaxPageSize))
        {
            var changed = await overrides.Where(change => candidates.Contains(change.OriginalStartUtc))
                .Select(change => change.OriginalStartUtc).ToListAsync(ct);
            foreach (var original in candidates.Except(changed))
                if (moved is null || original < cutoff || original == cutoff && original < moved.OriginalStartUtc)
                    return CalendarRecurrences.Resolve(item, original, null);
        }

        return moved is null ? null : CalendarRecurrences.Resolve(item, moved.OriginalStartUtc, moved);
    }

    private static OutboxMessageEntity CreateReminder(CalendarEventEntity item, int minutes, DateTimeOffset start, DateTimeOffset now)
    {
        var due = CalendarRecurrences.Shift(start, TimeSpan.FromMinutes(-minutes));
        return NewMessage(item, Reminder, null, now, due < now ? now : due);
    }

    public async Task HandleAsync(OutboxMessageEntity message, CancellationToken ct)
    {
        var reminder = message.Kind == Reminder;
        var events = reminder
            ? db.CalendarEvents.FromSqlInterpolated($"SELECT * FROM calendar_events WHERE id = {message.ResourceId} FOR UPDATE")
            : db.CalendarEvents.AsQueryable();
        var item = await events.AsNoTracking().SingleOrDefaultAsync(eventItem =>
            eventItem.Id == message.ResourceId && eventItem.OrganizationId == message.OrganizationId, ct);
        if (item is null) return;

        if ((item.CanceledAt is not null) != (message.Kind == Canceled)) return;
        if (reminder && CalendarRecurrences.IsRecurring(item))
        {
            if (!await AdvanceRecurringReminderAsync(item, message.Id, ct)) return;
        }
        else if (reminder && (item.ReminderOutboxId != message.Id || item.EndUtc <= DateTimeOffset.UtcNow))
            return;

        var eligible = CalendarAccess.EligibleInvitees(db, item.OrganizerId, item.OrganizationId, item.CommunityId);
        var attendees = db.CalendarAttendees.Where(attendee =>
            attendee.EventId == item.Id && eligible.Contains(attendee.UserId));
        var intended = message.RecipientIds ?? [];
        attendees = reminder
            ? attendees.Where(attendee => attendee.Status != CalendarRsvp.Declined)
            : attendees.Where(attendee => intended.Contains(attendee.UserId));
        var recipients = await attendees.Select(attendee => attendee.UserId).ToListAsync(ct);
        if (reminder && await CalendarAccess.VisibleTo(db, item.OrganizerId).AnyAsync(entry => entry.Id == item.Id, ct))
            recipients.Add(item.OrganizerId);

        var alreadyDelivered = await db.Notifications.Where(notification => notification.OutboxMessageId == message.Id)
            .Select(notification => notification.RecipientId).ToListAsync(ct);
        var pending = recipients.Except(alreadyDelivered).ToArray();
        if (pending.Length == 0) return;

        var actors = item.OrganizationId is { } orgId
            ? await db.OrganizationMemberships.AsNoTracking().Where(membership =>
                    membership.OrganizationId == orgId && membership.Account.Status == AccountStatus.Active && pending.Contains(membership.Id))
                .Select(membership => new ActorContext(membership.Id, membership.OrganizationId, membership.Status, membership.TrustLevel, membership.OrgRole))
                .ToListAsync(ct)
            : await db.Users.AsNoTracking().Where(account => pending.Contains(account.Id))
                .Select(account => new ActorContext(account.Id, Guid.Empty, account.Status, TrustLevel.Unverified, OrgRole.Member))
                .ToListAsync(ct);
        var resource = new ResourceContext(ResourceKind.CalendarEvent, item.OrganizationId ?? Guid.Empty, ResourceRole.Member);
        var notifications = actors.Where(actor => authz.Can(actor, ResourceAction.CalendarRead, resource))
            .Select(actor => new NotificationEntity
            {
                Id = Guid.CreateVersion7(), OutboxMessageId = message.Id, RecipientId = actor.UserId,
                OrganizationId = item.OrganizationId, ResourceId = item.Id, Kind = message.Kind,
                CreatedAt = message.AvailableAt
            }).ToList();

        db.Notifications.AddRange(notifications);
        await PushOutboxHandler.EnqueueAsync(db, push, notifications, ct);
        await EmailDigestHandler.EnqueueAsync(db, digest, notifications, ct);
    }

    private async Task<bool> AdvanceRecurringReminderAsync(CalendarEventEntity item, Guid outboxId, CancellationToken ct)
    {
        if (item.ReminderOutboxId != outboxId) return false;
        var metadata = await db.CalendarOccurrenceReminders.SingleOrDefaultAsync(reminder =>
            reminder.OutboxMessageId == outboxId && reminder.EventId == item.Id && reminder.GenerationId == item.ReminderGenerationId, ct);
        if (metadata is null || metadata.ProcessedAt is not null) return false;

        var occurrence = await CalendarRecurrences.FindAsync(db, item, metadata.OriginalStartUtc, ct);
        var now = DateTimeOffset.UtcNow;
        var shouldDeliver = occurrence is { CanceledAt: null } && occurrence.StartUtc == metadata.EffectiveStartUtc && occurrence.EndUtc > now;
        db.Attach(item);
        if (shouldDeliver) metadata.ProcessedAt = now;
        item.ReminderOutboxId = null;
        var after = shouldDeliver || metadata.EffectiveStartUtc > now ? metadata.EffectiveStartUtc : now;
        await ScheduleNextAsync(db, item, after, after == metadata.EffectiveStartUtc ? metadata.OriginalStartUtc : null, now, ct);
        return shouldDeliver;
    }

    private static OutboxMessageEntity NewMessage(
        CalendarEventEntity item, string kind, Guid[]? recipientIds, DateTimeOffset createdAt, DateTimeOffset availableAt) => new()
    {
        Id = Guid.CreateVersion7(), Kind = kind, OrganizationId = item.OrganizationId, ResourceId = item.Id,
        RecipientIds = recipientIds, CreatedAt = createdAt, AvailableAt = availableAt
    };
}
