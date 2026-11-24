using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;

namespace Nexus.Web.Modules.Notifications;

internal sealed class EmailDigestHandler(NexusDbContext db, IEmailDigestTransport transport) : IOutboxHandler
{
    public const string Kind = "notification.digest";

    public async Task HandleAsync(OutboxMessageEntity message, CancellationToken ct)
    {
        if (!transport.IsEnabled || message.DestinationId is not { } userId) return;

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - transport.UnreadAge;
        if (message.CreatedAt < cutoff.AddDays(-1)) return;

        // A recipient lock prevents concurrent digests from including the same unread row.
        var preferences = await db.EmailDigestOptIns.FromSqlInterpolated($"""
            SELECT * FROM email_digest_opt_ins WHERE user_id = {userId} FOR UPDATE
            """).ToListAsync(ct);
        var optedIn = preferences.SingleOrDefault();
        if (optedIn is null) return;

        var email = message.OrganizationId is { } orgId
            ? await VerifiedWorkAddresses(db, userId).Where(item => item.OrganizationId == orgId).Select(item => item.Email).SingleOrDefaultAsync(ct)
            : await ConfirmedAccountAddress(db, userId).SingleOrDefaultAsync(ct);
        if (!MailAddress.TryCreate(email, out var address)) return;

        var quiet = await db.NotificationQuietHours.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, ct);
        if (quiet?.ResumeAt(now) is { } resume)
        {
            message.AvailableAt = resume;
            return;
        }

        var visible = NotificationQueries.VisibleTo(db, userId);
        var eligible = NotificationQueries.Unread(db, visible, userId)
            .Where(item => item.OrganizationId == message.OrganizationId && item.PushAcceptedAt == null &&
                item.DigestedAt == null && item.CreatedAt <= cutoff && item.CreatedAt >= cutoff.AddDays(-1) &&
                item.CreatedAt >= optedIn.EnabledAt &&
                !db.ConversationNotificationPreferences.Any(pref => pref.ConversationId == item.ConversationId &&
                    pref.UserId == userId && (pref.Mode == ConversationNotificationMode.Muted ||
                    (pref.Mode == ConversationNotificationMode.Mentions && !item.Kind.StartsWith("mention.")))) &&
                (db.ConversationNotificationPreferences.Any(pref => pref.ConversationId == item.ConversationId && pref.UserId == userId) ||
                 !db.ConversationMembers.Any(member => member.ConversationId == item.ConversationId &&
                     member.UserId == userId && member.LeftAt == null && member.Muted)));
        var pending = db.OutboxMessages.Where(delivery => delivery.Kind == PushOutboxHandler.Kind &&
            delivery.CompletedAt == null && delivery.DeadLetteredAt == null);
        var notificationIds = await eligible.Where(item => !pending.Any(delivery => delivery.ResourceId == item.Id))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Select(item => item.Id).Take(50).ToArrayAsync(ct);

        if (notificationIds.Length == 0)
        {
            if (await eligible.AnyAsync(ct)) message.AvailableAt = now.AddMinutes(5);
            return;
        }

        await transport.SendAsync(address.Address, message.Id, notificationIds.Length, ct);
        await db.Notifications.Where(item => notificationIds.Contains(item.Id) && item.DigestedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.DigestedAt, now), ct);
    }

    public static async Task EnqueueAsync(NexusDbContext db, IEmailDigestTransport transport,
        IReadOnlyList<NotificationEntity> notifications, CancellationToken ct)
    {
        if (!transport.IsEnabled || notifications.Count == 0) return;

        var userIds = notifications.Select(item => item.RecipientId).Distinct().ToArray();
        var enabled = await db.EmailDigestOptIns.AsNoTracking().Where(item => userIds.Contains(item.UserId))
            .ToDictionaryAsync(item => item.UserId, item => item.EnabledAt, ct);
        foreach (var notification in notifications)
        {
            if (!enabled.TryGetValue(notification.RecipientId, out var enabledAt) || notification.CreatedAt < enabledAt) continue;

            db.OutboxMessages.Add(new OutboxMessageEntity
            {
                Id = Guid.CreateVersion7(), Kind = Kind, OrganizationId = notification.OrganizationId,
                ResourceId = notification.Id, DestinationId = notification.RecipientId,
                CreatedAt = notification.CreatedAt, AvailableAt = notification.CreatedAt + transport.UnreadAge
            });
        }
    }

    public static IQueryable<string?> ConfirmedAccountAddress(NexusDbContext db, Guid userId) =>
        db.Users.AsNoTracking().Where(user => user.Id == userId && user.Status == AccountStatus.Active &&
            user.EmailConfirmed && user.Email != null && user.Email != "").Select(user => user.Email);

    public static IQueryable<WorkAddress> VerifiedWorkAddresses(NexusDbContext db, Guid userId) =>
        from membership in db.OrganizationMemberships.AsNoTracking()
        join employee in db.Employees.AsNoTracking() on membership.EmployeeId equals employee.Id
        where membership.Id == userId && membership.OrganizationId == employee.OrganizationId &&
              membership.Status == AccountStatus.Active && membership.Account.Status == AccountStatus.Active &&
              membership.TrustLevel == TrustLevel.Verified && employee.IsActive && employee.Email != null && employee.Email != ""
        select new WorkAddress { OrganizationId = membership.OrganizationId, Email = employee.Email };

    public sealed class WorkAddress
    {
        public Guid OrganizationId { get; init; }
        public string? Email { get; init; }
    }
}
