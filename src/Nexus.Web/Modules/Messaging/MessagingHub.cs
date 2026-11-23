using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;

namespace Nexus.Web.Modules.Messaging;

/// <summary>
/// Realtime messaging hub using strictly user-scoped groups (`user:{id}`) (M2.4 / M2.10 / SEC-07).
/// Clients never join conversation groups directly; the server resolves authorized recipients from PostgreSQL.
/// </summary>
public sealed class MessagingHub(
    NexusDbContext db,
    IAuthorizationService authz,
    IPresenceTracker presence) : Hub
{
    public static string UserGroupName(Guid userId) => $"user:{userId:D}";

    public override async Task OnConnectedAsync()
    {
        if (!Context.User.TryGetUserId(out var userId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId), Context.ConnectionAborted);
        presence.OnConnected(userId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User.TryGetUserId(out var userId))
        {
            presence.OnDisconnected(userId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task SendTyping(Guid conversationId)
    {
        if (!Context.User.TryGetUserId(out var userId))
        {
            throw new HubException("Unauthorized.");
        }

        var actor = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .SingleOrDefaultAsync(Context.ConnectionAborted);

        if (actor is null)
        {
            throw new HubException("Unauthorized.");
        }

        var conv = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId && c.OrganizationId == actor.OrganizationId)
            .SingleOrDefaultAsync(Context.ConnectionAborted);

        var member = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null)
            .SingleOrDefaultAsync(Context.ConnectionAborted);

        var kind = conv?.Kind == ConversationKind.Dm ? ResourceKind.Dm : ResourceKind.GroupChat;
        var allowed = conv is not null && authz.Can(
            actor.ToActorContext(),
            ResourceAction.MessageSend,
            new ResourceContext(kind, actor.OrganizationId, MembershipRole: member?.Role));

        if (!allowed)
        {
            throw new HubException("Forbidden.");
        }

        if (!presence.TryAcquireTypingSlot(userId, conversationId, TimeSpan.FromSeconds(2)))
        {
            return;
        }

        var recipientIds = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.LeftAt == null && m.UserId != userId)
            .Select(m => m.UserId)
            .ToListAsync(Context.ConnectionAborted);

        var payload = new
        {
            conversationId,
            userId,
            displayName = actor.DisplayName,
            at = DateTimeOffset.UtcNow
        };

        foreach (var recipientId in recipientIds)
        {
            await Clients.Group(UserGroupName(recipientId))
                .SendAsync("TypingIndicator", payload, Context.ConnectionAborted);
        }
    }
}
