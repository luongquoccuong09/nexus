using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Identity;

public sealed class NexusUser : IdentityUser<Guid>
{
    public Guid? OrganizationId { get; set; }
    public required string DisplayName { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public TrustLevel TrustLevel { get; set; } = TrustLevel.Unverified;
    public OrgRole OrgRole { get; set; } = OrgRole.Member;
    public Guid? EmployeeId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public string? CustomStatus { get; set; }
    public string? AvatarObjectKey { get; set; }
    public string? TermsVersion { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool RequiresMfa => OrgRole is OrgRole.SystemAdmin or OrgRole.HRAdmin or OrgRole.CommsAdmin;

    public ActorContext ToActorContext() =>
        new(Id, OrganizationId ?? Guid.Empty, Status, TrustLevel, OrgRole);
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
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(80).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(x => x.TrustLevel).HasColumnName("trust_level").HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(x => x.OrgRole).HasColumnName("org_role").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.EmployeeId).HasColumnName("employee_id");
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.CustomStatus).HasColumnName("custom_status").HasMaxLength(100);
        builder.Property(x => x.AvatarObjectKey).HasColumnName("avatar_object_key").HasMaxLength(256);
        builder.Property(x => x.TermsVersion).HasColumnName("terms_version").HasMaxLength(32);
        builder.Property(x => x.TermsAcceptedAt).HasColumnName("terms_accepted_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EmployeeEntity>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<DepartmentEntity>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<BranchEntity>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => x.EmployeeId)
            .IsUnique()
            .HasFilter("employee_id IS NOT NULL")
            .HasDatabaseName("ux_users_employee_id");

        builder.HasIndex(x => x.OrganizationId).HasDatabaseName("ix_users_organization_id");
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
