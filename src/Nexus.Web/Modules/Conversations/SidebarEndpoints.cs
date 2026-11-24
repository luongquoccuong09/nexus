using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Conversations;

public static class SidebarEndpoints
{
    public const string ChatSurface = "chat";
    public const string CommunitiesSurface = "communities";
    private const int MaxSectionsPerSurface = PaginationGuard.MaxPageSize;
    private const int MaxSectionNameLength = 80;

    public sealed record CreateSectionRequest(string Surface, string Name);
    public sealed record UpdateSectionRequest(string? Name, int? SortOrder);
    public sealed record UpdateSidebarItemRequest(
        string Surface,
        bool? IsFavorite,
        Guid? SectionId,
        bool ClearSection = false,
        bool? IsHidden = null,
        int? SortOrder = null);
    public sealed record ReorderSidebarRequest(
        string Surface,
        Guid[]? OrderedTargetIds,
        Guid[]? OrderedSectionIds);

    public static IEndpointRouteBuilder MapSidebarEndpoints(this IEndpointRouteBuilder app)
    {
        var sidebar = app.MapGroup("/api/sidebar");

        sidebar.MapGet("", HandleGetSidebarAsync);
        sidebar.MapPost("/sections", HandleCreateSectionAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        sidebar.MapPut("/sections/{sectionId:guid}", HandleUpdateSectionAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        sidebar.MapDelete("/sections/{sectionId:guid}", HandleDeleteSectionAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        sidebar.MapPut("/items/{targetId:guid}", HandleUpsertItemAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        sidebar.MapPut("/reorder", HandleReorderAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);

        return app;
    }

    private static string? NormalizeSurface(string? surface) => surface?.Trim().ToLowerInvariant() switch
    {
        ChatSurface => ChatSurface,
        CommunitiesSurface => CommunitiesSurface,
        _ => null
    };

    private static async Task<IResult> HandleGetSidebarAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        string? surface,
        CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!ctx.User.TryGetUserId(out var actorId) || await db.FindPersonalActorAsync(actorId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var normalized = NormalizeSurface(surface);
        if (normalized is null)
        {
            return Results.BadRequest(new { code = "invalid_surface", message = "Surface must be 'chat' or 'communities'." });
        }

        var sections = await db.UserSidebarSections.AsNoTracking()
            .Where(s => s.UserId == actorId && s.Surface == normalized)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Take(MaxSectionsPerSurface)
            .Select(s => new
            {
                s.Id,
                s.Surface,
                s.Name,
                s.SortOrder
            })
            .ToListAsync(ct);

        // Enforce live membership checks on read so revoked conversations or communities vanish immediately.
        var itemsQuery = db.UserSidebarItems.AsNoTracking()
            .Where(i => i.UserId == actorId && i.Surface == normalized);

        if (normalized == ChatSurface)
        {
            itemsQuery = itemsQuery.Where(i =>
                db.ConversationMembers.Any(m =>
                    m.ConversationId == i.TargetId &&
                    m.UserId == actorId &&
                    m.LeftAt == null &&
                    db.Conversations.Any(c =>
                        c.Id == m.ConversationId &&
                        c.Kind != ConversationKind.Channel &&
                        (c.Kind != ConversationKind.Self || c.CreatedByUserId == actorId) &&
                        (c.OrganizationId == null ||
                         db.OrganizationMemberships.Any(om =>
                             om.OrganizationId == c.OrganizationId &&
                             om.Id == actorId &&
                             om.Status == AccountStatus.Active)))));
        }
        else
        {
            itemsQuery = itemsQuery.Where(i =>
                db.CommunityMembers.Any(cm =>
                    cm.CommunityId == i.TargetId &&
                    cm.UserId == actorId &&
                    cm.LeftAt == null &&
                    db.Communities.Any(c =>
                        c.Id == cm.CommunityId &&
                        db.OrganizationMemberships.Any(om =>
                            om.OrganizationId == c.OrganizationId &&
                            om.Id == actorId &&
                            om.Status == AccountStatus.Active &&
                            (!c.IsOfficial || om.TrustLevel == TrustLevel.Verified)))));
        }

        var items = await itemsQuery
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.TargetId)
            .Select(i => new
            {
                i.TargetId,
                i.Surface,
                i.IsFavorite,
                i.SectionId,
                i.IsHidden,
                i.SortOrder
            })
            .ToListAsync(ct);

        return Results.Ok(new { surface = normalized, sections, items });
    }

    private static async Task<IResult> HandleCreateSectionAsync(
        HttpContext ctx,
        CreateSectionRequest request,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId) || await db.FindPersonalActorAsync(actorId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var surface = NormalizeSurface(request.Surface);
        if (surface is null)
        {
            return Results.BadRequest(new { code = "invalid_surface", message = "Surface must be 'chat' or 'communities'." });
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > MaxSectionNameLength)
        {
            return Results.BadRequest(new { code = "invalid_section_name", message = $"Section name must be 1–{MaxSectionNameLength} characters." });
        }

        var existingCount = await db.UserSidebarSections.CountAsync(s => s.UserId == actorId && s.Surface == surface, ct);
        if (existingCount >= MaxSectionsPerSurface)
        {
            return Results.BadRequest(new { code = "section_limit_reached", message = $"You can create at most {MaxSectionsPerSurface} sections." });
        }

        var duplicate = await db.UserSidebarSections.AsNoTracking()
            .AnyAsync(s => s.UserId == actorId && s.Surface == surface && EF.Functions.ILike(s.Name, name), ct);
        if (duplicate)
        {
            return Results.Conflict(new { code = "section_name_exists", message = "A section with this name already exists." });
        }

        var maxOrder = await db.UserSidebarSections.AsNoTracking()
            .Where(s => s.UserId == actorId && s.Surface == surface)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(ct) ?? -1;

        var now = DateTimeOffset.UtcNow;
        var section = new UserSidebarSectionEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = actorId,
            Surface = surface,
            Name = name,
            SortOrder = maxOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.UserSidebarSections.Add(section);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/sidebar/sections/{section.Id}", new
        {
            section.Id,
            section.Surface,
            section.Name,
            section.SortOrder
        });
    }

    private static async Task<IResult> HandleUpdateSectionAsync(
        HttpContext ctx,
        Guid sectionId,
        UpdateSectionRequest request,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId) || await db.FindPersonalActorAsync(actorId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var section = await db.UserSidebarSections
            .SingleOrDefaultAsync(s => s.Id == sectionId && s.UserId == actorId, ct);
        if (section is null)
        {
            return Results.NotFound();
        }

        if (request.Name is not null)
        {
            var trimmed = request.Name.Trim();
            if (trimmed.Length is < 1 or > MaxSectionNameLength)
            {
                return Results.BadRequest(new { code = "invalid_section_name", message = $"Section name must be 1–{MaxSectionNameLength} characters." });
            }

            var duplicate = await db.UserSidebarSections.AsNoTracking()
                .AnyAsync(s => s.UserId == actorId && s.Surface == section.Surface && s.Id != section.Id && EF.Functions.ILike(s.Name, trimmed), ct);
            if (duplicate)
            {
                return Results.Conflict(new { code = "section_name_exists", message = "A section with this name already exists." });
            }

            section.Name = trimmed;
        }

        if (request.SortOrder is { } order)
        {
            section.SortOrder = Math.Max(0, order);
        }

        section.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            section.Id,
            section.Surface,
            section.Name,
            section.SortOrder
        });
    }

    private static async Task<IResult> HandleDeleteSectionAsync(
        HttpContext ctx,
        Guid sectionId,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId) || await db.FindPersonalActorAsync(actorId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var deleted = await db.UserSidebarSections
            .Where(s => s.Id == sectionId && s.UserId == actorId)
            .ExecuteDeleteAsync(ct);

        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> HandleUpsertItemAsync(
        HttpContext ctx,
        Guid targetId,
        UpdateSidebarItemRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId) || await db.FindPersonalActorAsync(actorId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var surface = NormalizeSurface(request.Surface);
        if (surface is null || targetId == Guid.Empty)
        {
            return Results.BadRequest(new { code = "invalid_request", message = "Valid surface and targetId are required." });
        }

        if (!await CanAccessTargetAsync(db, authz, actorId, surface, targetId, ct))
        {
            return Results.Forbid();
        }

        if (request.SectionId is { } secId && !request.ClearSection)
        {
            var ownsSection = await db.UserSidebarSections.AsNoTracking()
                .AnyAsync(s => s.Id == secId && s.UserId == actorId && s.Surface == surface, ct);
            if (!ownsSection)
            {
                return Results.BadRequest(new { code = "invalid_section", message = "Section not found." });
            }
        }

        var now = DateTimeOffset.UtcNow;
        var item = await db.UserSidebarItems
            .SingleOrDefaultAsync(i => i.UserId == actorId && i.Surface == surface && i.TargetId == targetId, ct);

        if (item is null)
        {
            item = new UserSidebarItemEntity
            {
                UserId = actorId,
                Surface = surface,
                TargetId = targetId,
                IsFavorite = request.IsFavorite ?? false,
                SectionId = request.ClearSection ? null : request.SectionId,
                IsHidden = request.IsHidden ?? false,
                SortOrder = Math.Max(0, request.SortOrder ?? 0),
                UpdatedAt = now
            };
            db.UserSidebarItems.Add(item);
        }
        else
        {
            if (request.IsFavorite.HasValue) item.IsFavorite = request.IsFavorite.Value;
            if (request.ClearSection) item.SectionId = null;
            else if (request.SectionId.HasValue) item.SectionId = request.SectionId.Value;
            if (request.IsHidden.HasValue) item.IsHidden = request.IsHidden.Value;
            if (request.SortOrder.HasValue) item.SortOrder = Math.Max(0, request.SortOrder.Value);
            item.UpdatedAt = now;
        }

        // Hiding clears favorite only? No — keep favorite intact or keep separate; restoring keeps everything clean.
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            item.TargetId,
            item.Surface,
            item.IsFavorite,
            item.SectionId,
            item.IsHidden,
            item.SortOrder
        });
    }

    private static async Task<IResult> HandleReorderAsync(
        HttpContext ctx,
        ReorderSidebarRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId) || await db.FindPersonalActorAsync(actorId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var surface = NormalizeSurface(request.Surface);
        if (surface is null)
        {
            return Results.BadRequest(new { code = "invalid_surface", message = "Surface must be 'chat' or 'communities'." });
        }

        var targetIds = (request.OrderedTargetIds ?? []).Where(id => id != Guid.Empty).Distinct().Take(200).ToArray();
        var sectionIds = (request.OrderedSectionIds ?? []).Where(id => id != Guid.Empty).Distinct().Take(MaxSectionsPerSurface).ToArray();

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (sectionIds.Length > 0)
        {
            var sections = await db.UserSidebarSections
                .Where(s => s.UserId == actorId && s.Surface == surface && sectionIds.Contains(s.Id))
                .ToListAsync(ct);
            var byId = sections.ToDictionary(s => s.Id);
            for (var idx = 0; idx < sectionIds.Length; idx++)
            {
                if (byId.TryGetValue(sectionIds[idx], out var sec))
                {
                    sec.SortOrder = idx;
                    sec.UpdatedAt = now;
                }
            }
        }

        if (targetIds.Length > 0)
        {
            var existing = await db.UserSidebarItems
                .Where(i => i.UserId == actorId && i.Surface == surface && targetIds.Contains(i.TargetId))
                .ToDictionaryAsync(i => i.TargetId, ct);

            for (var idx = 0; idx < targetIds.Length; idx++)
            {
                var tid = targetIds[idx];
                if (existing.TryGetValue(tid, out var item))
                {
                    item.SortOrder = idx;
                    item.UpdatedAt = now;
                }
                else if (await CanAccessTargetAsync(db, authz, actorId, surface, tid, ct))
                {
                    db.UserSidebarItems.Add(new UserSidebarItemEntity
                    {
                        UserId = actorId,
                        Surface = surface,
                        TargetId = tid,
                        SortOrder = idx,
                        UpdatedAt = now
                    });
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<bool> CanAccessTargetAsync(
        NexusDbContext db,
        IAuthorizationService authz,
        Guid actorId,
        string surface,
        Guid targetId,
        CancellationToken ct)
    {
        if (surface == ChatSurface)
        {
            var conv = await db.Conversations.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == targetId && c.Kind != ConversationKind.Channel, ct);
            var member = await db.ConversationMembers.AsNoTracking()
                .SingleOrDefaultAsync(m => m.ConversationId == targetId && m.UserId == actorId && m.LeftAt == null, ct);
            if (conv is null || member is null) return false;

            var actor = conv.OrganizationId is { } orgId
                ? await db.OrganizationMemberships.AsNoTracking()
                    .SingleOrDefaultAsync(m => m.Id == actorId && m.OrganizationId == orgId && m.Status == AccountStatus.Active, ct)
                : await db.FindPersonalActorAsync(actorId, ct);
            if (actor is null) return false;

            var kind = conv.Kind.ToResourceKind();
            var targetIsSelf = conv.Kind == ConversationKind.Self && conv.CreatedByUserId == actorId;
            return authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(kind, actor.OrganizationId, MembershipRole: member.Role, TargetIsSelf: targetIsSelf));
        }

        var community = await db.Communities.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == targetId, ct);
        var cm = await db.CommunityMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CommunityId == targetId && m.UserId == actorId && m.LeftAt == null, ct);
        if (community is null || cm is null) return false;

        var orgActor = await db.OrganizationMemberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == actorId && m.OrganizationId == community.OrganizationId && m.Status == AccountStatus.Active, ct);
        if (orgActor is null) return false;

        return authz.Can(
            orgActor.ToActorContext(),
            ResourceAction.ConversationRead,
            new ResourceContext(ResourceKind.CommunityChannel, community.OrganizationId, MembershipRole: cm.Role, IsOfficial: community.IsOfficial));
    }

    internal static Task RemoveSidebarItemAsync(NexusDbContext db, Guid userId, string surface, Guid targetId, CancellationToken ct) =>
        db.UserSidebarItems
            .Where(i => i.UserId == userId && i.Surface == surface && i.TargetId == targetId)
            .ExecuteDeleteAsync(ct);

    internal static Task RemoveOrganizationSidebarItemsAsync(NexusDbContext db, Guid orgId, Guid[] userIds, CancellationToken ct) =>
        db.UserSidebarItems
            .Where(i => userIds.Contains(i.UserId) &&
                ((i.Surface == CommunitiesSurface && db.Communities.Any(c => c.Id == i.TargetId && c.OrganizationId == orgId)) ||
                 (i.Surface == ChatSurface && db.Conversations.Any(c => c.Id == i.TargetId && c.OrganizationId == orgId))))
            .ExecuteDeleteAsync(ct);
}
