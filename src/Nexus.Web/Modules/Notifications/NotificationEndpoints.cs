using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Calendar;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Notifications;

internal static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var notifications = app.MapGroup("/api/notifications");
        notifications.MapGet("", ListAsync);
        notifications.MapGet("/mentions/unread", GetUnreadMentionsAsync);
        notifications.MapGet("/{notificationId:guid}/source", GetSourceAsync);
        notifications.MapPut("/{notificationId:guid}/read", MarkReadAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        notifications.MapNotificationPreferences();
        notifications.MapWebPush();
        notifications.MapEmailDigest();
        return app;
    }

    private static async Task<IResult> ListAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, DateTimeOffset? beforeAt, Guid? beforeId,
        int? limit, bool unreadOnly = false, CancellationToken ct = default)
    {
        if (!ctx.User.TryGetUserId(out var recipientId) || await db.FindPersonalActorAsync(recipientId, ct) is null)
        {
            return Results.Unauthorized();
        }

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        if (beforeAt.HasValue != beforeId.HasValue)
        {
            return Results.BadRequest(new { code = "invalid_cursor", message = "Both beforeAt and beforeId are required for pagination." });
        }

        var visible = NotificationQueries.VisibleTo(db, recipientId);
        var unread = NotificationQueries.Unread(db, visible, recipientId);
        var query = unreadOnly ? unread : visible;
        if (beforeAt is { } at && beforeId is { } id)
        {
            var utc = at.ToUniversalTime();
            query = query.Where(item => item.CreatedAt < utc || (item.CreatedAt == utc && item.Id.CompareTo(id) < 0));
        }

        var items = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Kind,
                item.CreatedAt,
                item.OrganizationId,
                item.ConversationId,
                item.ResourceId,
                ConversationTitle = db.Conversations.Where(conversation => conversation.Id == item.ConversationId)
                    .Select(conversation => conversation.Title).FirstOrDefault(),
                IsUnread = unread.Any(candidate => candidate.Id == item.Id)
            }).Take(pageSize + 1).ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(pageSize);
        }

        return Results.Ok(new
        {
            items,
            nextBefore = hasMore ? new { items[^1].CreatedAt, items[^1].Id } : null,
            unreadCount = await unread.CountAsync(ct)
        });
    }

    private static async Task<IResult> MarkReadAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, Guid notificationId, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var recipientId) || await db.FindPersonalActorAsync(recipientId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var visible = NotificationQueries.VisibleTo(db, recipientId).Where(item => item.Id == notificationId);
        if (!await visible.AnyAsync(ct))
        {
            return Results.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        await db.Notifications.Where(item => item.Id == notificationId && item.RecipientId == recipientId &&
            item.ReadAt == null && visible.Any(current => current.Id == item.Id))
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.ReadAt, now), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetSourceAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, Guid notificationId, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var recipientId) || await db.FindPersonalActorAsync(recipientId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var visible = NotificationQueries.VisibleTo(db, recipientId).Where(item => item.Id == notificationId);
        var kind = await visible.Select(notification => notification.Kind).SingleOrDefaultAsync(ct);
        if (kind is null)
        {
            return Results.NotFound();
        }

        if (CalendarOutbox.IsCalendarKind(kind))
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var calendar = await (from notification in visible
                join eventItem in db.CalendarEvents.AsNoTracking() on notification.ResourceId equals eventItem.Id
                select new
                {
                    Event = eventItem,
                    OriginalStartUtc = db.CalendarOccurrenceReminders.Where(reminder => reminder.OutboxMessageId == notification.OutboxMessageId)
                        .Select(reminder => (DateTimeOffset?)reminder.OriginalStartUtc).SingleOrDefault()
                }).SingleOrDefaultAsync(ct);
            if (calendar is null) return Results.NotFound();

            var occurrence = calendar.OriginalStartUtc is { } original
                ? await CalendarRecurrences.FindAsync(db, calendar.Event, original, ct)
                : null;
            if (calendar.OriginalStartUtc.HasValue && occurrence is not { CanceledAt: null }) return Results.NotFound();
            return Results.Ok(new
            {
                CalendarEventId = calendar.Event.Id,
                calendar.Event.OrganizationId,
                OccurrenceStartUtc = calendar.OriginalStartUtc,
                Title = occurrence?.Title ?? calendar.Event.Title,
                Body = (occurrence is null ? calendar.Event.Description : occurrence.Description) ?? string.Empty,
                TargetLink = (string?)null
            });
        }

        var source = kind switch
        {
            MentionOutbox.MessageKind or MentionOutbox.MessageActivity => await (from notification in visible
                join message in db.Messages.AsNoTracking() on notification.ResourceId equals message.Id
                select new
                {
                    Title = (string?)null,
                    message.Body,
                    PostId = (Guid?)null,
                    MessageId = (Guid?)message.Id,
                    Seq = (long?)message.Seq,
                    notification.OrganizationId,
                    notification.ConversationId
                }).SingleOrDefaultAsync(ct),
            MentionOutbox.CommentKind or MentionOutbox.CommentActivity => await (from notification in visible
                join comment in db.PostComments.AsNoTracking() on notification.ResourceId equals comment.Id
                join post in db.ChannelPosts.AsNoTracking() on comment.PostId equals post.Id
                select new
                {
                    post.Title,
                    comment.Body,
                    PostId = (Guid?)post.Id,
                    MessageId = (Guid?)null,
                    Seq = (long?)null,
                    notification.OrganizationId,
                    notification.ConversationId
                }).SingleOrDefaultAsync(ct),
            _ => await (from notification in visible
                join post in db.ChannelPosts.AsNoTracking() on notification.ResourceId equals post.Id
                select new
                {
                    post.Title,
                    post.Body,
                    PostId = (Guid?)post.Id,
                    MessageId = (Guid?)null,
                    Seq = (long?)null,
                    notification.OrganizationId,
                    notification.ConversationId
                }).SingleOrDefaultAsync(ct)
        };

        if (source is null)
        {
            return Results.NotFound();
        }

        string? targetLink = source.MessageId is { } msgId
            ? ContentLinkCodec.Encode("m", source.OrganizationId, source.ConversationId, msgId)
            : source.PostId is { } pId && source.OrganizationId is { } orgId
                ? ContentLinkCodec.Encode("p", orgId, source.ConversationId, pId)
                : null;

        return Results.Ok(new SourceContent(source.Title, source.Body, source.PostId, source.MessageId, source.Seq, targetLink));
    }

    private static async Task<IResult> GetUnreadMentionsAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, Guid? conversationId, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var recipientId) || await db.FindPersonalActorAsync(recipientId, ct) is null)
        {
            return Results.Unauthorized();
        }

        var query = NotificationQueries.UnreadMentions(db, recipientId);
        if (conversationId.HasValue)
        {
            query = query.Where(item => item.ConversationId == conversationId.Value);
        }

        return Results.Ok(new { unreadMentionCount = await query.CountAsync(ct) });
    }

    private sealed record SourceContent(string? Title, string Body, Guid? PostId, Guid? MessageId, long? Seq, string? TargetLink);
}
