using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Notifications;

internal static class EmailDigestEndpoints
{
    public sealed record SetDigestRequest(bool Enabled);

    public static void MapEmailDigest(this RouteGroupBuilder notifications)
    {
        notifications.MapGet("/email-digest", GetAsync);
        notifications.MapPut("/email-digest", PutAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
    }

    private static async Task<IResult> GetAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, IEmailDigestTransport transport, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId) || await db.FindPersonalActorAsync(userId, ct) is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new
        {
            configured = transport.IsEnabled,
            eligible = await HasVerifiedAddressAsync(db, userId, ct),
            enabled = await db.EmailDigestOptIns.AnyAsync(item => item.UserId == userId, ct),
            delayHours = transport.UnreadAge.TotalHours
        });
    }

    private static async Task<IResult> PutAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, IEmailDigestTransport transport, SetDigestRequest request, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId) || await db.FindPersonalActorAsync(userId, ct) is null)
        {
            return Results.Unauthorized();
        }

        if (!request.Enabled)
        {
            await db.EmailDigestOptIns.Where(item => item.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }

        if (!transport.IsEnabled || !await HasVerifiedAddressAsync(db, userId, ct))
        {
            return Results.Conflict(new { code = "email_digest_unavailable", message = "Email digest requires a verified email address and server setup." });
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO email_digest_opt_ins (user_id, enabled_at)
            VALUES ({userId}, {DateTimeOffset.UtcNow})
            ON CONFLICT (user_id) DO NOTHING
            """, ct);
        return Results.NoContent();
    }

    private static async Task<bool> HasVerifiedAddressAsync(NexusDbContext db, Guid userId, CancellationToken ct) =>
        await EmailDigestHandler.ConfirmedAccountAddress(db, userId).AnyAsync(ct) ||
        await EmailDigestHandler.VerifiedWorkAddresses(db, userId).AnyAsync(ct);
}
