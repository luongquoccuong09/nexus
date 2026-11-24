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
    private sealed record CommunityCursor(bool IsOfficial, string Name, Guid Id);
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record CreateCommunityRequest(string Name, string? Description, string? Visibility);
    public sealed record UpdateCommunityRequest(string Name, string? Description);
    public sealed record CreateChannelRequest(string Name, string? PostingPolicy, string? Description = null);
    public sealed record UpdateChannelRequest(string Name, string? Description);
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
        group.MapPut("/{communityId:guid}", HandleUpdateCommunityAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);

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
        group.MapPut("/{communityId:guid}/channels/{channelId:guid}", HandleUpdateChannelAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        group.MapPost("/{communityId:guid}/channels/{channelId:guid}/archive", HandleArchiveChannelAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        group.MapPost("/{communityId:guid}/channels/{channelId:guid}/restore", HandleRestoreChannelAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
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
                    description = x.Description,
                    postingPolicy = x.PostingPolicy.ToString(),
                    isArchived = x.ArchivedAt is not null,
                    archivedAt = x.ArchivedAt,
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

    private static async Task<IResult> HandleUpdateCommunityAsync(
        HttpContext ctx,
        Guid communityId,
        UpdateCommunityRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

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

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var community = await LockCommunityAsync(db, communityId, ct);
        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null, ct);

        if (community is null || !authz.Can(
                actor.ToActorContext(),
                ResourceAction.CommunityManageSettings,
                new ResourceContext(ResourceKind.Community, actor.OrganizationId, MembershipRole: myMember?.Role, IsOfficial: community.IsOfficial)))
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        await db.Communities
            .Where(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(c => c.Name, name)
                .SetProperty(c => c.Description, description)
                .SetProperty(c => c.UpdatedAt, now), ct);
        community.Name = name;
        community.Description = description;
        community.UpdatedAt = now;

        var mainFeed = await db.Conversations
            .SingleOrDefaultAsync(c => c.CommunityId == communityId && c.OrganizationId == actor.OrganizationId && c.IsCommunityMainFeed, ct);
        if (mainFeed is not null)
        {
            mainFeed.Title = name;
            mainFeed.UpdatedAt = now;
        }

        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actorId,
            Action = "COMMUNITY_SETTINGS_UPDATED",
            ResourceType = "Community",
            ResourceId = communityId.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(new { name }, AuditJsonOptions),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (mainFeed is not null)
        {
            await fanout.NotifyCommunityFeedChangedAsync(
                communityId,
                mainFeed.Id,
                new { type = "community_updated", communityId, name = community.Name, description = community.Description, updatedAt = now },
                ct);
        }

        return Results.Ok(new
        {
            id = community.Id,
            name = community.Name,
            description = community.Description,
            updatedAt = community.UpdatedAt
        });
    }

    private static Task<CommunityEntity?> LockCommunityAsync(NexusDbContext db, Guid id, CancellationToken ct) =>
        db.Communities.FromSqlInterpolated($"""
            SELECT * FROM communities WHERE id = {id} AND organization_id = {db.OrganizationScopeId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
}
