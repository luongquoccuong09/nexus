using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Messaging;

namespace Nexus.Web.Modules.Notifications;

internal sealed class AnnouncementReminderHandler(
    NexusDbContext db, IAuthorizationService authz, WebPushTransport push, IEmailDigestTransport digest) : IOutboxHandler
{
    public const string Kind = "announcement.reminder";

    public async Task HandleAsync(OutboxMessageEntity message, CancellationToken ct)
    {
        var source = await (
            from post in db.ChannelPosts.AsNoTracking()
            join channel in db.Conversations.AsNoTracking() on post.ConversationId equals channel.Id
            join community in db.Communities.AsNoTracking() on channel.CommunityId equals community.Id
            where post.Id == message.ResourceId && post.OrganizationId == message.OrganizationId &&
                  channel.OrganizationId == post.OrganizationId && community.OrganizationId == post.OrganizationId &&
                  channel.Kind == ConversationKind.Channel && post.RequiresAcknowledgment && post.DeletedAt == null
            select new { channel.Id, channel.PostingPolicy, Community = community }).SingleOrDefaultAsync(ct);

        if (source is null)
        {
            return;
        }

        var recipients = await AnnouncementRecipients.Outstanding(db, source.Community, message.ResourceId, message.CreatedAt)
            .Where(recipient => !db.Notifications.Any(notification =>
                notification.OutboxMessageId == message.Id && notification.RecipientId == recipient.Membership.Id))
            .ToListAsync(ct);
        if (recipients.Count == 0) return;
        var excluded = await NotificationPreferences.ExcludedRecipientsAsync(db, source.Id,
            recipients.Select(recipient => recipient.Membership.Id).ToArray(), isMention: false, ct);

        var notifications = new List<NotificationEntity>();
        foreach (var recipient in recipients)
        {
            var resource = new ResourceContext(
                ResourceKind.CommunityChannel, source.Community.OrganizationId,
                MembershipRole: recipient.Role, IsOfficial: source.Community.IsOfficial,
                Visibility: source.Community.Visibility, PostingPolicy: source.PostingPolicy);
            if (excluded.Contains(recipient.Membership.Id) ||
                !authz.Can(recipient.Membership.ToActorContext(), ResourceAction.ConversationRead, resource))
            {
                continue;
            }

            notifications.Add(new NotificationEntity
            {
                Id = Guid.CreateVersion7(),
                OutboxMessageId = message.Id,
                Kind = Kind,
                RecipientId = recipient.Membership.Id,
                OrganizationId = source.Community.OrganizationId,
                ConversationId = source.Id,
                ResourceId = message.ResourceId,
                CreatedAt = message.CreatedAt
            });
        }

        db.Notifications.AddRange(notifications);
        await PushOutboxHandler.EnqueueAsync(db, push, notifications, ct);
        await EmailDigestHandler.EnqueueAsync(db, digest, notifications, ct);
    }
}
