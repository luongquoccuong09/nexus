using Nexus.Web.Modules.Org;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;

namespace Nexus.Web.Modules.Files;

public sealed record CreateSignedUrlRequest(int? TtlSeconds);

public static class FilesModuleExtensions
{
    public static IServiceCollection AddFilesModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(FilePolicyOptions.SectionName);
        var options = section.Get<FilePolicyOptions>() ?? new FilePolicyOptions();
        options.Validate();

        services.AddOptions<FilePolicyOptions>()
            .Bind(section)
            .Validate(o =>
            {
                o.Validate();
                return true;
            })
            .ValidateOnStart();

        if (!string.IsNullOrWhiteSpace(options.ClamAvHost))
        {
            services.Replace(ServiceDescriptor.Singleton<IFileScanner, ClamAvFileScanner>());
        }

        services.AddSingleton<OrphanAttachmentCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<OrphanAttachmentCleanupService>());

        return services;
    }
}

public static class FilesEndpoints
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder app, string prefix = OrganizationScope.Api)
    {
        app.MapPost(prefix + "/conversations/{conversationId:guid}/attachments", HandleUploadAttachmentAsync);
        app.MapGet(prefix + "/conversations/{conversationId:guid}/attachments/pending", HandleCheckPendingAttachmentsAsync);
        app.MapGet(prefix + "/conversations/{conversationId:guid}/files", HandleListConversationFilesAsync);
        if (string.Equals(prefix, OrganizationScope.Api, StringComparison.Ordinal))
        {
            app.MapGet(prefix + "/channels/{conversationId:guid}/files", HandleListConversationFilesAsync);
        }

        var files = app.MapGroup(prefix + "/files/{attachmentId:guid}");
        files.MapGet("/download", HandleDownloadAttachmentAsync);
        files.MapGet("/thumbnail", HandleGetThumbnailAsync);
        files.MapGet("/preview", HandleGetImagePreviewAsync);
        files.MapGet("/audio", HandleGetAudioPlaybackAsync);
        files.MapPost("/signed-url", HandleCreateSignedUrlAsync);

        return app;
    }

    public static AttachmentDto ToDto(AttachmentEntity a) => AttachmentProjection.ToAttachmentDto(a);

    private static async Task<IResult> HandleUploadAttachmentAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IFileStorage storage,
        IFileScanner scanner,
        IOptions<FilePolicyOptions> policyOptions,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        var allowed = access is not null && authz.Can(
            actor.ToActorContext(),
            ResourceAction.FileUpload,
            access.Resource with { IsArchived = false });

        if (!allowed)
        {
            return Results.Forbid();
        }

        if (access!.Conversation.ArchivedAt is not null)
        {
            return Results.Conflict(new { code = "channel_archived", message = "This channel is archived and read-only." });
        }

        var policy = policyOptions.Value;
        string clientUploadId;
        string originalFileName;
        string rawDurationMs;
        byte[] rawBytes;

        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            clientUploadId = (form["clientUploadId"].ToString() ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(clientUploadId))
            {
                clientUploadId = ctx.Request.Headers["X-Client-Upload-Id"].ToString().Trim();
            }

            rawDurationMs = (form["durationMs"].ToString() ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(rawDurationMs))
            {
                rawDurationMs = ctx.Request.Headers["X-Audio-Duration-Ms"].ToString().Trim();
            }

            var formFile = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (formFile is null)
            {
                return Results.BadRequest(new { code = "file_required", message = "A file field is required." });
            }

            if (formFile.Length > policy.MaxFileSizeBytes)
            {
                return Results.Json(
                    new { code = "file_too_large", message = $"File exceeds maximum size of {policy.MaxFileSizeBytes / (1024 * 1024)} MB." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            originalFileName = string.IsNullOrWhiteSpace(formFile.FileName) ? "upload.bin" : formFile.FileName;
            await using var stream = formFile.OpenReadStream();
            rawBytes = await ReadBoundedBytesAsync(stream, policy.MaxFileSizeBytes, ct);
        }
        else
        {
            clientUploadId = ctx.Request.Headers["X-Client-Upload-Id"].ToString().Trim();
            originalFileName = ctx.Request.Headers["X-File-Name"].ToString().Trim();
            rawDurationMs = ctx.Request.Headers["X-Audio-Duration-Ms"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(originalFileName))
            {
                originalFileName = "upload.bin";
            }

            rawBytes = await ReadBoundedBytesAsync(ctx.Request.Body, policy.MaxFileSizeBytes, ct);
        }

        if (clientUploadId.Length is < 1 or > 128)
        {
            return Results.BadRequest(new { code = "invalid_client_upload_id", message = "clientUploadId must be 1 to 128 characters." });
        }

        if (rawBytes.Length > policy.MaxFileSizeBytes)
        {
            return Results.Json(
                new { code = "file_too_large", message = $"File exceeds maximum size of {policy.MaxFileSizeBytes / (1024 * 1024)} MB." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        // Idempotent stage-1 retry check on (uploader_user_id, client_upload_id) (M3.1)
        var existing = await db.Attachments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UploaderUserId == actorId && a.ClientUploadId == clientUploadId, ct);

        if (existing is not null)
        {
            if (existing.ConversationId != conversationId)
            {
                return Results.Conflict(new { code = "client_upload_id_conflict", message = "clientUploadId was already used in a different conversation." });
            }

            var existingQuota = await BuildQuotaSummaryAsync(db, actorId, conversationId, policy, ct);
            return Results.Ok(new { attachment = ToDto(existing), quota = existingQuota });
        }

        // Magic-byte validation + allowlist + image re-encoding & thumbnail generation (M3.2, M3.4, M6.34)
        if (!FileContentInspector.TryInspectAndProcess(rawBytes, originalFileName, policy.MaxFileSizeBytes, out var inspected, out var inspectError) ||
            inspected is null)
        {
            return Results.BadRequest(new { code = "invalid_file_type", message = inspectError });
        }

        int? validatedDurationMs = null;
        if (FileContentInspector.IsAllowedAudioContentType(inspected.CanonicalContentType))
        {
            if (inspected.ProcessedBytes.LongLength > policy.MaxVoiceNoteBytes)
            {
                return Results.Json(
                    new
                    {
                        code = "voice_note_too_large",
                        message = $"Voice note exceeds maximum size of {policy.MaxVoiceNoteBytes / (1024 * 1024)} MB."
                    },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (!string.IsNullOrEmpty(rawDurationMs))
            {
                var maxDurationMs = policy.MaxVoiceNoteDurationSeconds * 1000;
                if (!int.TryParse(rawDurationMs, out var parsedDurationMs) ||
                    parsedDurationMs < 1 ||
                    parsedDurationMs > maxDurationMs)
                {
                    return Results.BadRequest(new
                    {
                        code = "invalid_voice_note_duration",
                        message = $"Voice note duration must be between 1 and {maxDurationMs} ms."
                    });
                }

                validatedDurationMs = parsedDurationMs;
            }
        }

        // Enforce per-user and per-conversation storage quotas (M3.5)
        var currentUserBytes = await db.Attachments.AsNoTracking()
            .Where(a => a.UploaderUserId == actorId && (a.State == AttachmentState.Pending || a.State == AttachmentState.Attached))
            .SumAsync(a => (long?)a.SizeBytes, ct) ?? 0L;

        var currentConvBytes = await db.Attachments.AsNoTracking()
            .Where(a => a.ConversationId == conversationId && (a.State == AttachmentState.Pending || a.State == AttachmentState.Attached))
            .SumAsync(a => (long?)a.SizeBytes, ct) ?? 0L;

        var incomingBytes = inspected.ProcessedBytes.LongLength + (inspected.ThumbnailPngBytes?.LongLength ?? 0L);

        if (currentUserBytes + incomingBytes > policy.PerUserQuotaBytes)
        {
            return Results.Json(
                new
                {
                    code = "user_quota_exceeded",
                    message = "Your personal storage quota has been reached.",
                    usedBytes = currentUserBytes,
                    quotaBytes = policy.PerUserQuotaBytes
                },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (currentConvBytes + incomingBytes > policy.PerConversationQuotaBytes)
        {
            return Results.Json(
                new
                {
                    code = "conversation_quota_exceeded",
                    message = "This conversation's storage quota has been reached.",
                    usedBytes = currentConvBytes,
                    quotaBytes = policy.PerConversationQuotaBytes
                },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        // Quarantine & malware scan before releasing to private storage (M3.6)
        FileScanResult scanResult;
        await using (var scanStream = new MemoryStream(inspected.ProcessedBytes, writable: false))
        {
            scanResult = await scanner.ScanAsync(scanStream, inspected.SafeFileName, ct);
        }

        var scanRejected = scanResult.Verdict is FileScanVerdict.Infected or FileScanVerdict.FailedClosed ||
                           (policy.FailClosedScanning && scanResult.Verdict != FileScanVerdict.Clean);

        if (scanRejected)
        {
            db.AuditLogs.Add(new AuditLogEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = actor.OrganizationId,
                ActorUserId = actorId,
                Action = "FILE_SCAN_REJECTED",
                ResourceType = "Attachment",
                ResourceId = conversationId.ToString(),
                Result = "Denied",
                DetailJson = JsonSerializer.Serialize(new
                {
                    verdict = scanResult.StatusCode,
                    engine = scanResult.Engine,
                    sizeBytes = inspected.ProcessedBytes.LongLength
                }, AuditJsonOptions),
                IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);

            var statusCode = scanResult.Verdict == FileScanVerdict.Infected
                ? StatusCodes.Status422UnprocessableEntity
                : StatusCodes.Status503ServiceUnavailable;

            return Results.Json(
                new
                {
                    code = "file_rejected_by_scanner",
                    scanStatus = scanResult.StatusCode,
                    message = scanResult.Verdict == FileScanVerdict.Infected
                        ? "File was rejected by malware scanning."
                        : "File upload blocked because malware scanner is unavailable (fail-closed)."
                },
                statusCode: statusCode);
        }

        // Persist clean payload (and optional thumbnail) with random object keys in private storage
        StoredObjectDescriptor savedMain;
        await using (var mainStream = new MemoryStream(inspected.ProcessedBytes, writable: false))
        {
            savedMain = await storage.SaveAsync(mainStream, inspected.CanonicalContentType, ct);
        }

        StoredObjectDescriptor? savedThumb = null;
        if (inspected.ThumbnailPngBytes is not null)
        {
            await using var thumbStream = new MemoryStream(inspected.ThumbnailPngBytes, writable: false);
            savedThumb = await storage.SaveAsync(thumbStream, "image/png", ct);
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new AttachmentEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = db.ConversationOrganizationId,
            ConversationId = conversationId,
            UploaderUserId = actorId,
            ClientUploadId = clientUploadId,
            FileName = inspected.SafeFileName,
            ContentType = inspected.CanonicalContentType,
            SizeBytes = savedMain.SizeBytes,
            Sha256Hex = savedMain.Sha256Hex,
            ObjectKey = savedMain.ObjectKey,
            ThumbnailObjectKey = savedThumb?.ObjectKey,
            ImageWidth = inspected.ImageWidth,
            ImageHeight = inspected.ImageHeight,
            DurationMs = validatedDurationMs,
            State = AttachmentState.Pending,
            ScanStatus = scanResult.StatusCode,
            ScanDetail = scanResult.Detail is null ? null : scanResult.Detail[..Math.Min(scanResult.Detail.Length, 256)],
            CreatedAt = now
        };

        db.Attachments.Add(entity);
        await db.SaveChangesAsync(ct);

        NexusMeters.UploadCreated();
        var quota = await BuildQuotaSummaryAsync(db, actorId, conversationId, policy, ct);
        return Results.Created($"{OrganizationScope.ApiPath(entity.OrganizationId)}/files/{entity.Id}/download", new
        {
            attachment = ToDto(entity),
            quota
        });
    }

    private static async Task<IResult> HandleCheckPendingAttachmentsAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromQuery(Name = "id")] Guid[] ids,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId)) return Results.Unauthorized();
        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null) return Results.Unauthorized();

        if (ids.Length > 50) return Results.BadRequest(new { code = "too_many_attachment_ids" });

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
            return Results.Forbid();

        var pending = await db.Attachments.AsNoTracking()
            .Where(a => a.ConversationId == conversationId && a.UploaderUserId == actorId &&
                        a.State == AttachmentState.Pending && ids.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(ct);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { ids = pending });
    }

    private static async Task<IResult> HandleListConversationFilesAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IOptions<FilePolicyOptions> policyOptions,
        DateTimeOffset? before,
        Guid? beforeId,
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

        if (beforeId.HasValue && !before.HasValue)
        {
            return Results.BadRequest(new { code = "invalid_cursor", message = "before is required when beforeId is supplied." });
        }

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        if (access.Conversation.Kind == ConversationKind.Channel)
        {
            var channelQuery =
                from a in db.Attachments.AsNoTracking()
                join p in db.ChannelPosts.AsNoTracking() on a.PostId equals p.Id
                join u in db.OrganizationPeople.AsNoTracking() on a.UploaderUserId equals u.Id into uploaderJoin
                from uploader in uploaderJoin.DefaultIfEmpty()
                where a.ConversationId == conversationId
                      && a.State == AttachmentState.Attached
                      && a.PostId != null
                      && p.ConversationId == conversationId
                      && p.OrganizationId == actor.OrganizationId
                      && p.DeletedAt == null
                select new
                {
                    Attachment = a,
                    PostId = p.Id,
                    PostTitle = p.Title,
                    UploaderDisplayName = uploader != null ? uploader.DisplayName : "Member"
                };

            if (before is { } chTimestamp)
            {
                var utc = chTimestamp.ToUniversalTime();
                channelQuery = beforeId is { } chBeforeId
                    ? channelQuery.Where(x => x.Attachment.CreatedAt < utc ||
                        (x.Attachment.CreatedAt == utc && x.Attachment.Id.CompareTo(chBeforeId) < 0))
                    : channelQuery.Where(x => x.Attachment.CreatedAt < utc);
            }

            var chRows = await channelQuery
                .OrderByDescending(x => x.Attachment.CreatedAt)
                .ThenByDescending(x => x.Attachment.Id)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            var chHasMore = chRows.Count > pageSize;
            if (chHasMore)
            {
                chRows = chRows.Take(pageSize).ToList();
            }

            var chItems = chRows.Select(x =>
            {
                var dto = ToDto(x.Attachment);
                return new
                {
                    id = dto.Id,
                    conversationId = dto.ConversationId,
                    messageId = dto.MessageId,
                    postId = x.PostId,
                    postTitle = x.PostTitle,
                    uploaderUserId = dto.UploaderUserId,
                    uploaderDisplayName = x.UploaderDisplayName,
                    clientUploadId = dto.ClientUploadId,
                    fileName = dto.FileName,
                    contentType = dto.ContentType,
                    sizeBytes = dto.SizeBytes,
                    sha256Hex = dto.Sha256Hex,
                    imageWidth = dto.ImageWidth,
                    imageHeight = dto.ImageHeight,
                    width = dto.ImageWidth,
                    height = dto.ImageHeight,
                    durationMs = dto.DurationMs,
                    hasThumbnail = dto.ThumbnailUrl is not null,
                    state = dto.State,
                    scanStatus = dto.ScanStatus,
                    downloadUrl = dto.DownloadUrl,
                    thumbnailUrl = dto.ThumbnailUrl,
                    previewUrl = dto.PreviewUrl,
                    audioUrl = dto.AudioUrl,
                    sourceLink = ContentLinkCodec.Encode("p", actor.OrganizationId, conversationId, x.PostId),
                    createdAt = dto.CreatedAt
                };
            }).ToList();

            var chQuota = await BuildQuotaSummaryAsync(db, actorId, conversationId, policyOptions.Value, ct);
            return Results.Ok(new
            {
                items = chItems,
                hasMore = chHasMore,
                nextBefore = chItems.Count > 0 ? chItems[^1].createdAt : (DateTimeOffset?)null,
                nextBeforeId = chItems.Count > 0 ? chItems[^1].id : (Guid?)null,
                isArchived = access.Conversation.ArchivedAt is not null,
                quota = chQuota
            });
        }

        var query = db.Attachments
            .AsNoTracking()
            .Where(a => a.ConversationId == conversationId && a.State == AttachmentState.Attached && a.MessageId != null)
            .Join(
                MessageHistory.VisibleAfter(db, access.VisibleFromSeq).Where(m => m.ConversationId == conversationId && m.DeletedAt == null),
                a => a.MessageId,
                m => (Guid?)m.Id,
                (a, m) => new { Attachment = a, MessageSeq = m.Seq });

        if (before is { } timestamp)
        {
            var utc = timestamp.ToUniversalTime();
            query = beforeId is { } id
                ? query.Where(x => x.Attachment.CreatedAt < utc ||
                    (x.Attachment.CreatedAt == utc && x.Attachment.Id.CompareTo(id) < 0))
                : query.Where(x => x.Attachment.CreatedAt < utc);
        }

        var rows = await query
            .OrderByDescending(x => x.Attachment.CreatedAt)
            .ThenByDescending(x => x.Attachment.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows = rows.Take(pageSize).ToList();
        }

        var items = rows.Select(x => ToDto(x.Attachment)).ToList();
        var quota = await BuildQuotaSummaryAsync(db, actorId, conversationId, policyOptions.Value, ct);

        return Results.Ok(new
        {
            items,
            hasMore,
            nextBefore = items.Count > 0 ? items[^1].CreatedAt : (DateTimeOffset?)null,
            nextBeforeId = items.Count > 0 ? items[^1].Id : (Guid?)null,
            quota
        });
    }

    private static async Task<IResult> HandleDownloadAttachmentAsync(
        HttpContext ctx,
        Guid attachmentId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IFileStorage storage,
        CancellationToken ct)
    {
        var (attachment, errorResult) = await AuthorizeAttachmentReadAsync(ctx, attachmentId, db, authz, ct);
        if (errorResult is not null || attachment is null)
        {
            return errorResult ?? Results.Forbid();
        }

        var stream = await storage.OpenReadAsync(attachment.ObjectKey, ct);
        if (stream is null)
        {
            return Results.NotFound(new { code = "file_not_found" });
        }

        // SEC-11: Always enforce attachment disposition + X-Content-Type-Options: nosniff
        StorageKeyHelper.ApplySafeDownloadHeaders(ctx.Response, attachment.FileName, attachment.ContentType);
        ctx.Response.Headers.CacheControl = "private, no-store";

        await using (stream)
        {
            await stream.CopyToAsync(ctx.Response.Body, ct);
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleGetThumbnailAsync(
        HttpContext ctx,
        Guid attachmentId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IFileStorage storage,
        CancellationToken ct)
    {
        var (attachment, errorResult) = await AuthorizeAttachmentReadAsync(ctx, attachmentId, db, authz, ct);
        if (errorResult is not null || attachment is null)
        {
            return errorResult ?? Results.Forbid();
        }

        if (string.IsNullOrEmpty(attachment.ThumbnailObjectKey))
        {
            return Results.NotFound(new { code = "thumbnail_not_found" });
        }

        var stream = await storage.OpenReadAsync(attachment.ThumbnailObjectKey, ct);
        if (stream is null)
        {
            return Results.NotFound(new { code = "thumbnail_not_found" });
        }

        ctx.Response.ContentType = "image/png";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.CacheControl = "private, max-age=300";

        await using (stream)
        {
            await stream.CopyToAsync(ctx.Response.Body, ct);
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleGetImagePreviewAsync(
        HttpContext ctx,
        Guid attachmentId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IFileStorage storage,
        CancellationToken ct)
    {
        var (attachment, errorResult) = await AuthorizeAttachmentReadAsync(ctx, attachmentId, db, authz, ct);
        if (errorResult is not null || attachment is null)
        {
            return errorResult ?? Results.Forbid();
        }

        // Inline preview is strictly restricted to server-reencoded PNG/JPEG images (SEC-11)
        if (attachment.ContentType is not ("image/png" or "image/jpeg"))
        {
            return Results.BadRequest(new { code = "preview_images_only", message = "Inline preview is only supported for re-encoded PNG and JPEG images." });
        }

        var stream = await storage.OpenReadAsync(attachment.ObjectKey, ct);
        if (stream is null)
        {
            return Results.NotFound(new { code = "file_not_found" });
        }

        ctx.Response.ContentType = attachment.ContentType;
        ctx.Response.Headers.ContentDisposition = StorageKeyHelper.BuildContentDisposition("inline", attachment.FileName);
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.CacheControl = "private, max-age=300";

        await using (stream)
        {
            await stream.CopyToAsync(ctx.Response.Body, ct);
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleGetAudioPlaybackAsync(
        HttpContext ctx,
        Guid attachmentId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IFileStorage storage,
        IOptions<FilePolicyOptions> policyOptions,
        CancellationToken ct)
    {
        var (attachment, errorResult) = await AuthorizeAttachmentReadAsync(ctx, attachmentId, db, authz, ct);
        if (errorResult is not null || attachment is null)
        {
            return errorResult ?? Results.Forbid();
        }

        // Inline playback is strictly restricted to allowlisted voice-note audio types (M6.34, SEC-11)
        if (!FileContentInspector.IsAllowedAudioContentType(attachment.ContentType))
        {
            return Results.BadRequest(new
            {
                code = "audio_only",
                message = "Inline audio playback is only supported for allowlisted voice-note audio types."
            });
        }

        var stream = await storage.OpenReadAsync(attachment.ObjectKey, ct);
        if (stream is null)
        {
            return Results.NotFound(new { code = "file_not_found" });
        }

        Stream playbackStream = stream;
        if (!stream.CanSeek)
        {
            // Non-seekable storage streams (e.g. SupabaseFileStorage) are buffered up to MaxVoiceNoteBytes
            // so HTTP Range seeking (206 Partial Content) works identically across Chrome & WebView2.
            await using (stream)
            {
                var maxBytes = policyOptions.Value.MaxVoiceNoteBytes;
                var buffered = await ReadBoundedBytesAsync(stream, maxBytes, ct);
                if (buffered.LongLength > maxBytes)
                {
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                }
                playbackStream = new MemoryStream(buffered, writable: false);
            }
        }

        ctx.Response.Headers.ContentDisposition = StorageKeyHelper.BuildContentDisposition("inline", attachment.FileName);
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.CacheControl = "private, no-store";

        return Results.File(
            playbackStream,
            contentType: attachment.ContentType,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> HandleCreateSignedUrlAsync(
        HttpContext ctx,
        Guid attachmentId,
        CreateSignedUrlRequest? request,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IOptions<FilePolicyOptions> policyOptions,
        CancellationToken ct)
    {
        var (attachment, errorResult) = await AuthorizeAttachmentReadAsync(ctx, attachmentId, db, authz, ct);
        if (errorResult is not null || attachment is null)
        {
            return errorResult ?? Results.Forbid();
        }

        var requestedSeconds = request?.TtlSeconds ?? policyOptions.Value.DefaultSignedUrlTtlSeconds;
        if (requestedSeconds < 1 || requestedSeconds > FileStorageOptions.HardMaxSignedUrlTtlSeconds)
        {
            return Results.BadRequest(new
            {
                code = "invalid_signed_url_ttl",
                message = $"Signed URL TTL must be between 1 and {FileStorageOptions.HardMaxSignedUrlTtlSeconds} seconds."
            });
        }

        // The streaming endpoint rechecks membership when the file is opened.
        return Results.Ok(new
        {
            url = AttachmentProjection.BuildDownloadUrl(attachment.OrganizationId, attachment.Id),
            expiresInSeconds = requestedSeconds,
            mode = "stream"
        });
    }

    private static async Task<(AttachmentEntity? Attachment, IResult? ErrorResult)> AuthorizeAttachmentReadAsync(
        HttpContext ctx,
        Guid attachmentId,
        NexusDbContext db,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return (null, Results.Unauthorized());
        }

        var actor = await db.FindOrganizationActorAsync(actorId, ct);
        if (actor is null)
        {
            return (null, Results.Unauthorized());
        }

        var attachment = await db.Attachments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == attachmentId && a.OrganizationId == db.ConversationOrganizationId, ct);

        if (attachment is null ||
            attachment.State is AttachmentState.Quarantined or AttachmentState.Rejected ||
            attachment.ScanStatus is not ("clean" or "skipped_prototype"))
        {
            return (null, Results.Forbid());
        }

        // Pending (unattached) files are visible strictly to their uploader
        if (attachment.State == AttachmentState.Pending && attachment.UploaderUserId != actorId)
        {
            return (null, Results.Forbid());
        }

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, attachment.ConversationId, ct);
        var allowed = access is not null && authz.Can(
            actor.ToActorContext(),
            ResourceAction.FileDownload,
            access.Resource);

        if (!allowed)
        {
            return (null, Results.Forbid());
        }

        // Enforce soft-delete & visible_from_seq on attached message files (SEC-04 / SEC-18)
        if (attachment.MessageId.HasValue)
        {
            var canReadMessage = await MessageHistory.VisibleAfter(db, access!.VisibleFromSeq)
                .AnyAsync(m => m.Id == attachment.MessageId.Value &&
                               m.ConversationId == attachment.ConversationId &&
                               m.DeletedAt == null, ct);

            if (!canReadMessage)
            {
                return (null, Results.Forbid());
            }
        }

        // Prevent downloading attachments of soft-deleted channel posts
        if (attachment.PostId.HasValue)
        {
            var postActive = await db.ChannelPosts.AsNoTracking()
                .AnyAsync(p => p.Id == attachment.PostId.Value && p.ConversationId == attachment.ConversationId && p.DeletedAt == null, ct);
            if (!postActive)
            {
                return (null, Results.Forbid());
            }
        }

        return (attachment, null);
    }

    internal static async Task<object> BuildQuotaSummaryAsync(
        NexusDbContext db,
        Guid userId,
        Guid conversationId,
        FilePolicyOptions policy,
        CancellationToken ct)
    {
        var userUsed = await db.Attachments.AsNoTracking()
            .Where(a => a.UploaderUserId == userId && (a.State == AttachmentState.Pending || a.State == AttachmentState.Attached))
            .SumAsync(a => (long?)a.SizeBytes, ct) ?? 0L;

        var convUsed = await db.Attachments.AsNoTracking()
            .Where(a => a.ConversationId == conversationId && (a.State == AttachmentState.Pending || a.State == AttachmentState.Attached))
            .SumAsync(a => (long?)a.SizeBytes, ct) ?? 0L;

        return new
        {
            userUsedBytes = userUsed,
            userQuotaBytes = policy.PerUserQuotaBytes,
            userWarning = userUsed >= policy.PerUserQuotaBytes * policy.QuotaWarningRatio,
            conversationUsedBytes = convUsed,
            conversationQuotaBytes = policy.PerConversationQuotaBytes,
            conversationWarning = convUsed >= policy.PerConversationQuotaBytes * policy.QuotaWarningRatio
        };
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return new byte[maxBytes + 1];
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
