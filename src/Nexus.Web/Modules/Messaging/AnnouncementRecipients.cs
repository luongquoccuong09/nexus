using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

internal static class AnnouncementRecipients
{
    public sealed class Recipient
    {
        public required OrganizationMembership Membership { get; init; }
        public ResourceRole Role { get; init; }
    }

    public static IQueryable<Recipient> Eligible(
        NexusDbContext db, CommunityEntity community, DateTimeOffset? joinedBefore = null) =>
        from member in db.CommunityMembers.AsNoTracking()
        join membership in db.OrganizationMemberships.AsNoTracking() on member.UserId equals membership.Id
        where member.CommunityId == community.Id && member.LeftAt == null &&
              (joinedBefore == null || (member.JoinedAt <= joinedBefore && membership.CreatedAt <= joinedBefore)) &&
              membership.OrganizationId == community.OrganizationId &&
              membership.Status == AccountStatus.Active && membership.Account.Status == AccountStatus.Active &&
              (!community.IsOfficial || membership.TrustLevel == TrustLevel.Verified)
        select new Recipient { Membership = membership, Role = member.Role };

    public static IQueryable<Recipient> Outstanding(
        NexusDbContext db, CommunityEntity community, Guid postId, DateTimeOffset requestedAt) =>
        Eligible(db, community, requestedAt).Where(recipient => !db.AnnouncementAcknowledgments.Any(
            acknowledgment => acknowledgment.PostId == postId && acknowledgment.UserId == recipient.Membership.Id));
}
