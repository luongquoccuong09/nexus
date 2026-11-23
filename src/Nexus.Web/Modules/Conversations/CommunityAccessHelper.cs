using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Conversations;

public sealed record ResolvedConversationAccess(
    ConversationEntity Conversation,
    CommunityEntity? Community,
    ResourceContext Resource,
    long VisibleFromSeq);

/// <summary>
/// Resolves conversation authorization context uniformly for DMs, Group chats,
/// and Community main feeds / sub-channels (M5.1 / P0-D04).
/// </summary>
public static class CommunityAccessHelper
{
    public static async Task<ResolvedConversationAccess?> ResolveAsync(
        NexusDbContext db,
        NexusUser actor,
        Guid conversationId,
        CancellationToken ct)
    {
        var conv = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == actor.OrganizationId, ct);

        if (conv is null)
        {
            return null;
        }

        if (conv.Kind == ConversationKind.Channel && conv.CommunityId.HasValue)
        {
            var community = await db.Communities.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == conv.CommunityId.Value && c.OrganizationId == actor.OrganizationId, ct);
            if (community is null)
            {
                return null;
            }

            var cm = await db.CommunityMembers.AsNoTracking()
                .SingleOrDefaultAsync(m => m.CommunityId == community.Id && m.UserId == actor.Id && m.LeftAt == null, ct);

            var resource = new ResourceContext(
                ResourceKind.CommunityChannel,
                actor.OrganizationId,
                MembershipRole: cm?.Role,
                IsOfficial: community.IsOfficial,
                Visibility: community.Visibility,
                PostingPolicy: conv.PostingPolicy);

            return new ResolvedConversationAccess(conv, community, resource, VisibleFromSeq: 0);
        }

        var member = await db.ConversationMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actor.Id && m.LeftAt == null, ct);

        var isBlocked = false;
        if (conv.Kind == ConversationKind.Dm && member is not null)
        {
            var peerId = await db.ConversationMembers.AsNoTracking()
                .Where(m => m.ConversationId == conversationId && m.UserId != actor.Id)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync(ct);

            if (peerId != Guid.Empty)
            {
                isBlocked = await db.UserBlocks.AnyAsync(
                    b => (b.BlockerUserId == actor.Id && b.BlockedUserId == peerId) ||
                         (b.BlockerUserId == peerId && b.BlockedUserId == actor.Id),
                    ct);
            }
        }

        var kind = conv.Kind == ConversationKind.Dm ? ResourceKind.Dm : ResourceKind.GroupChat;
        var ctx = new ResourceContext(
            kind,
            actor.OrganizationId,
            MembershipRole: member?.Role,
            IsBlocked: isBlocked);

        return new ResolvedConversationAccess(conv, null, ctx, member?.VisibleFromSeq ?? 0);
    }
}
