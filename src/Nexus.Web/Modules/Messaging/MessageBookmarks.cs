using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Messaging;

public sealed class MessagePinEntity
{
    public Guid MessageId { get; set; }
    public Guid PinnedByUserId { get; set; }
    public DateTimeOffset PinnedAt { get; set; }
}

public sealed class SavedMessageEntity
{
    public Guid UserId { get; set; }
    public Guid MessageId { get; set; }
    public bool IsSaved { get; set; }
    public long Version { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}

public sealed class MessagePinEntityConfiguration : IEntityTypeConfiguration<MessagePinEntity>
{
    public void Configure(EntityTypeBuilder<MessagePinEntity> builder)
    {
        builder.ToTable("message_pins");
        builder.HasKey(pin => pin.MessageId);
        builder.Property(pin => pin.MessageId).HasColumnName("message_id");
        builder.Property(pin => pin.PinnedByUserId).HasColumnName("pinned_by_user_id");
        builder.Property(pin => pin.PinnedAt).HasColumnName("pinned_at");
        builder.HasOne<MessageEntity>().WithMany().HasForeignKey(pin => pin.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(pin => pin.PinnedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(pin => pin.PinnedByUserId).HasDatabaseName("ix_message_pins_pinned_by_user_id");
    }
}

public sealed class SavedMessageEntityConfiguration : IEntityTypeConfiguration<SavedMessageEntity>
{
    public void Configure(EntityTypeBuilder<SavedMessageEntity> builder)
    {
        builder.ToTable("saved_messages");
        builder.HasKey(saved => new { saved.UserId, saved.MessageId });
        builder.Property(saved => saved.UserId).HasColumnName("user_id");
        builder.Property(saved => saved.MessageId).HasColumnName("message_id");
        builder.Property(saved => saved.IsSaved).HasColumnName("is_saved");
        builder.Property(saved => saved.Version).HasColumnName("version");
        builder.Property(saved => saved.SavedAt).HasColumnName("saved_at");
        builder.HasOne<MessageEntity>().WithMany().HasForeignKey(saved => saved.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(saved => saved.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(saved => saved.MessageId).HasDatabaseName("ix_saved_messages_message_id");
        builder.HasIndex(saved => new { saved.UserId, saved.SavedAt, saved.MessageId })
            .HasFilter("is_saved").HasDatabaseName("ix_saved_messages_user_cursor");
    }
}
