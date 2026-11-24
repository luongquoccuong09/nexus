using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Messaging;

public sealed class MessageEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? ParentMessageId { get; set; }
    public long Seq { get; set; }
    public Guid SenderId { get; set; }
    public required string ClientMsgId { get; set; }
    public required string PayloadSha256 { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long Version { get; set; } = 1;
    public int ReplyCount { get; set; }
    public DateTimeOffset? LastReplyAt { get; set; }
    public long? LastReplySeq { get; set; }
}

public sealed class ThreadSubscriptionEntity
{
    public Guid ParentMessageId { get; set; }
    public Guid UserId { get; set; }
    public bool IsFollowing { get; set; }
    public long LastReadReplySeq { get; set; }
    public long Version { get; set; }
}

public sealed class ConversationEventEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long ChangeSeq { get; set; }
    public required string EventType { get; set; }
    public Guid ActorUserId { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MessageReactionEntity
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public required string Emoji { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MessageEntityConfiguration : IEntityTypeConfiguration<MessageEntity>
{
    public void Configure(EntityTypeBuilder<MessageEntity> builder)
    {
        builder.ToTable("messages", table => table.HasCheckConstraint(
            "ck_messages_thread_metadata",
            "(parent_message_id IS NULL OR (parent_message_id <> id AND reply_count = 0)) AND " +
            "((reply_count = 0 AND last_reply_at IS NULL AND last_reply_seq IS NULL) OR " +
            "(reply_count > 0 AND last_reply_at IS NOT NULL AND last_reply_seq IS NOT NULL AND last_reply_seq > seq))"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
        builder.Property(x => x.ParentMessageId).HasColumnName("parent_message_id");
        builder.Property(x => x.Seq).HasColumnName("seq").IsRequired();
        builder.Property(x => x.SenderId).HasColumnName("sender_id").IsRequired();
        builder.Property(x => x.ClientMsgId).HasColumnName("client_msg_id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Body).HasColumnName("body").HasMaxLength(4000).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.EditedAt).HasColumnName("edited_at");
        builder.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        builder.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1L);
        builder.Property(x => x.ReplyCount).HasColumnName("reply_count");
        builder.Property(x => x.LastReplyAt).HasColumnName("last_reply_at");
        builder.Property(x => x.LastReplySeq).HasColumnName("last_reply_seq");

        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MessageEntity>().WithMany()
            .HasForeignKey(x => new { x.ConversationId, x.ParentMessageId })
            .HasPrincipalKey(x => new { x.ConversationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ConversationId, x.ParentMessageId, x.Seq })
            .HasDatabaseName("ix_messages_conversation_parent_seq");

        builder.HasIndex(x => new { x.ConversationId, x.Seq })
            .IsUnique()
            .HasDatabaseName("ux_messages_conversation_seq");

        builder.HasIndex(x => new { x.SenderId, x.ClientMsgId })
            .IsUnique()
            .HasDatabaseName("ux_messages_sender_client_msg_id");
    }
}

public sealed class ThreadSubscriptionEntityConfiguration : IEntityTypeConfiguration<ThreadSubscriptionEntity>
{
    public void Configure(EntityTypeBuilder<ThreadSubscriptionEntity> builder)
    {
        builder.ToTable("thread_subscriptions", table => table.HasCheckConstraint(
            "ck_thread_subscriptions_cursor", "last_read_reply_seq >= 0 AND version >= 0"));
        builder.HasKey(x => new { x.ParentMessageId, x.UserId });
        builder.Property(x => x.ParentMessageId).HasColumnName("parent_message_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.IsFollowing).HasColumnName("is_following");
        builder.Property(x => x.LastReadReplySeq).HasColumnName("last_read_reply_seq");
        builder.Property(x => x.Version).HasColumnName("version").HasDefaultValue(0L);
        builder.HasOne<MessageEntity>().WithMany().HasForeignKey(x => x.ParentMessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_thread_subscriptions_user_id");
    }
}

public sealed class ConversationEventEntityConfiguration : IEntityTypeConfiguration<ConversationEventEntity>
{
    public void Configure(EntityTypeBuilder<ConversationEventEntity> builder)
    {
        builder.ToTable("conversation_events");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
        builder.Property(x => x.ChangeSeq).HasColumnName("change_seq").IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ConversationId, x.ChangeSeq })
            .IsUnique()
            .HasDatabaseName("ux_conversation_events_conv_change_seq");

        builder.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("ix_conversation_events_created_at");
    }
}

public sealed class MessageReactionEntityConfiguration : IEntityTypeConfiguration<MessageReactionEntity>
{
    public void Configure(EntityTypeBuilder<MessageReactionEntity> builder)
    {
        builder.ToTable("message_reactions", table =>
        {
            table.HasCheckConstraint(
                "ck_message_reactions_emoji_non_empty",
                "char_length(btrim(emoji)) >= 1 AND char_length(emoji) <= 32");
        });
        builder.HasKey(x => new { x.MessageId, x.UserId, x.Emoji });

        builder.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Emoji).HasColumnName("emoji").HasMaxLength(32).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<MessageEntity>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_message_reactions_user_id");
    }
}
