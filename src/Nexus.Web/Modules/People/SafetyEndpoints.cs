using Nexus.Web.Modules.Org;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;

namespace Nexus.Web.Modules.People;

public static class SafetyEndpoints
{
    public sealed record CreateReportRequest(
        Guid ReportedUserId,
        Guid? ConversationId,
        string Reason,
        string? Details);

    public static IEndpointRouteBuilder MapPeopleSafetyEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        var people = app.MapGroup(prefix + "/people");
        people.MapGet("/blocks", HandleListBlocksAsync);
        people.MapPut("/blocks/{targetUserId:guid}", HandleBlockUserAsync);
        people.MapDelete("/blocks/{targetUserId:guid}", HandleUnblockUserAsync);
        people.MapPost("/reports", HandleReportUserAsync);
        return app;
    }

    private static async Task<IResult> HandleListBlocksAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        int? limit,
        DateTimeOffset? beforeAt,
        Guid? beforeId,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!authz.Can(actor.ToActorContext(), ResourceAction.ManageOwnBlockList,
            new ResourceContext(ResourceKind.UserAccount, db.OrganizationScopeId, TargetIsSelf: true))) return Results.Forbid();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        if (beforeAt.HasValue != beforeId.HasValue)
        {
            return Results.BadRequest(new { code = "invalid_cursor", message = "Both beforeAt and beforeId are required for pagination." });
        }

        var blocks = db.UserBlocks
            .AsNoTracking()
            .Where(b => b.OrganizationId == db.OrganizationScopeId && b.BlockerUserId == actorId);
        if (beforeAt is { } before && beforeId is { } id)
        {
            var utc = before.ToUniversalTime();
            blocks = blocks.Where(b => b.CreatedAt < utc || (b.CreatedAt == utc && b.BlockedUserId.CompareTo(id) < 0));
        }

        var items = await (
            from b in blocks
            join target in db.ActiveScopePeople.AsNoTracking() on b.BlockedUserId equals target.Id into matches
            from target in matches.DefaultIfEmpty()
            orderby b.CreatedAt descending, b.BlockedUserId descending
            select new { b.BlockedUserId, b.CreatedAt, target.DisplayName, Username = target.UserName })
            .Take(pageSize + 1)
            .ToListAsync(ct);
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(pageSize);
        var nextBefore = hasMore ? new { items[^1].CreatedAt, Id = items[^1].BlockedUserId } : null;
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { items, nextBefore });
    }

    private static async Task<IResult> HandleBlockUserAsync(
        HttpContext ctx,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
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

        var target = await db.OrganizationPeople
            .AsNoTracking()
            .Where(u => u.Id == targetUserId)
            .Select(u => new { u.Id, u.Status })
            .SingleOrDefaultAsync(ct);

        var allowed = target is not null && authz.Can(
            actor.ToActorContext(),
            ResourceAction.DmBlockOrReport,
            new ResourceContext(
                ResourceKind.Dm,
                actor.OrganizationId,
                TargetIsActive: target.Status == AccountStatus.Active,
                TargetIsSelf: targetUserId == actorId));

        if (!allowed || (db.IsPersonalScope && !await MayActOnPersonalUserAsync(db, actorId, targetUserId, ct)))
        {
            return Results.Forbid();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exists = await db.UserBlocks.AnyAsync(
            b => b.OrganizationId == db.OrganizationScopeId && b.BlockerUserId == actorId && b.BlockedUserId == targetUserId, ct);

        if (!exists)
        {
            db.UserBlocks.Add(new UserBlockEntity
            {
                BlockerUserId = actorId,
                BlockedUserId = targetUserId,
                OrganizationId = actor.OrganizationId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await DmReadReceipts.BumpPrivacyVersionAsync(db, actorId, ct);
        }
        await transaction.CommitAsync(ct);
        if (!exists) await fanout.NotifyDmReceiptPrivacyAsync(actorId, targetUserId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> HandleUnblockUserAsync(
        HttpContext ctx,
        Guid targetUserId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (!authz.Can(actor.ToActorContext(), ResourceAction.ManageOwnBlockList,
            new ResourceContext(ResourceKind.UserAccount, db.OrganizationScopeId, TargetIsSelf: true))) return Results.Forbid();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.UserBlocks
            .Where(b => b.OrganizationId == db.OrganizationScopeId && b.BlockerUserId == actorId && b.BlockedUserId == targetUserId)
            .ExecuteDeleteAsync(ct);
        if (changed != 0) await DmReadReceipts.BumpPrivacyVersionAsync(db, actorId, ct);
        await transaction.CommitAsync(ct);
        if (changed != 0) await fanout.NotifyDmReceiptPrivacyAsync(actorId, targetUserId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> HandleReportUserAsync(
        HttpContext ctx,
        CreateReportRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
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

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length is < 1 or > 64)
        {
            return Results.BadRequest(new { code = "invalid_reason", message = "Report reason is required (max 64 characters)." });
        }

        var target = await db.OrganizationPeople
            .AsNoTracking()
            .Where(u => u.Id == request.ReportedUserId)
            .Select(u => new { u.Id, u.Status })
            .SingleOrDefaultAsync(ct);

        var allowed = target is not null && authz.Can(
            actor.ToActorContext(),
            ResourceAction.DmBlockOrReport,
            new ResourceContext(
                ResourceKind.Dm,
                actor.OrganizationId,
                TargetIsActive: target.Status == AccountStatus.Active,
                TargetIsSelf: request.ReportedUserId == actorId));

        if (!allowed || (db.IsPersonalScope && !await MayActOnPersonalUserAsync(db, actorId, request.ReportedUserId, ct)))
        {
            return Results.Forbid();
        }

        var report = new UserReportEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = db.ConversationOrganizationId,
            ReporterUserId = actorId,
            ReportedUserId = request.ReportedUserId,
            ConversationId = request.ConversationId,
            Reason = reason,
            Details = string.IsNullOrWhiteSpace(request.Details) ? null : request.Details.Trim()[..Math.Min(request.Details.Trim().Length, 1000)],
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.UserReports.Add(report);
        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = actor.OrganizationId,
            ActorUserId = actorId,
            Action = "USER_REPORTED",
            ResourceType = "UserAccount",
            ResourceId = request.ReportedUserId.ToString(),
            Result = "Allowed",
            DetailJson = JsonSerializer.Serialize(new { reportId = report.Id, reason }),
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = report.CreatedAt
        });
        await db.SaveChangesAsync(ct);

        return Results.Created($"{OrganizationScope.ApiPath(db.ConversationOrganizationId)}/people/reports/{report.Id}", new { id = report.Id, reason = report.Reason, createdAt = report.CreatedAt });
    }
    private static Task<bool> MayActOnPersonalUserAsync(NexusDbContext db, Guid actorId, Guid targetId, CancellationToken ct) =>
        db.ConversationMembers.AnyAsync(m => m.UserId == actorId && m.LeftAt == null &&
            db.Conversations.Any(c => c.Id == m.ConversationId && c.OrganizationId == null) &&
            db.ConversationMembers.Any(peer => peer.ConversationId == m.ConversationId && peer.UserId == targetId), ct);
}
