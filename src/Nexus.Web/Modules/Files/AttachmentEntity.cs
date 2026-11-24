using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Files;

public enum AttachmentState
{
    Pending,
    Attached,
    Quarantined,
    Rejected
}

public sealed class AttachmentEntity
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? PostId { get; set; }
    public Guid UploaderUserId { get; set; }
    public required string ClientUploadId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256Hex { get; set; }
    public required string ObjectKey { get; set; }
    public string? ThumbnailObjectKey { get; set; }
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public int? DurationMs { get; set; }
    public AttachmentState State { get; set; } = AttachmentState.Pending;
    public required string ScanStatus { get; set; }
    public string? ScanDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AttachedAt { get; set; }
}

public sealed class AttachmentEntityConfiguration : IEntityTypeConfiguration<AttachmentEntity>
{
    public void Configure(EntityTypeBuilder<AttachmentEntity> builder)
    {
        builder.ToTable("attachments", table =>
        {
            table.HasCheckConstraint(
                "ck_attachments_single_parent",
                "NOT (message_id IS NOT NULL AND post_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_attachments_duration_ms",
                "duration_ms IS NULL OR (duration_ms > 0 AND duration_ms <= 120000)");
        });
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
        builder.Property(x => x.MessageId).HasColumnName("message_id");
        builder.Property(x => x.PostId).HasColumnName("post_id");
        builder.Property(x => x.UploaderUserId).HasColumnName("uploader_user_id").IsRequired();
        builder.Property(x => x.ClientUploadId).HasColumnName("client_upload_id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(128).IsRequired();
        builder.Property(x => x.SizeBytes).HasColumnName("size_bytes").IsRequired();
        builder.Property(x => x.Sha256Hex).HasColumnName("sha256_hex").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ObjectKey).HasColumnName("object_key").HasMaxLength(256).IsRequired();
        builder.Property(x => x.ThumbnailObjectKey).HasColumnName("thumbnail_object_key").HasMaxLength(256);
        builder.Property(x => x.ImageWidth).HasColumnName("image_width");
        builder.Property(x => x.ImageHeight).HasColumnName("image_height");
        builder.Property(x => x.DurationMs).HasColumnName("duration_ms");
        builder.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(x => x.ScanStatus).HasColumnName("scan_status").HasMaxLength(32).IsRequired();
        builder.Property(x => x.ScanDetail).HasColumnName("scan_detail").HasMaxLength(256);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.AttachedAt).HasColumnName("attached_at");

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MessageEntity>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ChannelPostEntity>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UploaderUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.UploaderUserId, x.ClientUploadId })
            .IsUnique()
            .HasDatabaseName("ux_attachments_uploader_client_upload_id");

        builder.HasIndex(x => new { x.ConversationId, x.State, x.CreatedAt })
            .HasDatabaseName("ix_attachments_conversation_state_created");

        builder.HasIndex(x => x.MessageId)
            .HasDatabaseName("ix_attachments_message_id");

        builder.HasIndex(x => x.PostId)
            .HasFilter("post_id IS NOT NULL")
            .HasDatabaseName("ix_attachments_post_id");

        builder.HasIndex(x => new { x.ConversationId, x.CreatedAt, x.Id })
            .IsDescending(false, true, true)
            .HasFilter("state = 'Attached' AND post_id IS NOT NULL")
            .HasDatabaseName("ix_attachments_channel_files");

        builder.HasIndex(x => new { x.State, x.CreatedAt })
            .HasDatabaseName("ix_attachments_state_created");
    }
}
