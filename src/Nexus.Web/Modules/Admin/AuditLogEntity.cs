using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Nexus.Web.Modules.Admin;

public sealed class AuditLogEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string ResourceType { get; set; }
    public required string ResourceId { get; set; }
    public required string Result { get; set; }
    public string? DetailJson { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AuditLogEntityConfiguration : IEntityTypeConfiguration<AuditLogEntity>
{
    public void Configure(EntityTypeBuilder<AuditLogEntity> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(x => x.Action).HasColumnName("action").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResourceType).HasColumnName("resource_type").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResourceId).HasColumnName("resource_id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Result).HasColumnName("result").HasMaxLength(32).IsRequired();
        builder.Property(x => x.DetailJson).HasColumnName("detail_json");
        builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_audit_logs_created_at");
        builder.HasIndex(x => x.ActorUserId).HasDatabaseName("ix_audit_logs_actor_user_id");
        builder.HasIndex(x => new { x.Action, x.CreatedAt }).HasDatabaseName("ix_audit_logs_action_created");
    }
}
