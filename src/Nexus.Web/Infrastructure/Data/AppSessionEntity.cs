using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Nexus.Web.Infrastructure.Data;

public sealed class AppSessionEntity
{
    public required string Id { get; set; }
    public Guid? UserId { get; set; }
    public required byte[] TicketBytes { get; set; }
    public string? SecurityStamp { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AppSessionEntityConfiguration : IEntityTypeConfiguration<AppSessionEntity>
{
    public void Configure(EntityTypeBuilder<AppSessionEntity> builder)
    {
        builder.ToTable("app_sessions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").HasMaxLength(128).IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.TicketBytes).HasColumnName("ticket_bytes").IsRequired();
        builder.Property(x => x.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(128);
        builder.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        builder.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.LastActivityAt).HasColumnName("last_activity_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at");

        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_app_sessions_user_id");
        builder.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_app_sessions_expires_at");
    }
}
