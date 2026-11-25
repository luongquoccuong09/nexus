using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Notifications;

namespace Nexus.Web.Modules.Calendar;

public sealed class CalendarOccurrenceReminderEntity
{
    public Guid OutboxMessageId { get; set; }
    public Guid EventId { get; set; }
    public Guid GenerationId { get; set; }
    public DateTimeOffset OriginalStartUtc { get; set; }
    public DateTimeOffset EffectiveStartUtc { get; set; }
    public int ReminderMinutes { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

internal sealed class CalendarOccurrenceReminderConfiguration : IEntityTypeConfiguration<CalendarOccurrenceReminderEntity>
{
    public void Configure(EntityTypeBuilder<CalendarOccurrenceReminderEntity> builder)
    {
        builder.ToTable("calendar_occurrence_reminders");
        builder.HasKey(reminder => reminder.OutboxMessageId);
        builder.Property(reminder => reminder.OutboxMessageId).HasColumnName("outbox_message_id");
        builder.Property(reminder => reminder.EventId).HasColumnName("event_id");
        builder.Property(reminder => reminder.GenerationId).HasColumnName("generation_id");
        builder.Property(reminder => reminder.OriginalStartUtc).HasColumnName("original_start_utc");
        builder.Property(reminder => reminder.EffectiveStartUtc).HasColumnName("effective_start_utc");
        builder.Property(reminder => reminder.ReminderMinutes).HasColumnName("reminder_minutes");
        builder.Property(reminder => reminder.ProcessedAt).HasColumnName("processed_at");
        builder.HasOne<OutboxMessageEntity>().WithOne().HasForeignKey<CalendarOccurrenceReminderEntity>(reminder => reminder.OutboxMessageId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CalendarEventEntity>().WithMany().HasForeignKey(reminder => reminder.EventId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(reminder => new { reminder.EventId, reminder.OriginalStartUtc, reminder.EffectiveStartUtc })
            .HasDatabaseName("ix_calendar_occurrence_reminders_time");
    }
}
