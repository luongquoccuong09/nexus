using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Idempotency;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;
using Nexus.Web.Modules.People;

namespace Nexus.Web.Infrastructure.Data;

public sealed class NexusDbContext
    : IdentityDbContext<NexusUser, IdentityRole<Guid>, Guid>, IDataProtectionKeyContext
{
    public NexusDbContext(DbContextOptions<NexusDbContext> options)
        : base(options)
    {
    }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<AppSessionEntity> AppSessions => Set<AppSessionEntity>();
    public DbSet<IdempotencyKeyEntity> IdempotencyKeys => Set<IdempotencyKeyEntity>();

    public DbSet<OrganizationEntity> Organizations => Set<OrganizationEntity>();
    public DbSet<DepartmentEntity> Departments => Set<DepartmentEntity>();
    public DbSet<BranchEntity> Branches => Set<BranchEntity>();
    public DbSet<EmployeeEntity> Employees => Set<EmployeeEntity>();

    public DbSet<AdminPasswordResetLinkEntity> AdminPasswordResetLinks => Set<AdminPasswordResetLinkEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();

    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<ConversationMemberEntity> ConversationMembers => Set<ConversationMemberEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<ConversationEventEntity> ConversationEvents => Set<ConversationEventEntity>();
    public DbSet<UserBlockEntity> UserBlocks => Set<UserBlockEntity>();
    public DbSet<UserReportEntity> UserReports => Set<UserReportEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();

    [DbFunction("immutable_unaccent", Schema = "public")]
    public static string ImmutableUnaccent(string input) =>
        throw new NotSupportedException("ImmutableUnaccent is translated to PostgreSQL SQL.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<NexusUser>().ToTable("users");
        modelBuilder.Entity<IdentityRole<Guid>>().ToTable("roles");
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("data_protection_keys");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.FriendlyName).HasColumnName("friendly_name");
            entity.Property(x => x.Xml).HasColumnName("xml");
        });

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NexusDbContext).Assembly);
    }
}
