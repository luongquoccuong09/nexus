namespace Nexus.Web.Infrastructure.Security;

public enum AccountStatus
{
    Active,
    Disabled,
    Former
}

public enum TrustLevel
{
    Unverified,
    Verified
}

public enum OrgRole
{
    Member,
    CommsAdmin,
    HRAdmin,
    SystemAdmin
}

public enum ResourceRole
{
    Member,
    Admin,
    Owner
}

public enum ResourceKind
{
    Organization,
    UserAccount,
    AuditLog,
    Dm,
    GroupChat,
    SelfChat,
    Community,
    CommunityChannel,
    Monitor,
    CalendarEvent,
    Meeting
}

public enum Visibility
{
    Public,
    Private
}

public enum PostingPolicy
{
    Members,
    AdminsOnly
}

public enum ResourceAction
{
    DirectorySearch,
    DmOpen,
    DmBlockOrReport,
    GroupCreate,
    ConversationRead,
    MessageSend,
    MessageSearch,
    FileUpload,
    FileDownload,
    DeltaSync,
    GroupAddMember,
    GroupRemoveMember,
    GroupManageAdmins,
    GroupTransferOwnership,
    GroupLeave,
    CommunityCreate,
    CommunityDesignateOfficial,
    CommunityDiscoverAndJoin,
    ChannelCreate,
    CommunityManageSettings,
    MentionChannelOrAll,
    CommunityAddOrRemoveMember,
    CommunityManageAdmins,
    CommunityTransferOwnership,
    CommunityEmergencyTransferOwnership,
    AdminImportDirectory,
    AdminVerifyAccount,
    AdminDisableOrEnableAccount,
    AdminManageRoles,
    AdminViewAuditLog,
    ManageOwnBlockList,
    ManageOwnDmReadReceipts,
    MonitorRead,
    MonitorExecute,
    MessagePin,
    CalendarRead,
    CalendarCreate,
    CalendarEdit,
    CalendarRespond,
    MeetingJoin,
    MeetingManage
}

public sealed record ActorContext(
    Guid UserId,
    Guid OrganizationId,
    AccountStatus Status,
    TrustLevel TrustLevel,
    OrgRole OrgRole = OrgRole.Member)
{
    public bool CanMonitor { get; init; }
    public bool CanRunLoadTests { get; init; }
}

public sealed record ResourceContext(
    ResourceKind Kind,
    Guid OrganizationId = default,
    ResourceRole? MembershipRole = null,
    bool IsOfficial = false,
    Visibility Visibility = Visibility.Public,
    PostingPolicy PostingPolicy = PostingPolicy.Members,
    bool TargetIsActive = true,
    bool IsBlocked = false,
    bool DailyNewDmQuotaExceeded = false,
    bool TargetIsSelf = false,
    bool TargetIsOwner = false,
    bool TargetIsVerified = true,
    bool HasOtherMembers = false,
    bool IsHrAuditScope = false,
    bool IsArchived = false);
