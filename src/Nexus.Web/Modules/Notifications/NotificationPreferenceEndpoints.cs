using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Notifications;

internal static class NotificationPreferenceEndpoints
{
    public sealed record ConversationPreferenceRequest(string? Mode);
    public sealed record QuietHoursRequest(bool Enabled, string? Start, string? End, string? TimeZoneId);

    public static void MapNotificationPreferences(this RouteGroupBuilder notifications)
    {
        notifications.MapGet("/conversations/{conversationId:guid}/preference", GetConversationAsync);
        notifications.MapPut("/conversations/{conversationId:guid}/preference", PutConversationAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        notifications.MapGet("/quiet-hours", GetQuietHoursAsync);
        notifications.MapPut("/quiet-hours", PutQuietHoursAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
    }

    private static async Task<IResult> GetConversationAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, IAuthorizationService authz, Guid conversationId, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();
        if (!await CanReadAsync(db, authz, userId, conversationId, ct)) return Results.NotFound();

        var mode = await NotificationPreferences.GetModeAsync(db, conversationId, userId, ct);
        return Results.Ok(new { mode = mode.ToString().ToLowerInvariant() });
    }

    private static async Task<IResult> PutConversationAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, IAuthorizationService authz, Guid conversationId,
        ConversationPreferenceRequest request, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();
        if (!await CanReadAsync(db, authz, userId, conversationId, ct)) return Results.NotFound();

        var mode = request.Mode switch
        {
            "all" => ConversationNotificationMode.All,
            "mentions" => ConversationNotificationMode.Mentions,
            "muted" => ConversationNotificationMode.Muted,
            _ => (ConversationNotificationMode?)null
        };
        if (mode is null)
        {
            return Results.BadRequest(new { code = "invalid_notification_mode", message = "Choose All activity, Mentions only, or Muted." });
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO conversation_notification_preferences (conversation_id, user_id, mode)
            VALUES ({conversationId}, {userId}, {mode.Value.ToString()})
            ON CONFLICT (conversation_id, user_id) DO UPDATE SET mode = EXCLUDED.mode
            """, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetQuietHoursAsync(HttpContext ctx, [FromServices] NexusDbContext db, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId) || await db.FindPersonalActorAsync(userId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var hours = await db.NotificationQuietHours.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, ct);
        return Results.Ok(new
        {
            enabled = hours is not null,
            start = hours?.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
            end = hours?.End.ToString("HH:mm", CultureInfo.InvariantCulture),
            timeZoneId = hours?.TimeZoneId,
            isQuietNow = hours?.IsQuietAt(DateTimeOffset.UtcNow) ?? false
        });
    }

    private static async Task<IResult> PutQuietHoursAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, QuietHoursRequest request, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId) || await db.FindPersonalActorAsync(userId, ct) is null)
        {
            return Results.Unauthorized();
        }

        if (!request.Enabled)
        {
            await db.NotificationQuietHours.Where(item => item.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }

        var timeZoneId = request.TimeZoneId?.Trim();
        if (!TimeOnly.TryParseExact(request.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(request.End, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ||
            start == end || timeZoneId is not { Length: > 0 and <= 128 } ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
        {
            return Results.BadRequest(new { code = "invalid_quiet_hours", message = "Choose different start and end times and a valid time zone." });
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notification_quiet_hours (user_id, start_time, end_time, time_zone_id)
            VALUES ({userId}, {start}, {end}, {timeZoneId})
            ON CONFLICT (user_id) DO UPDATE
            SET start_time = EXCLUDED.start_time, end_time = EXCLUDED.end_time, time_zone_id = EXCLUDED.time_zone_id
            """, ct);
        return Results.NoContent();
    }

    private static async Task<bool> CanReadAsync(
        NexusDbContext db, IAuthorizationService authz, Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == conversationId, ct);
        if (conversation is null) return false;

        if (conversation.OrganizationId is { } orgId)
        {
            var actor = await db.OrganizationMemberships.AsNoTracking().SingleOrDefaultAsync(membership =>
                membership.OrganizationId == orgId && membership.Id == userId &&
                membership.Status == AccountStatus.Active && membership.Account.Status == AccountStatus.Active, ct);
            if (actor is null) return false;
            var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
            return access is not null && authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource);
        }

        var account = await db.FindPersonalActorAsync(userId, ct);
        var role = await db.ConversationMembers.AsNoTracking().Where(member =>
                member.ConversationId == conversationId && member.UserId == userId && member.LeftAt == null)
            .Select(member => (ResourceRole?)member.Role).SingleOrDefaultAsync(ct);
        var kind = conversation.Kind == ConversationKind.Dm ? ResourceKind.Dm : ResourceKind.GroupChat;
        return authz.Can(account?.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(kind, MembershipRole: role));
    }
}
