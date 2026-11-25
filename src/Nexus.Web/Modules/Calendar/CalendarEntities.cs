using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Calendar;

public sealed class CalendarEventEntity
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? CommunityId { get; set; }
    public Guid OrganizerId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public required string TimeZone { get; set; }
    public int? ReminderMinutes { get; set; }
    public bool IsOnlineMeeting { get; set; }
    public CalendarRecurrenceFrequency? RecurrenceFrequency { get; set; }
    public int? RecurrenceInterval { get; set; }
    public DateOnly? RecurrenceUntilLocalDate { get; set; }
    public Guid? ReminderOutboxId { get; set; }
    public Guid? ReminderGenerationId { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
    public int Version { get; set; } = 1;
    public required string CreationKeyHash { get; set; }
    public required string CreationRequestHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum CalendarRsvp
{
    Going,
    Tentative,
    Declined
}

public enum CalendarRecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly
}

public sealed class CalendarOccurrenceOverride
{
    public Guid EventId { get; set; }
    public DateTimeOffset OriginalStartUtc { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
}

public sealed class CalendarAttendeeEntity
{
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
    public CalendarRsvp? Status { get; set; }
}

internal sealed class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEventEntity>
{
    internal const string CreationKeyIndex = "ux_calendar_events_scope_creation";

    public void Configure(EntityTypeBuilder<CalendarEventEntity> builder)
    {
        builder.ToTable("calendar_events", table =>
        {
            table.HasCheckConstraint("ck_calendar_events_time_range", "start_utc < end_utc");
            table.HasCheckConstraint("ck_calendar_events_community_scope", "community_id IS NULL OR organization_id IS NOT NULL");
            table.HasCheckConstraint("ck_calendar_events_reminder", "reminder_minutes BETWEEN 0 AND 10080");
            table.HasCheckConstraint("ck_calendar_events_recurrence",
                "(recurrence_frequency IS NULL AND recurrence_interval IS NULL AND recurrence_until_local_date IS NULL) OR " +
                "(recurrence_frequency IN ('Daily', 'Weekly', 'Monthly') AND recurrence_frequency IS NOT NULL " +
                "AND recurrence_interval IS NOT NULL AND recurrence_interval BETWEEN 1 AND 365)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.CommunityId).HasColumnName("community_id");
        builder.Property(x => x.OrganizerId).HasColumnName("organizer_id");
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(160);
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(4000);
        builder.Property(x => x.StartUtc).HasColumnName("start_utc");
        builder.Property(x => x.EndUtc).HasColumnName("end_utc");
        builder.Property(x => x.TimeZone).HasColumnName("time_zone").HasMaxLength(128);
        builder.Property(x => x.ReminderMinutes).HasColumnName("reminder_minutes");
        builder.Property(x => x.IsOnlineMeeting).HasColumnName("is_online_meeting");
        builder.Property(x => x.RecurrenceFrequency).HasColumnName("recurrence_frequency").HasConversion<string>().HasMaxLength(12);
        builder.Property(x => x.RecurrenceInterval).HasColumnName("recurrence_interval");
        builder.Property(x => x.RecurrenceUntilLocalDate).HasColumnName("recurrence_until_local_date");
        builder.Property(x => x.ReminderOutboxId).HasColumnName("reminder_outbox_id");
        builder.Property(x => x.ReminderGenerationId).HasColumnName("reminder_generation_id");
        builder.Property(x => x.CanceledAt).HasColumnName("canceled_at");
        builder.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(x => x.CreationKeyHash).HasColumnName("creation_key_hash").HasMaxLength(64);
        builder.Property(x => x.CreationRequestHash).HasColumnName("creation_request_hash").HasMaxLength(64);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CommunityEntity>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.CommunityId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.OrganizerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OrganizationId, x.StartUtc, x.EndUtc })
            .HasDatabaseName("ix_calendar_events_scope_range");
        builder.HasIndex(x => new { x.OrganizerId, x.OrganizationId, x.CreationKeyHash })
            .IsUnique().AreNullsDistinct(false).HasDatabaseName(CreationKeyIndex);
    }
}

internal sealed class CalendarOccurrenceOverrideConfiguration : IEntityTypeConfiguration<CalendarOccurrenceOverride>
{
    public void Configure(EntityTypeBuilder<CalendarOccurrenceOverride> builder)
    {
        builder.ToTable("calendar_occurrence_overrides", table =>
            table.HasCheckConstraint("ck_calendar_occurrence_overrides_shape",
                "(start_utc IS NULL AND end_utc IS NULL AND title IS NULL AND canceled_at IS NOT NULL) OR " +
                "(start_utc IS NOT NULL AND end_utc IS NOT NULL AND title IS NOT NULL AND start_utc < end_utc)"));
        builder.HasKey(x => new { x.EventId, x.OriginalStartUtc });
        builder.Property(x => x.EventId).HasColumnName("event_id");
        builder.Property(x => x.OriginalStartUtc).HasColumnName("original_start_utc");
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(160);
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(4000);
        builder.Property(x => x.StartUtc).HasColumnName("start_utc");
        builder.Property(x => x.EndUtc).HasColumnName("end_utc");
        builder.Property(x => x.CanceledAt).HasColumnName("canceled_at");
        builder.HasOne<CalendarEventEntity>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.EventId, x.StartUtc, x.EndUtc })
            .HasFilter("start_utc IS NOT NULL AND canceled_at IS NULL")
            .HasDatabaseName("ix_calendar_occurrence_overrides_moved");
    }
}

internal sealed class CalendarAttendeeConfiguration : IEntityTypeConfiguration<CalendarAttendeeEntity>
{
    public void Configure(EntityTypeBuilder<CalendarAttendeeEntity> builder)
    {
        builder.ToTable("calendar_attendees", table =>
            table.HasCheckConstraint("ck_calendar_attendees_status", "status IN ('Going', 'Tentative', 'Declined')"));
        builder.HasKey(x => new { x.EventId, x.UserId });
        builder.Property(x => x.EventId).HasColumnName("event_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.HasOne<CalendarEventEntity>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.EventId }).HasDatabaseName("ix_calendar_attendees_user_event");
    }
}
