using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;

namespace Nexus.Web.Modules.Notifications;

internal sealed class PushOutboxHandler(NexusDbContext db, WebPushTransport transport) : IOutboxHandler
{
    public const string Kind = "notification.push";

    public async Task HandleAsync(OutboxMessageEntity message, CancellationToken ct)
    {
        if (transport.PublicKey is null || message.DestinationId is not { } subscriptionId) return;

        var now = DateTimeOffset.UtcNow;
        if (message.CreatedAt < now.AddDays(-1)) return;

        var subscription = await ActiveSubscriptions(db, now).SingleOrDefaultAsync(item => item.Id == subscriptionId, ct);
        if (subscription is null || !WebPushTransport.TryParseEndpoint(subscription.Endpoint, out var endpoint)) return;

        var visible = NotificationQueries.VisibleTo(db, subscription.UserId);
        var notification = await NotificationQueries.Unread(db, visible, subscription.UserId)
            .SingleOrDefaultAsync(item => item.Id == message.ResourceId, ct);
        if (notification is null) return;

        var mode = await NotificationPreferences.GetModeAsync(db, notification.ConversationId, subscription.UserId, ct);
        if (mode == ConversationNotificationMode.Muted ||
            (mode == ConversationNotificationMode.Mentions && !notification.Kind.StartsWith("mention.", StringComparison.Ordinal)))
        {
            return;
        }

        var quiet = await db.NotificationQuietHours.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == subscription.UserId, ct);
        if (quiet?.ResumeAt(now) is { } resume)
        {
            message.AvailableAt = resume;
            return;
        }

        var result = await transport.SendAsync(endpoint!, notification.ConversationId, ct);
        if (result == WebPushResult.Expired)
        {
            await db.PushSubscriptions.Where(item => item.Id == subscription.Id && item.SessionId == subscription.SessionId &&
                item.Endpoint == subscription.Endpoint).ExecuteDeleteAsync(ct);
        }

        if (result == WebPushResult.Accepted)
        {
            await db.Notifications.Where(item => item.Id == notification.Id && item.PushAcceptedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.PushAcceptedAt, DateTimeOffset.UtcNow), ct);
        }
    }

    public static async Task EnqueueAsync(NexusDbContext db, WebPushTransport transport,
        IReadOnlyList<NotificationEntity> notifications, CancellationToken ct)
    {
        if (transport.PublicKey is null || notifications.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var recipientIds = notifications.Select(item => item.RecipientId).Distinct().ToArray();
        var subscriptions = await ActiveSubscriptions(db, now).Where(item => recipientIds.Contains(item.UserId))
            .Select(item => new { item.Id, item.UserId }).ToListAsync(ct);
        var byUser = subscriptions.ToLookup(item => item.UserId);
        foreach (var notification in notifications)
        {
            foreach (var subscription in byUser[notification.RecipientId])
            {
                db.OutboxMessages.Add(new OutboxMessageEntity
                {
                    Id = Guid.CreateVersion7(), Kind = Kind, OrganizationId = notification.OrganizationId,
                    ResourceId = notification.Id, DestinationId = subscription.Id,
                    CreatedAt = now, AvailableAt = now
                });
            }
        }
    }

    private static IQueryable<PushSubscriptionEntity> ActiveSubscriptions(NexusDbContext db, DateTimeOffset now) =>
        from subscription in db.PushSubscriptions.AsNoTracking()
        join session in db.AppSessions.AsNoTracking() on subscription.SessionId equals session.Id
        join account in db.Users.AsNoTracking() on subscription.UserId equals account.Id
        where session.UserId == subscription.UserId && session.RevokedAt == null &&
              (session.ExpiresAt == null || session.ExpiresAt > now) && session.SecurityStamp == account.SecurityStamp &&
              account.Status == AccountStatus.Active
        select subscription;
}
