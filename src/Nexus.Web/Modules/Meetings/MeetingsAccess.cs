using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Calendar;
using Nexus.Web.Modules.Calls;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Meetings;

public sealed class MeetingsAccess(NexusDbContext db, IAuthorizationService authz)
{
    public async Task<bool> CanConnectSignalAsync(Guid grantId, string roomName, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (principal.Identity?.IsAuthenticated != true || !principal.TryGetUserId(out var userId) ||
            principal.GetSessionId() is not { } sessionId)
            return false;

        var admission = await (
            from grant in db.MeetingGrants.AsNoTracking()
            join room in db.Meetings.AsNoTracking() on grant.MeetingId equals room.Id
            where grant.Id == grantId && grant.UserId == userId && grant.SessionId == sessionId && grant.RevokedAt == null
            select new { Grant = grant, Room = room }).SingleOrDefaultAsync(ct);
        return admission is not null && string.Equals(admission.Room.RoomName, roomName, StringComparison.Ordinal) &&
            await CanUseGrantAsync(admission.Grant, admission.Room, ct);
    }

    internal async Task<bool> CanUseGrantAsync(MeetingGrantEntity grant, MeetingEntity room, CancellationToken ct)
    {
        if (grant.RevokedAt != null || grant.SessionId is not { } sessionId ||
            !await IsActiveSessionAsync(grant.UserId, sessionId, ct))
            return false;

        var actor = await FindActorAsync(grant.UserId, room.OrganizationId, ct);
        return actor is not null && await CanJoinAsync(room, actor, ct, sessionId);
    }

    internal Task<bool> IsActiveSessionAsync(Guid userId, string sessionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return (from session in db.AppSessions.AsNoTracking()
                join account in db.Users.AsNoTracking() on session.UserId equals account.Id
                where session.Id == sessionId && session.UserId == userId && session.RevokedAt == null &&
                    (session.ExpiresAt == null || session.ExpiresAt > now) &&
                    account.Status == AccountStatus.Active && session.SecurityStamp == account.SecurityStamp
                select session.Id).AnyAsync(ct);
    }

    internal Task<OrganizationMembership?> FindActorAsync(Guid userId, Guid? organizationId, CancellationToken ct) =>
        organizationId is null
            ? db.FindPersonalActorAsync(userId, ct)
            : db.OrganizationMemberships.AsNoTracking().SingleOrDefaultAsync(member => member.Id == userId &&
                member.OrganizationId == organizationId && member.Status == AccountStatus.Active &&
                member.Account.Status == AccountStatus.Active, ct);

    internal async Task<ResolvedConversationAccess?> FindConversationAsync(
        Guid conversationId, OrganizationMembership actor, bool starting, CancellationToken ct)
    {
        var scope = actor.OrganizationId == Guid.Empty ? (Guid?)null : actor.OrganizationId;
        var item = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(conversation => conversation.Id == conversationId && conversation.OrganizationId == scope, ct);
        if (item is null || item.Kind == ConversationKind.Self) return null;

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, item, ct);
        return access is not null && !access.Resource.IsBlocked && !access.Resource.IsArchived &&
            authz.Can(actor.ToActorContext(), starting ? ResourceAction.MessageSend : ResourceAction.ConversationRead, access.Resource)
            ? access
            : null;
    }

    internal async Task<bool> CanJoinAsync(MeetingEntity room, OrganizationMembership actor, CancellationToken ct, string? sessionId = null)
    {
        if (room.EndedAt is not null ||
            !authz.Can(actor.ToActorContext(), ResourceAction.MeetingJoin, Resource(room, actor.Id)) ||
            await db.MeetingBans.AnyAsync(ban => ban.MeetingId == room.Id && ban.UserId == actor.Id, ct))
            return false;

        if (room.CalendarEventId is { } eventId)
            return await CalendarAccess.VisibleTo(db, actor.Id).AnyAsync(item => item.Id == eventId &&
                item.OrganizationId == room.OrganizationId && item.IsOnlineMeeting && item.CanceledAt == null, ct);

        if (room.DirectCallId is { } callId)
        {
            var call = await DirectCallAccess.Eligible(db).SingleOrDefaultAsync(item => item.Id == callId &&
                item.OrganizationId == room.OrganizationId && item.State == DirectCallState.Accepted, ct);
            if (call is null || !DirectCallAccess.MatchesSession(call, actor.Id, sessionId)) return false;
            var peer = call.CallerId == actor.Id ? call.CalleeId : call.CallerId;
            var peerSession = call.CallerId == actor.Id ? call.CalleeSessionId : call.CallerSessionId;
            return peerSession is not null && await IsActiveSessionAsync(peer, peerSession, ct);
        }

        return room.ConversationId is { } conversationId && await FindConversationAsync(conversationId, actor, false, ct) is not null;
    }

    internal bool CanManage(MeetingEntity room, OrganizationMembership actor) =>
        room.DirectCallId is null && authz.Can(actor.ToActorContext(), ResourceAction.MeetingManage, Resource(room, actor.Id));

    private static ResourceContext Resource(MeetingEntity room, Guid actorId) =>
        new(ResourceKind.Meeting, room.OrganizationId ?? Guid.Empty, ResourceRole.Member, TargetIsOwner: room.HostId == actorId);
}
