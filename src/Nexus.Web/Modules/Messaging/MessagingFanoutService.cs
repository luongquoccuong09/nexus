using Nexus.Web.Modules.Org;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;

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
