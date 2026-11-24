using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

public static class MessageThreadEndpoints
{
    public sealed record MarkThreadReadRequest(long LastReadReplySeq);

    private sealed record ThreadAccess(MessageEntity Parent, long VisibleFromSeq, long ChangeSeq);

    private sealed record ThreadState(
        Guid ConversationId,
        Guid ParentMessageId,
        bool IsFollowing,
        long LastReadReplySeq,
        long? LastReplySeq,
        bool HasUnread,
        long Version);

    public static IEndpointRouteBuilder MapMessageThreadEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        var thread = app.MapGroup(prefix + "/conversations/{conversationId:guid}/messages/{parentMessageId:guid}/thread");
        thread.MapGet("", GetThreadAsync);
        thread.MapPut("/follow", FollowAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        thread.MapDelete("/follow", UnfollowAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        thread.MapPut("/read", MarkReadAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        return app;
    }

    private static async Task<IResult> GetThreadAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid parentMessageId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        long? beforeSeq,
        long? afterSeq,
        long? aroundSeq,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();
        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });

        var cursorCount = (beforeSeq.HasValue ? 1 : 0) + (afterSeq.HasValue ? 1 : 0) + (aroundSeq.HasValue ? 1 : 0);
        if (cursorCount > 1) return PaginationGuard.InvalidCursor();

        // The parent summary, replies, and cursor must describe the same snapshot.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var (access, error) = await ResolveAccessAsync(db, authz, userId, conversationId, parentMessageId, lockConversation: false, ct);
        if (access is null) return error!;

        var replies = db.Messages.AsNoTracking().Where(m =>
            m.ConversationId == conversationId && m.ParentMessageId == parentMessageId && m.Seq > access.VisibleFromSeq);
        var (page, hasMore, hasNewer) = await MessagingEndpoints.QueryMessagePageAsync(replies, pageSize, beforeSeq, afterSeq, aroundSeq, ct);
        var projected = await MessagingEndpoints.ProjectMessagesAsync(db, [access.Parent, .. page], userId, ct);
        var parent = projected[0];
        var items = projected.Skip(1).ToList();
        var state = new ThreadState(conversationId, parentMessageId, parent.IsFollowing, parent.LastReadReplySeq,
            parent.LastReplySeq, parent.LastReplySeq.GetValueOrDefault() > parent.LastReadReplySeq, parent.ThreadStateVersion);

        await tx.CommitAsync(ct);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            parent,
            items,
            hasMore,
            hasNewer,
            nextBeforeSeq = items.Count > 0 ? items[0].Seq : (long?)null,
            nextAfterSeq = hasNewer && items.Count > 0 ? items[^1].Seq : (long?)null,
            changeSeq = access.ChangeSeq,
            state
        });
    }

    private static Task<IResult> FollowAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid parentMessageId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IHubContext<MessagingHub> hub,
        CancellationToken ct) =>
        UpdateStateAsync(ctx, conversationId, parentMessageId, db, authz, hub, true, null, ct);

    private static Task<IResult> UnfollowAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid parentMessageId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IHubContext<MessagingHub> hub,
        CancellationToken ct) =>
        UpdateStateAsync(ctx, conversationId, parentMessageId, db, authz, hub, false, null, ct);

    private static Task<IResult> MarkReadAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid parentMessageId,
        MarkThreadReadRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IHubContext<MessagingHub> hub,
        CancellationToken ct) =>
        UpdateStateAsync(ctx, conversationId, parentMessageId, db, authz, hub, null, request.LastReadReplySeq, ct);

    private static async Task<IResult> UpdateStateAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid parentMessageId,
        NexusDbContext db,
        IAuthorizationService authz,
        IHubContext<MessagingHub> hub,
        bool? following,
        long? readThrough,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId)) return Results.Unauthorized();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (access, error) = await ResolveAccessAsync(db, authz, userId, conversationId, parentMessageId, lockConversation: true, ct);
        if (access is null) return error!;

        var subscription = await db.ThreadSubscriptions.SingleOrDefaultAsync(
            s => s.ParentMessageId == parentMessageId && s.UserId == userId, ct);
        var wasFollowing = subscription?.IsFollowing ?? false;
        var previousRead = subscription?.LastReadReplySeq ?? 0;
        var latestReplySeq = access.Parent.LastReplySeq;
        var nextFollowing = following ?? wasFollowing;
        var nextRead = Math.Max(previousRead, Math.Min(readThrough.GetValueOrDefault(), latestReplySeq.GetValueOrDefault()));
        var changed = nextFollowing != wasFollowing || nextRead != previousRead;
        if (changed)
        {
            if (subscription is null)
            {
                subscription = new ThreadSubscriptionEntity { ParentMessageId = parentMessageId, UserId = userId };
                db.ThreadSubscriptions.Add(subscription);
            }

            subscription.IsFollowing = nextFollowing;
            subscription.LastReadReplySeq = nextRead;
            subscription.Version += 1;
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);

        var state = new ThreadState(conversationId, parentMessageId, nextFollowing, nextRead, latestReplySeq,
            latestReplySeq.GetValueOrDefault() > nextRead, subscription?.Version ?? 0);
        if (changed && await CanReceiveStateAsync(db, access.Parent, userId, ct))
        {
            await hub.Clients.Groups([MessagingHub.UserGroupName(db.OrganizationScopeId, userId)])
                .SendAsync("ThreadStateUpdated", state, ct);
        }

        return Results.Ok(state);
    }

    private static Task<bool> CanReceiveStateAsync(NexusDbContext db, MessageEntity parent, Guid userId, CancellationToken ct) =>
        db.ConversationMembers.AsNoTracking()
            .AnyAsync(m => m.ConversationId == parent.ConversationId && m.UserId == userId &&
                m.LeftAt == null && m.VisibleFromSeq < parent.Seq && db.ActiveScopePeople.Any(p => p.Id == userId), ct);

    private static async Task<(ThreadAccess? Access, IResult? Error)> ResolveAccessAsync(
        NexusDbContext db,
        IAuthorizationService authz,
        Guid userId,
        Guid conversationId,
        Guid parentMessageId,
        bool lockConversation,
        CancellationToken ct)
    {
        // Writes take the same lock as group removal, rejoin, and reply allocation.
        var conversations = lockConversation
            ? db.Conversations.FromSqlInterpolated($"""
                SELECT * FROM conversations
                WHERE id = {conversationId} AND organization_id IS NOT DISTINCT FROM {db.ConversationOrganizationId}
                FOR UPDATE
                """)
            : db.Conversations.Where(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId);
        var conversation = await conversations.AsNoTracking().SingleOrDefaultAsync(ct);
        var actor = await db.ActiveScopePeople.AsNoTracking().SingleOrDefaultAsync(person => person.Id == userId, ct);
        if (actor is null) return (null, Results.Unauthorized());
        if (conversation is null || conversation.Kind == ConversationKind.Channel) return (null, Results.Forbid());

        var member = await db.ConversationMembers.AsNoTracking().SingleOrDefaultAsync(
            m => m.ConversationId == conversationId && m.UserId == userId && m.LeftAt == null, ct);
        if (member is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(
            conversation.Kind.ToResourceKind(), actor.OrganizationId, MembershipRole: member.Role,
            TargetIsSelf: conversation.Kind == ConversationKind.Self && conversation.CreatedByUserId == userId)))
        {
            return (null, Results.Forbid());
        }

        var parent = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == parentMessageId &&
            m.ConversationId == conversationId && m.ParentMessageId == null && m.Seq > member.VisibleFromSeq, ct);
        return parent is null
            ? (null, Results.NotFound(new { code = "thread_not_found", message = "Thread not found." }))
            : (new ThreadAccess(parent, member.VisibleFromSeq, conversation.ChangeSeq), null);
    }
}
