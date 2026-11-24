using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Conversations;

public sealed class UserSidebarSectionEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Surface { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UserSidebarItemEntity
{
    public Guid UserId { get; set; }
    public required string Surface { get; set; }
    public Guid TargetId { get; set; }
    public bool IsFavorite { get; set; }
    public Guid? SectionId { get; set; }
    public bool IsHidden { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UserSidebarSectionEntityConfiguration : IEntityTypeConfiguration<UserSidebarSectionEntity>
{
    public void Configure(EntityTypeBuilder<UserSidebarSectionEntity> builder)
    {
        builder.ToTable("user_sidebar_sections");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Surface).HasColumnName("surface").HasMaxLength(16).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne<NexusUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.UserId, x.Surface, x.SortOrder, x.Id })
            .HasDatabaseName("ix_user_sidebar_sections_user_surface_order");
    }
}

public sealed class UserSidebarItemEntityConfiguration : IEntityTypeConfiguration<UserSidebarItemEntity>
{
    public void Configure(EntityTypeBuilder<UserSidebarItemEntity> builder)
    {
        builder.ToTable("user_sidebar_items");
        builder.HasKey(x => new { x.UserId, x.Surface, x.TargetId });

        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Surface).HasColumnName("surface").HasMaxLength(16).IsRequired();
        builder.Property(x => x.TargetId).HasColumnName("target_id").IsRequired();
        builder.Property(x => x.IsFavorite).HasColumnName("is_favorite").IsRequired();
        builder.Property(x => x.SectionId).HasColumnName("section_id");
        builder.Property(x => x.IsHidden).HasColumnName("is_hidden").IsRequired();
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne<NexusUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<UserSidebarSectionEntity>()
            .WithMany()
            .HasForeignKey(x => x.SectionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => x.SectionId)
            .HasDatabaseName("ix_user_sidebar_items_section_id");

        builder.HasIndex(x => new { x.UserId, x.Surface, x.IsFavorite, x.SortOrder })
            .HasDatabaseName("ix_user_sidebar_items_user_surface_fav_order");
    }
}
