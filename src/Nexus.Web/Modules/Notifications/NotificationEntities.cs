using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Notifications;

public sealed class OutboxMessageEntity
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid[]? RecipientIds { get; set; }
    public Guid? DestinationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public string? LastFailure { get; set; }
}

public sealed class NotificationEntity
{
    public Guid Id { get; set; }
    public Guid OutboxMessageId { get; set; }
    public Guid RecipientId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid ResourceId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? PushAcceptedAt { get; set; }
    public DateTimeOffset? DigestedAt { get; set; }
}

public sealed class EmailDigestOptIn
{
    public Guid UserId { get; set; }
    public DateTimeOffset EnabledAt { get; set; }
}

public sealed class PushSubscriptionEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string SessionId { get; set; }
    public required string Endpoint { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessageEntity>
{
    public void Configure(EntityTypeBuilder<OutboxMessageEntity> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(64);
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.ResourceId).HasColumnName("resource_id");
        builder.Property(x => x.RecipientIds).HasColumnName("recipient_ids");
        builder.Property(x => x.DestinationId).HasColumnName("destination_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.AvailableAt).HasColumnName("available_at");
        builder.Property(x => x.Attempts).HasColumnName("attempts");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.DeadLetteredAt).HasColumnName("dead_lettered_at");
        builder.Property(x => x.LastFailure).HasColumnName("last_failure").HasMaxLength(128);
        builder.HasIndex(x => new { x.AvailableAt, x.Id })
            .HasDatabaseName("ix_outbox_messages_available")
            .HasFilter("completed_at IS NULL AND dead_lettered_at IS NULL");
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<NotificationEntity>
{
    public void Configure(EntityTypeBuilder<NotificationEntity> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OutboxMessageId).HasColumnName("outbox_message_id");
        builder.Property(x => x.RecipientId).HasColumnName("recipient_id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id");
        builder.Property(x => x.ResourceId).HasColumnName("resource_id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(64);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.ReadAt).HasColumnName("read_at");
        builder.Property(x => x.PushAcceptedAt).HasColumnName("push_accepted_at");
        builder.Property(x => x.DigestedAt).HasColumnName("digested_at");
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.OutboxMessageId, x.RecipientId })
            .IsUnique()
            .HasDatabaseName("ux_notifications_outbox_recipient");
        builder.HasIndex(x => new { x.RecipientId, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_notifications_recipient_created");
    }
}

internal sealed class EmailDigestOptInConfiguration : IEntityTypeConfiguration<EmailDigestOptIn>
{
    public void Configure(EntityTypeBuilder<EmailDigestOptIn> builder)
    {
        builder.ToTable("email_digest_opt_ins");
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.EnabledAt).HasColumnName("enabled_at");
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscriptionEntity>
{
    public void Configure(EntityTypeBuilder<PushSubscriptionEntity> builder)
    {
        builder.ToTable("push_subscriptions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(128);
        builder.Property(x => x.Endpoint).HasColumnName("endpoint").HasMaxLength(1600);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.HasOne<AppSessionEntity>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.Endpoint).IsUnique().HasDatabaseName("ux_push_subscriptions_endpoint");
        builder.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("ix_push_subscriptions_user_created");
    }
}
