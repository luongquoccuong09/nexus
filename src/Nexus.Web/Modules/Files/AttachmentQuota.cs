using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;

namespace Nexus.Web.Modules.Files;

internal static class AttachmentQuota
{
    // Both upload and forward reserve the uploader first, then the destination conversation.
    internal static Task LockAsync(NexusDbContext db, Guid userId, Guid conversationId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(67821, hashtext(CAST({userId} AS text)));
            SELECT pg_advisory_xact_lock(67822, hashtext(CAST({conversationId} AS text)));
            """, ct);

    internal static async Task<IResult?> CheckAsync(NexusDbContext db, Guid userId, Guid conversationId, long bytes,
        FilePolicyOptions policy, CancellationToken ct)
    {
        if (bytes == 0) return null;
        var active = db.Attachments.AsNoTracking().Where(item => item.State == AttachmentState.Pending || item.State == AttachmentState.Attached);
        var userUsed = await active.Where(item => item.UploaderUserId == userId).SumAsync(item => (long?)item.SizeBytes, ct) ?? 0L;
        if (bytes > policy.PerUserQuotaBytes - userUsed)
            return Exceeded("user_quota_exceeded", "Your personal storage quota has been reached.", userUsed, policy.PerUserQuotaBytes);
        var conversationUsed = await active.Where(item => item.ConversationId == conversationId).SumAsync(item => (long?)item.SizeBytes, ct) ?? 0L;
        if (bytes > policy.PerConversationQuotaBytes - conversationUsed)
            return Exceeded("conversation_quota_exceeded", "This conversation's storage quota has been reached.", conversationUsed, policy.PerConversationQuotaBytes);
        return null;
    }

    private static IResult Exceeded(string code, string message, long usedBytes, long quotaBytes) =>
        Results.Json(new { code, message, usedBytes, quotaBytes }, statusCode: StatusCodes.Status413PayloadTooLarge);
}
