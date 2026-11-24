using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Notifications;

public enum ConversationNotificationMode
{
    All,
    Mentions,
    Muted
}

public sealed class ConversationNotificationPreference
{
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public ConversationNotificationMode Mode { get; set; }
}

public sealed class NotificationQuietHours
{
    public Guid UserId { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public required string TimeZoneId { get; set; }

    public bool IsQuietAt(DateTimeOffset instant)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var zone)) return false;
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        return local.IsBetween(Start, End);
    }

    public DateTimeOffset? ResumeAt(DateTimeOffset instant)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var zone)) return null;
        var localNow = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        var localTime = TimeOnly.FromDateTime(localNow);
        if (!localTime.IsBetween(Start, End)) return null;

        var date = DateOnly.FromDateTime(localNow);
        if (End < Start && localTime >= Start) date = date.AddDays(1);
        var localEnd = date.ToDateTime(End);
        while (zone.IsInvalidTime(localEnd)) localEnd = localEnd.AddMinutes(1);
        if (zone.IsAmbiguousTime(localEnd))
        {
            return zone.GetAmbiguousTimeOffsets(localEnd).Select(offset => new DateTimeOffset(localEnd, offset).ToUniversalTime())
                .Where(candidate => candidate > instant).Min();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, zone));
    }
}

internal static class NotificationPreferences
{
    public static async Task<ConversationNotificationMode> GetModeAsync(
        NexusDbContext db, Guid conversationId, Guid userId, CancellationToken ct)
    {
        var explicitMode = await db.ConversationNotificationPreferences.AsNoTracking()
            .Where(preference => preference.ConversationId == conversationId && preference.UserId == userId)
            .Select(preference => (ConversationNotificationMode?)preference.Mode).SingleOrDefaultAsync(ct);
        if (explicitMode.HasValue) return explicitMode.Value;

        return await db.ConversationMembers.AnyAsync(member => member.ConversationId == conversationId &&
            member.UserId == userId && member.LeftAt == null && member.Muted, ct)
            ? ConversationNotificationMode.Muted
            : ConversationNotificationMode.All;
    }

    public static async Task<HashSet<Guid>> ExcludedRecipientsAsync(
        NexusDbContext db, Guid conversationId, Guid[] recipientIds, bool isMention, CancellationToken ct)
    {
        var preferences = await db.ConversationNotificationPreferences.AsNoTracking()
            .Where(preference => preference.ConversationId == conversationId && recipientIds.Contains(preference.UserId))
            .ToDictionaryAsync(preference => preference.UserId, preference => preference.Mode, ct);
        var legacyMuted = await db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == conversationId && member.LeftAt == null &&
                member.Muted && recipientIds.Contains(member.UserId))
            .Select(member => member.UserId).ToListAsync(ct);

        return preferences.Where(entry => entry.Value == ConversationNotificationMode.Muted ||
                (!isMention && entry.Value == ConversationNotificationMode.Mentions)).Select(entry => entry.Key)
            .Concat(legacyMuted.Where(userId => !preferences.ContainsKey(userId))).ToHashSet();
    }
}

internal sealed class ConversationNotificationPreferenceConfiguration : IEntityTypeConfiguration<ConversationNotificationPreference>
{
    public void Configure(EntityTypeBuilder<ConversationNotificationPreference> builder)
    {
        builder.ToTable("conversation_notification_preferences");
        builder.HasKey(x => new { x.ConversationId, x.UserId });
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id");
        builder.Property(x => x.Mode).HasColumnName("mode").HasConversion<string>().HasMaxLength(16);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NotificationQuietHoursConfiguration : IEntityTypeConfiguration<NotificationQuietHours>
{
    public void Configure(EntityTypeBuilder<NotificationQuietHours> builder)
    {
        builder.ToTable("notification_quiet_hours");
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Start).HasColumnName("start_time");
        builder.Property(x => x.End).HasColumnName("end_time");
        builder.Property(x => x.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(128);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
