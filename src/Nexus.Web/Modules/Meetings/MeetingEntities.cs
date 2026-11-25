using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Calendar;
using Nexus.Web.Modules.Calls;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Meetings;

public sealed class MeetingEntity
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? CalendarEventId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? DirectCallId { get; set; }
    public Guid HostId { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? ProviderEndedAt { get; set; }

    public string RoomName => Room(Id);
    internal static string Room(Guid meetingId) => $"nexus-{meetingId:N}";
}

public sealed class MeetingGrantEntity
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Guid UserId { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? ProviderRemovedAt { get; set; }
}

public sealed class MeetingBanEntity
{
    public Guid MeetingId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class MeetingConfiguration : IEntityTypeConfiguration<MeetingEntity>
{
    public void Configure(EntityTypeBuilder<MeetingEntity> builder)
    {
        builder.ToTable("meetings", table => table.HasCheckConstraint(
            "ck_meetings_single_source", "num_nonnulls(calendar_event_id, conversation_id, direct_call_id) = 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.CalendarEventId).HasColumnName("calendar_event_id");
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id");
        builder.Property(x => x.DirectCallId).HasColumnName("direct_call_id");
        builder.Property(x => x.HostId).HasColumnName("host_id");
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(160);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.EndedAt).HasColumnName("ended_at");
        builder.Property(x => x.ProviderEndedAt).HasColumnName("provider_ended_at");
        builder.Ignore(x => x.RoomName);
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CalendarEventEntity>().WithMany().HasForeignKey(x => x.CalendarEventId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DirectCallEntity>().WithMany().HasForeignKey(x => x.DirectCallId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.CalendarEventId).IsUnique().HasDatabaseName("ux_meetings_calendar_event");
        builder.HasIndex(x => x.DirectCallId).IsUnique().HasDatabaseName("ux_meetings_direct_call");
        builder.HasIndex(x => x.ConversationId);
        builder.HasIndex(x => new { x.ConversationId, x.EndedAt }).IsUnique().AreNullsDistinct(false)
            .HasFilter("conversation_id IS NOT NULL AND ended_at IS NULL")
            .HasDatabaseName("ux_meetings_active_conversation");
        builder.HasIndex(x => new { x.EndedAt, x.Id }).HasFilter("ended_at IS NOT NULL AND provider_ended_at IS NULL")
            .HasDatabaseName("ix_meetings_pending_close");
    }
}

internal sealed class MeetingGrantConfiguration : IEntityTypeConfiguration<MeetingGrantEntity>
{
    public void Configure(EntityTypeBuilder<MeetingGrantEntity> builder)
    {
        builder.ToTable("meeting_grants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.MeetingId).HasColumnName("meeting_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(128);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.CheckedAt).HasColumnName("checked_at");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        builder.Property(x => x.ProviderRemovedAt).HasColumnName("provider_removed_at");
        builder.HasOne<MeetingEntity>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppSessionEntity>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.MeetingId, x.SessionId }).IsUnique()
            .HasFilter("revoked_at IS NULL AND session_id IS NOT NULL")
            .HasDatabaseName("ux_meeting_grants_active_session");
        builder.HasIndex(x => new { x.UserId, x.MeetingId }).HasDatabaseName("ix_meeting_grants_user_meeting");
        builder.HasIndex(x => new { x.CheckedAt, x.Id }).HasFilter("revoked_at IS NULL")
            .HasDatabaseName("ix_meeting_grants_access_check");
        builder.HasIndex(x => new { x.RevokedAt, x.Id }).HasFilter("revoked_at IS NOT NULL AND provider_removed_at IS NULL")
            .HasDatabaseName("ix_meeting_grants_pending_removal");
    }
}

internal sealed class MeetingBanConfiguration : IEntityTypeConfiguration<MeetingBanEntity>
{
    public void Configure(EntityTypeBuilder<MeetingBanEntity> builder)
    {
        builder.ToTable("meeting_bans");
        builder.HasKey(x => new { x.MeetingId, x.UserId });
        builder.Property(x => x.MeetingId).HasColumnName("meeting_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.HasOne<MeetingEntity>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
