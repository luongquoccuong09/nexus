using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Org;
using Npgsql;

namespace Nexus.Web.Modules.Messaging;

public static class MessageForwardEndpoints
{
    public sealed record ForwardRequest(string? ClientMsgId, string? SourceLink);
    private sealed record SourceContent(string? Title, string Body);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMessageForwardEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        app.MapPost(prefix + "/conversations/{conversationId:guid}/forward", ForwardAsync)
            .AddEndpointFilter(OrganizationManagementEndpoints.ValidateCsrfAsync);
        return app;
    }

    private static async Task<IResult> ForwardAsync(HttpContext ctx, Guid conversationId, ForwardRequest request,
        [FromServices] NexusDbContext db, IAuthorizationService authz, IOptions<FilePolicyOptions> filePolicy, MessagingFanoutService fanout,
        CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var clientMsgId = request.ClientMsgId?.Trim() ?? string.Empty;
        if (clientMsgId.Length is < 1 or > 128)
            return Results.BadRequest(new { code = "invalid_client_msg_id", message = "clientMsgId must be 1 to 128 characters." });
        if (!ContentLinkCodec.TryDecode(request.SourceLink, out var source) || source?.T is not { } sourceId || source.K == "c")
            return SourceUnavailable();

        var targetScope = db.ConversationOrganizationId;
        var targetActor = await FindActorAsync(db, actorId, targetScope, ct);
        var sourceActor = source.O == targetScope ? targetActor : await FindActorAsync(db, actorId, source.O, ct);
        if (targetActor is null) return Results.Forbid();
        if (sourceActor is null) return SourceUnavailable();
        var sourceAccess = await FindAccessAsync(db, sourceActor, source.O, source.C, ct);
        if (!CanReadSource(authz, sourceActor, sourceAccess, source.K)) return SourceUnavailable();
        var destination = await FindAccessAsync(db, targetActor, targetScope, conversationId, ct);
        if (!CanSend(authz, targetActor, destination)) return Results.Forbid();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AttachmentQuota.LockAsync(db, actorId, conversationId, ct);
        var locked = await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM conversations
            WHERE (id = {source.C} AND organization_id IS NOT DISTINCT FROM {source.O})
               OR (id = {conversationId} AND organization_id IS NOT DISTINCT FROM {targetScope})
            ORDER BY id FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        var sourceConversation = locked.Find(item => item.Id == source.C && item.OrganizationId == source.O);
        var targetConversation = locked.Find(item => item.Id == conversationId && item.OrganizationId == targetScope);
        targetActor = await FindActorAsync(db, actorId, targetScope, ct);
        sourceActor = source.O == targetScope ? targetActor : await FindActorAsync(db, actorId, source.O, ct);
        if (targetActor is null || targetConversation is null) return Results.Forbid();
        if (sourceActor is null || sourceConversation is null) return SourceUnavailable();
        sourceAccess = await CommunityAccessHelper.ResolveAsync(db, sourceActor, sourceConversation, ct);
        if (!CanReadSource(authz, sourceActor, sourceAccess, source.K)) return SourceUnavailable();
        destination = await CommunityAccessHelper.ResolveAsync(db, targetActor, targetConversation, ct);
        if (!CanSend(authz, targetActor, destination)) return Results.Forbid();
        if (targetConversation.ArchivedAt is not null)
            return Results.Conflict(new { code = "channel_archived", message = "This channel is archived and read-only." });

        var isSourceMessage = source.K == "m";
        var content = isSourceMessage
            ? await MessageHistory.VisibleAfter(db, sourceAccess!.VisibleFromSeq)
                .Where(item => item.Id == sourceId && item.ConversationId == source.C && item.DeletedAt == null)
                .Select(item => new SourceContent(null, item.Body)).SingleOrDefaultAsync(ct)
            : await db.ChannelPosts.AsNoTracking().Where(item => item.Id == sourceId && item.ConversationId == source.C &&
                    item.OrganizationId == source.O && item.DeletedAt == null)
                .Select(item => new SourceContent(item.Title, item.Body)).SingleOrDefaultAsync(ct);
        if (content is null) return SourceUnavailable();

        var files = await db.Attachments.AsNoTracking().Where(file => file.ConversationId == source.C && file.OrganizationId == source.O &&
                (isSourceMessage ? file.MessageId == sourceId : file.PostId == sourceId))
            .OrderBy(file => file.CreatedAt).ThenBy(file => file.Id).ToListAsync(ct);
        if (files.Count != 0 && (!authz.Can(sourceActor.ToActorContext(), ResourceAction.FileDownload, sourceAccess!.Resource) ||
                files.Any(file => file.State != AttachmentState.Attached || file.ScanStatus is not ("clean" or "skipped_prototype"))))
            return SourceUnavailable();

        var isChannel = targetConversation.Kind == ConversationKind.Channel;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"forward\n{conversationId:N}\n{ContentLinkCodec.Encode(source.K, source.O, source.C, sourceId)}")));
        var oldMessage = await db.Messages.AsNoTracking().SingleOrDefaultAsync(item => item.SenderId == actorId && item.ClientMsgId == clientMsgId, ct);
        if (oldMessage is not null)
        {
            return !isChannel && oldMessage.IsForwarded && oldMessage.ConversationId == conversationId &&
                oldMessage.Seq > destination!.VisibleFromSeq && oldMessage.DeletedAt is null && oldMessage.PayloadSha256 == hash
                ? Results.Ok(Result("m", oldMessage.Id, conversationId, targetScope)) : Conflict();
        }
        var oldPost = await db.ChannelPosts.AsNoTracking().SingleOrDefaultAsync(item => item.AuthorUserId == actorId &&
            item.ForwardClientMsgId == clientMsgId, ct);
        if (oldPost is not null)
        {
            return isChannel && oldPost.ConversationId == conversationId && oldPost.DeletedAt is null && oldPost.ForwardPayloadSha256 == hash
                ? Results.Ok(Result("p", oldPost.Id, conversationId, targetScope)) : Conflict();
        }

        var policy = filePolicy.Value;
        if (files.Count > (isChannel ? AttachmentProjection.MaxAttachmentsPerItem : policy.MaxAttachmentsPerMessage))
            return Results.BadRequest(new { code = "too_many_attachments", message = "This destination allows fewer attachments." });
        if (files.Count > 0 && !authz.Can(targetActor.ToActorContext(), ResourceAction.FileUpload, destination!.Resource))
            return Results.Forbid();
        var quotaError = await AttachmentQuota.CheckAsync(db, actorId, conversationId, files.Sum(file => file.SizeBytes), policy, ct);
        if (quotaError is not null) return quotaError;

        var body = !isChannel && !string.IsNullOrEmpty(content.Title)
            ? (content.Title + "\n\n" + content.Body).TrimEnd() : content.Body;
        if (!isChannel && body.Length > MessagingEndpoints.MaxMessageBodyLength)
            return Results.BadRequest(new { code = "invalid_message_body", message = "This post is too long to forward to a chat." });
        if (body.Length == 0 && files.Count == 0)
            return Results.BadRequest(new { code = "empty_forward", message = "There is no text or attachment to forward." });

        var now = DateTimeOffset.UtcNow;
        var newId = Guid.CreateVersion7();
        var copies = files.Select(file => CopyAttachment(file, actorId, targetScope, conversationId, newId, isChannel, now)).ToList();
        db.Attachments.AddRange(copies);
        var attachments = copies.Select(AttachmentProjection.ToAttachmentDto).ToList();
        var changeSeq = 0L;
        object payload;
        if (isChannel)
        {
            var post = new ChannelPostEntity
            {
                Id = newId, OrganizationId = targetActor.OrganizationId, ConversationId = conversationId, AuthorUserId = actorId,
                Title = content.Title, Body = body, ForwardClientMsgId = clientMsgId, ForwardPayloadSha256 = hash, CreatedAt = now, UpdatedAt = now
            };
            db.ChannelPosts.Add(post);
            foreach (var link in SharedLinkExtractor.Extract(body))
            {
                db.SharedLinks.Add(new SharedLinkEntity
                {
                    Id = Guid.CreateVersion7(), OrganizationId = targetScope, ConversationId = conversationId, PostId = newId,
                    SenderUserId = actorId, Url = link.Url, Label = link.Label, Host = link.Host, CreatedAt = now
                });
            }
            payload = new
            {
                id = newId, conversationId, authorUserId = actorId, authorDisplayName = targetActor.DisplayName,
                authorIsUnverified = targetActor.TrustLevel == TrustLevel.Unverified,
                authorAvatarUrl = string.IsNullOrEmpty(targetActor.AvatarObjectKey) ? null : $"/api/organizations/{targetActor.OrganizationId}/people/{actorId}/avatar",
                title = post.Title, body, createdAt = now,
                isForwarded = true, attachments
            };
        }
        else
        {
            var allocated = await MessagingEndpoints.AllocateSequencesAsync(db, conversationId, now, true, ct);
            changeSeq = allocated.ChangeSeq;
            var message = new MessageEntity
            {
                Id = newId, ConversationId = conversationId, Seq = allocated.NextSeq, SenderId = actorId,
                ClientMsgId = clientMsgId, PayloadSha256 = hash, Body = body, IsForwarded = true, CreatedAt = now
            };
            payload = MessagingEndpoints.ToMessageDto(message, targetActor, attachments);
            db.Messages.Add(message);
            MessagingEndpoints.AddSharedLinks(db, message, body);
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                Id = Guid.CreateVersion7(), ConversationId = conversationId, ChangeSeq = changeSeq, EventType = "message.created",
                ActorUserId = actorId, PayloadJson = JsonSerializer.Serialize(payload, WebJson), CreatedAt = now
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_messages_sender_client_msg_id" or "ux_channel_posts_forward_author_client_id" })
        {
            return Conflict();
        }

        if (isChannel)
        {
            NexusMeters.PostCreated();
            await fanout.NotifyCommunityFeedChangedAsync(destination!.Community!.Id, conversationId,
                new { type = "post_created", communityId = destination.Community.Id, conversationId, post = payload }, ct);
        }
        else
        {
            NexusMeters.MessageCreated();
            await fanout.NotifyMessageCreatedAsync(conversationId, changeSeq, targetConversation.Kind, null,
                (MessagingEndpoints.MessageDto)payload, ct);
        }
        var result = Result(isChannel ? "p" : "m", newId, conversationId, targetScope);
        return Results.Created("/?link=" + result.DestinationLink, result);
    }

    private static Task<OrganizationMembership?> FindActorAsync(NexusDbContext db, Guid actorId, Guid? scope, CancellationToken ct) =>
        scope is null ? db.FindPersonalActorAsync(actorId, ct) : db.OrganizationMemberships.AsNoTracking()
            .SingleOrDefaultAsync(member => member.OrganizationId == scope && member.Id == actorId && member.Status == AccountStatus.Active &&
                member.Account.Status == AccountStatus.Active, ct);

    private static async Task<ResolvedConversationAccess?> FindAccessAsync(NexusDbContext db, OrganizationMembership actor,
        Guid? scope, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == conversationId && item.OrganizationId == scope, ct);
        return conversation is null ? null : await CommunityAccessHelper.ResolveAsync(db, actor, conversation, ct);
    }

    private static bool CanReadSource(IAuthorizationService authz, OrganizationMembership actor, ResolvedConversationAccess? access,
        string kind) => access is not null && (kind == "p" ? access.Community is not null : access.Conversation.Kind != ConversationKind.Channel) &&
        authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource);

    private static bool CanSend(IAuthorizationService authz, OrganizationMembership actor, ResolvedConversationAccess? access) =>
        access is not null && authz.Can(actor.ToActorContext(), ResourceAction.MessageSend, access.Resource with { IsArchived = false });

    private static AttachmentEntity CopyAttachment(AttachmentEntity source, Guid actorId, Guid? scope, Guid conversationId,
        Guid newId, bool isChannel, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new AttachmentEntity
        {
            Id = id, OrganizationId = scope, ConversationId = conversationId, MessageId = isChannel ? null : newId,
            PostId = isChannel ? newId : null, UploaderUserId = actorId, ClientUploadId = $"forward-{id:N}", FileName = source.FileName,
            ContentType = source.ContentType, SizeBytes = source.SizeBytes, Sha256Hex = source.Sha256Hex, ObjectKey = source.ObjectKey,
            ThumbnailObjectKey = source.ThumbnailObjectKey, ImageWidth = source.ImageWidth, ImageHeight = source.ImageHeight,
            DurationMs = source.DurationMs, ScanStatus = source.ScanStatus, State = AttachmentState.Attached, CreatedAt = now, AttachedAt = now
        };
    }

    private sealed record ForwardResult(Guid Id, string Kind, Guid ConversationId, string DestinationLink);

    private static ForwardResult Result(string kind, Guid id, Guid conversationId, Guid? scope) =>
        new(id, kind == "p" ? "post" : "message", conversationId, ContentLinkCodec.Encode(kind, scope, conversationId, id));

    private static IResult SourceUnavailable() => Results.NotFound(new { code = "forward_source_unavailable", message = "The source is unavailable." });
    private static IResult Conflict() => Results.Conflict(new { code = "client_msg_id_conflict", message = "This send ID has already been used." });
}
