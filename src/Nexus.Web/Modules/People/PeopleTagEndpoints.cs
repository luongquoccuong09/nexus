using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;
using Npgsql;

namespace Nexus.Web.Modules.People;

public static class PeopleTagEndpoints
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public sealed record CreatePeopleTagRequest(string Name, string Slug, string? Description = null, Guid[]? MemberUserIds = null);
    public sealed record UpdatePeopleTagRequest(string Name, string Slug, string? Description = null);
    public sealed record AddPeopleTagMemberRequest(Guid UserId);

    public static IEndpointRouteBuilder MapPeopleTagEndpoints(this IEndpointRouteBuilder app)
    {
        var orgTags = app.MapGroup(OrganizationScope.Api + "/tags");
        orgTags.MapGet("", HandleListOrgTagsAsync);
        orgTags.MapPost("", HandleCreateOrgTagAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        orgTags.MapPut("/{tagId:guid}", HandleUpdateOrgTagAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        orgTags.MapDelete("/{tagId:guid}", HandleDeleteOrgTagAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        orgTags.MapGet("/{tagId:guid}/members", HandleListOrgTagMembersAsync);
        orgTags.MapPost("/{tagId:guid}/members", HandleAddOrgTagMemberAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        orgTags.MapDelete("/{tagId:guid}/members/{targetUserId:guid}", HandleRemoveOrgTagMemberAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);

        var commTags = app.MapGroup(OrganizationScope.Api + "/communities/{communityId:guid}/tags");
        commTags.MapGet("", HandleListCommunityTagsAsync);
        commTags.MapPost("", HandleCreateCommunityTagAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        commTags.MapPut("/{tagId:guid}", HandleUpdateCommunityTagAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        commTags.MapDelete("/{tagId:guid}", HandleDeleteCommunityTagAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        commTags.MapGet("/{tagId:guid}/members", HandleListCommunityTagMembersAsync);
        commTags.MapPost("/{tagId:guid}/members", HandleAddCommunityTagMemberAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        commTags.MapDelete("/{tagId:guid}/members/{targetUserId:guid}", HandleRemoveCommunityTagMemberAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);

        return app;
    }

    internal static Task RemoveOrganizationTagMembershipsAsync(NexusDbContext db, Guid orgId, Guid[] userIds, CancellationToken ct) =>
        db.PeopleTagMembers
            .Where(tm => userIds.Contains(tm.UserId) &&
                         db.PeopleTags.Any(t => t.Id == tm.TagId && t.OrganizationId == orgId))
            .ExecuteDeleteAsync(ct);

    internal static Task RemoveCommunityTagMembershipsAsync(NexusDbContext db, Guid communityId, Guid userId, CancellationToken ct) =>
        db.PeopleTagMembers
            .Where(tm => tm.UserId == userId &&
                         db.PeopleTags.Any(t => t.Id == tm.TagId && t.CommunityId == communityId))
            .ExecuteDeleteAsync(ct);

    private static bool CanManageOrgTags(OrganizationMembership actor) =>
        actor.IsOwner || actor.OrgRole is OrgRole.SystemAdmin or OrgRole.CommsAdmin;

    private static async Task<IResult> HandleListOrgTagsAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        string? q,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        return await ListTagsCoreAsync(db, actor.OrganizationId, communityId: null, includeOrgTags: false,
            canManage: CanManageOrgTags(actor), q, cursor, limit, ct);
    }

    private static async Task<IResult> HandleListCommunityTagsAsync(
        HttpContext ctx,
        Guid communityId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        bool includeOrgTags = false,
        string? q = null,
        string? cursor = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var (community, myMember, readAllowed, canManage) = await ResolveCommunityTagAccessAsync(db, authz, actor, communityId, ct);
        if (community is null || !readAllowed) return Results.Forbid();

        return await ListTagsCoreAsync(db, actor.OrganizationId, communityId, includeOrgTags, canManage, q, cursor, limit, ct);
    }

    private static async Task<IResult> ListTagsCoreAsync(
        NexusDbContext db,
        Guid orgId,
        Guid? communityId,
        bool includeOrgTags,
        bool canManage,
        string? q,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        if (!PaginationGuard.TryDecodeCursor<PaginationGuard.NameCursor>(cursor, out var after) ||
            after is not null && (after.Name is null || after.Id == Guid.Empty))
            return PaginationGuard.InvalidCursor();

        IQueryable<PeopleTagEntity> query = db.PeopleTags.AsNoTracking().Where(t => t.OrganizationId == orgId);
        if (communityId is { } commId)
        {
            query = includeOrgTags
                ? query.Where(t => t.CommunityId == commId || t.CommunityId == null)
                : query.Where(t => t.CommunityId == commId);
        }
        else
        {
            query = query.Where(t => t.CommunityId == null);
        }

        var search = q?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{EscapeLike(search)}%";
            query = query.Where(t => EF.Functions.ILike(t.Name, pattern) || EF.Functions.ILike(t.Slug, pattern));
        }

        if (after is not null)
        {
            query = query.Where(t => string.Compare(t.Name, after.Name) > 0 ||
                (t.Name == after.Name && t.Id.CompareTo(after.Id) > 0));
        }

        var rows = await query
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);

        var tagIds = rows.Select(t => t.Id).ToArray();
        var counts = tagIds.Length == 0
            ? new Dictionary<Guid, int>()
            : await db.PeopleTagMembers.AsNoTracking()
                .Where(tm => tagIds.Contains(tm.TagId))
                .GroupBy(tm => tm.TagId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var items = rows.Select(t => ToTagDto(t, counts.GetValueOrDefault(t.Id))).ToList();
        var nextCursor = hasMore ? PaginationGuard.EncodeCursor(new PaginationGuard.NameCursor(rows[^1].Name, rows[^1].Id)) : null;

        return Results.Ok(new { items, nextCursor, canManage });
    }

    private static async Task<IResult> HandleCreateOrgTagAsync(
        HttpContext ctx,
        CreatePeopleTagRequest request,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!CanManageOrgTags(actor)) return Results.Forbid();

        return await CreateTagCoreAsync(ctx, db, actor, community: null, request, ct);
    }

    private static async Task<IResult> HandleCreateCommunityTagAsync(
        HttpContext ctx,
        Guid communityId,
        CreatePeopleTagRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var (community, _, _, canManage) = await ResolveCommunityTagAccessAsync(db, authz, actor, communityId, ct);
        if (community is null || !canManage) return Results.Forbid();

        return await CreateTagCoreAsync(ctx, db, actor, community, request, ct);
    }

    private static async Task<IResult> CreateTagCoreAsync(
        HttpContext ctx,
        NexusDbContext db,
        OrganizationMembership actor,
        CommunityEntity? community,
        CreatePeopleTagRequest request,
        CancellationToken ct)
    {
        if (!PeopleTagValidator.TryNormalizeName(request.Name, out var name, out var nameErr))
            return Results.BadRequest(new { code = "invalid_tag_name", message = nameErr });
        if (!PeopleTagValidator.TryNormalizeSlug(request.Slug, out var slug, out var slugErr))
            return Results.BadRequest(new { code = "invalid_tag_slug", message = slugErr });
        if (!PeopleTagValidator.TryNormalizeDescription(request.Description, out var description, out var descErr))
            return Results.BadRequest(new { code = "invalid_tag_description", message = descErr });

        var initialIds = (request.MemberUserIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (initialIds.Length > PaginationGuard.MaxPageSize)
            return Results.BadRequest(new { code = "too_many_members", message = $"At most {PaginationGuard.MaxPageSize} initial members may be added at once." });

        if (initialIds.Length > 0)
        {
            var eligibleCount = await EligibleMemberIdsQuery(db, actor.OrganizationId, community)
                .CountAsync(id => initialIds.Contains(id), ct);
            if (eligibleCount != initialIds.Length)
                return Results.BadRequest(new { code = "ineligible_tag_member", message = "One or more selected users are not eligible members for this tag scope." });
        }

        var now = DateTimeOffset.UtcNow;
        var tag = new PeopleTagEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            CommunityId = community?.Id,
            Name = name,
            Slug = slug,
            Description = description,
            CreatedByUserId = actor.Id,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.PeopleTags.Add(tag);
        foreach (var uid in initialIds)
        {
            db.PeopleTagMembers.Add(new PeopleTagMemberEntity
            {
                TagId = tag.Id,
                UserId = uid,
                AddedByUserId = actor.Id,
                AddedAt = now
            });
        }

        WriteAudit(db, ctx, actor.OrganizationId, actor.Id, "PEOPLE_TAG_CREATED", tag.Id,
            new { communityId = community?.Id, slug, memberCount = initialIds.Length }, now);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { code = "tag_slug_exists", message = $"A tag with handle '@{slug}' already exists in this scope." });
        }

        var location = community is null
            ? $"/api/organizations/{actor.OrganizationId}/tags/{tag.Id}"
            : $"/api/organizations/{actor.OrganizationId}/communities/{community.Id}/tags/{tag.Id}";

        return Results.Created(location, ToTagDto(tag, initialIds.Length));
    }

    private static async Task<IResult> HandleUpdateOrgTagAsync(
        HttpContext ctx,
        Guid tagId,
        UpdatePeopleTagRequest request,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!CanManageOrgTags(actor)) return Results.Forbid();

        return await UpdateTagCoreAsync(ctx, db, actor, communityId: null, tagId, request, ct);
    }

    private static async Task<IResult> HandleUpdateCommunityTagAsync(
        HttpContext ctx,
        Guid communityId,
        Guid tagId,
        UpdatePeopleTagRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var (community, _, _, canManage) = await ResolveCommunityTagAccessAsync(db, authz, actor, communityId, ct);
        if (community is null || !canManage) return Results.Forbid();

        return await UpdateTagCoreAsync(ctx, db, actor, communityId, tagId, request, ct);
    }

    private static async Task<IResult> UpdateTagCoreAsync(
        HttpContext ctx,
        NexusDbContext db,
        OrganizationMembership actor,
        Guid? communityId,
        Guid tagId,
        UpdatePeopleTagRequest request,
        CancellationToken ct)
    {
        if (!PeopleTagValidator.TryNormalizeName(request.Name, out var name, out var nameErr))
            return Results.BadRequest(new { code = "invalid_tag_name", message = nameErr });
        if (!PeopleTagValidator.TryNormalizeSlug(request.Slug, out var slug, out var slugErr))
            return Results.BadRequest(new { code = "invalid_tag_slug", message = slugErr });
        if (!PeopleTagValidator.TryNormalizeDescription(request.Description, out var description, out var descErr))
            return Results.BadRequest(new { code = "invalid_tag_description", message = descErr });

        var tag = await db.PeopleTags
            .SingleOrDefaultAsync(t => t.Id == tagId && t.OrganizationId == actor.OrganizationId && t.CommunityId == communityId, ct);
        if (tag is null) return Results.NotFound(new { code = "tag_not_found" });

        var now = DateTimeOffset.UtcNow;
        tag.Name = name;
        tag.Slug = slug;
        tag.Description = description;
        tag.UpdatedAt = now;

        WriteAudit(db, ctx, actor.OrganizationId, actor.Id, "PEOPLE_TAG_UPDATED", tag.Id,
            new { communityId, slug }, now);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { code = "tag_slug_exists", message = $"A tag with handle '@{slug}' already exists in this scope." });
        }

        var memberCount = await db.PeopleTagMembers.CountAsync(tm => tm.TagId == tag.Id, ct);
        return Results.Ok(ToTagDto(tag, memberCount));
    }

    private static async Task<IResult> HandleDeleteOrgTagAsync(
        HttpContext ctx,
        Guid tagId,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!CanManageOrgTags(actor)) return Results.Forbid();

        return await DeleteTagCoreAsync(ctx, db, actor, communityId: null, tagId, ct);
    }

    private static async Task<IResult> HandleDeleteCommunityTagAsync(
        HttpContext ctx,
        Guid communityId,
        Guid tagId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var (community, _, _, canManage) = await ResolveCommunityTagAccessAsync(db, authz, actor, communityId, ct);
        if (community is null || !canManage) return Results.Forbid();

        return await DeleteTagCoreAsync(ctx, db, actor, communityId, tagId, ct);
    }

    private static async Task<IResult> DeleteTagCoreAsync(
        HttpContext ctx,
        NexusDbContext db,
        OrganizationMembership actor,
        Guid? communityId,
        Guid tagId,
        CancellationToken ct)
    {
        var tag = await db.PeopleTags
            .SingleOrDefaultAsync(t => t.Id == tagId && t.OrganizationId == actor.OrganizationId && t.CommunityId == communityId, ct);
        if (tag is null) return Results.NotFound(new { code = "tag_not_found" });

        db.PeopleTags.Remove(tag);
        WriteAudit(db, ctx, actor.OrganizationId, actor.Id, "PEOPLE_TAG_DELETED", tag.Id,
            new { communityId, tag.Slug }, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> HandleListOrgTagMembersAsync(
        HttpContext ctx,
        Guid tagId,
        [FromServices] NexusDbContext db,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        return await ListTagMembersCoreAsync(db, actor.OrganizationId, communityId: null, tagId, cursor, limit, ct);
    }

    private static async Task<IResult> HandleListCommunityTagMembersAsync(
        HttpContext ctx,
        Guid communityId,
        Guid tagId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var (community, _, readAllowed, _) = await ResolveCommunityTagAccessAsync(db, authz, actor, communityId, ct);
        if (community is null || !readAllowed) return Results.Forbid();

        return await ListTagMembersCoreAsync(db, actor.OrganizationId, communityId, tagId, cursor, limit, ct);
    }

    private static async Task<IResult> ListTagMembersCoreAsync(
        NexusDbContext db,
        Guid orgId,
        Guid? communityId,
        Guid tagId,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        if (!PaginationGuard.TryDecodeCursor<PaginationGuard.NameCursor>(cursor, out var after) ||
            after is not null && (after.Name is null || after.Id == Guid.Empty))
            return PaginationGuard.InvalidCursor();

        var exists = await db.PeopleTags.AsNoTracking()
            .AnyAsync(t => t.Id == tagId && t.OrganizationId == orgId && t.CommunityId == communityId, ct);
        if (!exists) return Results.NotFound(new { code = "tag_not_found" });

        var query = db.PeopleTagMembers.AsNoTracking()
            .Where(tm => tm.TagId == tagId)
            .Join(
                db.ActiveScopePeople.AsNoTracking(),
                tm => tm.UserId,
                u => u.Id,
                (tm, u) => new
                {
                    userId = u.Id,
                    username = u.UserName ?? string.Empty,
                    displayName = u.DisplayName,
                    isUnverified = u.TrustLevel == TrustLevel.Unverified,
                    addedAt = tm.AddedAt
                });

        if (after is not null)
        {
            query = query.Where(x => string.Compare(x.displayName, after.Name) > 0 ||
                (x.displayName == after.Name && x.userId.CompareTo(after.Id) > 0));
        }

        var items = await query
            .OrderBy(x => x.displayName)
            .ThenBy(x => x.userId)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(pageSize);
        var nextCursor = hasMore ? PaginationGuard.EncodeCursor(new PaginationGuard.NameCursor(items[^1].displayName, items[^1].userId)) : null;

        return Results.Ok(new { items, nextCursor });
    }

    private static async Task<IResult> HandleAddOrgTagMemberAsync(
        HttpContext ctx,
        Guid tagId,
        AddPeopleTagMemberRequest request,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!CanManageOrgTags(actor)) return Results.Forbid();

        return await AddTagMemberCoreAsync(db, actor, community: null, tagId, request.UserId, ct);
    }

    private static async Task<IResult> HandleAddCommunityTagMemberAsync(
        HttpContext ctx,
        Guid communityId,
        Guid tagId,
        AddPeopleTagMemberRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var (community, _, _, canManage) = await ResolveCommunityTagAccessAsync(db, authz, actor, communityId, ct);
        if (community is null || !canManage) return Results.Forbid();

        return await AddTagMemberCoreAsync(db, actor, community, tagId, request.UserId, ct);
    }

    private static async Task<IResult> AddTagMemberCoreAsync(
        NexusDbContext db,
        OrganizationMembership actor,
        CommunityEntity? community,
        Guid tagId,
        Guid targetUserId,
        CancellationToken ct)
    {
        var commId = community?.Id;
        var exists = await db.PeopleTags.AsNoTracking()
            .AnyAsync(t => t.Id == tagId && t.OrganizationId == actor.OrganizationId && t.CommunityId == commId, ct);
        if (!exists) return Results.NotFound(new { code = "tag_not_found" });

        var eligible = await EligibleMemberIdsQuery(db, actor.OrganizationId, community)
            .AnyAsync(id => id == targetUserId, ct);
        if (!eligible)
            return Results.BadRequest(new { code = "ineligible_tag_member", message = "Target user is not an eligible member for this tag scope." });

        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO people_tag_members (tag_id, user_id, added_by_user_id, added_at)
            VALUES ({tagId}, {targetUserId}, {actor.Id}, {now})
            ON CONFLICT (tag_id, user_id) DO NOTHING
            """, ct);

        return Results.Ok(new { tagId, userId = targetUserId, added = true });
    }

    private static async Task<IResult> HandleRemoveOrgTagMemberAsync(
        HttpContext ctx,
        Guid tagId,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!CanManageOrgTags(actor)) return Results.Forbid();

        return await RemoveTagMemberCoreAsync(db, actor.OrganizationId, communityId: null, tagId, targetUserId, ct);
    }

    private static async Task<IResult> HandleRemoveCommunityTagMemberAsync(
        HttpContext ctx,
        Guid communityId,
        Guid tagId,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var (community, _, _, canManage) = await ResolveCommunityTagAccessAsync(db, authz, actor, communityId, ct);
        if (community is null || !canManage) return Results.Forbid();

        return await RemoveTagMemberCoreAsync(db, actor.OrganizationId, communityId, tagId, targetUserId, ct);
    }

    private static async Task<IResult> RemoveTagMemberCoreAsync(
        NexusDbContext db,
        Guid orgId,
        Guid? communityId,
        Guid tagId,
        Guid targetUserId,
        CancellationToken ct)
    {
        var exists = await db.PeopleTags.AsNoTracking()
            .AnyAsync(t => t.Id == tagId && t.OrganizationId == orgId && t.CommunityId == communityId, ct);
        if (!exists) return Results.NotFound(new { code = "tag_not_found" });

        await db.PeopleTagMembers
            .Where(tm => tm.TagId == tagId && tm.UserId == targetUserId)
            .ExecuteDeleteAsync(ct);

        return Results.NoContent();
    }

    private static async Task<(CommunityEntity? Community, CommunityMemberEntity? Member, bool ReadAllowed, bool CanManage)>
        ResolveCommunityTagAccessAsync(
            NexusDbContext db,
            IAuthorizationService authz,
            OrganizationMembership actor,
            Guid communityId,
            CancellationToken ct)
    {
        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
        if (community is null) return (null, null, false, false);

        var myMember = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == communityId && m.UserId == actor.Id && m.LeftAt == null, ct);
        var resource = new ResourceContext(
            ResourceKind.Community,
            actor.OrganizationId,
            MembershipRole: myMember?.Role,
            IsOfficial: community.IsOfficial,
            Visibility: community.Visibility);

        var readAllowed = authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, resource);
        var canManage = authz.Can(actor.ToActorContext(), ResourceAction.CommunityManageSettings, resource);
        return (community, myMember, readAllowed, canManage);
    }

    private static IQueryable<Guid> EligibleMemberIdsQuery(NexusDbContext db, Guid orgId, CommunityEntity? community)
    {
        if (community is null)
        {
            return db.OrganizationMemberships.AsNoTracking()
                .Where(m => m.OrganizationId == orgId && m.Status == AccountStatus.Active && m.Account.Status == AccountStatus.Active)
                .Select(m => m.Id);
        }

        var query = db.CommunityMembers.AsNoTracking()
            .Where(cm => cm.CommunityId == community.Id && cm.LeftAt == null)
            .Join(
                db.OrganizationMemberships.AsNoTracking().Where(m => m.OrganizationId == orgId && m.Status == AccountStatus.Active && m.Account.Status == AccountStatus.Active),
                cm => cm.UserId,
                m => m.Id,
                (_, m) => m);

        if (community.IsOfficial)
        {
            query = query.Where(m => m.TrustLevel == TrustLevel.Verified);
        }

        return query.Select(m => m.Id);
    }

    private static object ToTagDto(PeopleTagEntity tag, int memberCount) => new
    {
        id = tag.Id,
        organizationId = tag.OrganizationId,
        communityId = tag.CommunityId,
        scope = tag.CommunityId is null ? "organization" : "community",
        name = tag.Name,
        slug = tag.Slug,
        description = tag.Description,
        memberCount,
        createdAt = tag.CreatedAt,
        updatedAt = tag.UpdatedAt
    };

    private static void WriteAudit(
        NexusDbContext db,
        HttpContext ctx,
        Guid orgId,
        Guid actorId,
        string action,
        Guid tagId,
        object detail,
        DateTimeOffset now)
    {
        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = orgId,
            ActorUserId = actorId,
            Action = action,
            ResourceType = "PeopleTag",
            ResourceId = tagId.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(detail, AuditJson),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}
