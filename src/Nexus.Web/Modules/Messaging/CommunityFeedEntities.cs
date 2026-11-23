using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Messaging;

public sealed class ChannelPostEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string? Title { get; set; }
    public required string Body { get; set; }
    public bool IsAnnouncement { get; set; }
    public bool RequiresAcknowledgment { get; set; }
    public bool IsPinned { get; set; }
    public DateTimeOffset? PinnedAt { get; set; }
    public Guid? PinnedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class PostCommentEntity
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Guid AuthorUserId { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class PostReactionEntity
{
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public required string Emoji { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AnnouncementAcknowledgmentEntity
{
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset AcknowledgedAt { get; set; }
}

public sealed class PollEntity
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public required string Question { get; set; }
    public required string OptionsJson { get; set; }
    public bool AllowMultiple { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PollVoteEntity
{
    public Guid PollId { get; set; }
    public Guid UserId { get; set; }
    public required string OptionId { get; set; }
    public DateTimeOffset VotedAt { get; set; }
}

public sealed class ChannelPostEntityConfiguration : IEntityTypeConfiguration<ChannelPostEntity>
{
    public void Configure(EntityTypeBuilder<ChannelPostEntity> builder)
    {
        builder.ToTable("channel_posts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
        builder.Property(x => x.AuthorUserId).HasColumnName("author_user_id").IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(160);
        builder.Property(x => x.Body).HasColumnName("body").HasMaxLength(8000).IsRequired();
        builder.Property(x => x.IsAnnouncement).HasColumnName("is_announcement").IsRequired();
        builder.Property(x => x.RequiresAcknowledgment).HasColumnName("requires_acknowledgment").IsRequired();
        builder.Property(x => x.IsPinned).HasColumnName("is_pinned").IsRequired();
        builder.Property(x => x.PinnedAt).HasColumnName("pinned_at");
        builder.Property(x => x.PinnedByUserId).HasColumnName("pinned_by_user_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.DeletedAt).HasColumnName("deleted_at");

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ConversationId, x.IsPinned, x.CreatedAt })
            .HasDatabaseName("ix_channel_posts_conv_pinned_created");
    }
}

public sealed class PostCommentEntityConfiguration : IEntityTypeConfiguration<PostCommentEntity>
{
    public void Configure(EntityTypeBuilder<PostCommentEntity> builder)
    {
        builder.ToTable("post_comments");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.PostId).HasColumnName("post_id").IsRequired();
        builder.Property(x => x.AuthorUserId).HasColumnName("author_user_id").IsRequired();
        builder.Property(x => x.Body).HasColumnName("body").HasMaxLength(2000).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.DeletedAt).HasColumnName("deleted_at");

        builder.HasOne<ChannelPostEntity>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.PostId, x.CreatedAt })
            .HasDatabaseName("ix_post_comments_post_created");
    }
}

public sealed class PostReactionEntityConfiguration : IEntityTypeConfiguration<PostReactionEntity>
{
    public void Configure(EntityTypeBuilder<PostReactionEntity> builder)
    {
        builder.ToTable("post_reactions");
        builder.HasKey(x => new { x.PostId, x.UserId, x.Emoji });

        builder.Property(x => x.PostId).HasColumnName("post_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Emoji).HasColumnName("emoji").HasMaxLength(32).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<ChannelPostEntity>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AnnouncementAcknowledgmentEntityConfiguration : IEntityTypeConfiguration<AnnouncementAcknowledgmentEntity>
{
    public void Configure(EntityTypeBuilder<AnnouncementAcknowledgmentEntity> builder)
    {
        builder.ToTable("announcement_acknowledgments");
        builder.HasKey(x => new { x.PostId, x.UserId });

        builder.Property(x => x.PostId).HasColumnName("post_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at").IsRequired();

        builder.HasOne<ChannelPostEntity>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PollEntityConfiguration : IEntityTypeConfiguration<PollEntity>
{
    public void Configure(EntityTypeBuilder<PollEntity> builder)
    {
        builder.ToTable("polls");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.PostId).HasColumnName("post_id").IsRequired();
        builder.Property(x => x.Question).HasColumnName("question").HasMaxLength(240).IsRequired();
        builder.Property(x => x.OptionsJson).HasColumnName("options_json").IsRequired();
        builder.Property(x => x.AllowMultiple).HasColumnName("allow_multiple").IsRequired();
        builder.Property(x => x.ClosesAt).HasColumnName("closes_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<ChannelPostEntity>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.PostId).IsUnique().HasDatabaseName("ux_polls_post_id");
    }
}

public sealed class PollVoteEntityConfiguration : IEntityTypeConfiguration<PollVoteEntity>
{
    public void Configure(EntityTypeBuilder<PollVoteEntity> builder)
    {
        builder.ToTable("poll_votes");
        builder.HasKey(x => new { x.PollId, x.UserId, x.OptionId });

        builder.Property(x => x.PollId).HasColumnName("poll_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.OptionId).HasColumnName("option_id").HasMaxLength(64).IsRequired();
        builder.Property(x => x.VotedAt).HasColumnName("voted_at").IsRequired();

        builder.HasOne<PollEntity>().WithMany().HasForeignKey(x => x.PollId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
