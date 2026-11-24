using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Notifications;

internal static class WebPushEndpoints
{
    public sealed record SubscribeRequest(string? Endpoint);

    public static void MapWebPush(this RouteGroupBuilder notifications)
    {
        notifications.MapGet("/push", GetSettingsAsync);
        notifications.MapPut("/push", SubscribeAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        notifications.MapDelete("/push", UnsubscribeAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
    }

    private static async Task<IResult> GetSettingsAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, WebPushTransport transport, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId) || ctx.User.GetSessionId() is not { } sessionId)
        {
            return Results.Unauthorized();
        }

        var subscribed = await db.PushSubscriptions.AnyAsync(item => item.UserId == userId && item.SessionId == sessionId, ct);
        return Results.Ok(new { publicKey = transport.PublicKey, subscribed });
    }

    private static async Task<IResult> SubscribeAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, WebPushTransport transport, SubscribeRequest request, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId) || ctx.User.GetSessionId() is not { } sessionId)
        {
            return Results.Unauthorized();
        }

        if (transport.PublicKey is null)
        {
            return Results.Conflict(new { code = "web_push_unavailable", message = "Web push is unavailable on this server." });
        }

        if (!WebPushTransport.TryParseEndpoint(request.Endpoint, out var endpoint))
        {
            return Results.BadRequest(new { code = "invalid_push_endpoint", message = "This browser push service is unsupported." });
        }

        var address = endpoint!.AbsoluteUri;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.PushSubscriptions.Where(item => item.SessionId == sessionId && item.UserId == userId && item.Endpoint != address)
            .ExecuteDeleteAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO push_subscriptions (id, user_id, session_id, endpoint, created_at)
            VALUES ({Guid.CreateVersion7()}, {userId}, {sessionId}, {address}, {DateTimeOffset.UtcNow})
            ON CONFLICT (endpoint) DO UPDATE
            SET user_id = EXCLUDED.user_id, session_id = EXCLUDED.session_id, created_at = EXCLUDED.created_at
            """, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnsubscribeAsync(HttpContext ctx, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId) || ctx.User.GetSessionId() is not { } sessionId)
        {
            return Results.Unauthorized();
        }

        await db.PushSubscriptions.Where(item => item.UserId == userId && item.SessionId == sessionId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}
