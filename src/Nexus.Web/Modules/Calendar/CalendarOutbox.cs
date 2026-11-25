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

    internal static void ScheduleReminder(NexusDbContext db, CalendarEventEntity item, DateTimeOffset now)
    {
        item.ReminderOutboxId = null;
        if (item.ReminderMinutes is not { } minutes || item.StartUtc <= now) return;

        var due = item.StartUtc.AddMinutes(-minutes);
        var message = NewMessage(item, Reminder, null, now, due < now ? now : due);
        item.ReminderOutboxId = message.Id;
        db.OutboxMessages.Add(message);
    }

    public async Task HandleAsync(OutboxMessageEntity message, CancellationToken ct)
    {
        var item = await db.CalendarEvents.AsNoTracking().SingleOrDefaultAsync(eventItem =>
            eventItem.Id == message.ResourceId && eventItem.OrganizationId == message.OrganizationId, ct);
        if (item is null) return;

        var reminder = message.Kind == Reminder;
        if ((item.CanceledAt is not null) != (message.Kind == Canceled) ||
            (reminder && (item.ReminderOutboxId != message.Id || item.EndUtc <= DateTimeOffset.UtcNow)))
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

    private static OutboxMessageEntity NewMessage(
        CalendarEventEntity item, string kind, Guid[]? recipientIds, DateTimeOffset createdAt, DateTimeOffset availableAt) => new()
    {
        Id = Guid.CreateVersion7(), Kind = kind, OrganizationId = item.OrganizationId, ResourceId = item.Id,
        RecipientIds = recipientIds, CreatedAt = createdAt, AvailableAt = availableAt
    };
}
