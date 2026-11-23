using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;

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
        var recipientIds = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        var payload = new { conversationId, changeSeq };
        foreach (var recipientId in recipientIds)
        {
            await hubContext.Clients
                .Group(MessagingHub.UserGroupName(recipientId))
                .SendAsync("ConversationChanged", payload, cancellationToken);
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
            .Group(MessagingHub.UserGroupName(userId))
            .SendAsync("ReadStateUpdated", payload, cancellationToken);
    }
}
