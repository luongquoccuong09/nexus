using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Conversations;

public sealed class CommunityEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Visibility Visibility { get; set; } = Visibility.Public;
    public bool IsOfficial { get; set; }
    public Guid? MappedDepartmentId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CommunityMemberEntity
{
    public Guid CommunityId { get; set; }
    public Guid UserId { get; set; }
    public ResourceRole Role { get; set; } = ResourceRole.Member;
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

public sealed class CommunityEntityConfiguration : IEntityTypeConfiguration<CommunityEntity>
{
    public void Configure(EntityTypeBuilder<CommunityEntity> builder)
    {
        builder.ToTable("communities");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(x => x.Visibility).HasColumnName("visibility").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.IsOfficial).HasColumnName("is_official").IsRequired();
        builder.Property(x => x.MappedDepartmentId).HasColumnName("mapped_department_id");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DepartmentEntity>().WithMany().HasForeignKey(x => x.MappedDepartmentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.OrganizationId, x.Visibility, x.Name })
            .HasDatabaseName("ix_communities_org_visibility_name");
    }
}

public sealed class CommunityMemberEntityConfiguration : IEntityTypeConfiguration<CommunityMemberEntity>
{
    public void Configure(EntityTypeBuilder<CommunityMemberEntity> builder)
    {
        builder.ToTable("community_members");
        builder.HasKey(x => new { x.CommunityId, x.UserId });

        builder.Property(x => x.CommunityId).HasColumnName("community_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.JoinedAt).HasColumnName("joined_at").IsRequired();
        builder.Property(x => x.LeftAt).HasColumnName("left_at");

        builder.HasOne<CommunityEntity>().WithMany().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.UserId, x.LeftAt })
            .HasDatabaseName("ix_community_members_user_active");
    }
}
