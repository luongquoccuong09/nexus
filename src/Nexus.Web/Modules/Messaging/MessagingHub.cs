using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

/// <summary>
/// Realtime messaging hub using groups scoped to a chat context and user (M2.4 / M2.10 / SEC-07).
/// Clients never join conversation groups directly; the server resolves authorized recipients from PostgreSQL.
/// </summary>
public sealed class MessagingHub(
    NexusDbContext db,
    IAuthorizationService authz,
    IPresenceTracker presence,
    MessagingFanoutService fanout) : Hub
{
    public static string UserGroupName(Guid organizationId, Guid userId) => $"scope:{organizationId:D}:user:{userId:D}";

    private Guid ScopeId => OrganizationScope.GetId(Context.GetHttpContext());

    public override async Task OnConnectedAsync()
    {
        if (!Context.User.TryGetUserId(out var userId) || !await SetScopeAsync(userId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(ScopeId, userId), Context.ConnectionAborted);
        if (db.IsPersonalScope)
        {
            await SubscribeActiveOrganizationsAsync(userId, Context.ConnectionAborted);
        }

        var becameOnline = presence.OnConnected(ScopeId, userId, Context.ConnectionId);
        await base.OnConnectedAsync();

        if (becameOnline)
        {
            var account = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId && u.Status == AccountStatus.Active)
                .Select(u => new { u.AvailabilityState, u.AvailabilityExpiresAt })
                .SingleOrDefaultAsync(Context.ConnectionAborted);
            if (account is not null)
            {
                var availability = AvailabilityResolver.Resolve(account.AvailabilityState, account.AvailabilityExpiresAt, DateTimeOffset.UtcNow);
                await fanout.NotifyAvailabilityChangedAsync(userId, availability, isOnline: true, Context.ConnectionAborted);
            }
        }
    }

    public async Task RefreshOrganizationSubscriptions()
    {
        if (!Context.User.TryGetUserId(out var userId) || !await SetScopeAsync(userId) || !db.IsPersonalScope)
        {
            return;
        }

        await SubscribeActiveOrganizationsAsync(userId, Context.ConnectionAborted);
    }

    private async Task SubscribeActiveOrganizationsAsync(Guid userId, CancellationToken ct)
    {
        var orgIds = await db.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.Id == userId && m.Status == AccountStatus.Active)
            .Select(m => m.OrganizationId)
            .ToListAsync(ct);

        foreach (var orgId in orgIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(orgId, userId), ct);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User.TryGetUserId(out var userId))
        {
            var becameOffline = presence.OnDisconnected(ScopeId, userId, Context.ConnectionId);
            if (becameOffline)
            {
                try
                {
                    var account = await db.Users.AsNoTracking()
                        .Where(u => u.Id == userId && u.Status == AccountStatus.Active)
                        .Select(u => new { u.AvailabilityState, u.AvailabilityExpiresAt })
                        .SingleOrDefaultAsync(CancellationToken.None);
                    if (account is not null)
                    {
                        var availability = AvailabilityResolver.Resolve(account.AvailabilityState, account.AvailabilityExpiresAt, DateTimeOffset.UtcNow);
                        await fanout.NotifyAvailabilityChangedAsync(userId, availability, isOnline: false, CancellationToken.None);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task SendTyping(Guid conversationId)
    {
        if (!Context.User.TryGetUserId(out var userId))
        {
            throw new HubException("Unauthorized.");
        }

        if (!await SetScopeAsync(userId)) throw new HubException("Forbidden.");
        var actor = db.OrganizationActor;

        if (actor is null)
        {
            throw new HubException("Unauthorized.");
        }

        var conv = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId)
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
            .Join(db.ActiveScopePeople.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => u.Id)
            .Where(recipientId => kind != ResourceKind.Dm || !db.UserBlocks.Any(block =>
                block.OrganizationId == ScopeId &&
                ((block.BlockerUserId == userId && block.BlockedUserId == recipientId) ||
                 (block.BlockerUserId == recipientId && block.BlockedUserId == userId))))
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
            await Clients.Group(UserGroupName(ScopeId, recipientId))
                .SendAsync("TypingIndicator", payload, Context.ConnectionAborted);
        }
    }
    private async Task<bool> SetScopeAsync(Guid userId)
    {
        var http = Context.GetHttpContext();
        db.IsPersonalScope = OrganizationScope.IsPersonal(http);
        db.OrganizationScopeId = ScopeId;
        db.OrganizationActor = null;
        if (!db.IsPersonalScope && ScopeId == Guid.Empty) return false;
        db.OrganizationActor = db.IsPersonalScope
            ? await db.FindPersonalActorAsync(userId, Context.ConnectionAborted)
            : await db.FindOrganizationActorAsync(userId, Context.ConnectionAborted);
        return db.OrganizationActor is not null;
    }
}
