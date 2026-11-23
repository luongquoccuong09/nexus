using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Conversations;

public enum ConversationKind
{
    Dm,
    Group,
    Channel
}

public sealed class ConversationEntity
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public ConversationKind Kind { get; set; }
    public Guid? CommunityId { get; set; }
    public bool IsCommunityMainFeed { get; set; }
    public PostingPolicy PostingPolicy { get; set; } = PostingPolicy.Members;
    public string? Title { get; set; }
    public string? DmKey { get; set; }
    public long NextSeq { get; set; }
    public long ChangeSeq { get; set; }
    public long MinAvailableChangeSeq { get; set; } = 1;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ConversationMemberEntity
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public ResourceRole Role { get; set; } = ResourceRole.Member;
    public long LastReadSeq { get; set; }
    public long VisibleFromSeq { get; set; }
    public bool Muted { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

public sealed class ConversationEntityConfiguration : IEntityTypeConfiguration<ConversationEntity>
{
    public void Configure(EntityTypeBuilder<ConversationEntity> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.CommunityId).HasColumnName("community_id");
        builder.Property(x => x.IsCommunityMainFeed).HasColumnName("is_community_main_feed").IsRequired();
        builder.Property(x => x.PostingPolicy).HasColumnName("posting_policy").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(120);
        builder.Property(x => x.DmKey).HasColumnName("dm_key").HasMaxLength(128);
        builder.Property(x => x.NextSeq).HasColumnName("next_seq").IsRequired();
        builder.Property(x => x.ChangeSeq).HasColumnName("change_seq").IsRequired();
        builder.Property(x => x.MinAvailableChangeSeq).HasColumnName("min_available_change_seq").IsRequired();
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CommunityEntity>().WithMany().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.CommunityId, x.IsCommunityMainFeed })
            .HasDatabaseName("ix_conversations_community_feed");

        builder.HasIndex(x => new { x.OrganizationId, x.DmKey })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("dm_key IS NOT NULL")
            .HasDatabaseName("ux_conversations_org_dm_key");

        builder.HasIndex(x => new { x.OrganizationId, x.UpdatedAt })
            .HasDatabaseName("ix_conversations_org_updated");
    }
}

public sealed class ConversationMemberEntityConfiguration : IEntityTypeConfiguration<ConversationMemberEntity>
{
    public void Configure(EntityTypeBuilder<ConversationMemberEntity> builder)
    {
        builder.ToTable("conversation_members");
        builder.HasKey(x => new { x.ConversationId, x.UserId });

        builder.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.LastReadSeq).HasColumnName("last_read_seq").IsRequired();
        builder.Property(x => x.VisibleFromSeq).HasColumnName("visible_from_seq").IsRequired();
        builder.Property(x => x.Muted).HasColumnName("muted").IsRequired();
        builder.Property(x => x.JoinedAt).HasColumnName("joined_at").IsRequired();
        builder.Property(x => x.LeftAt).HasColumnName("left_at");

        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.UserId, x.LeftAt })
            .HasDatabaseName("ix_conversation_members_user_active");
    }
}
