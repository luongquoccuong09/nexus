using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Conversations;

namespace Nexus.Web.Modules.Org;

public static class OrganizationManagementEndpoints
{
    public sealed record CreateOrganization(string Name);
    public sealed record AcceptInvitation(string Token);
    public sealed record SetAdministrator(bool Administrator);

    public static void MapOrganizations(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/api/organizations");
        account.MapGet("", ListAsync);
        account.MapPost("", CreateAsync).AddEndpointFilter(ValidateCsrfAsync);
        account.MapPost("/invitations/preview", PreviewAsync).AddEndpointFilter(ValidateCsrfAsync);
        account.MapPost("/invitations/redeem", RedeemAsync).AddEndpointFilter(ValidateCsrfAsync);

        var organization = app.MapGroup(OrganizationScope.Api);
        organization.MapGet("/me", ([FromServices] NexusDbContext db) => Results.Ok(ToProfile(db.OrganizationActor!)));
        organization.MapGet("/invitations", ListInvitationsAsync);
        organization.MapPost("/invitations", IssueInvitationAsync).AddEndpointFilter(ValidateCsrfAsync);
        organization.MapDelete("/invitations/{invitationId:guid}", RevokeInvitationAsync).AddEndpointFilter(ValidateCsrfAsync);
        organization.MapGet("/members", ListMembersAsync);
        organization.MapPut("/members/{memberId:guid}/administrator", ChangeAdministratorAsync).AddEndpointFilter(ValidateCsrfAsync);
        organization.MapDelete("/members/{memberId:guid}", RevokeMemberAsync).AddEndpointFilter(ValidateCsrfAsync);
    }

    internal static async ValueTask<object?> ValidateCsrfAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var ctx = invocation.HttpContext;
        var antiforgery = ctx.RequestServices.GetRequiredService<IAntiforgery>();
        return await antiforgery.IsRequestValidAsync(ctx) ? await next(invocation) : Results.BadRequest(new { code = "invalid_csrf" });
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, [FromServices] NexusDbContext db, Guid? after, int? limit, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var id)) return Results.Unauthorized();
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var error)) return Results.BadRequest(new { code = "invalid_page_size", message = error });
        var items = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.Id == id && m.Status == AccountStatus.Active)
            .Where(m => after == null || m.OrganizationId.CompareTo(after.Value) > 0)
            .OrderBy(m => m.OrganizationId).Take(pageSize + 1)
            .Select(m => new { id = m.OrganizationId, name = m.Organization.Name, m.IsOwner, role = m.OrgRole.ToString() })
            .ToListAsync(ct);
        return PaginationGuard.CursorPage(items, pageSize, item => item.id);
    }

    private static async Task<IResult> CreateAsync(CreateOrganization request, HttpContext ctx, [FromServices] NexusDbContext db,
        IConfiguration config, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var id)) return Results.Unauthorized();
        if (!config.GetValue("NexusOrganizations:AllowCreation", true))
            return Results.Json(new { code = "organization_creation_unavailable" }, statusCode: StatusCodes.Status403Forbidden);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 120) return Results.BadRequest(new { code = "invalid_organization_name" });
        var user = await db.Users.SingleAsync(u => u.Id == id, ct);
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.CreateVersion7();
        var organization = new OrganizationEntity { Id = organizationId, Name = name, Slug = organizationId.ToString("N"), CreatedAt = now };
        db.Organizations.Add(organization);
        var member = OrganizationMembership.Create(organizationId, user, now);
        member.IsOwner = true;
        member.OrgRole = OrgRole.SystemAdmin;
        member.TrustLevel = TrustLevel.Verified;
        db.OrganizationMemberships.Add(member);
        Audit(db, organizationId, id, "ORGANIZATION_CREATED", organizationId, now);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/me", new { id = organizationId, name });
    }

    private static async Task<IResult> PreviewAsync(AcceptInvitation request, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        var hash = TokenHash(request.Token);
        if (hash is null) return Results.NotFound();
        var now = DateTimeOffset.UtcNow;
        var invitation = await (from invite in db.OrganizationInvitations.AsNoTracking()
                                join organization in db.Organizations.AsNoTracking() on invite.OrganizationId equals organization.Id
                                where invite.TokenHash == hash && invite.RedeemedAt == null && invite.RevokedAt == null &&
                                      invite.ExpiresAt > now
                                select new { organization.Name, invite.ExpiresAt }).SingleOrDefaultAsync(ct);
        return invitation is null ? Results.NotFound() : Results.Ok(invitation);
    }

    private static async Task<IResult> RedeemAsync(AcceptInvitation request, HttpContext ctx, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var id)) return Results.Unauthorized();
        var hash = TokenHash(request.Token);
        if (hash is null) return Results.NotFound();
        var match = await db.OrganizationInvitations.AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (match is null) return Results.NotFound();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOrganizationAsync(db, match.OrganizationId, ct);
        var now = DateTimeOffset.UtcNow;
        var affected = await db.OrganizationInvitations.Where(i => i.Id == match.Id && i.RevokedAt == null && i.RedeemedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.RedeemedAt, now).SetProperty(i => i.RedeemedByUserId, id), ct);
        if (affected == 0) return Results.NotFound();

        var member = await db.OrganizationMemberships.SingleOrDefaultAsync(m => m.OrganizationId == match.OrganizationId && m.Id == id, ct);
        if (member?.Status == AccountStatus.Active) return Results.Conflict(new { code = "already_member" });
        if (member is null)
        {
            var account = await db.Users.SingleAsync(u => u.Id == id, ct);
            db.OrganizationMemberships.Add(OrganizationMembership.Create(match.OrganizationId, account, now));
        }
        else
        {
            member.Status = AccountStatus.Active;
            member.OrgRole = OrgRole.Member;
            member.TrustLevel = TrustLevel.Unverified;
            member.EmployeeId = null;
            member.DepartmentId = null;
            member.BranchId = null;
        }

        Audit(db, match.OrganizationId, id, "ORGANIZATION_INVITATION_REDEEMED", match.Id, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { id = match.OrganizationId });
    }

    private static async Task<IResult> ListInvitationsAsync(HttpContext ctx, [FromServices] NexusDbContext db, Guid? after, int? limit, CancellationToken ct)
    {
        if (!CanManage(ctx, db)) return Results.Forbid();
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var error)) return Results.BadRequest(new { code = "invalid_page_size", message = error });
        var items = await db.OrganizationInvitations.AsNoTracking().Where(i => i.OrganizationId == db.OrganizationScopeId)
            .Where(i => after == null || i.Id.CompareTo(after.Value) < 0)
            .OrderByDescending(i => i.Id).Take(pageSize + 1)
            .Select(i => new { i.Id, i.CreatedAt, i.ExpiresAt, i.RevokedAt, i.RedeemedAt }).ToListAsync(ct);
        return PaginationGuard.CursorPage(items, pageSize, item => item.Id);
    }

    private static async Task<IResult> IssueInvitationAsync(HttpContext ctx, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        if (!CanManage(ctx, db)) return Results.Forbid();
        var actor = db.OrganizationActor!;
        var now = DateTimeOffset.UtcNow;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var invitation = new OrganizationInvitation
        {
            Id = Guid.CreateVersion7(), OrganizationId = actor.OrganizationId, CreatedByUserId = actor.Id,
            CreatedAt = now, ExpiresAt = now.AddDays(7), TokenHash = TokenHash(token)!
        };
        db.OrganizationInvitations.Add(invitation);
        Audit(db, actor.OrganizationId, actor.Id, "ORGANIZATION_INVITATION_CREATED", invitation.Id, now);
        await db.SaveChangesAsync(ct);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { invitation.Id, invitation.ExpiresAt, token });
    }

    private static async Task<IResult> RevokeInvitationAsync(Guid invitationId, HttpContext ctx, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        if (!CanManage(ctx, db)) return Results.Forbid();
        var now = DateTimeOffset.UtcNow;
        var actor = db.OrganizationActor!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var affected = await db.OrganizationInvitations.Where(i => i.Id == invitationId && i.OrganizationId == actor.OrganizationId &&
                i.RedeemedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.RevokedAt, now), ct);
        if (affected == 0) return Results.NotFound();
        Audit(db, actor.OrganizationId, actor.Id, "ORGANIZATION_INVITATION_REVOKED", invitationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListMembersAsync(HttpContext ctx, [FromServices] NexusDbContext db, Guid? after, int? limit, CancellationToken ct)
    {
        if (!CanManage(ctx, db)) return Results.Forbid();
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var error)) return Results.BadRequest(new { code = "invalid_page_size", message = error });
        var items = await db.ActiveScopePeople.AsNoTracking()
            .Where(m => after == null || m.Id.CompareTo(after.Value) > 0)
            .OrderBy(m => m.Id).Take(pageSize + 1)
            .Select(m => new { m.Id, m.DisplayName, m.UserName, role = m.OrgRole.ToString(), m.IsOwner }).ToListAsync(ct);
        return PaginationGuard.CursorPage(items, pageSize, item => item.Id);
    }

    private static async Task<IResult> ChangeAdministratorAsync(Guid memberId, SetAdministrator request, HttpContext ctx,
        [FromServices] NexusDbContext db, CancellationToken ct)
    {
        if (!CanManage(ctx, db) || db.OrganizationActor is not { IsOwner: true } actor) return Results.Forbid();
        var member = await db.OrganizationPeople.SingleOrDefaultAsync(m => m.Id == memberId && !m.IsOwner && m.Status == AccountStatus.Active, ct);
        if (member is null) return Results.NotFound();
        if (member.OrgRole is not (OrgRole.SystemAdmin or OrgRole.Member)) return Results.Conflict(new { code = "specialized_role" });
        member.OrgRole = request.Administrator ? OrgRole.SystemAdmin : OrgRole.Member;
        Audit(db, actor.OrganizationId, actor.Id, "ORGANIZATION_ADMINISTRATOR_CHANGED", memberId, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeMemberAsync(Guid memberId, HttpContext ctx, [FromServices] NexusDbContext db,
        IUserConnectionRegistry connections, CancellationToken ct)
    {
        if (!CanManage(ctx, db)) return Results.Forbid();
        var actor = db.OrganizationActor!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockOrganizationAsync(db, actor.OrganizationId, ct);
        var member = await db.OrganizationPeople.SingleOrDefaultAsync(m => m.Id == memberId && m.Status == AccountStatus.Active, ct);
        if (member is null) return Results.NotFound();
        if (member.IsOwner || memberId == actor.Id || (!actor.IsOwner && member.OrgRole != OrgRole.Member)) return Results.Forbid();
        if (!await RemoveResourceMembershipsAsync(db, actor.OrganizationId, [memberId], DateTimeOffset.UtcNow, ct))
            return Results.Conflict(new { code = "resource_owner_transfer_required" });

        member.Status = AccountStatus.Former;
        Audit(db, actor.OrganizationId, actor.Id, "ORGANIZATION_MEMBER_REVOKED", memberId, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        connections.AbortOrganizationConnections(actor.OrganizationId, memberId);
        return Results.NoContent();
    }

    internal static async Task<bool> RemoveResourceMembershipsAsync([FromServices] NexusDbContext db, Guid orgId, Guid[] removedIds,
        DateTimeOffset now, CancellationToken ct)
    {
        var communities = db.CommunityMembers.Where(m => removedIds.Contains(m.UserId) && m.LeftAt == null &&
            db.Communities.Any(c => c.Id == m.CommunityId && c.OrganizationId == orgId));
        var conversations = db.ConversationMembers.Where(m => removedIds.Contains(m.UserId) && m.LeftAt == null &&
            db.Conversations.Any(c => c.Id == m.ConversationId && c.OrganizationId == orgId));
        if (await communities.AnyAsync(m => m.Role == ResourceRole.Owner, ct) ||
            await conversations.AnyAsync(m => m.Role == ResourceRole.Owner && db.Conversations.Any(c => c.Id == m.ConversationId && c.Kind == ConversationKind.Group), ct)) return false;
        await communities.ExecuteUpdateAsync(set => set.SetProperty(m => m.LeftAt, now), ct);
        await conversations.ExecuteUpdateAsync(set => set.SetProperty(m => m.LeftAt, now), ct);
        return true;
    }

    internal static async Task LockOrganizationAsync([FromServices] NexusDbContext db, Guid orgId, CancellationToken ct) =>
        _ = await db.Organizations.FromSqlInterpolated($"SELECT * FROM organizations WHERE id = {orgId} FOR UPDATE").AsNoTracking().ToListAsync(ct);

    private static bool CanManage(HttpContext ctx, [FromServices] NexusDbContext db) =>
        db.OrganizationActor?.OrgRole == OrgRole.SystemAdmin &&
        (!ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("NexusOrganizations:RequireAdminMfa", false) || HasMfa(ctx));
    private static bool HasMfa(HttpContext ctx) => ctx.User.FindFirstValue(ClaimsPrincipalExtensions.MfaVerifiedClaimType) == "true";
    private static string? TokenHash(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit)
        ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))) : null;

    private static object ToProfile(OrganizationMembership member) => new
    {
        member.Id, username = member.UserName, member.DisplayName, member.CustomStatus, member.IsOwner,
        orgRole = member.OrgRole.ToString(), isUnverified = member.TrustLevel == TrustLevel.Unverified,
        avatarUrl = member.AvatarObjectKey is null ? null : $"/api/organizations/{member.OrganizationId}/people/{member.Id}/avatar"
    };

    internal static void Audit([FromServices] NexusDbContext db, Guid orgId, Guid actor, string action, Guid target, DateTimeOffset now) =>
        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(), OrganizationId = orgId, ActorUserId = actor, Action = action,
            ResourceType = "Organization", ResourceId = target.ToString(), Result = "Allowed", CreatedAt = now
        });
}
