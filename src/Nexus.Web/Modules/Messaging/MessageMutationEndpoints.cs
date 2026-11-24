using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

public static class MessageMutationEndpoints
{
    private const int EditWindowMinutes = 15;
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record EditMessageRequest(string? Body);

    public sealed record MessageMutationDelta(string Type, Guid MessageId, long Seq, long VisibilitySeq,
        Guid? ParentMessageId, long Version, string Body, DateTimeOffset? EditedAt, DateTimeOffset? DeletedAt);

    public static IEndpointRouteBuilder MapMessageMutationEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        var route = prefix + "/conversations/{conversationId:guid}/messages/{messageId:guid}";
        app.MapPatch(route, EditAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        app.MapDelete(route, RecallAsync).AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        return app;
    }

    private static Task<IResult> EditAsync(HttpContext ctx, Guid conversationId, Guid messageId,
        EditMessageRequest request, [FromServices] NexusDbContext db, IAuthorizationService authz,
        MessagingFanoutService fanout, CancellationToken ct) =>
        MutateAsync(ctx, conversationId, messageId, request.Body, recall: false, db, authz, fanout, ct);

    private static Task<IResult> RecallAsync(HttpContext ctx, Guid conversationId, Guid messageId,
        [FromServices] NexusDbContext db, IAuthorizationService authz, MessagingFanoutService fanout,
        CancellationToken ct) =>
        MutateAsync(ctx, conversationId, messageId, null, recall: true, db, authz, fanout, ct);

    private static async Task<IResult> MutateAsync(HttpContext ctx, Guid conversationId, Guid messageId,
        string? requestedBody, bool recall, NexusDbContext db, IAuthorizationService authz,
        MessagingFanoutService fanout, CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        ctx.Response.Headers.CacheControl = "no-store";

        var precondition = ctx.Request.Headers.IfMatch;
        if (precondition.Count == 0)
        {
            return Results.Json(new { code = "precondition_required", message = "Provide the current message version in If-Match." },
                statusCode: StatusCodes.Status428PreconditionRequired);
        }
        if (precondition.Count != 1 || precondition[0] is not { Length: >= 3 } tag ||
            tag[0] != '"' || tag[^1] != '"' ||
            !long.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var expectedVersion) ||
            expectedVersion <= 0 || !string.Equals(tag, VersionTag(expectedVersion), StringComparison.Ordinal))
        {
            return Results.BadRequest(new { code = "invalid_if_match", message = "Use one quoted numeric version in If-Match." });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await MessagingEndpoints.LockConversationAsync(db, conversationId, ct);
        var actor = await db.ActiveScopePeople.AsNoTracking().SingleOrDefaultAsync(a => a.Id == actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (conversation is null) return Results.Forbid();
        if (conversation.Kind == ConversationKind.Channel) return Results.NotFound();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversation, ct);
        if (access is null || access.Resource.MembershipRole is null ||
            !authz.Can(actor.ToActorContext(), ResourceAction.MessageSend, access.Resource))
        {
            return Results.Forbid();
        }

        var target = await MessageHistory.VisibleAfter(db, access.VisibleFromSeq)
            .Where(m => m.ConversationId == conversationId && m.Id == messageId)
            .Select(m => new
            {
                Message = m,
                VisibilitySeq = m.ParentMessageId == null ? m.Seq : db.Messages.Where(p =>
                    p.ConversationId == conversationId && p.Id == m.ParentMessageId).Select(p => p.Seq).FirstOrDefault()
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) return Results.NotFound(new { code = "message_not_found", message = "Message not found." });
        var message = target.Message;
        if (message.SenderId != actorId) return Results.Forbid();
        if (message.DeletedAt.HasValue) return AlreadyRecalled();

        var now = DateTimeOffset.UtcNow;
        if (now >= message.CreatedAt.AddMinutes(EditWindowMinutes))
        {
            return Results.Conflict(new { code = "message_edit_window_expired", message = "The 15-minute edit and recall window has ended." });
        }
        if (expectedVersion != message.Version) return VersionConflict(ctx, message.Version);

        var body = recall ? string.Empty : requestedBody?.Trim();
        if (body is null || body.Length > MessagingEndpoints.MaxMessageBodyLength ||
            (!recall && body.Length == 0 && !await db.Attachments.AnyAsync(a => a.MessageId == messageId &&
                a.State == AttachmentState.Attached, ct)))
        {
            return Results.BadRequest(new { code = "invalid_message_body", message = "Enter a message of up to 4000 characters or keep an attachment." });
        }

        var editedAt = recall ? message.EditedAt : now;
        DateTimeOffset? deletedAt = recall ? now : null;
        var version = message.Version + 1;
        var updated = await db.Messages.Where(m => m.Id == messageId && m.ConversationId == conversationId &&
                m.Version == expectedVersion && m.DeletedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.Body, body)
                .SetProperty(m => m.Version, version)
                .SetProperty(m => m.EditedAt, editedAt)
                .SetProperty(m => m.DeletedAt, deletedAt), ct);
        if (updated == 0)
        {
            var current = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId, ct);
            if (current is null) return Results.NotFound(new { code = "message_not_found", message = "Message not found." });
            return current.DeletedAt.HasValue ? AlreadyRecalled() : VersionConflict(ctx, current.Version);
        }

        await db.SharedLinks.Where(link => link.ConversationId == conversationId && link.MessageId == messageId)
            .ExecuteDeleteAsync(ct);
        if (recall)
        {
            await db.MessageReactions.Where(reaction => reaction.MessageId == messageId).ExecuteDeleteAsync(ct);
        }
        else
        {
            MessagingEndpoints.AddSharedLinks(db, message, body);
        }

        var delta = new MessageMutationDelta(recall ? "message_recalled" : "message_edited", messageId, message.Seq,
            target.VisibilitySeq, message.ParentMessageId, version, body, editedAt, deletedAt);
        await ScrubHistoricalEventsAsync(db, message, delta, ct);

        var changeSeq = conversation.ChangeSeq + 1;
        await db.Conversations.Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ChangeSeq, changeSeq), ct);
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            ChangeSeq = changeSeq,
            EventType = recall ? "message.recalled" : "message.edited",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(delta, WebJsonOptions),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await fanout.NotifyChatDeltaAsync(conversationId, changeSeq, target.VisibilitySeq, delta, ct);
        ctx.Response.Headers.ETag = VersionTag(version);
        return Results.Ok(delta);
    }

    private static string VersionTag(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    private static IResult AlreadyRecalled() => Results.Conflict(new
    {
        code = "message_recalled", message = "This message was recalled."
    });

    private static IResult VersionConflict(HttpContext ctx, long version)
    {
        ctx.Response.Headers.ETag = VersionTag(version);
        return Results.Json(new { code = "message_version_conflict", message = "The message has changed.", version },
            statusCode: StatusCodes.Status412PreconditionFailed);
    }

    private static async Task ScrubHistoricalEventsAsync(NexusDbContext db, MessageEntity message,
        MessageMutationDelta delta, CancellationToken ct)
    {
        var editState = JsonSerializer.Serialize(new { delta.Body, delta.Version, delta.EditedAt, delta.DeletedAt }, WebJsonOptions);
        var messageState = delta.DeletedAt.HasValue
            ? JsonSerializer.Serialize(new { delta.Body, delta.Version, delta.EditedAt, delta.DeletedAt,
                attachments = Array.Empty<object>(), reactions = Array.Empty<object>() }, WebJsonOptions)
            : editState;
        var messageKey = message.Id.ToString("D");
        var recalled = delta.DeletedAt.HasValue;
        // Retain event order and identifiers while removing superseded content from stored payloads.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE conversation_events
            SET event_type = CASE WHEN event_type IN ('message.reaction.added', 'message.reaction.removed')
                    THEN 'message.reaction.redacted' ELSE event_type END,
                payload_json = (CASE event_type
                WHEN 'message.thread_reply.created' THEN jsonb_set(payload_json::jsonb, ARRAY['message'],
                    (payload_json::jsonb -> 'message') || CAST({messageState} AS jsonb))
                WHEN 'message.created' THEN payload_json::jsonb || CAST({messageState} AS jsonb)
                WHEN 'message.edited' THEN payload_json::jsonb || CAST({editState} AS jsonb)
                ELSE payload_json::jsonb - 'emoji' - 'userId' - 'reacted'
                END)::text
            WHERE conversation_id = {message.ConversationId}
              AND event_type IN ('message.created', 'message.thread_reply.created', 'message.edited',
                  'message.reaction.added', 'message.reaction.removed')
              AND ({recalled} OR event_type NOT IN ('message.reaction.added', 'message.reaction.removed'))
              AND (CASE event_type
                  WHEN 'message.created' THEN payload_json::jsonb ->> 'id'
                  WHEN 'message.thread_reply.created' THEN payload_json::jsonb -> 'message' ->> 'id'
                  ELSE payload_json::jsonb ->> 'messageId' END) = {messageKey}
            """, ct);
    }
}
