using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;
using Nexus.Web.Modules.Conversations;

namespace Nexus.Web.Modules.People;

public static class PeopleEndpoints
{
    private sealed record DirectoryCursor(string Name, Guid Id, double Score);
    public const int MaxCustomStatusLength = 100;

    public sealed record UpdateCustomStatusRequest(string? CustomStatus);

    public sealed record DirectoryProfileDto(
        Guid Id,
        string Username,
        string DisplayName,
        string TrustLevel,
        bool IsUnverified,
        Guid? DepartmentId,
        string? DepartmentName,
        string? BranchName,
        string? CustomStatus,
        string? AvatarUrl);

    public static IEndpointRouteBuilder MapPeopleEndpoints(this IEndpointRouteBuilder app)
    {
        var people = app.MapGroup(OrganizationScope.Api + "/people");

        people.MapGet("/directory", HandleDirectorySearchAsync);
        people.MapPut("/me/status", HandleUpdateCustomStatusAsync);
        people.MapPost("/me/avatar", HandleUploadAvatarAsync);
        people.MapDelete("/me/avatar", HandleDeleteAvatarAsync);
        people.MapGet("/{targetUserId:guid}/avatar", HandleGetAvatarAsync);

        return app;
    }

    private static async Task<IResult> HandleDirectorySearchAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        string? q,
        Guid? departmentId,
        Guid? excludeCommunityId,
        string? cursor,
        int? limit,
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

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }
        if (!PaginationGuard.TryDecodeCursor<DirectoryCursor>(cursor, out var after) ||
            after is not null && (after.Name is null || after.Id == Guid.Empty || !double.IsFinite(after.Score)))
            return PaginationGuard.InvalidCursor();

        // Hide Disabled and Former accounts strictly in SQL (M1.10).
        var baseQuery = db.ActiveScopePeople.AsNoTracking();

        if (excludeCommunityId is { } communityId)
        {
            var community = await db.Communities.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == communityId && c.OrganizationId == actor.OrganizationId, ct);
            var role = await db.CommunityMembers.AsNoTracking()
                .Where(m => m.CommunityId == communityId && m.UserId == actorId && m.LeftAt == null)
                .Select(m => (ResourceRole?)m.Role).SingleOrDefaultAsync(ct);
            if (community is null || !authz.Can(actor.ToActorContext(), ResourceAction.CommunityAddOrRemoveMember,
                new ResourceContext(ResourceKind.Community, actor.OrganizationId, MembershipRole: role, IsOfficial: community.IsOfficial)))
                return Results.Forbid();
            baseQuery = baseQuery.Where(u => (!community.IsOfficial || u.TrustLevel == TrustLevel.Verified) &&
                !db.CommunityMembers.Any(m => m.CommunityId == communityId && m.UserId == u.Id && m.LeftAt == null));
        }

        if (departmentId.HasValue)
        {
            baseQuery = baseQuery.Where(u => u.DepartmentId == departmentId.Value);
        }

        var normalizedQuery = HrCsvImportService.NormalizeVietnamese(q ?? string.Empty);

        var searching = normalizedQuery.Length > 0;
        var ranked = baseQuery.Select(u => new
        {
            User = u,
            Score = searching
                ? EF.Functions.TrigramsSimilarity(NexusDbContext.ImmutableUnaccent(u.DisplayName.ToLower()), normalizedQuery)
                : 0d
        });
        if (searching)
        {
            var likePattern = $"%{EscapeLike(normalizedQuery)}%";
            ranked = ranked.Where(r =>
                EF.Functions.ILike(NexusDbContext.ImmutableUnaccent(r.User.DisplayName.ToLower()), likePattern) ||
                EF.Functions.ILike(NexusDbContext.ImmutableUnaccent((r.User.UserName ?? string.Empty).ToLower()), likePattern) ||
                r.Score >= 0.25);
        }
        if (after is not null)
        {
            ranked = ranked.Where(r => r.Score < after.Score || r.Score == after.Score &&
                (string.Compare(r.User.DisplayName, after.Name) > 0 ||
                 r.User.DisplayName == after.Name && r.User.Id.CompareTo(after.Id) > 0));
        }

        var rows = await ranked
            .OrderByDescending(r => r.Score).ThenBy(r => r.User.DisplayName).ThenBy(r => r.User.Id)
            .Take(pageSize + 1)
            .Select(r => new
            {
                r.User.Id,
                Username = r.User.UserName ?? string.Empty,
                r.User.DisplayName,
                r.User.TrustLevel,
                r.User.DepartmentId,
                r.User.BranchId,
                r.User.CustomStatus,
                r.User.AvatarObjectKey,
                r.Score
            })
            .ToListAsync(ct);
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);

        var deptIds = rows.Where(r => r.DepartmentId.HasValue).Select(r => r.DepartmentId!.Value).Distinct().ToList();
        var branchIds = rows.Where(r => r.BranchId.HasValue).Select(r => r.BranchId!.Value).Distinct().ToList();

        var deptNames = deptIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var branchNames = branchIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        var items = rows.Select(r => new DirectoryProfileDto(
            r.Id,
            r.Username,
            r.DisplayName,
            r.TrustLevel.ToString(),
            r.TrustLevel == TrustLevel.Unverified,
            r.DepartmentId,
            r.DepartmentId.HasValue && deptNames.TryGetValue(r.DepartmentId.Value, out var dn) ? dn : null,
            r.BranchId.HasValue && branchNames.TryGetValue(r.BranchId.Value, out var bn) ? bn : null,
            r.CustomStatus,
            string.IsNullOrEmpty(r.AvatarObjectKey) ? null : $"/api/organizations/{actor.OrganizationId}/people/{r.Id}/avatar"))
            .ToList();

        var nextCursor = hasMore ? PaginationGuard.EncodeCursor(new DirectoryCursor(rows[^1].DisplayName, rows[^1].Id, rows[^1].Score)) : null;
        return Results.Ok(new { items, nextCursor });
    }

    private static async Task<IResult> HandleUpdateCustomStatusAsync(
        HttpContext ctx,
        UpdateCustomStatusRequest request,
        [FromServices] NexusDbContext db)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await db.FindOrganizationActorAsync(userId, ctx.RequestAborted);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var cleaned = SanitizeCustomStatus(request.CustomStatus);
        if (cleaned is not null && cleaned.Length > MaxCustomStatusLength)
        {
            return Results.BadRequest(new
            {
                code = "invalid_custom_status",
                message = $"Custom status must be at most {MaxCustomStatusLength} characters."
            });
        }

        user.CustomStatus = cleaned;
        await db.SaveChangesAsync();
        return Results.Ok(new { customStatus = user.CustomStatus });
    }

    private static async Task<IResult> HandleUploadAvatarAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IFileStorage storage,
        IFileScanner scanner,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await db.FindOrganizationActorAsync(userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        Stream sourceStream;
        var fileName = "avatar";

        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { code = "missing_avatar", message = "Upload an image file." });
            }

            fileName = file.FileName;
            sourceStream = file.OpenReadStream();
        }
        else
        {
            sourceStream = ctx.Request.Body;
        }

        MemoryStream reencoded;
        try
        {
            await using (sourceStream)
            {
                reencoded = await AvatarProcessor.ReencodeToPngAsync(sourceStream, ct);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { code = "invalid_avatar_image", message = ex.Message });
        }

        await using (reencoded)
        {
            var scan = await scanner.ScanAsync(reencoded, fileName, ct);
            if (scan.Verdict is FileScanVerdict.Infected or FileScanVerdict.FailedClosed)
            {
                return Results.BadRequest(new { code = "avatar_scan_rejected", message = "Avatar failed security scanning." });
            }

            reencoded.Position = 0;
            var stored = await storage.SaveAsync(reencoded, "image/png", ct);

            var previousKey = user.AvatarObjectKey;
            user.AvatarObjectKey = stored.ObjectKey;
            await db.SaveChangesAsync(ct);

            if (!string.IsNullOrEmpty(previousKey) && !string.Equals(previousKey, stored.ObjectKey, StringComparison.Ordinal))
            {
                await storage.DeleteAsync(previousKey, ct);
            }

            return Results.Ok(new
            {
                avatarUrl = $"/api/organizations/{user.OrganizationId}/people/{user.Id}/avatar",
                sizeBytes = stored.SizeBytes,
                sha256 = stored.Sha256Hex,
                scanStatus = scan.StatusCode
            });
        }
    }

    private static async Task<IResult> HandleDeleteAvatarAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IFileStorage storage,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await db.FindOrganizationActorAsync(userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var previousKey = user.AvatarObjectKey;
        user.AvatarObjectKey = null;
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(previousKey))
        {
            await storage.DeleteAsync(previousKey, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> HandleGetAvatarAsync(
        HttpContext ctx,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        IFileStorage storage,
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

        var target = await db.ActiveScopePeople
            .AsNoTracking()
            .Where(u => u.Id == targetUserId)
            .Select(u => new { u.AvatarObjectKey })
            .SingleOrDefaultAsync(ct);

        if (target is null || string.IsNullOrEmpty(target.AvatarObjectKey))
        {
            return Results.NotFound();
        }

        var stream = await storage.OpenReadAsync(target.AvatarObjectKey, ct);
        if (stream is null)
        {
            return Results.NotFound();
        }

        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.CacheControl = "private, max-age=300";
        return Results.Stream(stream, "image/png");
    }

    private static string? SanitizeCustomStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var filtered = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(filtered) ? null : filtered;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}
