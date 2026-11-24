using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.People;

public static class ProfileAndAvailabilityEndpoints
{
    public const int MinAvailabilityDurationMinutes = 1;
    public const int MaxAvailabilityDurationMinutes = 7 * 24 * 60; // 10,080 minutes (7 days)

    public sealed record UpdateAvailabilityRequest(
        string? State,
        DateTimeOffset? ExpiresAt = null,
        int? DurationMinutes = null);

    public static void MapProfileAndAvailabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/people/me/availability", HandleUpdateAvailabilityAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);

        app.MapGet("/api/people/{targetUserId:guid}/profile-card", HandleContextProfileCardAsync);
        app.MapGet(OrganizationScope.Api + "/people/{targetUserId:guid}/profile-card", HandleOrganizationProfileCardAsync);
        app.MapGet(OrganizationScope.PersonalApi + "/people/{targetUserId:guid}/profile-card", HandlePersonalProfileCardAsync);

        app.MapGet(OrganizationScope.Api + "/people/presence", HandleOrganizationPresenceAsync);
        app.MapGet(OrganizationScope.PersonalApi + "/people/presence", HandlePersonalPresenceAsync);
    }

    private static async Task<IResult> HandleUpdateAvailabilityAsync(
        HttpContext ctx,
        UpdateAvailabilityRequest request,
        [FromServices] NexusDbContext db,
        IPresenceTracker presence,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        if (!AvailabilityResolver.TryParseState(request.State, out var state))
        {
            return Results.BadRequest(new
            {
                code = "invalid_availability_state",
                message = "Availability state must be Available, Busy, Away, or DoNotDisturb."
            });
        }

        if (request.ExpiresAt.HasValue && request.DurationMinutes.HasValue)
        {
            return Results.BadRequest(new
            {
                code = "invalid_availability_expiry",
                message = "Specify either expiresAt or durationMinutes, not both."
            });
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? expiresAt = null;

        if (state == AvailabilityState.Available)
        {
            if (request.ExpiresAt.HasValue || request.DurationMinutes.HasValue)
            {
                return Results.BadRequest(new
                {
                    code = "invalid_availability_expiry",
                    message = "Available status cannot have an expiration time."
                });
            }
        }
        else if (request.DurationMinutes is { } minutes)
        {
            if (minutes is < MinAvailabilityDurationMinutes or > MaxAvailabilityDurationMinutes)
            {
                return Results.BadRequest(new
                {
                    code = "invalid_availability_expiry",
                    message = $"Duration must be between {MinAvailabilityDurationMinutes} minute and 7 days."
                });
            }

            expiresAt = now.AddMinutes(minutes);
        }
        else if (request.ExpiresAt is { } requestedExpiry)
        {
            var utcExpiry = requestedExpiry.ToUniversalTime();
            if (utcExpiry <= now || utcExpiry > now.AddDays(7))
            {
                return Results.BadRequest(new
                {
                    code = "invalid_availability_expiry",
                    message = "Expiration must be in the future and within 7 days."
                });
            }

            expiresAt = utcExpiry;
        }

        var affected = await db.Users
            .Where(u => u.Id == userId && u.Status == AccountStatus.Active)
            .ExecuteUpdateAsync(set => set
                .SetProperty(u => u.AvailabilityState, state)
                .SetProperty(u => u.AvailabilityExpiresAt, expiresAt), ct);

        if (affected == 0)
        {
            return Results.Unauthorized();
        }

        var resolved = AvailabilityResolver.Resolve(state, expiresAt, now);
        var isOnline = presence.IsOnline(userId);
        await fanout.NotifyAvailabilityChangedAsync(userId, resolved, isOnline, ct);

        return Results.Ok(new
        {
            state = resolved.State,
            expiresAt = resolved.ExpiresAt,
            isOnline,
            suppressChime = AvailabilityResolver.IsChimeSuppressed(state, expiresAt, now)
        });
    }

    private static async Task<IResult> HandleContextProfileCardAsync(
        HttpContext ctx,
        Guid targetUserId,
        Guid? organizationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IOptions<IdentityModuleOptions> identityOptions,
        IPresenceTracker presence,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        if (organizationId is { } orgId)
        {
            if (orgId == Guid.Empty)
            {
                return Results.Json(new { code = "organization_access_revoked" }, statusCode: StatusCodes.Status403Forbidden);
            }

            db.IsPersonalScope = false;
            db.OrganizationScopeId = orgId;
            var orgActor = await db.FindOrganizationActorAsync(actorId, ct);
            if (orgActor is null)
            {
                return Results.Json(new { code = "organization_access_revoked" }, statusCode: StatusCodes.Status403Forbidden);
            }

            db.OrganizationActor = orgActor;
            return await HandleOrganizationProfileCardCoreAsync(orgActor, targetUserId, db, authz, identityOptions, presence, ct);
        }

        db.IsPersonalScope = true;
        db.OrganizationScopeId = Guid.Empty;
        var personalActor = await db.FindPersonalActorAsync(actorId, ct);
        if (personalActor is null)
        {
            return Results.Forbid();
        }

        db.OrganizationActor = personalActor;
        return await HandlePersonalProfileCardCoreAsync(personalActor, targetUserId, db, authz, identityOptions, presence, ct);
    }

    private static async Task<IResult> HandleOrganizationProfileCardAsync(
        HttpContext ctx,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IOptions<IdentityModuleOptions> identityOptions,
        IPresenceTracker presence,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        return await HandleOrganizationProfileCardCoreAsync(actor, targetUserId, db, authz, identityOptions, presence, ct);
    }

    private static async Task<IResult> HandleOrganizationProfileCardCoreAsync(
        OrganizationMembership actor,
        Guid targetUserId,
        NexusDbContext db,
        IAuthorizationService authz,
        IOptions<IdentityModuleOptions> identityOptions,
        IPresenceTracker presence,
        CancellationToken ct)
    {
        if (!authz.Can(actor.ToActorContext(), ResourceAction.DirectorySearch, new ResourceContext(ResourceKind.Organization, actor.OrganizationId)))
        {
            return Results.Forbid();
        }

        var target = await (
            from member in db.OrganizationMemberships.AsNoTracking()
            join account in db.Users.AsNoTracking() on member.Id equals account.Id
            join dept in db.Departments.AsNoTracking() on member.DepartmentId equals dept.Id into deptJoin
            from dept in deptJoin.DefaultIfEmpty()
            join branch in db.Branches.AsNoTracking() on member.BranchId equals branch.Id into branchJoin
            from branch in branchJoin.DefaultIfEmpty()
            where member.OrganizationId == actor.OrganizationId
                  && member.Id == targetUserId
                  && member.Status == AccountStatus.Active
                  && account.Status == AccountStatus.Active
            select new
            {
                member.Id,
                Username = member.UserName,
                member.DisplayName,
                member.AvatarObjectKey,
                member.CustomStatus,
                member.TrustLevel,
                member.OrgRole,
                member.IsOwner,
                member.DepartmentId,
                DepartmentName = dept != null ? dept.Name : null,
                member.BranchId,
                BranchName = branch != null ? branch.Name : null,
                account.AvailabilityState,
                account.AvailabilityExpiresAt
            }).SingleOrDefaultAsync(ct);

        if (target is null)
        {
            return Results.NotFound(new { code = "user_not_found", message = "Target user not found." });
        }

        var isSelf = target.Id == actor.Id;
        var blockers = isSelf
            ? []
            : await db.UserBlocks.AsNoTracking()
                .Where(b => (b.OrganizationId == actor.OrganizationId || b.OrganizationId == Guid.Empty) &&
                    ((b.BlockerUserId == actor.Id && b.BlockedUserId == target.Id) ||
                     (b.BlockerUserId == target.Id && b.BlockedUserId == actor.Id)))
                .Select(b => b.BlockerUserId)
                .ToListAsync(ct);

        var isBlockedByMe = blockers.Contains(actor.Id);
        var isBlockedEitherWay = blockers.Count > 0;

        var dailyQuotaExceeded = false;
        if (!isSelf && !isBlockedEitherWay && actor.TrustLevel == TrustLevel.Unverified)
        {
            var dmKey = ConversationEndpoints.BuildDmKey(actor.Id, target.Id);
            var existingDm = await db.Conversations.AsNoTracking()
                .AnyAsync(c => c.OrganizationId == actor.OrganizationId && c.DmKey == dmKey, ct);
            if (!existingDm)
            {
                var utcDayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
                var todayCount = await db.Conversations.CountAsync(
                    c => c.OrganizationId == actor.OrganizationId &&
                         c.Kind == ConversationKind.Dm &&
                         c.CreatedByUserId == actor.Id &&
                         c.CreatedAt >= utcDayStart,
                    ct);
                dailyQuotaExceeded = todayCount >= identityOptions.Value.DailyNewDmLimitForUnverified;
            }
        }

        var canStartDirectChat = authz.Can(
            actor.ToActorContext(),
            ResourceAction.DmOpen,
            new ResourceContext(
                ResourceKind.Dm,
                actor.OrganizationId,
                TargetIsActive: true,
                IsBlocked: isBlockedEitherWay,
                DailyNewDmQuotaExceeded: dailyQuotaExceeded,
                TargetIsSelf: isSelf));

        var now = DateTimeOffset.UtcNow;
        return Results.Ok(new
        {
            scope = "organization",
            organizationId = (Guid?)actor.OrganizationId,
            userId = target.Id,
            username = target.Username,
            displayName = target.DisplayName,
            avatarUrl = string.IsNullOrEmpty(target.AvatarObjectKey)
                ? null
                : $"/api/organizations/{actor.OrganizationId}/people/{target.Id}/avatar",
            customStatus = target.CustomStatus,
            isOnline = presence.IsOnline(actor.OrganizationId, target.Id),
            availability = AvailabilityResolver.Resolve(target.AvailabilityState, target.AvailabilityExpiresAt, now),
            trustLevel = target.TrustLevel.ToString(),
            isUnverified = target.TrustLevel == TrustLevel.Unverified,
            orgRole = target.OrgRole.ToString(),
            isOwner = target.IsOwner,
            departmentId = target.DepartmentId,
            departmentName = target.DepartmentName,
            branchId = target.BranchId,
            branchName = target.BranchName,
            isBlockedByMe,
            canStartDirectChat
        });
    }

    private static async Task<IResult> HandlePersonalProfileCardAsync(
        HttpContext ctx,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IOptions<IdentityModuleOptions> identityOptions,
        IPresenceTracker presence,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = db.OrganizationActor ?? await db.FindPersonalActorAsync(actorId, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        return await HandlePersonalProfileCardCoreAsync(actor, targetUserId, db, authz, identityOptions, presence, ct);
    }

    private static async Task<IResult> HandlePersonalProfileCardCoreAsync(
        OrganizationMembership actor,
        Guid targetUserId,
        NexusDbContext db,
        IAuthorizationService authz,
        IOptions<IdentityModuleOptions> identityOptions,
        IPresenceTracker presence,
        CancellationToken ct)
    {
        var actorId = actor.Id;
        var target = await db.Users.AsNoTracking()
            .Where(u => u.Id == targetUserId && u.Status == AccountStatus.Active)
            .Select(u => new
            {
                u.Id,
                Username = u.UserName ?? string.Empty,
                u.DisplayName,
                u.AvailabilityState,
                u.AvailabilityExpiresAt
            })
            .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            return Results.NotFound(new { code = "user_not_found", message = "Target user not found." });
        }

        var isSelf = target.Id == actorId;
        var isContact = !isSelf && await db.PersonalConnections.AsNoTracking()
            .AnyAsync(c => c.AcceptedAt != null &&
                ((c.FirstUserId == actorId && c.SecondUserId == target.Id) ||
                 (c.FirstUserId == target.Id && c.SecondUserId == actorId)), ct);

        var sharedOrgs = isSelf
            ? await db.OrganizationMemberships.AsNoTracking()
                .Where(m => m.Id == actorId && m.Status == AccountStatus.Active)
                .OrderBy(m => m.OrganizationId)
                .Select(m => new { m.OrganizationId, OrgName = m.Organization.Name, m.AvatarObjectKey })
                .ToListAsync(ct)
            : await db.OrganizationMemberships.AsNoTracking()
                .Where(mine => mine.Id == actorId && mine.Status == AccountStatus.Active)
                .Join(
                    db.OrganizationMemberships.AsNoTracking().Where(peer => peer.Id == target.Id && peer.Status == AccountStatus.Active),
                    mine => mine.OrganizationId,
                    peer => peer.OrganizationId,
                    (_, peer) => new { peer.OrganizationId, OrgName = peer.Organization.Name, peer.AvatarObjectKey })
                .OrderBy(x => x.OrganizationId)
                .ToListAsync(ct);

        if (!isSelf && !isContact && sharedOrgs.Count == 0)
        {
            var sharesPersonalConversation = await db.ConversationMembers.AsNoTracking()
                .Where(mine => mine.UserId == actorId && mine.LeftAt == null)
                .Join(
                    db.Conversations.AsNoTracking().Where(c => c.OrganizationId == null),
                    mine => mine.ConversationId,
                    c => c.Id,
                    (mine, _) => mine.ConversationId)
                .Join(
                    db.ConversationMembers.AsNoTracking().Where(peer => peer.UserId == target.Id && peer.LeftAt == null),
                    convId => convId,
                    peer => peer.ConversationId,
                    (convId, _) => convId)
                .AnyAsync(ct);

            if (!sharesPersonalConversation)
            {
                return Results.NotFound(new { code = "user_not_found", message = "Target user not found." });
            }
        }

        var blockers = isSelf
            ? []
            : await db.UserBlocks.AsNoTracking()
                .Where(b => b.OrganizationId == Guid.Empty &&
                    ((b.BlockerUserId == actorId && b.BlockedUserId == target.Id) ||
                     (b.BlockerUserId == target.Id && b.BlockedUserId == actorId)))
                .Select(b => b.BlockerUserId)
                .ToListAsync(ct);

        var isBlockedByMe = blockers.Contains(actorId);
        var isBlockedEitherWay = blockers.Count > 0;

        var canStartDirectChat = false;
        if (!isSelf && !isBlockedEitherWay && (isContact || sharedOrgs.Count > 0))
        {
            var dmKey = ConversationEndpoints.BuildDmKey(actorId, target.Id);
            var existingDm = await db.Conversations.AsNoTracking()
                .AnyAsync(c => c.OrganizationId == null && c.DmKey == dmKey, ct);
            var dailyQuotaExceeded = false;
            if (!existingDm && actor.TrustLevel == TrustLevel.Unverified)
            {
                var utcDayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
                var todayCount = await db.Conversations.CountAsync(
                    c => c.OrganizationId == null &&
                         c.Kind == ConversationKind.Dm &&
                         c.CreatedByUserId == actorId &&
                         c.CreatedAt >= utcDayStart,
                    ct);
                dailyQuotaExceeded = todayCount >= identityOptions.Value.DailyNewDmLimitForUnverified;
            }

            canStartDirectChat = authz.Can(
                actor.ToActorContext(),
                ResourceAction.DmOpen,
                new ResourceContext(
                    ResourceKind.Dm,
                    Guid.Empty,
                    TargetIsActive: true,
                    IsBlocked: isBlockedEitherWay,
                    DailyNewDmQuotaExceeded: dailyQuotaExceeded,
                    TargetIsSelf: false));
        }

        var avatarOrg = sharedOrgs.FirstOrDefault(o => !string.IsNullOrEmpty(o.AvatarObjectKey));
        var avatarUrl = avatarOrg is null
            ? null
            : $"/api/organizations/{avatarOrg.OrganizationId}/people/{target.Id}/avatar";

        var sharedWorkspaceNames = isSelf
            ? Array.Empty<string>()
            : sharedOrgs.Select(o => o.OrgName).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var now = DateTimeOffset.UtcNow;
        return Results.Ok(new
        {
            scope = "personal",
            organizationId = (Guid?)null,
            userId = target.Id,
            username = target.Username,
            displayName = target.DisplayName,
            avatarUrl,
            customStatus = (string?)null,
            isOnline = presence.IsOnline(target.Id),
            availability = AvailabilityResolver.Resolve(target.AvailabilityState, target.AvailabilityExpiresAt, now),
            isContact,
            sharedWorkspaceNames,
            isBlockedByMe,
            canStartDirectChat
        });
    }

    private static async Task<IResult> HandleOrganizationPresenceAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IPresenceTracker presence,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        if (!authz.Can(actor.ToActorContext(), ResourceAction.DirectorySearch, new ResourceContext(ResourceKind.Organization, actor.OrganizationId)))
        {
            return Results.Forbid();
        }

        if (!TryParseUserIds(ctx.Request.Query["userIds"], out var requestedIds, out var errorResult))
        {
            return errorResult!;
        }

        if (requestedIds.Length == 0)
        {
            return Results.Ok(new { items = Array.Empty<object>() });
        }

        var rows = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == actor.OrganizationId
                        && requestedIds.Contains(m.Id)
                        && m.Status == AccountStatus.Active
                        && m.Account.Status == AccountStatus.Active)
            .Select(m => new { m.Id, m.Account.AvailabilityState, m.Account.AvailabilityExpiresAt })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(r => new
        {
            userId = r.Id,
            isOnline = presence.IsOnline(actor.OrganizationId, r.Id),
            availability = AvailabilityResolver.Resolve(r.AvailabilityState, r.AvailabilityExpiresAt, now)
        }).ToList();

        return Results.Ok(new { items });
    }

    private static async Task<IResult> HandlePersonalPresenceAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IPresenceTracker presence,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = db.OrganizationActor ?? await db.FindPersonalActorAsync(actorId, ct);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        if (!TryParseUserIds(ctx.Request.Query["userIds"], out var requestedIds, out var errorResult))
        {
            return errorResult!;
        }

        if (requestedIds.Length == 0)
        {
            return Results.Ok(new { items = Array.Empty<object>() });
        }

        var allowedIds = PersonalConnections.AllowedChatPeerIds(db, actorId)
            .Union(
                db.ConversationMembers.AsNoTracking()
                    .Where(mine => mine.UserId == actorId && mine.LeftAt == null)
                    .Join(
                        db.Conversations.AsNoTracking().Where(c => c.OrganizationId == null),
                        mine => mine.ConversationId,
                        c => c.Id,
                        (mine, _) => mine.ConversationId)
                    .Join(
                        db.ConversationMembers.AsNoTracking().Where(peer => peer.UserId != actorId && peer.LeftAt == null),
                        convId => convId,
                        peer => peer.ConversationId,
                        (_, peer) => peer.UserId)
                    .Where(peerId => !db.UserBlocks.Any(b => b.OrganizationId == Guid.Empty &&
                        ((b.BlockerUserId == actorId && b.BlockedUserId == peerId) ||
                         (b.BlockerUserId == peerId && b.BlockedUserId == actorId)))));

        var rows = await db.Users.AsNoTracking()
            .Where(u => requestedIds.Contains(u.Id) && u.Status == AccountStatus.Active &&
                        (u.Id == actorId || allowedIds.Contains(u.Id)))
            .Select(u => new { u.Id, u.AvailabilityState, u.AvailabilityExpiresAt })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(r => new
        {
            userId = r.Id,
            isOnline = presence.IsOnline(r.Id),
            availability = AvailabilityResolver.Resolve(r.AvailabilityState, r.AvailabilityExpiresAt, now)
        }).ToList();

        return Results.Ok(new { items });
    }

    private static bool TryParseUserIds(
        Microsoft.Extensions.Primitives.StringValues rawValues,
        out Guid[] userIds,
        out IResult? errorResult)
    {
        var parsed = new List<Guid>();
        var seen = new HashSet<Guid>();
        var rawCount = 0;

        foreach (var raw in rawValues)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                rawCount++;
                if (rawCount > PaginationGuard.MaxPageSize)
                {
                    userIds = [];
                    errorResult = Results.BadRequest(new
                    {
                        code = "invalid_page_size",
                        message = $"At most {PaginationGuard.MaxPageSize} userIds may be requested at once."
                    });
                    return false;
                }

                if (!Guid.TryParse(part, out var id) || id == Guid.Empty)
                {
                    userIds = [];
                    errorResult = Results.BadRequest(new
                    {
                        code = "invalid_user_id",
                        message = "One or more userIds are invalid."
                    });
                    return false;
                }

                if (seen.Add(id))
                {
                    parsed.Add(id);
                }
            }
        }

        userIds = parsed.ToArray();
        errorResult = null;
        return true;
    }
}
