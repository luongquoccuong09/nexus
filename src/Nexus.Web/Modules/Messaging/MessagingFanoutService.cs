using Nexus.Web.Modules.Org;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.People;

namespace Nexus.Web.Modules.Messaging;

public sealed class MessagingFanoutService(
    NexusDbContext db,
    IHubContext<MessagingHub> hubContext)
{
    public Task NotifyConversationChangedAsync(
        Guid conversationId,
        long changeSeq,
        CancellationToken cancellationToken = default) =>
        NotifyConversationChangedInternalAsync(conversationId, changeSeq, null, null, null, cancellationToken);

    public Task NotifyConversationChangedAsync(
        Guid conversationId,
        long changeSeq,
        ConversationKind conversationKind,
        Guid? communityId,
        object? message = null,
        CancellationToken cancellationToken = default) =>
        NotifyConversationChangedInternalAsync(conversationId, changeSeq, conversationKind, communityId, message, cancellationToken);

    private async Task NotifyConversationChangedInternalAsync(
        Guid conversationId,
        long changeSeq,
        ConversationKind? conversationKind,
        Guid? communityId,
        object? message,
        CancellationToken cancellationToken)
    {
        var recipientIds = await ResolveAuthorizedRecipientsAsync(conversationId, conversationKind, communityId, cancellationToken);
        if (recipientIds.Count == 0) return;

        var payload = new { conversationId, changeSeq, message };
        var groups = recipientIds.Select(id => MessagingHub.UserGroupName(db.OrganizationScopeId, id)).ToArray();
        await hubContext.Clients.Groups(groups).SendAsync("ConversationChanged", payload, cancellationToken);
    }

    public Task NotifyCommunityFeedChangedAsync(
        Guid communityId,
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        NotifyCommunityFeedChangedAsync(communityId, conversationId, null, cancellationToken);

    public async Task NotifyCommunityFeedChangedAsync(
        Guid communityId,
        Guid conversationId,
        object? delta,
        CancellationToken cancellationToken = default)
    {
        var recipientIds = await ResolveCommunityRecipientsAsync(communityId, cancellationToken);
        if (recipientIds.Count == 0) return;

        var payload = new { communityId, conversationId, delta };
        var groups = recipientIds.Select(id => MessagingHub.UserGroupName(db.OrganizationScopeId, id)).ToArray();
        await hubContext.Clients.Groups(groups).SendAsync("CommunityFeedChanged", payload, cancellationToken);
    }

    public async Task NotifyReadStateUpdatedAsync(
        Guid userId,
        Guid conversationId,
        long lastReadSeq,
        CancellationToken cancellationToken = default)
    {
        var payload = new { conversationId, lastReadSeq };
        await hubContext.Clients
            .Group(MessagingHub.UserGroupName(db.OrganizationScopeId, userId))
            .SendAsync("ReadStateUpdated", payload, cancellationToken);
    }

    public async Task NotifyCommunityOwnershipChangedAsync(Guid communityId, CancellationToken ct)
    {
        var mainFeed = await db.Conversations.AsNoTracking()
            .Where(c => c.CommunityId == communityId && c.IsCommunityMainFeed && c.OrganizationId == db.ConversationOrganizationId)
            .Select(c => (Guid?)c.Id)
            .SingleOrDefaultAsync(ct);
        if (mainFeed is { } id)
            await NotifyCommunityFeedChangedAsync(communityId, id, new { type = "ownership", communityId }, ct);
    }

    public Task NotifyConversationAccessRevokedAsync(Guid userId, Guid conversationId, CancellationToken ct) =>
        hubContext.Clients.Group(MessagingHub.UserGroupName(db.OrganizationScopeId, userId))
            .SendAsync("ConversationAccessRevoked", new { conversationId, organizationId = db.ConversationOrganizationId }, ct);

    public async Task NotifyAvailabilityChangedAsync(
        Guid userId,
        AvailabilityDto availability,
        bool isOnline,
        CancellationToken cancellationToken = default)
    {
        var myOrgIds = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.Id == userId && m.Status == AccountStatus.Active && m.Account.Status == AccountStatus.Active)
            .Select(m => m.OrganizationId)
            .ToListAsync(cancellationToken);

        var orgPeers = myOrgIds.Count == 0
            ? []
            : await db.OrganizationMemberships.AsNoTracking()
                .Where(m => myOrgIds.Contains(m.OrganizationId) && m.Id != userId &&
                            m.Status == AccountStatus.Active && m.Account.Status == AccountStatus.Active)
                .Select(m => new { m.OrganizationId, m.Id })
                .ToListAsync(cancellationToken);

        var personalPeerIds = await PersonalConnections.AcceptedContactIds(db, userId)
            .Union(
                db.ConversationMembers.AsNoTracking()
                    .Where(mine => mine.UserId == userId && mine.LeftAt == null)
                    .Join(
                        db.Conversations.AsNoTracking().Where(c => c.OrganizationId == null),
                        mine => mine.ConversationId,
                        c => c.Id,
                        (mine, _) => mine.ConversationId)
                    .Join(
                        db.ConversationMembers.AsNoTracking().Where(peer => peer.UserId != userId && peer.LeftAt == null),
                        convId => convId,
                        peer => peer.ConversationId,
                        (_, peer) => peer.UserId)
                    .Where(peerId => !db.UserBlocks.Any(b => b.OrganizationId == Guid.Empty &&
                        ((b.BlockerUserId == userId && b.BlockedUserId == peerId) ||
                         (b.BlockerUserId == peerId && b.BlockedUserId == userId)))))
            .Join(
                db.Users.AsNoTracking().Where(u => u.Status == AccountStatus.Active),
                id => id,
                u => u.Id,
                (id, _) => id)
            .Distinct()
            .ToListAsync(cancellationToken);

        var groups = new HashSet<string>(StringComparer.Ordinal)
        {
            MessagingHub.UserGroupName(Guid.Empty, userId)
        };
        foreach (var orgId in myOrgIds)
        {
            groups.Add(MessagingHub.UserGroupName(orgId, userId));
        }
        foreach (var peerId in personalPeerIds)
        {
            groups.Add(MessagingHub.UserGroupName(Guid.Empty, peerId));
        }
        foreach (var peer in orgPeers)
        {
            groups.Add(MessagingHub.UserGroupName(peer.OrganizationId, peer.Id));
        }

        var payload = new
        {
            userId,
            availability,
            isOnline,
            updatedAt = DateTimeOffset.UtcNow
        };

        var groupList = groups.ToArray();
        const int chunkSize = 500;
        for (var offset = 0; offset < groupList.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, groupList.Length - offset);
            var batch = offset == 0 && count == groupList.Length
                ? groupList
                : groupList[offset..(offset + count)];
            await hubContext.Clients.Groups(batch).SendAsync("AvailabilityChanged", payload, cancellationToken);
        }
    }

    private async Task<List<Guid>> ResolveAuthorizedRecipientsAsync(
        Guid conversationId,
        ConversationKind? knownKind,
        Guid? knownCommunityId,
        CancellationToken ct)
    {
        var kind = knownKind;
        var communityId = knownCommunityId;

        if (!kind.HasValue)
        {
            var conv = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId)
                .Select(c => new { c.Kind, c.CommunityId })
                .SingleOrDefaultAsync(ct);

            if (conv is null)
            {
                return [];
            }

            kind = conv.Kind;
            communityId = conv.CommunityId;
        }

        if (kind == ConversationKind.Channel && communityId.HasValue)
        {
            return await ResolveCommunityRecipientsAsync(communityId.Value, ct);
        }

        if (kind == ConversationKind.Self)
        {
            var ownerId = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == conversationId && c.OrganizationId == null && c.Kind == ConversationKind.Self)
                .Join(db.Users.AsNoTracking().Where(u => u.Status == AccountStatus.Active), c => c.CreatedByUserId, u => u.Id, (c, _) => c.CreatedByUserId)
                .SingleOrDefaultAsync(ct);
            return ownerId == Guid.Empty ? [] : [ownerId];
        }

        return await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .Join(db.ActiveScopePeople.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u.Id)
            .ToListAsync(ct);
    }

    private async Task<List<Guid>> ResolveCommunityRecipientsAsync(Guid communityId, CancellationToken ct)
    {
        var isOfficial = await db.Communities.AsNoTracking()
            .Where(c => c.Id == communityId && c.OrganizationId == db.OrganizationScopeId)
            .Select(c => c.IsOfficial)
            .SingleOrDefaultAsync(ct);

        var query = db.CommunityMembers.AsNoTracking()
            .Where(m => m.CommunityId == communityId && m.LeftAt == null)
            .Join(
                db.ActiveScopePeople.AsNoTracking(),
                m => m.UserId,
                u => u.Id,
                (m, u) => new { m.UserId, u.TrustLevel });

        if (isOfficial)
        {
            query = query.Where(x => x.TrustLevel == TrustLevel.Verified);
        }

        return await query.Select(x => x.UserId).ToListAsync(ct);
    }
}
