using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.People;

public sealed class UserBlockEntity
{
    public Guid BlockerUserId { get; set; }
    public Guid BlockedUserId { get; set; }
    public Guid OrganizationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UserReportEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReporterUserId { get; set; }
    public Guid ReportedUserId { get; set; }
    public Guid? ConversationId { get; set; }
    public required string Reason { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class UserBlockEntityConfiguration : IEntityTypeConfiguration<UserBlockEntity>
{
    public void Configure(EntityTypeBuilder<UserBlockEntity> builder)
    {
        builder.ToTable("user_blocks");
        builder.HasKey(x => new { x.BlockerUserId, x.BlockedUserId });

        builder.Property(x => x.BlockerUserId).HasColumnName("blocker_user_id").IsRequired();
        builder.Property(x => x.BlockedUserId).HasColumnName("blocked_user_id").IsRequired();
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.BlockerUserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.BlockedUserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UserReportEntityConfiguration : IEntityTypeConfiguration<UserReportEntity>
{
    public void Configure(EntityTypeBuilder<UserReportEntity> builder)
    {
        builder.ToTable("user_reports");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.ReporterUserId).HasColumnName("reporter_user_id").IsRequired();
        builder.Property(x => x.ReportedUserId).HasColumnName("reported_user_id").IsRequired();
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id");
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Details).HasColumnName("details").HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.ReporterUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.ReportedUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_user_reports_created_at");
    }
}
