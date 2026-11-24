using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

public static class MessageBookmarkEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] MutationMethods = [HttpMethods.Put, HttpMethods.Delete];

    private sealed record PinDelta(string Type, Guid MessageId, long Seq, long VisibilitySeq, Guid? ParentMessageId, bool Pinned);
    private sealed record SaveState(Guid ConversationId, Guid MessageId, Guid? ParentMessageId, bool Saved, long Version);

    private sealed class BookmarkRow
    {
        public required ConversationEntity Conversation { get; init; }
        public required MessageEntity Message { get; init; }
        public ResourceRole Role { get; init; }
        public DateTimeOffset AddedAt { get; init; }
    }

    public static IEndpointRouteBuilder MapMessageBookmarkEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        var conversation = prefix + "/conversations/{conversationId:guid}";
        var message = conversation + "/messages/{messageId:guid}";
        app.MapMethods(message + "/pin", MutationMethods, SetPinAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        app.MapMethods(message + "/save", MutationMethods, SetSavedAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        app.MapGet(conversation + "/pins", ListPinsAsync);
        app.MapGet(conversation + "/saved-state", GetSavedStateAsync);
        app.MapGet(prefix + "/saved-messages", ListSavedAsync);
        return app;
    }

    private static async Task<IResult> GetSavedStateAsync(
        HttpContext ctx, Guid conversationId, [FromQuery] Guid[] ids, [FromServices] NexusDbContext db,
        IAuthorizationService authz, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();
        if (ids.Length > 50) return Results.BadRequest(new { code = "invalid_page_size" });
        var actor = await db.FindOrganizationActorAsync(userId, ct);
        if (actor is null) return Results.Unauthorized();
        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || access.Conversation.Kind == ConversationKind.Channel ||
            !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource)) return Results.Forbid();

        var messages = MessageHistory.VisibleAfter(db, access.VisibleFromSeq)
            .Where(message => message.ConversationId == conversationId && message.DeletedAt == null && ids.Contains(message.Id));
        var items = await (from message in messages
                           join state in db.SavedMessages.AsNoTracking().Where(saved => saved.UserId == userId)
                               on message.Id equals state.MessageId into saveState
                           from state in saveState.DefaultIfEmpty()
                           select new SaveState(conversationId, message.Id, message.ParentMessageId,
                               state != null && state.IsSaved, state == null ? 0 : state.Version)).ToListAsync(ct);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { items });
    }

    private static async Task<IResult> SetPinAsync(
        HttpContext ctx, Guid conversationId, Guid messageId, [FromServices] NexusDbContext db,
        IAuthorizationService authz, MessagingFanoutService fanout, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();
        var pinned = HttpMethods.IsPut(ctx.Request.Method);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (conversation, message, visibilitySeq, error) = await FindTargetAsync(
            db, authz, userId, conversationId, messageId, ResourceAction.MessagePin, ct);
        if (message is null || conversation is null) return error!;

        var now = DateTimeOffset.UtcNow;
        var affected = pinned
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO message_pins (message_id, pinned_by_user_id, pinned_at)
                VALUES ({messageId}, {userId}, {now}) ON CONFLICT (message_id) DO NOTHING
                """, ct)
            : await db.MessagePins.Where(pin => pin.MessageId == messageId).ExecuteDeleteAsync(ct);

        var delta = new PinDelta("message_pin", messageId, message.Seq, visibilitySeq, message.ParentMessageId, pinned);
        var changeSeq = conversation.ChangeSeq;
        if (affected != 0)
        {
            changeSeq++;
            await db.Conversations.Where(item => item.Id == conversationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ChangeSeq, changeSeq), ct);
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                Id = Guid.CreateVersion7(), ConversationId = conversationId, ChangeSeq = changeSeq,
                EventType = pinned ? "message.pinned" : "message.unpinned", ActorUserId = userId,
                PayloadJson = JsonSerializer.Serialize(delta, WebJson), CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        if (affected != 0) await fanout.NotifyChatDeltaAsync(conversationId, changeSeq, visibilitySeq, delta, ct);
        return Results.Ok(new { delta, changeSeq });
    }

    private static async Task<IResult> SetSavedAsync(
        HttpContext ctx, Guid conversationId, Guid messageId, [FromServices] NexusDbContext db,
        IAuthorizationService authz, IHubContext<MessagingHub> hub, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();
        var saved = HttpMethods.IsPut(ctx.Request.Method);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (_, message, visibilitySeq, error) = await FindTargetAsync(
            db, authz, userId, conversationId, messageId, ResourceAction.ConversationRead, ct);
        if (message is null) return error!;

        var entry = await db.SavedMessages.SingleOrDefaultAsync(row => row.UserId == userId && row.MessageId == messageId, ct);
        var changed = (entry?.IsSaved ?? false) != saved;
        if (changed)
        {
            if (entry is null)
            {
                entry = new SavedMessageEntity { UserId = userId, MessageId = messageId };
                db.SavedMessages.Add(entry);
            }
            entry.IsSaved = saved;
            if (saved) entry.SavedAt = DateTimeOffset.UtcNow;
            entry.Version++;
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);

        var state = new SaveState(conversationId, messageId, message.ParentMessageId, saved, entry?.Version ?? 0);
        if (changed && await db.ConversationMembers.AnyAsync(member => member.ConversationId == conversationId &&
                member.UserId == userId && member.LeftAt == null && member.VisibleFromSeq < visibilitySeq &&
                db.ActiveScopePeople.Any(person => person.Id == userId), ct))
        {
            await hub.Clients.Groups([MessagingHub.UserGroupName(db.OrganizationScopeId, userId)])
                .SendAsync("SavedMessageChanged", state, ct);
        }
        return Results.Ok(state);
    }

    private static async Task<(ConversationEntity? Conversation, MessageEntity? Message, long VisibilitySeq, IResult? Error)> FindTargetAsync(
        NexusDbContext db, IAuthorizationService authz, Guid userId, Guid conversationId, Guid messageId,
        ResourceAction action, CancellationToken ct)
    {
        var conversation = await MessagingEndpoints.LockConversationAsync(db, conversationId, ct);
        var actor = await db.ActiveScopePeople.AsNoTracking().SingleOrDefaultAsync(person => person.Id == userId, ct);
        if (actor is null) return (null, null, 0, Results.Unauthorized());
        if (conversation is null || conversation.Kind == ConversationKind.Channel) return (null, null, 0, Results.Forbid());
        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversation, ct);
        if (access is null || !authz.Can(actor.ToActorContext(), action, access.Resource))
            return (null, null, 0, Results.Forbid());

        var target = await MessageHistory.VisibleAfter(db, access.VisibleFromSeq)
            .Where(message => message.Id == messageId && message.ConversationId == conversationId)
            .Select(message => new
            {
                Message = message,
                VisibilitySeq = message.ParentMessageId == null ? message.Seq
                    : db.Messages.Where(parent => parent.Id == message.ParentMessageId).Select(parent => parent.Seq).FirstOrDefault()
            }).SingleOrDefaultAsync(ct);
        if (target is null) return (null, null, 0, Results.NotFound(new { code = "message_not_found" }));
        if (target.Message.DeletedAt.HasValue) return (null, null, 0, Results.Conflict(new { code = "message_recalled" }));
        return (conversation, target.Message, target.VisibilitySeq, null);
    }

    private static Task<IResult> ListSavedAsync(
        HttpContext ctx, [FromServices] NexusDbContext db, IAuthorizationService authz,
        DateTimeOffset? beforeAt, Guid? beforeId, int? limit, CancellationToken ct) =>
        ListAsync(ctx, db, authz, null, beforeAt, beforeId, limit, ct);

    private static Task<IResult> ListPinsAsync(
        HttpContext ctx, Guid conversationId, [FromServices] NexusDbContext db, IAuthorizationService authz,
        DateTimeOffset? beforeAt, Guid? beforeId, int? limit, CancellationToken ct) =>
        ListAsync(ctx, db, authz, conversationId, beforeAt, beforeId, limit, ct);

    private static async Task<IResult> ListAsync(
        HttpContext ctx, NexusDbContext db, IAuthorizationService authz, Guid? pinnedConversationId,
        DateTimeOffset? beforeAt, Guid? beforeId, int? limit, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var error))
            return Results.BadRequest(new { code = "invalid_page_size", message = error });
        if (beforeAt.HasValue != beforeId.HasValue) return PaginationGuard.InvalidCursor();
        var actor = await db.FindOrganizationActorAsync(userId, ct);
        if (actor is null) return Results.Unauthorized();
        var actorContext = actor.ToActorContext();

        if (pinnedConversationId is { } id)
        {
            var access = await CommunityAccessHelper.ResolveAsync(db, actor, id, ct);
            if (access is null || access.Conversation.Kind == ConversationKind.Channel ||
                !authz.Can(actorContext, ResourceAction.ConversationRead, access.Resource)) return Results.Forbid();
        }

        var visible = from member in db.ConversationMembers.AsNoTracking()
                      join conversation in db.Conversations.AsNoTracking() on member.ConversationId equals conversation.Id
                      join message in db.Messages.AsNoTracking() on conversation.Id equals message.ConversationId
                      where member.UserId == userId && member.LeftAt == null && conversation.OrganizationId == db.ConversationOrganizationId &&
                            conversation.Kind != ConversationKind.Channel && (conversation.Kind != ConversationKind.Self ||
                                (conversation.CreatedByUserId == userId && member.Role == ResourceRole.Owner)) &&
                            message.DeletedAt == null && message.Seq > member.VisibleFromSeq &&
                            (message.ParentMessageId == null || db.Messages.Any(parent => parent.Id == message.ParentMessageId &&
                                parent.ConversationId == conversation.Id && parent.Seq > member.VisibleFromSeq))
                      select new { Conversation = conversation, Message = message, member.Role };

        var bookmarks = pinnedConversationId is { } pinnedId
            ? from item in visible.Where(row => row.Conversation.Id == pinnedId)
              join pin in db.MessagePins.AsNoTracking() on item.Message.Id equals pin.MessageId
              select new BookmarkRow { Conversation = item.Conversation, Message = item.Message, Role = item.Role, AddedAt = pin.PinnedAt }
            : from item in visible
              join saved in db.SavedMessages.AsNoTracking().Where(row => row.UserId == userId && row.IsSaved)
                  on item.Message.Id equals saved.MessageId
              select new BookmarkRow { Conversation = item.Conversation, Message = item.Message, Role = item.Role, AddedAt = saved.SavedAt };

        if (beforeAt is { } at && beforeId is { } cursorId)
        {
            var utc = at.ToUniversalTime();
            bookmarks = bookmarks.Where(row => row.AddedAt < utc || (row.AddedAt == utc && row.Message.Id.CompareTo(cursorId) < 0));
        }
        var rows = await bookmarks.OrderByDescending(row => row.AddedAt).ThenByDescending(row => row.Message.Id)
            .Take(pageSize + 1).ToListAsync(ct);
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(pageSize);

        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            items = rows.Where(row => authz.Can(actorContext, ResourceAction.ConversationRead,
                new ResourceContext(row.Conversation.Kind.ToResourceKind(), actor.OrganizationId, MembershipRole: row.Role,
                    TargetIsSelf: row.Conversation.Kind == ConversationKind.Self && row.Conversation.CreatedByUserId == userId)))
                .Select(row => new
                {
                    messageId = row.Message.Id, conversationId = row.Conversation.Id,
                    conversationTitle = row.Conversation.Title ?? "Direct message", row.Message.ParentMessageId,
                    row.Message.Seq, row.Message.CreatedAt, row.AddedAt,
                    bodyPreview = ConversationEndpoints.FormatPreview(row.Message.Body, "[Attachment]"),
                    sourceLink = ContentLinkCodec.Encode("m", row.Conversation.OrganizationId, row.Conversation.Id, row.Message.Id)
                }),
            hasMore,
            nextBefore = hasMore ? new { beforeAt = rows[^1].AddedAt, beforeId = rows[^1].Message.Id } : null,
            nextCursor = hasMore ? $"{rows[^1].AddedAt:O}|{rows[^1].Message.Id:D}" : null
        });
    }
}
