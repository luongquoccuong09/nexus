using Nexus.Web.Modules.Org;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Modules.Notifications;
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

    public sealed record SendMessageRequest(string ClientMsgId, string? Body, Guid[]? AttachmentIds = null,
        bool PreserveReadState = false, Guid? ParentMessageId = null);
    public sealed record MarkReadRequest(long LastReadSeq);

    public sealed record ReactionSummaryDto(string Emoji, int Count, bool ReactedByMe);

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
        IReadOnlyList<AttachmentDto>? Attachments = null,
        IReadOnlyList<ReactionSummaryDto>? Reactions = null,
        Guid? ParentMessageId = null,
        int ReplyCount = 0,
        DateTimeOffset? LastReplyAt = null,
        long? LastReplySeq = null,
        bool IsFollowing = false,
        long LastReadReplySeq = 0,
        long ThreadStateVersion = 0,
        bool HasUnread = false);

    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        var convs = app.MapGroup(prefix + "/conversations/{conversationId:guid}");
        convs.MapPost("/messages", HandleSendMessageAsync)
            .AddEndpointFilter(static (invocation, next) =>
                invocation.GetArgument<SendMessageRequest>(2).ParentMessageId.HasValue
                    ? OrganizationManagementEndpoints.ValidateCsrfAsync(invocation, next)
                    : next(invocation));
        convs.MapGet("/messages", HandleGetMessagesAsync);
        convs.MapGet("/changes", HandleGetChangesAsync);
        convs.MapPut("/read", HandleMarkReadAsync);

        app.MapGet(prefix + "/sync/reconcile", HandleReconcileAsync);
        return app;
    }

    private static async Task<IResult> HandleSendMessageAsync(
        HttpContext ctx,
        Guid conversationId,
        SendMessageRequest request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        WebPushTransport push,
        IEmailDigestTransport digest,
        IOptions<FilePolicyOptions> filePolicy,
        CancellationToken ct)
    {
        var requestStarted = Stopwatch.GetTimestamp();
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
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

        var payloadHash = ComputePayloadHash(conversationId, body, attachmentIds, request.ParentMessageId);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Membership changes and chat mutations use the same conversation lock.
        var conv = await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM conversations
            WHERE id = {conversationId} AND organization_id IS NOT DISTINCT FROM {db.ConversationOrganizationId}
            FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        var actor = await db.ActiveScopePeople.AsNoTracking().SingleOrDefaultAsync(a => a.Id == actorId, ct);
        if (actor is null) return Results.Unauthorized();
        if (conv is null) return Results.Forbid();

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
                    b => b.OrganizationId == db.OrganizationScopeId && ((b.BlockerUserId == actorId && b.BlockedUserId == peerId) ||
                         (b.BlockerUserId == peerId && b.BlockedUserId == actorId)),
                    ct);
            }
        }

        var kind = conv.Kind.ToResourceKind();
        var targetIsSelf = conv.Kind == ConversationKind.Self && conv.CreatedByUserId == actorId;
        var resource = new ResourceContext(kind, actor.OrganizationId, MembershipRole: member?.Role, IsBlocked: isBlocked, TargetIsSelf: targetIsSelf);
        if (member is null || !authz.Can(actor.ToActorContext(), ResourceAction.MessageSend, resource))
        {
            return Results.Forbid();
        }

        MessageEntity? parent = null;
        if (request.ParentMessageId is { } parentMessageId)
        {
            if (conv.Kind == ConversationKind.Channel) return Results.NotFound();

            parent = await MessageHistory.VisibleAfter(db, member.VisibleFromSeq).AsTracking()
                .SingleOrDefaultAsync(m => m.Id == parentMessageId && m.ConversationId == conversationId, ct);
            if (parent is null) return Results.NotFound(new { code = "message_not_found" });
            if (parent.ParentMessageId.HasValue)
            {
                return Results.BadRequest(new { code = "nested_thread_not_allowed", message = "Replies must be attached to the first message in the thread." });
            }
        }

        // Idempotency check on (sender_id, client_msg_id) inside the transaction (M2.3 / SEC-19).
        var existing = await db.Messages
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.SenderId == actorId && m.ClientMsgId == clientMsgId, ct);

        if (existing is not null)
        {
            if (existing.ConversationId == conversationId &&
                existing.ParentMessageId == request.ParentMessageId &&
                string.Equals(existing.PayloadSha256, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                if (existing.Seq <= member.VisibleFromSeq) return Results.NotFound(new { code = "message_not_found" });
                var existingAttachments = await db.Attachments.AsNoTracking()
                    .Where(a => a.MessageId == existing.Id && a.State == AttachmentState.Attached)
                    .OrderBy(a => a.CreatedAt)
                    .Select(a => FilesEndpoints.ToDto(a))
                    .ToListAsync(ct);
                return Results.Ok(ToMessageDto(existing, actor, existingAttachments));
            }

            return Results.Conflict(new { code = "client_msg_id_conflict", message = "clientMsgId was already used with a different conversation or payload." });
        }

        if (parent?.DeletedAt is not null)
        {
            return Results.Conflict(new { code = "message_recalled", message = "This thread starts with a recalled message." });
        }

        List<AttachmentEntity> pendingAttachments = [];
        if (attachmentIds.Length > 0)
        {
            pendingAttachments = await AttachmentProjection.LoadPendingForBindingAsync(
                db, conversationId, db.ConversationOrganizationId, actorId, attachmentIds, ct);

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
        var (nextSeq, changeSeq) = await AllocateSequencesAsync(db, conversationId, now, parent is null, ct);

        var message = new MessageEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            Seq = nextSeq,
            SenderId = actorId,
            ClientMsgId = clientMsgId,
            PayloadSha256 = payloadHash,
            Body = body,
            CreatedAt = now,
            ParentMessageId = parent?.Id
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
        object eventPayload = dto;
        if (parent is not null)
        {
            parent.ReplyCount++;
            parent.LastReplyAt = now;
            parent.LastReplySeq = nextSeq;
            eventPayload = new
            {
                type = "thread_reply_created",
                messageId = parent.Id,
                seq = parent.Seq,
                message = dto,
                parent.ReplyCount,
                parent.LastReplyAt,
                parent.LastReplySeq
            };
        }

        var evt = new ConversationEventEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            ChangeSeq = changeSeq,
            EventType = parent is null ? "message.created" : "message.thread_reply.created",
            ActorUserId = actorId,
            PayloadJson = JsonSerializer.Serialize(eventPayload, WebJsonOptions),
            CreatedAt = now
        };

        db.Messages.Add(message);
        foreach (var link in SharedLinkExtractor.Extract(body))
        {
            db.SharedLinks.Add(new SharedLinkEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = db.ConversationOrganizationId,
                ConversationId = conversationId,
                MessageId = message.Id,
                MessageSeq = message.Seq,
                SenderUserId = actorId,
                Url = link.Url,
                Host = link.Host,
                Label = link.Label,
                CreatedAt = now
            });
        }
        db.ConversationEvents.Add(evt);
        if (conv.Kind != ConversationKind.Self)
        {
            await MentionOutbox.EnqueueAsync(db, authz, actor, resource, conversationId, null,
                MentionOutbox.MessageKind, message.Id, body, now, push.PublicKey is not null || digest.IsEnabled, ct, parent);
        }

        try
        {
            await db.SaveChangesAsync(ct);

            // Legacy senders advance their own cursor during send. The SPA reports what it displays.
            if (parent is null && !request.PreserveReadState)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE conversation_members
                    SET last_read_seq = GREATEST(last_read_seq, {nextSeq})
                    WHERE conversation_id = {conversationId} AND user_id = {actorId} AND left_at IS NULL
                    """, ct);
            }

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
                    raced.ParentMessageId == request.ParentMessageId && raced.Seq > member.VisibleFromSeq &&
                    string.Equals(raced.PayloadSha256, payloadHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Ok(ToMessageDto(raced, actor));
                }

                return Results.Conflict(new { code = "client_msg_id_conflict" });
            }

            throw;
        }

        NexusMeters.MessageCreated();
        // Fan out lightweight (conversationId, changeSeq) signal to user:{id} groups after commit (M2.4).
        var committedAt = Stopwatch.GetTimestamp();
        if (parent is null)
        {
            await fanout.NotifyMessageCreatedAsync(conversationId, changeSeq, conv.Kind, conv.CommunityId, dto, ct);
        }
        else
        {
            await fanout.NotifyChatDeltaAsync(conversationId, changeSeq, parent.Seq, eventPayload, ct);
        }
        var commitMs = Stopwatch.GetElapsedTime(requestStarted, committedAt).TotalMilliseconds;
        var fanoutMs = Stopwatch.GetElapsedTime(committedAt).TotalMilliseconds;
        NexusMeters.RecordFanout(fanoutMs);
        ctx.Response.Headers.Append("Server-Timing", FormattableString.Invariant($"message_commit;dur={commitMs:F1}, message_fanout;dur={fanoutMs:F1}"));

        return Results.Created($"{OrganizationScope.ApiPath(db.ConversationOrganizationId)}/conversations/{conversationId}/messages/{message.Id}", dto);
    }

    private static async Task<IResult> HandleGetMessagesAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        long? beforeSeq,
        long? afterSeq,
        long? aroundSeq,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var cursorCount = (beforeSeq.HasValue ? 1 : 0) + (afterSeq.HasValue ? 1 : 0) + (aroundSeq.HasValue ? 1 : 0);
        if (cursorCount > 1)
        {
            return PaginationGuard.InvalidCursor();
        }

        // Pair the reaction summaries with the change cursor from the same snapshot.
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var conv = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId, ct);
        var member = await db.ConversationMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var kind = conv?.Kind.ToResourceKind() ?? ResourceKind.GroupChat;
        var targetIsSelf = conv?.Kind == ConversationKind.Self && conv.CreatedByUserId == actorId;
        if (conv is null || member is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(kind, actor.OrganizationId, MembershipRole: member.Role, TargetIsSelf: targetIsSelf)))
        {
            return Results.Forbid();
        }

        var visibleQuery = db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.ParentMessageId == null && m.Seq > member.VisibleFromSeq);

        var (selectedMessages, hasMore, hasNewer) = await QueryMessagePageAsync(visibleQuery, pageSize, beforeSeq, afterSeq, aroundSeq, ct);
        var items = await ProjectMessagesAsync(db, selectedMessages, actorId, ct);

        await snapshot.CommitAsync(ct);
        return Results.Ok(new
        {
            items,
            hasMore,
            hasNewer,
            nextBeforeSeq = items.Count > 0 ? items[0].Seq : (long?)null,
            nextAfterSeq = hasNewer && items.Count > 0 ? items[^1].Seq : (long?)null,
            changeSeq = conv.ChangeSeq,
            lastReadSeq = member.LastReadSeq
        });
    }

    internal static async Task<(List<MessageEntity> Items, bool HasMore, bool HasNewer)> QueryMessagePageAsync(
        IQueryable<MessageEntity> visibleQuery,
        int pageSize,
        long? beforeSeq,
        long? afterSeq,
        long? aroundSeq,
        CancellationToken ct)
    {

        bool hasMore;
        bool hasNewer;
        List<MessageEntity> selectedMessages;

        if (aroundSeq.HasValue)
        {
            var olderBudget = Math.Max(1, (pageSize + 1) / 2);
            var olderPart = await visibleQuery
                .Where(m => m.Seq <= aroundSeq.Value)
                .OrderByDescending(m => m.Seq)
                .Take(olderBudget + 1)
                .ToListAsync(ct);

            hasMore = olderPart.Count > olderBudget;
            if (hasMore) olderPart = olderPart.Take(olderBudget).ToList();
            olderPart.Reverse();

            var newerBudget = Math.Max(0, pageSize - olderPart.Count);
            var newerPart = await visibleQuery
                .Where(m => m.Seq > aroundSeq.Value)
                .OrderBy(m => m.Seq)
                .Take(newerBudget + 1)
                .ToListAsync(ct);

            hasNewer = newerPart.Count > newerBudget;
            if (hasNewer) newerPart = newerPart.Take(newerBudget).ToList();

            selectedMessages = [.. olderPart, .. newerPart];
        }
        else if (afterSeq.HasValue)
        {
            var newerRows = await visibleQuery
                .Where(m => m.Seq > afterSeq.Value)
                .OrderBy(m => m.Seq)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            hasNewer = newerRows.Count > pageSize;
            if (hasNewer) newerRows = newerRows.Take(pageSize).ToList();
            hasMore = newerRows.Count > 0 && await visibleQuery.AnyAsync(m => m.Seq < newerRows[0].Seq, ct);
            selectedMessages = newerRows;
        }
        else
        {
            var query = beforeSeq.HasValue
                ? visibleQuery.Where(m => m.Seq < beforeSeq.Value)
                : visibleQuery;

            var pageRows = await query
                .OrderByDescending(m => m.Seq)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            hasMore = pageRows.Count > pageSize;
            if (hasMore) pageRows = pageRows.Take(pageSize).ToList();
            pageRows.Reverse();
            var newestInPage = pageRows.Count > 0 ? pageRows[^1].Seq : 0;
            hasNewer = beforeSeq.HasValue && pageRows.Count > 0 && await visibleQuery.AnyAsync(m => m.Seq > newestInPage, ct);
            selectedMessages = pageRows;
        }

        return (selectedMessages, hasMore, hasNewer);
    }

    internal static async Task<List<MessageDto>> ProjectMessagesAsync(
        NexusDbContext db, IReadOnlyList<MessageEntity> selectedMessages, Guid actorId, CancellationToken ct)
    {
        if (selectedMessages.Count == 0) return [];

        var senderIds = selectedMessages.Select(m => m.SenderId).Distinct().ToList();
        var senderMap = senderIds.Count == 0
            ? new Dictionary<Guid, (string DisplayName, TrustLevel TrustLevel)>()
            : (await db.OrganizationPeople.AsNoTracking()
                .Where(u => senderIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName, u.TrustLevel })
                .ToListAsync(ct))
                .ToDictionary(u => u.Id, u => (u.DisplayName, u.TrustLevel));

        var msgIds = selectedMessages.Select(m => m.Id).ToList();
        var attachmentsByMsg = msgIds.Count == 0
            ? new Dictionary<Guid, List<AttachmentDto>>()
            : (await db.Attachments.AsNoTracking()
                .Where(a => a.MessageId != null && msgIds.Contains(a.MessageId.Value) && a.State == AttachmentState.Attached)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(ct))
                .GroupBy(a => a.MessageId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(FilesEndpoints.ToDto).ToList());

        var activeMsgIds = selectedMessages
            .Where(m => !m.DeletedAt.HasValue)
            .Select(m => m.Id)
            .ToArray();
        var reactionsByMsg = activeMsgIds.Length == 0
            ? new Dictionary<Guid, List<ReactionSummaryDto>>()
            : (await db.MessageReactions.AsNoTracking()
                .Where(r => activeMsgIds.Contains(r.MessageId))
                .GroupBy(r => new { r.MessageId, r.Emoji })
                .Select(g => new
                {
                    g.Key.MessageId,
                    g.Key.Emoji,
                    FirstCreatedAt = g.Min(r => r.CreatedAt),
                    Count = g.Count(),
                    MyCount = g.Count(r => r.UserId == actorId)
                })
                .ToListAsync(ct))
                .GroupBy(r => r.MessageId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(r => r.FirstCreatedAt)
                        .ThenBy(r => r.Emoji, StringComparer.Ordinal)
                        .Select(r => new ReactionSummaryDto(r.Emoji, r.Count, r.MyCount > 0))
                        .ToList());

        var rootIds = selectedMessages.Where(m => !m.ParentMessageId.HasValue).Select(m => m.Id).ToArray();
        var threadState = rootIds.Length == 0
            ? new Dictionary<Guid, ThreadSubscriptionEntity>()
            : await db.ThreadSubscriptions.AsNoTracking()
                .Where(s => s.UserId == actorId && rootIds.Contains(s.ParentMessageId))
                .ToDictionaryAsync(s => s.ParentMessageId, ct);

        return selectedMessages.Select(m =>
        {
            var hasSender = senderMap.TryGetValue(m.SenderId, out var sender);
            var state = threadState.GetValueOrDefault(m.Id);
            return new MessageDto(
                m.Id,
                m.ConversationId,
                m.Seq,
                m.SenderId,
                hasSender ? sender.DisplayName : "Unknown",
                !hasSender || sender.TrustLevel == TrustLevel.Unverified,
                m.ClientMsgId,
                m.DeletedAt.HasValue ? string.Empty : m.Body,
                m.CreatedAt,
                m.EditedAt,
                m.DeletedAt,
                m.DeletedAt.HasValue
                    ? []
                    : attachmentsByMsg.GetValueOrDefault(m.Id) ?? [],
                m.DeletedAt.HasValue
                    ? []
                    : reactionsByMsg.GetValueOrDefault(m.Id) ?? [],
                m.ParentMessageId,
                m.ReplyCount,
                m.LastReplyAt,
                m.LastReplySeq,
                state?.IsFollowing ?? false,
                state?.LastReadReplySeq ?? 0,
                state?.Version ?? 0,
                state?.IsFollowing == true && m.LastReplySeq > state.LastReadReplySeq);
        }).ToList();
    }

    private static async Task<IResult> HandleGetChangesAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        long after = 0,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var conv = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId, ct);
        var member = await db.ConversationMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var kind = conv?.Kind.ToResourceKind() ?? ResourceKind.GroupChat;
        var targetIsSelf = conv?.Kind == ConversationKind.Self && conv.CreatedByUserId == actorId;
        if (conv is null || member is null || !authz.Can(actor.ToActorContext(), ResourceAction.DeltaSync, new ResourceContext(kind, actor.OrganizationId, MembershipRole: member.Role, TargetIsSelf: targetIsSelf)))
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
                (payload.TryGetProperty("visibilitySeq", out var seqEl) || payload.TryGetProperty("seq", out seqEl)) &&
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
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var items = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == actorId && m.LeftAt == null)
            .Join(
                db.Conversations.AsNoTracking().Where(c =>
                    c.OrganizationId == db.ConversationOrganizationId &&
                    (c.Kind != ConversationKind.Self || c.CreatedByUserId == actorId)),
                m => m.ConversationId,
                c => c.Id,
                (m, c) => new
                {
                    conversationId = c.Id,
                    kind = c.Kind.ToString().ToLowerInvariant(),
                    changeSeq = c.ChangeSeq,
                    nextSeq = c.NextSeq,
                    latestMainSeq = c.Kind == ConversationKind.Channel ? c.NextSeq : c.LatestMainSeq,
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
        IAuthorizationService authz,
        MessagingFanoutService fanout,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var conv = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == conversationId && c.OrganizationId == db.ConversationOrganizationId, ct);
        var member = await db.ConversationMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null, ct);

        var kind = conv?.Kind.ToResourceKind() ?? ResourceKind.GroupChat;
        var targetIsSelf = conv?.Kind == ConversationKind.Self && conv.CreatedByUserId == actorId;
        if (conv is null || member is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, new ResourceContext(kind, actor.OrganizationId, MembershipRole: member.Role, TargetIsSelf: targetIsSelf)))
        {
            return Results.Forbid();
        }

        var requested = Math.Max(0L, request.LastReadSeq);

        // Atomic monotonic GREATEST(last_read_seq, LEAST(@seq, @maxSeq)) (M2.7).
        var latestMainSeq = conv.Kind == ConversationKind.Channel ? conv.NextSeq : conv.LatestMainSeq;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE conversation_members
            SET last_read_seq = GREATEST(last_read_seq, LEAST({requested}, {latestMainSeq}))
            WHERE conversation_id = {conversationId} AND user_id = {actorId} AND left_at IS NULL
            """, ct);

        var updatedSeq = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.UserId == actorId && m.LeftAt == null)
            .Select(m => (long?)m.LastReadSeq)
            .SingleOrDefaultAsync(ct);

        if (updatedSeq is null) return Results.Forbid();
        var readBoundary = Math.Max(updatedSeq.Value, member.VisibleFromSeq);
        var unreadCount = conv.Kind == ConversationKind.Channel
            ? Math.Max(0L, conv.NextSeq - updatedSeq.Value)
            : await db.Messages.LongCountAsync(m => m.ConversationId == conversationId &&
                m.ParentMessageId == null && m.Seq > readBoundary, ct);

        await fanout.NotifyReadStateUpdatedAsync(actorId, conversationId, updatedSeq.Value, unreadCount, ct);

        return Results.Ok(new
        {
            conversationId,
            lastReadSeq = updatedSeq,
            unreadCount
        });
    }

    private static async Task<(long NextSeq, long ChangeSeq)> AllocateSequencesAsync(
        NexusDbContext db,
        Guid conversationId,
        DateTimeOffset now,
        bool isMainMessage,
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
                latest_main_seq = CASE WHEN @is_main_message THEN next_seq + 1 ELSE latest_main_seq END,
                updated_at = CASE WHEN @is_main_message THEN @now ELSE updated_at END
            WHERE id = @id
            RETURNING next_seq, change_seq
            """,
            conn,
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());

        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("id", conversationId);
        cmd.Parameters.AddWithValue("is_main_message", isMainMessage);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Conversation not found during sequence allocation.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static string ComputePayloadHash(Guid conversationId, string body, Guid[] attachmentIds, Guid? parentMessageId)
    {
        var attSuffix = attachmentIds.Length == 0
            ? string.Empty
            : "\n" + string.Join(',', attachmentIds.OrderBy(x => x).Select(x => x.ToString("N")));
        var payload = parentMessageId.HasValue
            ? JsonSerializer.Serialize(new { conversationId, body, attachmentIds = attachmentIds.Order(), parentMessageId }, WebJsonOptions)
            : $"{conversationId:N}\n{body}{attSuffix}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static MessageDto ToMessageDto(
        MessageEntity m,
        OrganizationMembership sender,
        IReadOnlyList<AttachmentDto>? attachments = null,
        IReadOnlyList<ReactionSummaryDto>? reactions = null) => new(
        m.Id,
        m.ConversationId,
        m.Seq,
        m.SenderId,
        sender.DisplayName,
        sender.TrustLevel == TrustLevel.Unverified,
        m.ClientMsgId,
        m.DeletedAt.HasValue ? string.Empty : m.Body,
        m.CreatedAt,
        m.EditedAt,
        m.DeletedAt,
        m.DeletedAt.HasValue ? [] : attachments ?? [],
        m.DeletedAt.HasValue ? [] : reactions ?? [],
        m.ParentMessageId,
        m.ReplyCount,
        m.LastReplyAt,
        m.LastReplySeq);
}
