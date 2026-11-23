using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Nexus.Web.Modules.Org;

public sealed class OrganizationEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DepartmentEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BranchEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public required string Code { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EmployeeEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string EmployeeCode { get; set; }
    public required string FullName { get; set; }
    public string? Email { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OrganizationEntityConfiguration : IEntityTypeConfiguration<OrganizationEntity>
{
    public void Configure(EntityTypeBuilder<OrganizationEntity> builder)
    {
        builder.ToTable("organizations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        builder.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(64).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_organizations_slug");
    }
}

public sealed class DepartmentEntityConfiguration : IEntityTypeConfiguration<DepartmentEntity>
{
    public void Configure(EntityTypeBuilder<DepartmentEntity> builder)
    {
        builder.ToTable("departments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_departments_org_code");
    }
}

public sealed class BranchEntityConfiguration : IEntityTypeConfiguration<BranchEntity>
{
    public void Configure(EntityTypeBuilder<BranchEntity> builder)
    {
        builder.ToTable("branches");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_branches_org_code");
    }
}

public sealed class EmployeeEntityConfiguration : IEntityTypeConfiguration<EmployeeEntity>
{
    public void Configure(EntityTypeBuilder<EmployeeEntity> builder)
    {
        builder.ToTable("employees");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.EmployeeCode).HasColumnName("employee_code").HasMaxLength(64).IsRequired();
        builder.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(160).IsRequired();
        builder.Property(x => x.Email).HasColumnName("email").HasMaxLength(256);
        builder.Property(x => x.DepartmentId).HasColumnName("department_id");
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DepartmentEntity>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<BranchEntity>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.OrganizationId, x.EmployeeCode }).IsUnique().HasDatabaseName("ux_employees_org_code");
    }
}
