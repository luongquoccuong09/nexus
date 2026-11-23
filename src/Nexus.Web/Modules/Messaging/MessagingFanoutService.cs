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
    public async Task NotifyConversationChangedAsync(
        Guid conversationId,
        long changeSeq,
        CancellationToken cancellationToken = default)
    {
        var recipientIds = await ResolveAuthorizedRecipientsAsync(conversationId, cancellationToken);

        var payload = new { conversationId, changeSeq };
        foreach (var recipientId in recipientIds)
        {
            await hubContext.Clients
                .Group(MessagingHub.UserGroupName(db.OrganizationScopeId, recipientId))
                .SendAsync("ConversationChanged", payload, cancellationToken);
        }
    }

    public async Task NotifyCommunityFeedChangedAsync(
        Guid communityId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var recipientIds = await ResolveCommunityRecipientsAsync(communityId, cancellationToken);

        var payload = new { communityId, conversationId };
        foreach (var recipientId in recipientIds)
        {
            await hubContext.Clients
                .Group(MessagingHub.UserGroupName(db.OrganizationScopeId, recipientId))
                .SendAsync("CommunityFeedChanged", payload, cancellationToken);
        }
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

    private async Task<List<Guid>> ResolveAuthorizedRecipientsAsync(Guid conversationId, CancellationToken ct)
    {
        var conv = await db.Conversations.AsNoTracking()
            .Where(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId)
            .Select(c => new { c.Kind, c.CommunityId })
            .SingleOrDefaultAsync(ct);

        if (conv is null)
        {
            return [];
        }

        if (conv.Kind == ConversationKind.Channel && conv.CommunityId.HasValue)
        {
            return await ResolveCommunityRecipientsAsync(conv.CommunityId.Value, ct);
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
