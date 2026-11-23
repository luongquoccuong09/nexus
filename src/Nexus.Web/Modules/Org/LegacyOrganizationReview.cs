using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;

namespace Nexus.Web.Modules.Org;

public static class LegacyOrganizationReview
{
    public sealed record OwnershipTransfer(Guid ResourceId, Guid NewOwnerId);
    public sealed record Manifest(Guid OrganizationId, Guid OwnerId, Guid[] ApprovedAccountIds,
        OwnershipTransfer[]? Communities, OwnershipTransfer[]? Groups);

    public static async Task ReviewAsync(NexusDbContext db, string manifestFile, bool apply, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(manifestFile);
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(input, JsonSerializerOptions.Web, ct)
            ?? throw new InvalidOperationException("A legacy Organization review manifest is required.");
        var approved = manifest.ApprovedAccountIds?.ToHashSet() ?? [];
        if (!approved.Contains(manifest.OwnerId)) throw new InvalidOperationException("The approved list must include the Organization owner.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OrganizationManagementEndpoints.LockOrganizationAsync(db, manifest.OrganizationId, ct);
        var organization = await db.Organizations.SingleOrDefaultAsync(o => o.Id == manifest.OrganizationId && o.LegacyReviewPending, ct)
            ?? throw new InvalidOperationException("The Organization does not have a pending legacy review.");
        var members = await db.OrganizationMemberships.Include(m => m.Account)
            .Where(m => m.OrganizationId == organization.Id).ToListAsync(ct);
        var memberById = members.ToDictionary(m => m.Id);
        if (approved.Any(id => !memberById.TryGetValue(id, out var member) || member.Account.Status != AccountStatus.Active))
            throw new InvalidOperationException("Approved accounts must be active legacy users in this Organization.");
        if (!memberById[manifest.OwnerId].Account.TwoFactorEnabled)
            throw new InvalidOperationException("The Organization owner must have TOTP enabled.");

        var communityTransfers = (manifest.Communities ?? []).ToDictionary(t => t.ResourceId, t => t.NewOwnerId);
        var groupTransfers = (manifest.Groups ?? []).ToDictionary(t => t.ResourceId, t => t.NewOwnerId);
        var communityMembers = await db.CommunityMembers.Where(m => m.LeftAt == null &&
            db.Communities.Any(c => c.Id == m.CommunityId && c.OrganizationId == organization.Id)).ToListAsync(ct);
        var groupMembers = await db.ConversationMembers.Where(m => m.LeftAt == null && db.Conversations.Any(c =>
            c.Id == m.ConversationId && c.OrganizationId == organization.Id && c.Kind == ConversationKind.Group)).ToListAsync(ct);

        ValidateTransfers(communityMembers.Select(m => (m.CommunityId, m.UserId, m.Role)), communityTransfers, approved, "Community");
        ValidateTransfers(groupMembers.Select(m => (m.ConversationId, m.UserId, m.Role)), groupTransfers, approved, "group chat");
        var officialIds = await db.Communities.Where(c => c.OrganizationId == organization.Id && c.IsOfficial).Select(c => c.Id).ToListAsync(ct);
        if (communityTransfers.Any(t => officialIds.Contains(t.Key) && memberById[t.Value].TrustLevel != TrustLevel.Verified))
            throw new InvalidOperationException("An Official Community must transfer to a Verified member.");

        var now = DateTimeOffset.UtcNow;
        foreach (var member in communityMembers.Where(m => communityTransfers.ContainsKey(m.CommunityId)))
        {
            if (member.UserId == communityTransfers[member.CommunityId]) member.Role = ResourceRole.Owner;
            else if (member.Role == ResourceRole.Owner) member.Role = ResourceRole.Member;
        }
        foreach (var member in groupMembers.Where(m => groupTransfers.ContainsKey(m.ConversationId)))
        {
            if (member.UserId == groupTransfers[member.ConversationId]) member.Role = ResourceRole.Owner;
            else if (member.Role == ResourceRole.Owner) member.Role = ResourceRole.Member;
        }

        await db.SaveChangesAsync(ct);
        var excluded = members.Where(m => !approved.Contains(m.Id)).Select(m => m.Id).ToArray();
        if (!await OrganizationManagementEndpoints.RemoveResourceMembershipsAsync(db, organization.Id, excluded, now, ct))
            throw new InvalidOperationException("Every excluded resource owner needs an ownership transfer.");
        foreach (var member in members.Where(m => approved.Contains(m.Id)))
        {
            member.Status = AccountStatus.Active;
            member.IsOwner = member.Id == manifest.OwnerId;
            if (member.IsOwner) member.OrgRole = OrgRole.SystemAdmin;
        }
        organization.LegacyReviewPending = false;
        OrganizationManagementEndpoints.Audit(db, organization.Id, manifest.OwnerId, "LEGACY_ORGANIZATION_REVIEWED", organization.Id, now);
        await db.SaveChangesAsync(ct);
        if (apply) await transaction.CommitAsync(ct);
        else await transaction.RollbackAsync(ct);
    }

    private static void ValidateTransfers(IEnumerable<(Guid ResourceId, Guid UserId, ResourceRole Role)> source,
        Dictionary<Guid, Guid> transfers, HashSet<Guid> approved, string resourceType)
    {
        var members = source.ToLookup(m => m.ResourceId);
        foreach (var (resourceId, newOwnerId) in transfers)
        {
            if (!approved.Contains(newOwnerId) || !members[resourceId].Any(m => m.UserId == newOwnerId))
                throw new InvalidOperationException($"Select an approved existing member to own {resourceType} {resourceId}.");
        }
        if (members.SelectMany(group => group).Any(m => m.Role == ResourceRole.Owner && !approved.Contains(m.UserId) && !transfers.ContainsKey(m.ResourceId)))
            throw new InvalidOperationException($"Select a replacement for every excluded {resourceType} owner.");
    }
}
