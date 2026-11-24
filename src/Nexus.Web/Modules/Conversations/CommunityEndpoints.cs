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

public static class CommunityEndpoints
{
    private sealed record CommunityCursor(bool IsOfficial, string Name, Guid Id);
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record CreateCommunityRequest(string Name, string? Description, string? Visibility);
    public sealed record CreateChannelRequest(string Name, string? PostingPolicy);
    public sealed record AddCommunityMemberRequest(Guid UserId);
    public sealed record SetCommunityAdminRequest(bool IsAdmin);
    public sealed record TransferCommunityOwnerRequest(Guid NewOwnerUserId);
    public sealed record DesignateOfficialRequest(bool IsOfficial);
    public sealed record MapDepartmentRequest(Guid? DepartmentId);

    public static IEndpointRouteBuilder MapCommunityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(OrganizationScope.Api + "/communities");

        group.MapGet("/", HandleListCommunitiesAsync);
        group.MapPost("/", HandleCreateCommunityAsync)
            .AddEndpointFilter<IdempotencyEndpointFilter>();

        group.MapPost("/{communityId:guid}/join", HandleJoinCommunityAsync);
        group.MapGet("/{communityId:guid}/members", HandleListMembersAsync);
        group.MapPost("/{communityId:guid}/members", HandleAddMemberAsync);
        group.MapDelete("/{communityId:guid}/members/{targetUserId:guid}", HandleRemoveMemberAsync);
        group.MapPut("/{communityId:guid}/admins/{targetUserId:guid}", HandleSetAdminAsync);
        group.MapPost("/{communityId:guid}/leave", HandleLeaveCommunityAsync);
        group.MapPost("/{communityId:guid}/transfer-ownership", HandleTransferOwnershipAsync);
        group.MapPost("/{communityId:guid}/official", HandleDesignateOfficialAsync);
        group.MapPost("/{communityId:guid}/emergency-transfer-ownership", HandleEmergencyTransferAsync);
        group.MapPost("/{communityId:guid}/channels", HandleCreateChannelAsync);
        group.MapPut("/{communityId:guid}/department-mapping", HandleMapDepartmentAsync);
        group.MapPost("/{communityId:guid}/sync-department", HandleSyncDepartmentAsync);

        return app;
    }

    private static async Task<IResult> HandleListCommunitiesAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        bool discover = false,
        string? q = null,
        string? cursor = null,
        Guid? communityId = null,
        Guid? conversationId = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }
        if (!PaginationGuard.TryDecodeCursor<CommunityCursor>(cursor, out var after) ||
            after is not null && (after.Id == Guid.Empty || after.Name is null)) return PaginationGuard.InvalidCursor();

        var myMemberships = await db.CommunityMembers.AsNoTracking()
            .Where(m => m.UserId == actorId && m.LeftAt == null)
            .ToDictionaryAsync(m => m.CommunityId, m => m, ct);

        IQueryable<CommunityEntity> query = db.Communities.AsNoTracking()
            .Where(c => c.OrganizationId == actor.OrganizationId);
        if (communityId is { } requestedCommunity)
            query = query.Where(c => c.Id == requestedCommunity);
        if (conversationId is { } requestedConversation)
            query = query.Where(c => db.Conversations.Any(ch => ch.CommunityId == c.Id && ch.Id == requestedConversation));

        if (discover)
        {
            query = query.Where(c => c.Visibility == Visibility.Public);
            if (actor.TrustLevel != TrustLevel.Verified)
            {
                query = query.Where(c => !c.IsOfficial);
            }
        }
        else
        {
            var memberCommunityIds = myMemberships.Keys.ToList();
            query = query.Where(c => memberCommunityIds.Contains(c.Id));
            if (actor.TrustLevel != TrustLevel.Verified)
            {
                query = query.Where(c => !c.IsOfficial);
            }
        }

        var search = q?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern) ||
                db.Organizations.Any(o => o.Id == c.OrganizationId && EF.Functions.ILike(o.Name, pattern)) ||
                (discover && c.Description != null && EF.Functions.ILike(c.Description, pattern)) ||
                (!discover && db.Conversations.Any(ch => ch.CommunityId == c.Id && !ch.IsCommunityMainFeed &&
                    ch.Title != null && EF.Functions.ILike(ch.Title, pattern))));
        }
        if (after is not null)
        {
            query = query.Where(c => (after.IsOfficial && !c.IsOfficial) ||
                (c.IsOfficial == after.IsOfficial && (string.Compare(c.Name, after.Name) > 0 ||
                 c.Name == after.Name && c.Id.CompareTo(after.Id) > 0)));
        }

        var communities = await query
            .OrderByDescending(c => c.IsOfficial)
            .ThenBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);
        var hasMore = communities.Count > pageSize;
        if (hasMore) communities.RemoveAt(pageSize);

        var commIds = communities.Select(c => c.Id).ToList();

        var channelRows = commIds.Count == 0
            ? []
            : await db.Conversations.AsNoTracking()
                .Where(c => c.CommunityId != null && commIds.Contains(c.CommunityId.Value) && c.Kind == ConversationKind.Channel)
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(ct);

        var memberCounts = commIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.CommunityMembers.AsNoTracking()
                .Where(m => commIds.Contains(m.CommunityId) && m.LeftAt == null)
                .GroupBy(m => m.CommunityId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var channelsByComm = channelRows.GroupBy(c => c.CommunityId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        // Batch-load the latest post preview only for communities the actor is authorized to read (never Discover non-members or Unverified Official).
        var latestPostByComm = new Dictionary<Guid, object>();
        if (!discover && commIds.Count > 0)
        {
            var readableCommIds = communities
                .Where(c => myMemberships.ContainsKey(c.Id) && (!c.IsOfficial || actor.TrustLevel == TrustLevel.Verified))
                .Select(c => c.Id)
                .ToHashSet();
            var readableChannelIds = channelRows
                .Where(ch => ch.CommunityId.HasValue && readableCommIds.Contains(ch.CommunityId.Value))
                .Select(ch => ch.Id)
                .ToList();
            var commByChannel = channelRows
                .Where(ch => ch.CommunityId.HasValue)
                .ToDictionary(ch => ch.Id, ch => ch.CommunityId!.Value);

            if (readableChannelIds.Count > 0)
            {
                var recentPosts = await db.ChannelPosts.AsNoTracking()
                    .Where(p => readableChannelIds.Contains(p.ConversationId) && p.DeletedAt == null)
                    .GroupBy(p => p.ConversationId)
                    .Select(g => g.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                        .Select(p => new { p.ConversationId, p.Title, p.Body, p.AuthorUserId, p.CreatedAt })
                        .First())
                    .ToListAsync(ct);

                var authorIds = recentPosts.Select(p => p.AuthorUserId).Distinct().ToList();
                var authorNames = authorIds.Count == 0
                    ? new Dictionary<Guid, string>()
                    : await db.OrganizationPeople.AsNoTracking()
                        .Where(u => authorIds.Contains(u.Id))
                        .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

                foreach (var group in recentPosts.GroupBy(p => commByChannel[p.ConversationId]))
                {
                    var latest = group.OrderByDescending(p => p.CreatedAt).First();
                    var sourceText = string.IsNullOrWhiteSpace(latest.Title) ? latest.Body : latest.Title;
                    latestPostByComm[group.Key] = new
                    {
                        bodyPreview = ConversationEndpoints.FormatPreview(sourceText, "New post"),
                        authorDisplayName = authorNames.GetValueOrDefault(latest.AuthorUserId, "Member"),
                        createdAt = latest.CreatedAt
                    };
                }
            }
        }

        var items = communities.Select(c =>
        {
            var allCh = channelsByComm.GetValueOrDefault(c.Id) ?? [];
            var mainFeed = allCh.FirstOrDefault(x => x.IsCommunityMainFeed);
            var subChannels = allCh
                .Where(x => !x.IsCommunityMainFeed)
                .Select(x => new
                {
                    id = x.Id,
                    name = x.Title ?? "Channel",
                    postingPolicy = x.PostingPolicy.ToString(),
                    createdAt = x.CreatedAt
                })
                .ToList();

            var membership = myMemberships.GetValueOrDefault(c.Id);

            return new
            {
                id = c.Id,
                name = c.Name,
                description = c.Description,
                visibility = c.Visibility.ToString(),
                isOfficial = c.IsOfficial,
                mappedDepartmentId = c.MappedDepartmentId,
                memberCount = memberCounts.GetValueOrDefault(c.Id),
                isMember = membership is not null,
                myRole = membership?.Role.ToString(),
                mainFeedConversationId = mainFeed?.Id,
                mainFeedPostingPolicy = mainFeed?.PostingPolicy.ToString() ?? PostingPolicy.Members.ToString(),
                channels = subChannels,
                latestPostPreview = latestPostByComm.GetValueOrDefault(c.Id),
                createdAt = c.CreatedAt
            };
        }).ToList();

        var nextCursor = hasMore
            ? PaginationGuard.EncodeCursor(new CommunityCursor(communities[^1].IsOfficial, communities[^1].Name, communities[^1].Id))
            : null;
        return Results.Ok(new { items, nextCursor });
    }

    private static async Task<IResult> HandleCreateCommunityAsync(
        HttpContext ctx,
        CreateCommunityRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        // Single standard Create Community flow (M5.2): new communities always start non-official.
        if (!authz.Can(actor.ToActorContext(), ResourceAction.CommunityCreate, new ResourceContext(ResourceKind.Community, actor.OrganizationId, IsOfficial: false)))
        {
            return Results.Forbid();
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 120)
        {
            return Results.BadRequest(new { code = "invalid_name", message = "Community name must be 1 to 120 characters." });
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is not null && description.Length > 500)
        {
            return Results.BadRequest(new { code = "invalid_description", message = "Description must be at most 500 characters." });
        }

        var visibility = string.Equals(request.Visibility, "Private", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Private
            : Visibility.Public;

        var now = DateTimeOffset.UtcNow;
        var community = new CommunityEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            Name = name,
            Description = description,
            Visibility = visibility,
            IsOfficial = false,
            CreatedByUserId = actorId,
            CreatedAt = now,
            UpdatedAt = now
        };

        // P0-D04 (Microsoft Teams Communities): atomically create the Community's own primary main feed
        // directly with ZERO default `# General` sub-channel.
        var mainFeed = new ConversationEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            Kind = ConversationKind.Channel,
            CommunityId = community.Id,
            IsCommunityMainFeed = true,
            PostingPolicy = PostingPolicy.Members,
            Title = name,
            NextSeq = 0,
            ChangeSeq = 1,
            MinAvailableChangeSeq = 1,
            CreatedByUserId = actorId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Communities.Add(community);
        db.CommunityMembers.Add(new CommunityMemberEntity
        {
            CommunityId = community.Id,
            UserId = actorId,
            Role = ResourceRole.Owner,
            JoinedAt = now
        });
        db.Conversations.Add(mainFeed);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Created($"/api/organizations/{actor.OrganizationId}/communities/{community.Id}", new
        {
            id = community.Id,
            name = community.Name,
            description = community.Description,
            visibility = community.Visibility.ToString(),
            isOfficial = community.IsOfficial,
            mainFeedConversationId = mainFeed.Id,
            channels = Array.Empty<object>()
        });
    }

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
        var items = await db.CommunityMembers.AsNoTracking()
            .Where(m => m.CommunityId == communityId && m.LeftAt == null)
            .Join(db.OrganizationPeople.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new
            {
                userId = u.Id,
                username = u.UserName ?? string.Empty,
                displayName = u.DisplayName,
                isUnverified = u.TrustLevel == TrustLevel.Unverified,
                isActive = db.ActiveScopePeople.Any(active => active.Id == u.Id),
                role = m.Role.ToString(),
                joinedAt = m.JoinedAt
            })
            .Where(x => after == null || string.Compare(x.displayName, after.Name) > 0 ||
                x.displayName == after.Name && x.userId.CompareTo(after.Id) > 0)
            .OrderBy(x => x.displayName).ThenBy(x => x.userId)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(pageSize);
        var nextCursor = hasMore ? PaginationGuard.EncodeCursor(new PaginationGuard.NameCursor(items[^1].displayName, items[^1].userId)) : null;
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

    private static async Task<IResult> HandleCreateChannelAsync(
        HttpContext ctx,
        Guid communityId,
        CreateChannelRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.ChannelCreate,
                new ResourceContext(ResourceKind.Community, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 120)
        {
            return Results.BadRequest(new { code = "invalid_channel_name", message = "Channel name must be 1 to 120 characters." });
        }

        var postingPolicy = string.Equals(request.PostingPolicy, "AdminsOnly", StringComparison.OrdinalIgnoreCase)
            ? PostingPolicy.AdminsOnly
            : PostingPolicy.Members;

        var now = DateTimeOffset.UtcNow;
        var channel = new ConversationEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            Kind = ConversationKind.Channel,
            CommunityId = communityId,
            IsCommunityMainFeed = false,
            PostingPolicy = postingPolicy,
            Title = name,
            NextSeq = 0,
            ChangeSeq = 1,
            MinAvailableChangeSeq = 1,
            CreatedByUserId = actorId,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Conversations.Add(channel);
        await db.SaveChangesAsync(ct);
        await fanout.NotifyCommunityFeedChangedAsync(communityId, channel.Id, ct);

        return Results.Created($"/api/organizations/{actor.OrganizationId}/channels/{channel.Id}/posts", new
        {
            id = channel.Id,
            communityId,
            name = channel.Title,
            postingPolicy = channel.PostingPolicy.ToString(),
            createdAt = channel.CreatedAt
        });
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

    private static Task<CommunityEntity?> LockCommunityAsync(NexusDbContext db, Guid id, CancellationToken ct) =>
        db.Communities.FromSqlInterpolated($"""
            SELECT * FROM communities WHERE id = {id} AND organization_id = {db.OrganizationScopeId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
}
