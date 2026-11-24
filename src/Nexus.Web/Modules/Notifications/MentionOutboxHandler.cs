using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Notifications;

internal sealed class MentionOutboxHandler(
    NexusDbContext db, IAuthorizationService authz, WebPushTransport push, IEmailDigestTransport digest) : IOutboxHandler
{
    public async Task HandleAsync(OutboxMessageEntity message, CancellationToken ct)
    {
        if (message.RecipientIds is not { Length: > 0 } recipients)
        {
            return;
        }

        var source = await FindSourceAsync(message, ct);
        if (source is null)
        {
            return;
        }

        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == source.ConversationId && item.OrganizationId == message.OrganizationId, ct);
        if (conversation is null)
        {
            return;
        }

        var existing = db.Notifications.Where(item => item.OutboxMessageId == message.Id);
        var eligible = await FindEligibleAsync(message, source, conversation, recipients, existing, ct);
        if (eligible.Count == 0) return;
        var excluded = await NotificationPreferences.ExcludedRecipientsAsync(db, conversation.Id,
            eligible.Select(recipient => recipient.Actor.UserId).ToArray(),
            message.Kind.StartsWith("mention.", StringComparison.Ordinal), ct);
        var notifications = new List<NotificationEntity>();
        foreach (var recipient in eligible)
        {
            if (excluded.Contains(recipient.Actor.UserId) ||
                !authz.Can(recipient.Actor, ResourceAction.ConversationRead, recipient.Resource))
            {
                continue;
            }

            notifications.Add(new NotificationEntity
            {
                Id = Guid.CreateVersion7(),
                OutboxMessageId = message.Id,
                Kind = message.Kind,
                RecipientId = recipient.Actor.UserId,
                OrganizationId = message.OrganizationId,
                ConversationId = conversation.Id,
                ResourceId = message.ResourceId,
                CreatedAt = message.CreatedAt
            });
        }

        db.Notifications.AddRange(notifications);
        await PushOutboxHandler.EnqueueAsync(db, push, notifications, ct);
        await EmailDigestHandler.EnqueueAsync(db, digest, notifications, ct);
    }

    private Task<MentionSource?> FindSourceAsync(OutboxMessageEntity message, CancellationToken ct) => message.Kind switch
    {
        MentionOutbox.MessageKind or MentionOutbox.MessageActivity => db.Messages.AsNoTracking()
            .Where(item => item.Id == message.ResourceId && item.DeletedAt == null)
            .Select(item => new MentionSource(item.ConversationId, item.Seq, item.SenderId, item.ParentMessageId,
                item.ParentMessageId == null ? item.Seq : db.Messages.Where(root => root.Id == item.ParentMessageId &&
                    root.ConversationId == item.ConversationId).Select(root => (long?)root.Seq).FirstOrDefault()))
            .SingleOrDefaultAsync(ct),
        MentionOutbox.PostKind or MentionOutbox.PostActivity => db.ChannelPosts.AsNoTracking()
            .Where(post => post.Id == message.ResourceId && post.OrganizationId == message.OrganizationId && post.DeletedAt == null)
            .Select(post => new MentionSource(post.ConversationId, null, post.AuthorUserId, null, null)).SingleOrDefaultAsync(ct),
        MentionOutbox.CommentKind or MentionOutbox.CommentActivity => (
            from comment in db.PostComments.AsNoTracking()
            join post in db.ChannelPosts.AsNoTracking() on comment.PostId equals post.Id
            where comment.Id == message.ResourceId && comment.DeletedAt == null &&
                  post.OrganizationId == message.OrganizationId && post.DeletedAt == null
            select new MentionSource(post.ConversationId, null, comment.AuthorUserId, null, null)).SingleOrDefaultAsync(ct),
        _ => throw new InvalidOperationException("Unsupported mention event kind.")
    };

    private async Task<IReadOnlyList<MentionRecipient>> FindEligibleAsync(
        OutboxMessageEntity message,
        MentionSource source,
        ConversationEntity conversation,
        Guid[] recipients,
        IQueryable<NotificationEntity> existing,
        CancellationToken ct)
    {
        if (conversation.Kind == ConversationKind.Channel)
        {
            var community = await db.Communities.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == conversation.CommunityId && item.OrganizationId == message.OrganizationId, ct);
            if (community is null)
            {
                return [];
            }

            var members = await AnnouncementRecipients.Eligible(db, community, message.CreatedAt)
                .Where(member => recipients.Contains(member.Membership.Id) &&
                    !existing.Any(notification => notification.RecipientId == member.Membership.Id)).ToListAsync(ct);
            return members.Select(member => new MentionRecipient(member.Membership.ToActorContext(),
                new ResourceContext(ResourceKind.CommunityChannel, community.OrganizationId,
                    MembershipRole: member.Role, IsOfficial: community.IsOfficial,
                    Visibility: community.Visibility, PostingPolicy: conversation.PostingPolicy))).ToArray();
        }

        if (conversation.Kind == ConversationKind.Self)
        {
            return [];
        }

        if (source.Seq is not { } seq || source.VisibleSeq is not { } visibleSeq)
        {
            return [];
        }

        var orgId = message.OrganizationId.GetValueOrDefault();
        var people = message.OrganizationId is null
            ? db.Users.AsNoTracking().Where(user => user.Status == AccountStatus.Active)
                .Select(user => new OrganizationMembership
                {
                    Id = user.Id, UserName = user.UserName!, DisplayName = user.DisplayName,
                    TrustLevel = TrustLevel.Unverified, OrgRole = OrgRole.Member
                })
            : db.OrganizationMemberships.AsNoTracking().Where(member => member.OrganizationId == orgId &&
                member.Status == AccountStatus.Active && member.Account.Status == AccountStatus.Active);
        var isDm = conversation.Kind == ConversationKind.Dm;
        var participants = await (
            from member in db.ConversationMembers.AsNoTracking()
            join person in people on member.UserId equals person.Id
            where member.ConversationId == conversation.Id && member.LeftAt == null &&
                  member.VisibleFromSeq < visibleSeq && recipients.Contains(person.Id) &&
                  (source.ParentMessageId == null
                    ? member.LastReadSeq < seq
                    : !db.ThreadSubscriptions.Any(subscription => subscription.ParentMessageId == source.ParentMessageId &&
                        subscription.UserId == member.UserId && subscription.LastReadReplySeq >= seq) &&
                        (message.Kind != MentionOutbox.MessageActivity || db.ThreadSubscriptions.Any(subscription =>
                            subscription.ParentMessageId == source.ParentMessageId && subscription.UserId == member.UserId &&
                            subscription.IsFollowing))) &&
                  !existing.Any(notification => notification.RecipientId == person.Id) &&
                  (!isDm || !db.UserBlocks.Any(block => block.OrganizationId == orgId &&
                      ((block.BlockedUserId == source.AuthorId && block.BlockerUserId == person.Id) ||
                       (block.BlockerUserId == source.AuthorId && block.BlockedUserId == person.Id))))
            select new { person.Id, person.TrustLevel, person.OrgRole, member.Role }).ToListAsync(ct);

        var kind = isDm ? ResourceKind.Dm : ResourceKind.GroupChat;
        return participants.Select(member => new MentionRecipient(
            new ActorContext(member.Id, orgId, AccountStatus.Active, member.TrustLevel, member.OrgRole),
            new ResourceContext(kind, orgId, MembershipRole: member.Role))).ToArray();
    }

    private sealed record MentionSource(Guid ConversationId, long? Seq, Guid AuthorId, Guid? ParentMessageId, long? VisibleSeq);
    private sealed record MentionRecipient(ActorContext Actor, ResourceContext Resource);
}
