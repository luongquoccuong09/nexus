using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Org;

public static class OrgEndpoints
{
    public sealed record CsvImportRequest(string CsvContent);

    public static IEndpointRouteBuilder MapOrgEndpoints(this IEndpointRouteBuilder app)
    {
        var org = app.MapGroup(OrganizationScope.Api + "/org");

        org.MapGet("/departments", HandleListDepartmentsAsync);
        org.MapPost("/employees/import", HandleImportEmployeesAsync);
        org.MapGet("/employees/match-suggestions", HandleGetMatchSuggestionsAsync);

        return app;
    }

    private static async Task<IResult> HandleListDepartmentsAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var actor = await db.FindOrganizationActorAsync(userId, ct);
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

        if (!PaginationGuard.TryDecodeCursor<PaginationGuard.NameCursor>(cursor, out var after) ||
            after is not null && (after.Name is null || after.Id == Guid.Empty))
        {
            return PaginationGuard.InvalidCursor();
        }

        var query = db.Departments
            .AsNoTracking()
            .Where(d => d.OrganizationId == actor.OrganizationId);

        if (after is not null)
        {
            query = query.Where(d => string.Compare(d.Name, after.Name) > 0 ||
                (d.Name == after.Name && d.Id.CompareTo(after.Id) > 0));
        }

        var items = await query
            .OrderBy(d => d.Name)
            .ThenBy(d => d.Id)
            .Take(pageSize + 1)
            .Select(d => new { id = d.Id, code = d.Code, name = d.Name })
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(pageSize);
        }

        var nextCursor = hasMore
            ? PaginationGuard.EncodeCursor(new PaginationGuard.NameCursor(items[^1].name, items[^1].id))
            : null;

        return Results.Ok(new { items, nextCursor });
    }

    private static async Task<IResult> HandleImportEmployeesAsync(
        HttpContext ctx,
        CsvImportRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        bool dryRun = true,
        CancellationToken ct = default)
    {
        var (actor, errorResult) = await AuthorizeHrAdminWithMfaAsync(
            ctx, db, authz, ResourceAction.AdminImportDirectory, ResourceKind.Organization);
        if (errorResult is not null)
        {
            return errorResult;
        }

        var report = await HrCsvImportService.ImportAsync(
            db, actor!.OrganizationId, request.CsvContent ?? string.Empty, dryRun, ct);

        if (report.Errors.Count > 0)
        {
            return Results.BadRequest(report);
        }

        if (!dryRun)
        {
            db.AuditLogs.Add(new AuditLogEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = actor.OrganizationId,
                ActorUserId = actor.Id,
                Action = "HR_DIRECTORY_IMPORT",
                ResourceType = "Organization",
                ResourceId = actor.OrganizationId.ToString(),
                Result = "Allowed",
                DetailJson = JsonSerializer.Serialize(new
                {
                    report.TotalRows,
                    report.AddedCount,
                    report.UpdatedCount,
                    report.UnchangedCount,
                    report.DeactivatedCount
                }),
                IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(report);
    }

    private static async Task<IResult> HandleGetMatchSuggestionsAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        var (actor, errorResult) = await AuthorizeHrAdminWithMfaAsync(
            ctx, db, authz, ResourceAction.AdminVerifyAccount, ResourceKind.UserAccount);
        if (errorResult is not null)
        {
            return errorResult;
        }

        var suggestions = await HrCsvImportService.GetMatchSuggestionsAsync(db, actor!.OrganizationId, ct);
        return Results.Ok(new { items = suggestions });
    }

    private static async Task<(OrganizationMembership? Actor, IResult? ErrorResult)> AuthorizeHrAdminWithMfaAsync(
        HttpContext ctx,
        NexusDbContext db,
        IAuthorizationService authz,
        ResourceAction action,
        ResourceKind kind)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return (null, Results.Unauthorized());
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ctx.RequestAborted);
        if (actor is null)
        {
            return (null, Results.Unauthorized());
        }

        if (!authz.Can(actor.ToActorContext(), action, new ResourceContext(kind, actor.OrganizationId)))
        {
            return (null, Results.Forbid());
        }

        if (actor.RequiresMfa)
        {
            var mfaClaim = string.Equals(
                ctx.User.FindFirstValue(ClaimsPrincipalExtensions.MfaVerifiedClaimType),
                "true",
                StringComparison.Ordinal);

            if (!mfaClaim)
            {
                return (null, Results.Json(
                    new { code = "admin_mfa_required", message = "TOTP multi-factor authentication is required for administrative actions." },
                    statusCode: StatusCodes.Status403Forbidden));
            }
        }

        return (actor, null);
    }
}
