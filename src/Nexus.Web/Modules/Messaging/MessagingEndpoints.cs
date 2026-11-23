using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using Npgsql;

namespace Nexus.Web.Modules.Messaging;

public static class MessagingEndpoints
{
    public const int MaxMessageBodyLength = 4000;
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record SendMessageRequest(string ClientMsgId, string? Body, Guid[]? AttachmentIds = null);
    public sealed record MarkReadRequest(long LastReadSeq);

    public sealed record MessageDto(
        Guid Id,
        Guid ConversationId,
        long Seq,
        Guid SenderId,
        string SenderDisplayName,
        bool SenderIsUnverified,
        string ClientMsgId,
        string Body,
        DateTimeOffset CreatedAt,
        DateTimeOffset? EditedAt,
        DateTimeOffset? DeletedAt,
        IReadOnlyList<AttachmentDto>? Attachments = null);

    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder app)
    {
        var convs = app.MapGroup("/api/conversations/{conversationId:guid}");
        convs.MapPost("/messages", HandleSendMessageAsync);
        convs.MapGet("/messages", HandleGetMessagesAsync);
        convs.MapGet("/changes", HandleGetChangesAsync);
        convs.MapPut("/read", HandleMarkReadAsync);

        app.MapGet("/api/sync/reconcile", HandleReconcileAsync);
        return app;
    }

    private static async Task<IResult> HandleSendMessageAsync(
        HttpContext ctx,
        Guid conversationId,
        SendMessageRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        IOptions<FilePolicyOptions> filePolicy,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await userManager.FindByIdAsync(actorId.ToString());
        if (actor is null) return Results.Unauthorized();

        var clientMsgId = (request.ClientMsgId ?? string.Empty).Trim();
        if (clientMsgId.Length is < 1 or > 128)
        {
            return Results.BadRequest(new { code = "invalid_client_msg_id", message = "clientMsgId must be 1 to 128 characters." });
        }

        var attachmentIds = (request.AttachmentIds ?? [])
            .Distinct()
            .ToArray();

        if (attachmentIds.Length > filePolicy.Value.MaxAttachmentsPerMessage)
        {
            return Results.BadRequest(new
            {
                code = "too_many_attachments",
                message = $"At most {filePolicy.Value.MaxAttachmentsPerMessage} attachments may be attached per message."
            });
        }

        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length > MaxMessageBodyLength || (body.Length == 0 && attachmentIds.Length == 0))
        {
            return Results.BadRequest(new { code = "invalid_message_body", message = $"Message body must be 1 to {MaxMessageBodyLength} characters (or include an attachment)." });
        }

        var payloadHash = ComputePayloadHash(conversationId, body, attachmentIds);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var conv = await db.Conversations
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == actor.OrganizationId, ct);

        var member = await db.ConversationMembers
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var isBlocked = false;
        if (conv?.Kind == ConversationKind.Dm && member is not null)
        {
            var peerId = await db.ConversationMembers
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId && m.UserId != actorId)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync(ct);

            if (peerId != Guid.Empty)
            {
                isBlocked = await db.UserBlocks.AnyAsync(
                    b => (b.BlockerUserId == actorId && b.BlockedUserId == peerId) ||
                         (b.BlockerUserId == peerId && b.BlockedUserId == actorId),
                    ct);
            }
        }

        var kind = conv?.Kind == ConversationKind.Dm ? ResourceKind.Dm : ResourceKind.GroupChat;
        var allowed = conv is not null && authz.Can(
            actor.ToActorContext(),
            ResourceAction.MessageSend,
            new ResourceContext(kind, actor.OrganizationId, MembershipRole: member?.Role, IsBlocked: isBlocked));

        if (!allowed)
        {
            return Results.Forbid();
        }

        // Idempotency check on (sender_id, client_msg_id) inside the transaction (M2.3 / SEC-19).
        var existing = await db.Messages
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.SenderId == actorId && m.ClientMsgId == clientMsgId, ct);

        if (existing is not null)
        {
            if (existing.ConversationId == conversationId &&
                string.Equals(existing.PayloadSha256, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                var existingAttachments = await db.Attachments.AsNoTracking()
                    .Where(a => a.MessageId == existing.Id && a.State == AttachmentState.Attached)
                    .OrderBy(a => a.CreatedAt)
                    .Select(a => FilesEndpoints.ToDto(a))
                    .ToListAsync(ct);
                return Results.Ok(ToMessageDto(existing, actor, existingAttachments));
            }

            return Results.Conflict(new { code = "client_msg_id_conflict", message = "clientMsgId was already used with a different conversation or payload." });
        }

        List<AttachmentEntity> pendingAttachments = [];
        if (attachmentIds.Length > 0)
        {
            pendingAttachments = await db.Attachments
                .Where(a => attachmentIds.Contains(a.Id) &&
                            a.ConversationId == conversationId &&
                            a.UploaderUserId == actorId &&
                            a.State == AttachmentState.Pending)
                .ToListAsync(ct);

            if (pendingAttachments.Count != attachmentIds.Length)
            {
                return Results.BadRequest(new
                {
                    code = "invalid_attachments",
                    message = "One or more attachments are missing, already attached, or do not belong to you in this conversation."
                });
            }
        }

        var now = DateTimeOffset.UtcNow;

        // Atomically allocate next_seq and change_seq in PostgreSQL (row lock held until commit).
        var (nextSeq, changeSeq) = await AllocateSequencesAsync(db, conversationId, now, ct);

        var message = new MessageEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            Seq = nextSeq,
            SenderId = actorId,
            ClientMsgId = clientMsgId,
            PayloadSha256 = payloadHash,
            Body = body,
            CreatedAt = now
        };

        foreach (var att in pendingAttachments)
        {
            att.MessageId = message.Id;
            att.State = AttachmentState.Attached;
            att.AttachedAt = now;
        }

        var attachmentDtos = pendingAttachments
            .OrderBy(a => a.CreatedAt)
            .Select(FilesEndpoints.ToDto)
            .ToList();

        var dto = ToMessageDto(message, actor, attachmentDtos);
        var evt = new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            ChangeSeq = changeSeq,
            EventType = "message.created",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(dto, WebJsonOptions),
            CreatedAt = now
        };

        db.Messages.Add(message);
        db.ConversationEvents.Add(evt);

        try
        {
            await db.SaveChangesAsync(ct);

            // Sender has implicitly read up to their own sent message.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE conversation_members
                SET last_read_seq = GREATEST(last_read_seq, {nextSeq})
                WHERE conversation_id = {conversationId} AND user_id = {actorId} AND left_at IS NULL
                """, ct);

            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();

            var raced = await db.Messages
                .AsNoTracking()
                .SingleOrDefaultAsync(m => m.SenderId == actorId && m.ClientMsgId == clientMsgId, ct);

            if (raced is not null)
            {
                if (raced.ConversationId == conversationId &&
                    string.Equals(raced.PayloadSha256, payloadHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Ok(ToMessageDto(raced, actor));
                }

                return Results.Conflict(new { code = "client_msg_id_conflict" });
            }

            throw;
        }

        // Fan out lightweight (conversationId, changeSeq) signal to user:{id} groups after commit (M2.4).
        await fanout.NotifyConversationChangedAsync(conversationId, changeSeq, ct);

        return Results.Created($"/api/conversations/{conversationId}/messages/{message.Id}", dto);
    }

    private static async Task<IResult> HandleGetMessagesAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IAuthorizationService authz,
        long? beforeSeq,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await userManager.FindByIdAsync(actorId.ToString());
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var conv = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == actor.OrganizationId, ct);
        var member = await db.ConversationMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var kind = conv?.Kind == ConversationKind.Dm ? ResourceKind.Dm : ResourceKind.GroupChat;
        if (conv is null || member is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(kind, actor.OrganizationId, MembershipRole: member.Role)))
        {
            return Results.Forbid();
        }

        var query = db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Seq > member.VisibleFromSeq);

        if (beforeSeq.HasValue)
        {
            query = query.Where(m => m.Seq < beforeSeq.Value);
        }

        var rows = await query
            .OrderByDescending(m => m.Seq)
            .Take(pageSize + 1)
            .Join(
                db.Users.AsNoTracking(),
                m => m.SenderId,
                u => u.Id,
                (m, u) => new
                {
                    Message = m,
                    u.DisplayName,
                    u.TrustLevel
                })
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows = rows.Take(pageSize).ToList();
        }

        rows.Reverse();
        var msgIds = rows.Select(r => r.Message.Id).ToList();
        var attachmentsByMsg = msgIds.Count == 0
            ? new Dictionary<Guid, List<AttachmentDto>>()
            : (await db.Attachments.AsNoTracking()
                .Where(a => a.MessageId != null && msgIds.Contains(a.MessageId.Value) && a.State == AttachmentState.Attached)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(ct))
                .GroupBy(a => a.MessageId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(FilesEndpoints.ToDto).ToList());

        var items = rows.Select(r => new MessageDto(
            r.Message.Id,
            r.Message.ConversationId,
            r.Message.Seq,
            r.Message.SenderId,
            r.DisplayName,
            r.TrustLevel == TrustLevel.Unverified,
            r.Message.ClientMsgId,
            r.Message.DeletedAt.HasValue ? string.Empty : r.Message.Body,
            r.Message.CreatedAt,
            r.Message.EditedAt,
            r.Message.DeletedAt,
            r.Message.DeletedAt.HasValue
                ? []
                : attachmentsByMsg.GetValueOrDefault(r.Message.Id) ?? [])).ToList();

        return Results.Ok(new
        {
            items,
            hasMore,
            nextBeforeSeq = items.Count > 0 ? items[0].Seq : (long?)null,
            changeSeq = conv.ChangeSeq,
            lastReadSeq = member.LastReadSeq
        });
    }

    private static async Task<IResult> HandleGetChangesAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IAuthorizationService authz,
        long after = 0,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await userManager.FindByIdAsync(actorId.ToString());
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var conv = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == actor.OrganizationId, ct);
        var member = await db.ConversationMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var kind = conv?.Kind == ConversationKind.Dm ? ResourceKind.Dm : ResourceKind.GroupChat;
        if (conv is null || member is null || !authz.Can(actor.ToActorContext(), ResourceAction.DeltaSync, new ResourceContext(kind, actor.OrganizationId, MembershipRole: member.Role)))
        {
            return Results.Forbid();
        }

        // Full-resync trigger when client cursor falls behind retained delta window (SYNC_PROTOCOL.md §3).
        if (after > 0 && after < conv.MinAvailableChangeSeq - 1)
        {
            return Results.Json(
                new { code = "resync_required", latestChangeSeq = conv.ChangeSeq },
                statusCode: StatusCodes.Status410Gone);
        }

        var events = await db.ConversationEvents
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.ChangeSeq > after)
            .OrderBy(e => e.ChangeSeq)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = events.Count > pageSize;
        if (hasMore)
        {
            events = events.Take(pageSize).ToList();
        }

        var nextAfter = events.Count > 0 ? events[^1].ChangeSeq : after;
        var items = new List<object>(events.Count);
        foreach (var e in events)
        {
            using var doc = JsonDocument.Parse(e.PayloadJson);
            var payload = doc.RootElement.Clone();

            // Enforce visible_from_seq on delta sync so rejoined members cannot read pre-join messages via /changes.
            if (member.VisibleFromSeq > 0 &&
                e.EventType.StartsWith("message.", StringComparison.Ordinal) &&
                payload.TryGetProperty("seq", out var seqEl) &&
                seqEl.TryGetInt64(out var msgSeq) &&
                msgSeq <= member.VisibleFromSeq)
            {
                continue;
            }

            items.Add(new
            {
                id = e.Id,
                conversationId = e.ConversationId,
                changeSeq = e.ChangeSeq,
                eventType = e.EventType,
                actorUserId = e.ActorUserId,
                payload,
                createdAt = e.CreatedAt
            });
        }

        return Results.Ok(new
        {
            items,
            hasMore,
            nextAfter,
            latestChangeSeq = conv.ChangeSeq
        });
    }

    private static async Task<IResult> HandleReconcileAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await userManager.FindByIdAsync(actorId.ToString());
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var items = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == actorId && m.LeftAt == null)
            .Join(
                db.Conversations.AsNoTracking().Where(c => c.OrganizationId == actor.OrganizationId),
                m => m.ConversationId,
                c => c.Id,
                (m, c) => new
                {
                    conversationId = c.Id,
                    kind = c.Kind.ToString().ToLowerInvariant(),
                    changeSeq = c.ChangeSeq,
                    nextSeq = c.NextSeq,
                    lastReadSeq = m.LastReadSeq,
                    minAvailableChangeSeq = c.MinAvailableChangeSeq,
                    updatedAt = c.UpdatedAt
                })
            .OrderByDescending(x => x.updatedAt)
            .Take(pageSize)
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }

    private static async Task<IResult> HandleMarkReadAsync(
        HttpContext ctx,
        Guid conversationId,
        MarkReadRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await userManager.FindByIdAsync(actorId.ToString());
        if (actor is null) return Results.Unauthorized();

        var conv = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == actor.OrganizationId, ct);
        var member = await db.ConversationMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var kind = conv?.Kind == ConversationKind.Dm ? ResourceKind.Dm : ResourceKind.GroupChat;
        if (conv is null || member is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(kind, actor.OrganizationId, MembershipRole: member.Role)))
        {
            return Results.Forbid();
        }

        var requested = Math.Max(0L, request.LastReadSeq);

        // Atomic monotonic GREATEST(last_read_seq, LEAST(@seq, @maxSeq)) (M2.7).
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE conversation_members
            SET last_read_seq = GREATEST(last_read_seq, LEAST({requested}, {conv.NextSeq}))
            WHERE conversation_id = {conversationId} AND user_id = {actorId} AND left_at IS NULL
            """, ct);

        var updatedSeq = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null)
            .Select(m => m.LastReadSeq)
            .SingleAsync(ct);

        await fanout.NotifyReadStateUpdatedAsync(actorId, conversationId, updatedSeq, ct);

        return Results.Ok(new
        {
            conversationId,
            lastReadSeq = updatedSeq,
            unreadCount = Math.Max(0L, conv.NextSeq - updatedSeq)
        });
    }

    private static async Task<(long NextSeq, long ChangeSeq)> AllocateSequencesAsync(
        NexusDbContext db,
        Guid conversationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        await using var cmd = new NpgsqlCommand(
            """
            UPDATE conversations
            SET next_seq = next_seq + 1,
                change_seq = change_seq + 1,
                updated_at = @now
            WHERE id = @id
            RETURNING next_seq, change_seq
            """,
            conn,
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());

        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("id", conversationId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Conversation not found during sequence allocation.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static string ComputePayloadHash(Guid conversationId, string body, Guid[] attachmentIds)
    {
        var attSuffix = attachmentIds.Length == 0
            ? string.Empty
            : "\n" + string.Join(',', attachmentIds.OrderBy(x => x).Select(x => x.ToString("N")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{conversationId:N}\n{body}{attSuffix}")));
    }

    private static MessageDto ToMessageDto(MessageEntity m, NexusUser sender, IReadOnlyList<AttachmentDto>? attachments = null) => new(
        m.Id,
        m.ConversationId,
        m.Seq,
        m.SenderId,
        sender.DisplayName,
        sender.TrustLevel == TrustLevel.Unverified,
        m.ClientMsgId,
        m.Body,
        m.CreatedAt,
        m.EditedAt,
        m.DeletedAt,
        attachments ?? []);
}
