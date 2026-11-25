using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Org;
using Nexus.Web.Modules.People;

namespace Nexus.Web.Modules.Calendar;

internal static class CalendarAccess
{
    internal static ResourceContext Scope(OrganizationMembership actor) =>
        new(ResourceKind.CalendarEvent, actor.OrganizationId, ResourceRole.Member);

    internal static async Task<ResourceContext> ResolveScopeAsync(
        NexusDbContext db, OrganizationMembership actor, Guid? communityId, CancellationToken ct)
    {
        if (communityId is null)
        {
            return Scope(actor);
        }

        var membership = await (
            from community in db.Communities.AsNoTracking()
            join member in db.CommunityMembers.AsNoTracking() on community.Id equals member.CommunityId
            where community.Id == communityId && community.OrganizationId == actor.OrganizationId &&
                member.UserId == actor.Id && member.LeftAt == null
            select new { member.Role, community.IsOfficial }).SingleOrDefaultAsync(ct);
        return new ResourceContext(ResourceKind.CalendarEvent, actor.OrganizationId,
            MembershipRole: membership?.Role, IsOfficial: membership?.IsOfficial == true);
    }

    internal static IQueryable<CalendarEventEntity> VisibleTo(NexusDbContext db, Guid userId)
    {
        var personalPeers = PersonalConnections.AllowedChatPeerIds(db, userId);
        return db.CalendarEvents.AsNoTracking()
            .Where(eventItem => db.Users.Any(user => user.Id == userId && user.Status == AccountStatus.Active))
            .Where(eventItem => eventItem.OrganizationId == null
                ? eventItem.OrganizerId == userId ||
                    personalPeers.Contains(eventItem.OrganizerId) &&
                    db.CalendarAttendees.Any(attendee => attendee.EventId == eventItem.Id && attendee.UserId == userId)
                : db.OrganizationMemberships.Any(membership => membership.OrganizationId == eventItem.OrganizationId &&
                    membership.Id == userId && membership.Status == AccountStatus.Active &&
                    (eventItem.CommunityId == null
                        ? eventItem.OrganizerId == userId ||
                            db.CalendarAttendees.Any(attendee => attendee.EventId == eventItem.Id && attendee.UserId == userId)
                        : db.Communities.Any(community => community.Id == eventItem.CommunityId &&
                            community.OrganizationId == eventItem.OrganizationId &&
                            (!community.IsOfficial || membership.TrustLevel == TrustLevel.Verified) &&
                            db.CommunityMembers.Any(member => member.CommunityId == community.Id &&
                                member.UserId == userId && member.LeftAt == null)))));
    }

    internal static IQueryable<Guid> EligibleInvitees(
        NexusDbContext db, Guid organizerId, Guid? organizationId, Guid? communityId)
    {
        if (organizationId is null)
        {
            var peers = PersonalConnections.AllowedChatPeerIds(db, organizerId);
            return db.Users.Where(user => user.Id != organizerId && user.Status == AccountStatus.Active &&
                peers.Contains(user.Id)).Select(user => user.Id);
        }

        return db.OrganizationMemberships
            .Where(member => member.OrganizationId == organizationId && member.Id != organizerId &&
                member.Status == AccountStatus.Active && member.Account.Status == AccountStatus.Active &&
                (communityId == null || db.Communities.Any(community => community.Id == communityId &&
                    community.OrganizationId == organizationId &&
                    (!community.IsOfficial || member.TrustLevel == TrustLevel.Verified) &&
                    db.CommunityMembers.Any(communityMember => communityMember.CommunityId == community.Id &&
                        communityMember.UserId == member.Id && communityMember.LeftAt == null))))
            .Select(member => member.Id);
    }
}
