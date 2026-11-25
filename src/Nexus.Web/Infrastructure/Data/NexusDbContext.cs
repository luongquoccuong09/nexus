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
using Nexus.Web.Modules.Notifications;
using Nexus.Web.Modules.Calendar;
using Nexus.Web.Modules.Meetings;
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
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();

    public Guid OrganizationScopeId { get; set; }
    public bool IsPersonalScope { get; set; }
    public Guid? ConversationOrganizationId => IsPersonalScope ? null : OrganizationScopeId;
    public OrganizationMembership? OrganizationActor { get; set; }
    public IQueryable<OrganizationMembership> OrganizationPeople => IsPersonalScope
        ? Users.AsNoTracking().Select(account => new OrganizationMembership
        {
            Id = account.Id, OrganizationId = OrganizationScopeId, UserName = account.UserName!, DisplayName = account.DisplayName,
            Status = account.Status, TrustLevel = Security.TrustLevel.Unverified, OrgRole = Security.OrgRole.Member,
            AvatarObjectKey = null, CustomStatus = null
        })
        : OrganizationMemberships.Where(member => member.OrganizationId == OrganizationScopeId);

    public IQueryable<OrganizationMembership> ActiveScopePeople => IsPersonalScope
        ? OrganizationPeople.Where(member => member.Status == Security.AccountStatus.Active)
        : OrganizationPeople.Where(member => member.Status == Security.AccountStatus.Active && member.Account.Status == Security.AccountStatus.Active);

    public DbSet<PersonalConnection> PersonalConnections => Set<PersonalConnection>();
    public DbSet<PersonalContactLink> PersonalContactLinks => Set<PersonalContactLink>();

    public async Task<OrganizationMembership?> FindPersonalActorAsync(Guid userId, CancellationToken ct)
    {
        var account = await Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.Status == Security.AccountStatus.Active, ct);
        return account is null ? null : OrganizationMembership.Create(Guid.Empty, account, account.CreatedAt);
    }

    public Task<OrganizationMembership?> FindOrganizationActorAsync(Guid userId, CancellationToken ct = default) =>
        OrganizationActor is { } actor && actor.Id == userId
            ? Task.FromResult<OrganizationMembership?>(actor)
            : OrganizationPeople.SingleOrDefaultAsync(member => member.Id == userId && member.Status == Security.AccountStatus.Active &&
                member.Account.Status == Security.AccountStatus.Active, ct);
    public DbSet<DepartmentEntity> Departments => Set<DepartmentEntity>();
    public DbSet<BranchEntity> Branches => Set<BranchEntity>();
    public DbSet<EmployeeEntity> Employees => Set<EmployeeEntity>();

    public DbSet<AdminPasswordResetLinkEntity> AdminPasswordResetLinks => Set<AdminPasswordResetLinkEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();

    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<ConversationMemberEntity> ConversationMembers => Set<ConversationMemberEntity>();
    public DbSet<CommunityEntity> Communities => Set<CommunityEntity>();
    public DbSet<CommunityMemberEntity> CommunityMembers => Set<CommunityMemberEntity>();
    public DbSet<UserSidebarSectionEntity> UserSidebarSections => Set<UserSidebarSectionEntity>();
    public DbSet<UserSidebarItemEntity> UserSidebarItems => Set<UserSidebarItemEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<ThreadSubscriptionEntity> ThreadSubscriptions => Set<ThreadSubscriptionEntity>();
    public DbSet<MessageReactionEntity> MessageReactions => Set<MessageReactionEntity>();
    public DbSet<MessagePinEntity> MessagePins => Set<MessagePinEntity>();
    public DbSet<SavedMessageEntity> SavedMessages => Set<SavedMessageEntity>();
    public DbSet<ConversationEventEntity> ConversationEvents => Set<ConversationEventEntity>();
    public DbSet<ChannelPostEntity> ChannelPosts => Set<ChannelPostEntity>();
    public DbSet<PostCommentEntity> PostComments => Set<PostCommentEntity>();
    public DbSet<PostReactionEntity> PostReactions => Set<PostReactionEntity>();
    public DbSet<AnnouncementAcknowledgmentEntity> AnnouncementAcknowledgments => Set<AnnouncementAcknowledgmentEntity>();
    public DbSet<PollEntity> Polls => Set<PollEntity>();
    public DbSet<PollVoteEntity> PollVotes => Set<PollVoteEntity>();
    public DbSet<UserBlockEntity> UserBlocks => Set<UserBlockEntity>();
    public DbSet<UserReportEntity> UserReports => Set<UserReportEntity>();
    public DbSet<PeopleTagEntity> PeopleTags => Set<PeopleTagEntity>();
    public DbSet<PeopleTagMemberEntity> PeopleTagMembers => Set<PeopleTagMemberEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();
    public DbSet<SharedLinkEntity> SharedLinks => Set<SharedLinkEntity>();
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
    public DbSet<CalendarEventEntity> CalendarEvents => Set<CalendarEventEntity>();
    public DbSet<CalendarAttendeeEntity> CalendarAttendees => Set<CalendarAttendeeEntity>();
    public DbSet<MeetingEntity> Meetings => Set<MeetingEntity>();
    public DbSet<MeetingGrantEntity> MeetingGrants => Set<MeetingGrantEntity>();
    public DbSet<MeetingBanEntity> MeetingBans => Set<MeetingBanEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<ConversationNotificationPreference> ConversationNotificationPreferences => Set<ConversationNotificationPreference>();
    public DbSet<NotificationQuietHours> NotificationQuietHours => Set<NotificationQuietHours>();
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();
    public DbSet<EmailDigestOptIn> EmailDigestOptIns => Set<EmailDigestOptIn>();

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
