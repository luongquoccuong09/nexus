using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;

namespace Nexus.Web.Modules.Org;

public sealed class OrganizationMembership
{
    public Guid OrganizationId { get; set; }
    public Guid Id { get; set; }
    public OrganizationEntity Organization { get; set; } = null!;
    public NexusUser Account { get; set; } = null!;
    public required string UserName { get; set; }
    public required string DisplayName { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public OrgRole OrgRole { get; set; }
    public TrustLevel TrustLevel { get; set; }
    public bool IsOwner { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public string? CustomStatus { get; set; }
    public string? AvatarObjectKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool RequiresMfa => OrgRole is not OrgRole.Member;

    public ActorContext ToActorContext() => new(Id, OrganizationId, Status, TrustLevel, OrgRole);

    public static OrganizationMembership Create(Guid organizationId, NexusUser account, DateTimeOffset now) => new()
    {
        OrganizationId = organizationId,
        Id = account.Id,
        UserName = account.UserName!,
        DisplayName = account.DisplayName,
        CreatedAt = now
    };
}

public sealed class OrganizationInvitation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public Guid? RedeemedByUserId { get; set; }
}

internal sealed class OrganizationMembershipConfiguration : IEntityTypeConfiguration<OrganizationMembership>
{
    public void Configure(EntityTypeBuilder<OrganizationMembership> entity)
    {
        entity.ToTable("organization_memberships");
        entity.HasKey(m => new { m.OrganizationId, m.Id });
        entity.Property(m => m.OrganizationId).HasColumnName("organization_id");
        entity.Property(m => m.Id).HasColumnName("user_id");
        entity.Property(m => m.UserName).HasColumnName("username").HasMaxLength(256);
        entity.Property(m => m.DisplayName).HasColumnName("display_name").HasMaxLength(80);
        entity.Property(m => m.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24);
        entity.Property(m => m.OrgRole).HasColumnName("org_role").HasConversion<string>().HasMaxLength(32);
        entity.Property(m => m.TrustLevel).HasColumnName("trust_level").HasConversion<string>().HasMaxLength(24);
        entity.Property(m => m.IsOwner).HasColumnName("is_owner");
        entity.Property(m => m.EmployeeId).HasColumnName("employee_id");
        entity.Property(m => m.DepartmentId).HasColumnName("department_id");
        entity.Property(m => m.BranchId).HasColumnName("branch_id");
        entity.Property(m => m.CustomStatus).HasColumnName("custom_status").HasMaxLength(100);
        entity.Property(m => m.AvatarObjectKey).HasColumnName("avatar_object_key").HasMaxLength(256);
        entity.Property(m => m.CreatedAt).HasColumnName("created_at");
        entity.HasOne(m => m.Organization).WithMany().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(m => m.Account).WithMany().HasForeignKey(m => m.Id).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<EmployeeEntity>().WithMany().HasForeignKey(m => m.EmployeeId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<DepartmentEntity>().WithMany().HasForeignKey(m => m.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<BranchEntity>().WithMany().HasForeignKey(m => m.BranchId).OnDelete(DeleteBehavior.SetNull);
        entity.HasIndex(m => new { m.Id, m.Status });
        entity.HasIndex(m => m.OrganizationId).IsUnique().HasFilter("is_owner");
        entity.HasIndex(m => m.EmployeeId).IsUnique();
        entity.Ignore(m => m.RequiresMfa);
    }
}

internal sealed class OrganizationInvitationConfiguration : IEntityTypeConfiguration<OrganizationInvitation>
{
    public void Configure(EntityTypeBuilder<OrganizationInvitation> entity)
    {
        entity.ToTable("organization_invitations");
        entity.HasKey(i => i.Id);
        entity.Property(i => i.Id).HasColumnName("id");
        entity.Property(i => i.OrganizationId).HasColumnName("organization_id");
        entity.Property(i => i.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(i => i.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
        entity.Property(i => i.CreatedAt).HasColumnName("created_at");
        entity.Property(i => i.ExpiresAt).HasColumnName("expires_at");
        entity.Property(i => i.RevokedAt).HasColumnName("revoked_at");
        entity.Property(i => i.RedeemedAt).HasColumnName("redeemed_at");
        entity.Property(i => i.RedeemedByUserId).HasColumnName("redeemed_by_user_id");
        entity.HasOne<OrganizationEntity>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<NexusUser>().WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<NexusUser>().WithMany().HasForeignKey(i => i.RedeemedByUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(i => i.TokenHash).IsUnique();
        entity.HasIndex(i => new { i.OrganizationId, i.CreatedAt });
    }
}
