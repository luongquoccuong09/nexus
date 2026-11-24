using Nexus.Web.Modules.Org;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Idempotency;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;

namespace Nexus.Web.Modules.Conversations;

public static partial class CommunityEndpoints
{
    private static async Task<IResult> HandleJoinCommunityAsync(
        HttpContext ctx,
        Guid communityId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await LockCommunityAsync(db, communityId, ct);

        if (community is null) return Results.NotFound(new { code = "community_not_found" });

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.CommunityDiscoverAndJoin,
            new ResourceContext(ResourceKind.Community, actor.OrganizationId, IsOfficial: community.IsOfficial, Visibility: community.Visibility));

        if (!allowed)
        {
            return Results.Forbid();
        }

        var existing = await db.CommunityMembers
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId, ct);

        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            db.CommunityMembers.Add(new CommunityMemberEntity
            {
                CommunityId = communityId,
                UserId = actorId,
                Role = ResourceRole.Member,
                JoinedAt = now
            });
            await db.SaveChangesAsync(ct);
        }
        else if (existing.LeftAt is not null)
        {
            existing.LeftAt = null;
            existing.Role = ResourceRole.Member;
            existing.JoinedAt = now;
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return Results.Ok(new { joined = true, communityId });
    }

    private static async Task<IResult> HandleListMembersAsync(
        HttpContext ctx,
        Guid communityId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IPresenceTracker presence,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.ConversationRead,
                new ResourceContext(ResourceKind.Community, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial, Visibility: community.Visibility)))
        {
            return Results.Forbid();
        }

        if (!PaginationGuard.TryDecodeCursor<PaginationGuard.NameCursor>(cursor, out var after) ||
            after is not null && (after.Name is null || after.Id == Guid.Empty)) return PaginationGuard.InvalidCursor();
        var rows = await db.CommunityMembers.AsNoTracking()
            .Where(m => m.CommunityId == communityId && m.LeftAt == null)
            .Join(db.OrganizationPeople.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new
            {
                userId = u.Id,
                username = u.UserName ?? string.Empty,
                displayName = u.DisplayName,
                isUnverified = u.TrustLevel == TrustLevel.Unverified,
                isActive = db.ActiveScopePeople.Any(active => active.Id == u.Id),
                role = m.Role.ToString(),
                joinedAt = m.JoinedAt,
                u.Account.AvailabilityState,
                u.Account.AvailabilityExpiresAt
            })
            .Where(x => after == null || string.Compare(x.displayName, after.Name) > 0 ||
                x.displayName == after.Name && x.userId.CompareTo(after.Id) > 0)
            .OrderBy(x => x.displayName).ThenBy(x => x.userId)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);
        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(x => new
        {
            x.userId,
            x.username,
            x.displayName,
            x.isUnverified,
            x.isActive,
            x.role,
            x.joinedAt,
            isOnline = presence.IsOnline(actor.OrganizationId, x.userId),
            availability = AvailabilityResolver.Resolve(x.AvailabilityState, x.AvailabilityExpiresAt, now)
        }).ToList();
        var nextCursor = hasMore ? PaginationGuard.EncodeCursor(new PaginationGuard.NameCursor(rows[^1].displayName, rows[^1].userId)) : null;
        return Results.Ok(new { items, nextCursor });
    }

    private static async Task<IResult> HandleAddMemberAsync(
        HttpContext ctx,
        Guid communityId,
        AddCommunityMemberRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await LockCommunityAsync(db, communityId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);
        var target = await db.OrganizationPeople.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == request.UserId && u.OrganizationId == actor.OrganizationId && u.Status == AccountStatus.Active, ct);

        if (community is null || target is null)
        {
            return Results.Forbid();
        }

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.CommunityAddOrRemoveMember,
            new ResourceContext(
                ResourceKind.Community,
                actor.OrganizationId,
                MembershipRole: myMember?.Role,
                IsOfficial: community.IsOfficial,
                TargetIsOwner: false,
                TargetIsVerified: target.TrustLevel == TrustLevel.Verified));

        if (!allowed)
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await db.CommunityMembers
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == target.Id, ct);

        if (existing is null)
        {
            db.CommunityMembers.Add(new CommunityMemberEntity
            {
                CommunityId = communityId,
                UserId = target.Id,
                Role = ResourceRole.Member,
                JoinedAt = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { added = true });
        }

        if (existing.LeftAt is not null)
        {
            existing.LeftAt = null;
            existing.Role = ResourceRole.Member;
            existing.JoinedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { added = true });
        }

        return Results.Ok(new { added = false, alreadyMember = true });
    }

    private static async Task<IResult> HandleRemoveMemberAsync(
        HttpContext ctx,
        Guid communityId,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await LockCommunityAsync(db, communityId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);
        var targetMember = await db.CommunityMembers
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == targetUserId && m.LeftAt == null, ct);
        var targetUser = await db.OrganizationPeople.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == targetUserId && u.OrganizationId == actor.OrganizationId, ct);

        if (community is null || targetMember is null || targetUser is null)
        {
            return Results.Forbid();
        }

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.CommunityAddOrRemoveMember,
            new ResourceContext(
                ResourceKind.Community,
                actor.OrganizationId,
                MembershipRole: myMember?.Role,
                IsOfficial: community.IsOfficial,
                TargetIsOwner: targetMember.Role == ResourceRole.Owner,
                TargetIsVerified: targetUser.TrustLevel == TrustLevel.Verified));

        if (!allowed || targetUserId == actorId)
        {
            return Results.Forbid();
        }

        targetMember.LeftAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await SidebarEndpoints.RemoveSidebarItemAsync(db, targetUserId, SidebarEndpoints.CommunitiesSurface, communityId, ct);
        await People.PeopleTagEndpoints.RemoveCommunityTagMembershipsAsync(db, communityId, targetUserId, ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> HandleSetAdminAsync(
        HttpContext ctx,
        Guid communityId,
        Guid targetUserId,
        SetCommunityAdminRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await LockCommunityAsync(db, communityId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);
        var targetMember = await db.CommunityMembers
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == targetUserId && m.LeftAt == null, ct);
        var targetUser = await db.OrganizationPeople.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == targetUserId && u.OrganizationId == actor.OrganizationId, ct);

        if (community is null || targetMember is null || targetUser is null || targetMember.Role == ResourceRole.Owner)
        {
            return Results.Forbid();
        }

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.CommunityManageAdmins,
            new ResourceContext(
                ResourceKind.Community,
                actor.OrganizationId,
                MembershipRole: myMember?.Role,
                IsOfficial: community.IsOfficial,
                TargetIsSelf: targetUserId == actorId,
                TargetIsActive: targetUser.Status == AccountStatus.Active,
                TargetIsVerified: targetUser.TrustLevel == TrustLevel.Verified));

        if (!allowed)
        {
            return Results.Forbid();
        }

        targetMember.Role = request.IsAdmin ? ResourceRole.Admin : ResourceRole.Member;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(new { userId = targetUserId, role = targetMember.Role.ToString() });
    }

    private static async Task<IResult> HandleLeaveCommunityAsync(
        HttpContext ctx,
        Guid communityId,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockCommunityAsync(db, communityId, ct) is null) return Results.Forbid();
        var myMember = await db.CommunityMembers
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);
        if (myMember is null) return Results.Forbid();

        if (myMember.Role == ResourceRole.Owner)
        {
            var hasOthers = await db.CommunityMembers.AnyAsync(m => m.CommunityId == communityId && m.UserId != actorId && m.LeftAt == null, ct);
            if (hasOthers)
            {
                return Results.BadRequest(new { code = "transfer_ownership_required", message = "Transfer community ownership before leaving." });
            }
        }

        myMember.LeftAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await SidebarEndpoints.RemoveSidebarItemAsync(db, actorId, SidebarEndpoints.CommunitiesSurface, communityId, ct);
        await People.PeopleTagEndpoints.RemoveCommunityTagMembershipsAsync(db, communityId, actorId, ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> HandleTransferOwnershipAsync(
        HttpContext ctx,
        Guid communityId,
        TransferCommunityOwnerRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await LockCommunityAsync(db, communityId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);
        var targetMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == request.NewOwnerUserId && m.LeftAt == null, ct);
        var targetUser = await db.ActiveScopePeople.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == request.NewOwnerUserId, ct);

        if (community is null || myMember is null || targetMember is null || targetUser is null)
        {
            return Results.Forbid();
        }

        var allowed = authz.Can(
            actor.ToActorContext(),
            ResourceAction.CommunityTransferOwnership,
            new ResourceContext(
                ResourceKind.Community,
                actor.OrganizationId,
                MembershipRole: myMember.Role,
                IsOfficial: community.IsOfficial,
                TargetIsSelf: request.NewOwnerUserId == actorId,
                TargetIsActive: targetUser.Status == AccountStatus.Active,
                TargetIsVerified: targetUser.TrustLevel == TrustLevel.Verified));

        if (!allowed) return Results.Forbid();

        var transition = await ConditionalTransition.ExecuteAsync(
            token => db.CommunityMembers
                .Where(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null && m.Role == ResourceRole.Owner)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ResourceRole.Admin), token),
            async token =>
            {
                var cur = await db.CommunityMembers.AsNoTracking()
                    .Where(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null)
                    .Select(m => new { m.Role })
                    .SingleOrDefaultAsync(token);
                return cur is null ? (false, ResourceRole.Member) : (true, cur.Role);
            },
            targetState: ResourceRole.Admin,
            ct);

        if (transition.Outcome != TransitionOutcome.Succeeded)
        {
            return Results.Conflict(new { code = "ownership_transfer_conflict" });
        }

        var promoted = await db.CommunityMembers
            .Where(m => m.CommunityId == communityId && m.UserId == request.NewOwnerUserId && m.LeftAt == null &&
                db.ActiveScopePeople.Any(active => active.Id == m.UserId &&
                    (!community.IsOfficial || active.TrustLevel == TrustLevel.Verified)))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ResourceRole.Owner), ct);
        if (promoted != 1) return Results.Conflict(new { code = "ownership_transfer_conflict" });

        await tx.CommitAsync(ct);
        await fanout.NotifyCommunityOwnershipChangedAsync(communityId, ct);
        return Results.Ok(new { newOwnerUserId = request.NewOwnerUserId });
    }

    private static async Task<IResult> HandleDesignateOfficialAsync(
        HttpContext ctx,
        Guid communityId,
        DesignateOfficialRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        // Admin actions require MFA enrolled (M1.8)
        if (ctx.User.FindFirst(ClaimsPrincipalExtensions.MfaVerifiedClaimType)?.Value != "true")
        {
            return Results.Forbid();
        }

        var community = await db.Communities
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        if (community is null) return Results.NotFound(new { code = "community_not_found" });

        if (!authz.Can(actor.ToActorContext(), ResourceAction.CommunityDesignateOfficial, new ResourceContext(ResourceKind.Community, actor.OrganizationId)))
        {
            return Results.Forbid();
        }

        community.IsOfficial = request.IsOfficial;
        community.UpdatedAt = DateTimeOffset.UtcNow;

        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actorId,
            Action = "COMMUNITY_OFFICIAL_CHANGED",
            ResourceType = "Community",
            ResourceId = community.Id.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(new { isOfficial = request.IsOfficial }, AuditJsonOptions),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = community.UpdatedAt
        });

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = community.Id, isOfficial = community.IsOfficial });
    }

    private static async Task<IResult> HandleEmergencyTransferAsync(
        HttpContext ctx,
        Guid communityId,
        TransferCommunityOwnerRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Forbid();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await LockCommunityAsync(db, communityId, ct);
        var targetUser = await db.ActiveScopePeople.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == request.NewOwnerUserId, ct);

        if (community is null || targetUser is null) return Results.NotFound();

        if (!authz.Can(actor.ToActorContext(), ResourceAction.CommunityEmergencyTransferOwnership,
                new ResourceContext(ResourceKind.Community, actor.OrganizationId, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        if (community.IsOfficial && targetUser.TrustLevel != TrustLevel.Verified)
        {
            return Results.BadRequest(new { code = "verified_owner_required", message = "Official community owners must be Verified." });
        }

        var now = DateTimeOffset.UtcNow;

        await db.CommunityMembers
            .Where(m => m.CommunityId == communityId && m.LeftAt == null && m.Role == ResourceRole.Owner)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ResourceRole.Admin), ct);

        var targetMember = await db.CommunityMembers
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == request.NewOwnerUserId, ct);
        if (targetMember is null)
        {
            db.CommunityMembers.Add(new CommunityMemberEntity
            {
                CommunityId = communityId,
                UserId = request.NewOwnerUserId,
                Role = ResourceRole.Owner,
                JoinedAt = now
            });
        }
        else
        {
            targetMember.LeftAt = null;
            targetMember.Role = ResourceRole.Owner;
        }

        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actorId,
            Action = "COMMUNITY_EMERGENCY_OWNERSHIP_TRANSFER",
            ResourceType = "Community",
            ResourceId = communityId.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(new { newOwnerUserId = request.NewOwnerUserId }, AuditJsonOptions),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await fanout.NotifyCommunityOwnershipChangedAsync(communityId, ct);

        return Results.Ok(new { communityId, newOwnerUserId = request.NewOwnerUserId });
    }

    private static async Task<IResult> HandleMapDepartmentAsync(
        HttpContext ctx,
        Guid communityId,
        MapDepartmentRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var community = await db.Communities
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.CommunityAddOrRemoveMember,
                new ResourceContext(ResourceKind.Community, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        if (request.DepartmentId.HasValue)
        {
            var deptExists = await db.Departments.AnyAsync(d => d.Id == request.DepartmentId.Value && d.OrganizationId == actor.OrganizationId, ct);
            if (!deptExists) return Results.NotFound(new { code = "department_not_found" });
        }

        community.MappedDepartmentId = request.DepartmentId;
        community.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { communityId, mappedDepartmentId = community.MappedDepartmentId });
    }

    /// <summary>
    /// Idempotent HR department -> Community membership sync (M5.4 / CM-02).
    /// Re-running yields the exact same membership set with 0 duplicates.
    /// </summary>
    private static async Task<IResult> HandleSyncDepartmentAsync(
        HttpContext ctx,
        Guid communityId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await LockCommunityAsync(db, communityId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.CommunityAddOrRemoveMember,
                new ResourceContext(ResourceKind.Community, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        if (!community.MappedDepartmentId.HasValue)
        {
            return Results.BadRequest(new { code = "no_department_mapped", message = "Map a department to this community before syncing." });
        }

        var deptId = community.MappedDepartmentId.Value;
        var eligibleQuery = db.OrganizationPeople.AsNoTracking()
            .Where(u => u.OrganizationId == actor.OrganizationId &&
                        u.Status == AccountStatus.Active &&
                        u.DepartmentId == deptId);

        if (community.IsOfficial)
        {
            eligibleQuery = eligibleQuery.Where(u => u.TrustLevel == TrustLevel.Verified);
        }

        var eligibleUserIds = await eligibleQuery.Select(u => u.Id).ToListAsync(ct);
        var existingRows = await db.CommunityMembers
            .Where(m => m.CommunityId == communityId && eligibleUserIds.Contains(m.UserId))
            .ToDictionaryAsync(m => m.UserId, ct);

        var added = 0;
        var unchanged = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var uid in eligibleUserIds)
        {
            if (!existingRows.TryGetValue(uid, out var row))
            {
                db.CommunityMembers.Add(new CommunityMemberEntity
                {
                    CommunityId = communityId,
                    UserId = uid,
                    Role = ResourceRole.Member,
                    JoinedAt = now
                });
                added++;
            }
            else if (row.LeftAt is not null)
            {
                row.LeftAt = null;
                row.Role = ResourceRole.Member;
                row.JoinedAt = now;
                added++;
            }
            else
            {
                unchanged++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return Results.Ok(new { communityId, departmentId = deptId, addedCount = added, unchangedCount = unchanged });
    }

}
