using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;

namespace Nexus.Web.Modules.Notifications;

internal static class NotificationQueries
{
    public static IQueryable<NotificationEntity> VisibleTo(NexusDbContext db, Guid recipientId) =>
        from notification in db.Notifications.AsNoTracking()
        join conversation in db.Conversations.AsNoTracking() on notification.ConversationId equals conversation.Id
        where notification.RecipientId == recipientId && notification.OrganizationId == conversation.OrganizationId &&
              (conversation.OrganizationId == null || db.OrganizationMemberships.Any(membership =>
                  membership.Id == recipientId && membership.OrganizationId == conversation.OrganizationId &&
                  membership.Status == AccountStatus.Active && membership.Account.Status == AccountStatus.Active))
        where conversation.Kind == ConversationKind.Channel
            ? db.Communities.Any(community => community.Id == conversation.CommunityId &&
                community.OrganizationId == conversation.OrganizationId &&
                db.CommunityMembers.Any(member => member.CommunityId == community.Id &&
                    member.UserId == recipientId && member.LeftAt == null) &&
                (!community.IsOfficial || db.OrganizationMemberships.Any(membership =>
                    membership.Id == recipientId && membership.OrganizationId == community.OrganizationId &&
                    membership.TrustLevel == TrustLevel.Verified)))
            : db.ConversationMembers.Any(member => member.ConversationId == conversation.Id &&
                member.UserId == recipientId && member.LeftAt == null &&
                ((notification.Kind != MentionOutbox.MessageKind && notification.Kind != MentionOutbox.MessageActivity) || db.Messages.Any(message =>
                    message.Id == notification.ResourceId && message.ConversationId == conversation.Id &&
                    message.DeletedAt == null && message.Seq > member.VisibleFromSeq)))
        where notification.Kind == MentionOutbox.MessageKind || notification.Kind == MentionOutbox.MessageActivity ||
              (notification.Kind == MentionOutbox.CommentKind || notification.Kind == MentionOutbox.CommentActivity
                  ? db.PostComments.Any(comment => comment.Id == notification.ResourceId && comment.DeletedAt == null &&
                      db.ChannelPosts.Any(post => post.Id == comment.PostId && post.ConversationId == conversation.Id && post.DeletedAt == null))
                  : db.ChannelPosts.Any(post => post.Id == notification.ResourceId && post.ConversationId == conversation.Id && post.DeletedAt == null))
        select notification;

    public static IQueryable<NotificationEntity> Unread(
        NexusDbContext db, IQueryable<NotificationEntity> visible, Guid recipientId) =>
        visible.Where(notification => notification.ReadAt == null &&
            ((notification.Kind != MentionOutbox.MessageKind && notification.Kind != MentionOutbox.MessageActivity) || db.Messages.Any(message =>
                message.Id == notification.ResourceId && db.ConversationMembers.Any(member =>
                    member.UserId == recipientId && member.ConversationId == notification.ConversationId &&
                    member.LeftAt == null && member.LastReadSeq < message.Seq))) &&
            (notification.Kind != AnnouncementReminderHandler.Kind || !db.AnnouncementAcknowledgments.Any(ack =>
                ack.PostId == notification.ResourceId && ack.UserId == recipientId)));

    public static IQueryable<NotificationEntity> UnreadMentions(NexusDbContext db, Guid recipientId) =>
        Unread(db, VisibleTo(db, recipientId), recipientId).Where(notification =>
            notification.Kind == MentionOutbox.MessageKind || notification.Kind == MentionOutbox.PostKind ||
            notification.Kind == MentionOutbox.CommentKind);
}
