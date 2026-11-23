using Nexus.Web.Modules.Org;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Files;

public sealed record AttachmentDto(
    Guid Id,
    Guid ConversationId,
    Guid? MessageId,
    Guid UploaderUserId,
    string ClientUploadId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256Hex,
    int? ImageWidth,
    int? ImageHeight,
    string State,
    string ScanStatus,
    string DownloadUrl,
    string? ThumbnailUrl,
    string? PreviewUrl,
    DateTimeOffset CreatedAt);

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
        app.MapGet(prefix + "/conversations/{conversationId:guid}/files", HandleListConversationFilesAsync);

        var files = app.MapGroup(prefix + "/files/{attachmentId:guid}");
        files.MapGet("/download", HandleDownloadAttachmentAsync);
        files.MapGet("/thumbnail", HandleGetThumbnailAsync);
        files.MapGet("/preview", HandleGetImagePreviewAsync);
        files.MapPost("/signed-url", HandleCreateSignedUrlAsync);

        return app;
    }

    public static AttachmentDto ToDto(AttachmentEntity a)
    {
        var hasThumb = !string.IsNullOrEmpty(a.ThumbnailObjectKey);
        return new AttachmentDto(
            a.Id,
            a.ConversationId,
            a.MessageId,
            a.UploaderUserId,
            a.ClientUploadId,
            a.FileName,
            a.ContentType,
            a.SizeBytes,
            a.Sha256Hex,
            a.ImageWidth,
            a.ImageHeight,
            a.State.ToString().ToLowerInvariant(),
            a.ScanStatus,
            $"{OrganizationScope.ApiPath(a.OrganizationId)}/files/{a.Id}/download",
            hasThumb ? $"{OrganizationScope.ApiPath(a.OrganizationId)}/files/{a.Id}/thumbnail" : null,
            hasThumb ? $"{OrganizationScope.ApiPath(a.OrganizationId)}/files/{a.Id}/preview" : null,
            a.CreatedAt);
    }

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
            access.Resource);

        if (!allowed)
        {
            return Results.Forbid();
        }

        var policy = policyOptions.Value;
        string clientUploadId;
        string originalFileName;
        byte[] rawBytes;

        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            clientUploadId = (form["clientUploadId"].ToString() ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(clientUploadId))
            {
                clientUploadId = ctx.Request.Headers["X-Client-Upload-Id"].ToString().Trim();
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

        // Magic-byte validation + allowlist + image re-encoding & thumbnail generation (M3.2, M3.4)
        if (!FileContentInspector.TryInspectAndProcess(rawBytes, originalFileName, policy.MaxFileSizeBytes, out var inspected, out var inspectError) ||
            inspected is null)
        {
            return Results.BadRequest(new { code = "invalid_file_type", message = inspectError });
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
            State = AttachmentState.Pending,
            ScanStatus = scanResult.StatusCode,
            ScanDetail = scanResult.Detail is null ? null : scanResult.Detail[..Math.Min(scanResult.Detail.Length, 256)],
            CreatedAt = now
        };

        db.Attachments.Add(entity);
        await db.SaveChangesAsync(ct);

        var quota = await BuildQuotaSummaryAsync(db, actorId, conversationId, policy, ct);
        return Results.Created($"{OrganizationScope.ApiPath(entity.OrganizationId)}/files/{entity.Id}/download", new
        {
            attachment = ToDto(entity),
            quota
        });
    }

    private static async Task<IResult> HandleListConversationFilesAsync(
        HttpContext ctx,
        Guid conversationId,
        [FromServices] NexusDbContext db,
        IAuthorizationService authz,
        IOptions<FilePolicyOptions> policyOptions,
        DateTimeOffset? before,
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

        var access = await CommunityAccessHelper.ResolveAsync(db, actor, conversationId, ct);
        if (access is null || !authz.Can(actor.ToActorContext(), ResourceAction.ConversationRead, access.Resource))
        {
            return Results.Forbid();
        }

        var query = db.Attachments
            .AsNoTracking()
            .Where(a => a.ConversationId == conversationId && a.State == AttachmentState.Attached && a.MessageId != null)
            .Join(
                db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId && m.Seq > access.VisibleFromSeq && m.DeletedAt == null),
                a => a.MessageId,
                m => (Guid?)m.Id,
                (a, m) => new { Attachment = a, MessageSeq = m.Seq });

        if (before.HasValue)
        {
            query = query.Where(x => x.Attachment.CreatedAt < before.Value);
        }

        var rows = await query
            .OrderByDescending(x => x.Attachment.CreatedAt)
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
            url = $"{OrganizationScope.ApiPath(attachment.OrganizationId)}/files/{attachment.Id}/download",
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

        if (attachment is null || attachment.State is AttachmentState.Quarantined or AttachmentState.Rejected)
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

        // Enforce visible_from_seq on attached files so rejoined members cannot download pre-join files (SEC-04 / SEC-18)
        if (attachment.MessageId.HasValue && access!.VisibleFromSeq > 0)
        {
            var msgSeq = await db.Messages.AsNoTracking()
                .Where(m => m.Id == attachment.MessageId.Value)
                .Select(m => (long?)m.Seq)
                .SingleOrDefaultAsync(ct);

            if (!msgSeq.HasValue || msgSeq.Value <= access.VisibleFromSeq)
            {
                return (null, Results.Forbid());
            }
        }

        return (attachment, null);
    }

    private static async Task<object> BuildQuotaSummaryAsync(
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
