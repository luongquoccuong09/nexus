using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Security;

namespace Nexus.Web.Modules.Identity;

public enum AvailabilityState
{
    Available,
    Busy,
    Away,
    DoNotDisturb
}

public sealed record AvailabilityDto(string State, DateTimeOffset? ExpiresAt);

public static class AvailabilityResolver
{
    public static AvailabilityState ResolveState(
        AvailabilityState storedState,
        DateTimeOffset? expiresAt,
        DateTimeOffset nowUtc) =>
        storedState == AvailabilityState.Available || (expiresAt is { } exp && exp <= nowUtc)
            ? AvailabilityState.Available
            : storedState;

    public static AvailabilityDto Resolve(
        AvailabilityState storedState,
        DateTimeOffset? expiresAt,
        DateTimeOffset nowUtc)
    {
        var effective = ResolveState(storedState, expiresAt, nowUtc);
        return effective == AvailabilityState.Available
            ? new AvailabilityDto(nameof(AvailabilityState.Available), null)
            : new AvailabilityDto(effective.ToString(), expiresAt);
    }

    public static bool IsChimeSuppressed(
        AvailabilityState storedState,
        DateTimeOffset? expiresAt,
        DateTimeOffset nowUtc) =>
        ResolveState(storedState, expiresAt, nowUtc) == AvailabilityState.DoNotDisturb;

    public static bool TryParseState(string? raw, out AvailabilityState state)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            state = AvailabilityState.Available;
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "available":
                state = AvailabilityState.Available;
                return true;
            case "busy":
                state = AvailabilityState.Busy;
                return true;
            case "away":
                state = AvailabilityState.Away;
                return true;
            case "donotdisturb":
                state = AvailabilityState.DoNotDisturb;
                return true;
            default:
                state = AvailabilityState.Available;
                return false;
        }
    }
}

public sealed class NexusUser : IdentityUser<Guid>
{
    public required string DisplayName { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public AvailabilityState AvailabilityState { get; set; } = AvailabilityState.Available;
    public DateTimeOffset? AvailabilityExpiresAt { get; set; }
    public bool DmReadReceiptsEnabled { get; set; } = true;
    public long DmReadReceiptsVersion { get; set; }
    public string? TermsVersion { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AdminPasswordResetLinkEntity
{
    public Guid Id { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid IssuedByUserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NexusUserConfiguration : IEntityTypeConfiguration<NexusUser>
{
    public void Configure(EntityTypeBuilder<NexusUser> builder)
    {
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint(
                "ck_users_availability_state",
                "availability_state IN ('Available', 'Busy', 'Away', 'DoNotDisturb')");
            table.HasCheckConstraint(
                "ck_users_availability_expires_at",
                "availability_state <> 'Available' OR availability_expires_at IS NULL");
        });

        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(80).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(x => x.AvailabilityState)
            .HasColumnName("availability_state")
            .HasColumnType("text")
            .HasConversion<string>()
            .HasDefaultValue(AvailabilityState.Available)
            .IsRequired();
        builder.Property(x => x.AvailabilityExpiresAt).HasColumnName("availability_expires_at");
        builder.Property(x => x.DmReadReceiptsEnabled).HasColumnName("dm_read_receipts_enabled").HasDefaultValue(true);
        builder.Property(x => x.DmReadReceiptsVersion).HasColumnName("dm_read_receipts_version").HasDefaultValue(0L);
        builder.Property(x => x.TermsVersion).HasColumnName("terms_version").HasMaxLength(32);
        builder.Property(x => x.TermsAcceptedAt).HasColumnName("terms_accepted_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
    }
}

public sealed class AdminPasswordResetLinkEntityConfiguration : IEntityTypeConfiguration<AdminPasswordResetLinkEntity>
{
    public void Configure(EntityTypeBuilder<AdminPasswordResetLinkEntity> builder)
    {
        builder.ToTable("admin_reset_links");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TargetUserId).HasColumnName("target_user_id").IsRequired();
        builder.Property(x => x.IssuedByUserId).HasColumnName("issued_by_user_id").IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.UsedAt).HasColumnName("used_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.IssuedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_admin_reset_links_token_hash");
    }
}
