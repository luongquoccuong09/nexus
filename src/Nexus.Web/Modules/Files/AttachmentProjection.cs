using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Files;

public sealed record AttachmentDto(
    Guid Id,
    Guid ConversationId,
    Guid? MessageId,
    Guid? PostId,
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

/// <summary>
/// Shared projection, scoped URL builder, and transactional pending-attachment validation
/// used by FilesEndpoints, MessagingEndpoints, CommunityFeedEndpoints, and Shared View (M6.32).
/// </summary>
public static class AttachmentProjection
{
    public const int MaxAttachmentsPerItem = 10;

    public static string BuildDownloadUrl(Guid? organizationId, Guid attachmentId) =>
        $"{OrganizationScope.ApiPath(organizationId)}/files/{attachmentId}/download";

    public static string BuildThumbnailUrl(Guid? organizationId, Guid attachmentId) =>
        $"{OrganizationScope.ApiPath(organizationId)}/files/{attachmentId}/thumbnail";

    public static string BuildPreviewUrl(Guid? organizationId, Guid attachmentId) =>
        $"{OrganizationScope.ApiPath(organizationId)}/files/{attachmentId}/preview";

    public static bool IsMediaContentType(string? contentType) =>
        !string.IsNullOrEmpty(contentType) &&
        (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
         contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    public static AttachmentDto ToAttachmentDto(AttachmentEntity a)
    {
        var hasThumb = !string.IsNullOrEmpty(a.ThumbnailObjectKey);
        return new AttachmentDto(
            a.Id,
            a.ConversationId,
            a.MessageId,
            a.PostId,
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
            BuildDownloadUrl(a.OrganizationId, a.Id),
            hasThumb ? BuildThumbnailUrl(a.OrganizationId, a.Id) : null,
            hasThumb ? BuildPreviewUrl(a.OrganizationId, a.Id) : null,
            a.CreatedAt);
    }

    public static Task<List<AttachmentEntity>> LoadPendingForBindingAsync(
        NexusDbContext db,
        Guid conversationId,
        Guid? organizationId,
        Guid uploaderUserId,
        Guid[] attachmentIds,
        CancellationToken ct) =>
        db.Attachments
            .Where(a => attachmentIds.Contains(a.Id) &&
                        a.ConversationId == conversationId &&
                        a.OrganizationId == organizationId &&
                        a.UploaderUserId == uploaderUserId &&
                        a.State == AttachmentState.Pending &&
                        a.MessageId == null &&
                        a.PostId == null &&
                        (a.ScanStatus == "clean" || a.ScanStatus == "skipped_prototype"))
            .ToListAsync(ct);
}
