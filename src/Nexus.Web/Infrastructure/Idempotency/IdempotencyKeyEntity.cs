using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Nexus.Web.Infrastructure.Idempotency;

public sealed class IdempotencyKeyEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string HttpMethod { get; set; }
    public required string RouteTemplate { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public int? StatusCode { get; set; }
    public string? ResponseJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class IdempotencyKeyEntityConfiguration : IEntityTypeConfiguration<IdempotencyKeyEntity>
{
    public void Configure(EntityTypeBuilder<IdempotencyKeyEntity> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.HttpMethod).HasColumnName("http_method").HasMaxLength(16).IsRequired();
        builder.Property(x => x.RouteTemplate).HasColumnName("route_template").HasMaxLength(256).IsRequired();
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).IsRequired();
        builder.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.StatusCode).HasColumnName("status_code");
        builder.Property(x => x.ResponseJson).HasColumnName("response_json");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();

        builder.HasIndex(x => new { x.UserId, x.HttpMethod, x.RouteTemplate, x.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_idempotency_keys_scope");

        builder.HasIndex(x => x.ExpiresAt)
            .HasDatabaseName("ix_idempotency_keys_expires_at");
    }
}
