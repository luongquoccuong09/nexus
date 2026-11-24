using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

public static class MessageReactionsEndpoints
{
    public const int MaxEmojiLength = 32;
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMessageReactionsEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        var route = prefix + "/conversations/{conversationId:guid}/messages/{messageId:guid}/reactions/{emoji}";
        app.MapPut(route, HandleAddReactionAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        app.MapDelete(route, HandleRemoveReactionAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        return app;
    }

    private static Task<IResult> HandleAddReactionAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid messageId,
        string emoji,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct) =>
        HandleToggleReactionAsync(ctx, conversationId, messageId, emoji, isAdd: true, db, authz, fanout, ct);

    private static Task<IResult> HandleRemoveReactionAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid messageId,
        string emoji,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct) =>
        HandleToggleReactionAsync(ctx, conversationId, messageId, emoji, isAdd: false, db, authz, fanout, ct);

    private static async Task<IResult> HandleToggleReactionAsync(
        HttpContext ctx,
        Guid conversationId,
        Guid messageId,
        string emoji,
        bool isAdd,
        NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var decodedEmoji = Uri.UnescapeDataString(emoji ?? string.Empty).Trim();
        if (decodedEmoji.Length is < 1 or > MaxEmojiLength || decodedEmoji.Any(char.IsControl))
        {
            return Results.BadRequest(new
            {
                code = "invalid_emoji",
                message = $"Emoji must be 1 to {MaxEmojiLength} non-control characters."
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Membership changes and message sequence allocation take the same conversation lock.
        var conv = await db.Conversations
            .FromSqlInterpolated($"""
                SELECT * FROM conversations
                WHERE id = {conversationId} AND organization_id IS NOT DISTINCT FROM {db.ConversationOrganizationId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (conv is null) return Results.Forbid();

        if (conv.Kind == ConversationKind.Channel)
        {
            return Results.NotFound();
        }

        var member = await db.ConversationMembers
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var isBlocked = false;
        if (conv.Kind == ConversationKind.Dm && member is not null)
        {
            var peerId = await db.ConversationMembers
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId && m.UserId != actorId)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync(ct);

            if (peerId != Guid.Empty)
            {
                isBlocked = await db.UserBlocks.AnyAsync(
                    b => b.OrganizationId == db.OrganizationScopeId &&
                         ((b.BlockerUserId == actorId && b.BlockedUserId == peerId) ||
                          (b.BlockerUserId == peerId && b.BlockedUserId == actorId)),
                    ct);
            }
        }

        var kind = conv.Kind.ToResourceKind();
        var targetIsSelf = conv.Kind == ConversationKind.Self && conv.CreatedByUserId == actorId;
        var resource = new ResourceContext(kind, actor.OrganizationId, MembershipRole: member?.Role, IsBlocked: isBlocked, TargetIsSelf: targetIsSelf);
        var allowed = authz.Can(actor.ToActorContext(), ResourceAction.MessageSend, resource);
        if (!allowed || member is null)
        {
            return Results.Forbid();
        }

        var message = await db.Messages
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == messageId && m.ConversationId == conversationId, ct);

        if (message is null || message.Seq <= member.VisibleFromSeq)
        {
            return Results.NotFound(new { code = "message_not_found", message = "Message not found." });
        }

        if (message.DeletedAt.HasValue)
        {
            return Results.Conflict(new { code = "message_recalled", message = "Cannot react to a recalled message." });
        }

        var now = DateTimeOffset.UtcNow;
        var rowsAffected = isAdd
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO message_reactions (message_id, user_id, emoji, created_at)
                VALUES ({messageId}, {actorId}, {decodedEmoji}, {now})
                ON CONFLICT (message_id, user_id, emoji) DO NOTHING
                """, ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM message_reactions
                WHERE message_id = {messageId} AND user_id = {actorId} AND emoji = {decodedEmoji}
                """, ct);

        var changeSeq = conv.ChangeSeq;
        if (rowsAffected > 0)
        {
            changeSeq++;
            await db.Conversations.Where(c => c.Id == conversationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ChangeSeq, changeSeq), ct);
            var eventPayload = new
            {
                messageId,
                seq = message.Seq,
                emoji = decodedEmoji,
                userId = actorId,
                reacted = isAdd
            };

            db.ConversationEvents.Add(new ConversationEventEntity
            {
                Id = Guid.CreateVersion7(),
                ConversationId = conversationId,
                ChangeSeq = changeSeq,
                EventType = isAdd ? "message.reaction.added" : "message.reaction.removed",
                ActorUserId = actorId,
                PayloadJson = JsonSerializer.Serialize(eventPayload, WebJsonOptions),
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        if (rowsAffected > 0)
        {
            if (isAdd)
            {
                NexusMeters.ReactionCreated();
            }

            await fanout.NotifyChatDeltaAsync(
                conversationId,
                changeSeq,
                message.Seq,
                new
                {
                    type = "reaction",
                    messageId,
                    seq = message.Seq,
                    emoji = decodedEmoji,
                    userId = actorId,
                    reacted = isAdd
                },
                ct);
        }
        return Results.Ok(new
        {
            messageId,
            seq = message.Seq,
            emoji = decodedEmoji,
            reacted = isAdd,
            changed = rowsAffected > 0,
            changeSeq
        });
    }
}
