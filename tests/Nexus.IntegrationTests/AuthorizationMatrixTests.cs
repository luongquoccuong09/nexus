using Nexus.Web.Infrastructure.Security;

namespace Nexus.IntegrationTests;

public sealed class AuthorizationMatrixTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly ActorContext VerifiedMember = new(Guid.NewGuid(), OrgA, AccountStatus.Active, TrustLevel.Verified, OrgRole.Member);
    private static readonly ActorContext UnverifiedMember = new(Guid.NewGuid(), OrgA, AccountStatus.Active, TrustLevel.Unverified, OrgRole.Member);
    private static readonly ActorContext DisabledVerified = new(Guid.NewGuid(), OrgA, AccountStatus.Disabled, TrustLevel.Verified, OrgRole.SystemAdmin);
    private static readonly ActorContext FormerVerified = new(Guid.NewGuid(), OrgA, AccountStatus.Former, TrustLevel.Verified, OrgRole.Member);
    private static readonly ActorContext VerifiedCommsAdmin = new(Guid.NewGuid(), OrgA, AccountStatus.Active, TrustLevel.Verified, OrgRole.CommsAdmin);
    private static readonly ActorContext UnverifiedCommsAdmin = new(Guid.NewGuid(), OrgA, AccountStatus.Active, TrustLevel.Unverified, OrgRole.CommsAdmin);
    private static readonly ActorContext VerifiedHrAdmin = new(Guid.NewGuid(), OrgA, AccountStatus.Active, TrustLevel.Verified, OrgRole.HRAdmin);
    private static readonly ActorContext VerifiedSystemAdmin = new(Guid.NewGuid(), OrgA, AccountStatus.Active, TrustLevel.Verified, OrgRole.SystemAdmin);

    private readonly NexusAuthorizationService _authz = new();

    public static IEnumerable<object?[]> MatrixCases()
    {
        // --- Account status & boundary checks ---
        yield return ["Anonymous cannot read DM", null, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.Dm, OrgA, ResourceRole.Member), false];
        yield return ["Disabled SystemAdmin cannot read DM", DisabledVerified, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.Dm, OrgA, ResourceRole.Member), false];
        yield return ["Disabled SystemAdmin cannot disable accounts", DisabledVerified, ResourceAction.AdminDisableOrEnableAccount, new ResourceContext(ResourceKind.UserAccount, OrgA), false];
        yield return ["Former user denied conversation read", FormerVerified, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.GroupChat, OrgA, ResourceRole.Member), false];
        yield return ["Cross-organization resource denied", VerifiedMember, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.Dm, OrgB, ResourceRole.Member), false];

        // --- Directory & DMs (P0-D01, SEC-20b) ---
        yield return ["Verified member searches directory", VerifiedMember, ResourceAction.DirectorySearch, new ResourceContext(ResourceKind.Organization, OrgA), true];
        yield return ["Unverified member searches trial directory", UnverifiedMember, ResourceAction.DirectorySearch, new ResourceContext(ResourceKind.Organization, OrgA), true];
        yield return ["Verified opens DM with active colleague", VerifiedMember, ResourceAction.DmOpen, new ResourceContext(ResourceKind.Dm, OrgA, TargetIsActive: true), true];
        yield return ["Verified cannot open DM when blocked", VerifiedMember, ResourceAction.DmOpen, new ResourceContext(ResourceKind.Dm, OrgA, TargetIsActive: true, IsBlocked: true), false];
        yield return ["Verified cannot open DM with inactive user", VerifiedMember, ResourceAction.DmOpen, new ResourceContext(ResourceKind.Dm, OrgA, TargetIsActive: false), false];
        yield return ["Verified cannot open DM with self", VerifiedMember, ResourceAction.DmOpen, new ResourceContext(ResourceKind.Dm, OrgA, TargetIsSelf: true), false];
        yield return ["Unverified opens DM within daily quota", UnverifiedMember, ResourceAction.DmOpen, new ResourceContext(ResourceKind.Dm, OrgA, DailyNewDmQuotaExceeded: false), true];
        yield return ["Unverified denied DM when daily quota exceeded (SEC-20b)", UnverifiedMember, ResourceAction.DmOpen, new ResourceContext(ResourceKind.Dm, OrgA, DailyNewDmQuotaExceeded: true), false];
        yield return ["Active member can block/report active peer", VerifiedMember, ResourceAction.DmBlockOrReport, new ResourceContext(ResourceKind.Dm, OrgA, TargetIsActive: true), true];

        // --- DM & GroupChat content isolation (SEC-03, SEC-05, SEC-15, SEC-18) ---
        yield return ["DM member can read", VerifiedMember, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.Dm, OrgA, ResourceRole.Member), true];
        yield return ["DM member can send when unblocked", VerifiedMember, ResourceAction.MessageSend, new ResourceContext(ResourceKind.Dm, OrgA, ResourceRole.Member, IsBlocked: false), true];
        yield return ["DM member cannot send when blocked", VerifiedMember, ResourceAction.MessageSend, new ResourceContext(ResourceKind.Dm, OrgA, ResourceRole.Member, IsBlocked: true), false];
        yield return ["Nonmember SystemAdmin denied DM read (SEC-03)", VerifiedSystemAdmin, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.Dm, OrgA, MembershipRole: null), false];
        yield return ["Nonmember HRAdmin denied group search (SEC-15)", VerifiedHrAdmin, ResourceAction.MessageSearch, new ResourceContext(ResourceKind.GroupChat, OrgA, MembershipRole: null), false];
        yield return ["Removed group member denied delta sync (SEC-18)", VerifiedMember, ResourceAction.DeltaSync, new ResourceContext(ResourceKind.GroupChat, OrgA, MembershipRole: null), false];
        yield return ["Removed group member denied file download (SEC-18)", VerifiedMember, ResourceAction.FileDownload, new ResourceContext(ResourceKind.GroupChat, OrgA, MembershipRole: null), false];
        yield return ["Nonmember denied group send (SEC-05)", VerifiedMember, ResourceAction.MessageSend, new ResourceContext(ResourceKind.GroupChat, OrgA, MembershipRole: null), false];

        // --- Group management ---
        yield return ["Group Owner can add member", VerifiedMember, ResourceAction.GroupAddMember, new ResourceContext(ResourceKind.GroupChat, OrgA, ResourceRole.Owner), true];
        yield return ["Group Admin can remove ordinary member", VerifiedMember, ResourceAction.GroupRemoveMember, new ResourceContext(ResourceKind.GroupChat, OrgA, ResourceRole.Admin, TargetIsOwner: false), true];
        yield return ["Group Admin cannot remove Owner", VerifiedMember, ResourceAction.GroupRemoveMember, new ResourceContext(ResourceKind.GroupChat, OrgA, ResourceRole.Admin, TargetIsOwner: true), false];
        yield return ["Group Member cannot add member", VerifiedMember, ResourceAction.GroupAddMember, new ResourceContext(ResourceKind.GroupChat, OrgA, ResourceRole.Member), false];
        yield return ["Group Owner cannot leave while other members exist", VerifiedMember, ResourceAction.GroupLeave, new ResourceContext(ResourceKind.GroupChat, OrgA, ResourceRole.Owner, HasOtherMembers: true), false];
        yield return ["Group Owner can leave when sole member", VerifiedMember, ResourceAction.GroupLeave, new ResourceContext(ResourceKind.GroupChat, OrgA, ResourceRole.Owner, HasOtherMembers: false), true];
        yield return ["Group Member can leave", VerifiedMember, ResourceAction.GroupLeave, new ResourceContext(ResourceKind.GroupChat, OrgA, ResourceRole.Member, HasOtherMembers: true), true];

        // --- Teams-style Communities & optional Sub-channels (P0-D04) ---
        yield return ["Unverified member can create social community", UnverifiedMember, ResourceAction.CommunityCreate, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: false), true];
        yield return ["Unverified member cannot create official community (SEC-20b)", UnverifiedMember, ResourceAction.CommunityCreate, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: true), false];
        yield return ["Verified CommsAdmin can create official community", VerifiedCommsAdmin, ResourceAction.CommunityCreate, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: true), true];
        yield return ["Unverified CommsAdmin denied official designation (SEC-20b)", UnverifiedCommsAdmin, ResourceAction.CommunityDesignateOfficial, new ResourceContext(ResourceKind.Community, OrgA), false];
        yield return ["Verified CommsAdmin can designate official community", VerifiedCommsAdmin, ResourceAction.CommunityDesignateOfficial, new ResourceContext(ResourceKind.Community, OrgA), true];
        yield return ["Ordinary member denied official designation", VerifiedMember, ResourceAction.CommunityDesignateOfficial, new ResourceContext(ResourceKind.Community, OrgA), false];

        yield return ["Unverified can join public social community", UnverifiedMember, ResourceAction.CommunityDiscoverAndJoin, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: false, Visibility: Visibility.Public), true];
        yield return ["Unverified denied joining public official community (SEC-20b)", UnverifiedMember, ResourceAction.CommunityDiscoverAndJoin, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: true, Visibility: Visibility.Public), false];
        yield return ["Verified can join public official community", VerifiedMember, ResourceAction.CommunityDiscoverAndJoin, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: true, Visibility: Visibility.Public), true];
        yield return ["Verified cannot self-join private community", VerifiedMember, ResourceAction.CommunityDiscoverAndJoin, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: false, Visibility: Visibility.Private), false];

        yield return ["Community Owner can add sub-channel", VerifiedMember, ResourceAction.ChannelCreate, new ResourceContext(ResourceKind.Community, OrgA, ResourceRole.Owner), true];
        yield return ["Community Admin can add sub-channel", VerifiedMember, ResourceAction.ChannelCreate, new ResourceContext(ResourceKind.Community, OrgA, ResourceRole.Admin), true];
        yield return ["Community Member denied adding sub-channel", VerifiedMember, ResourceAction.ChannelCreate, new ResourceContext(ResourceKind.Community, OrgA, ResourceRole.Member), false];
        yield return ["Admin of another community (nonmember) denied adding sub-channel", VerifiedMember, ResourceAction.ChannelCreate, new ResourceContext(ResourceKind.Community, OrgA, MembershipRole: null), false];

        yield return ["Community member reads main community feed", VerifiedMember, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.Community, OrgA, ResourceRole.Member), true];
        yield return ["Community member reads inherited sub-channel", VerifiedMember, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.CommunityChannel, OrgA, ResourceRole.Member), true];
        yield return ["Nonmember SystemAdmin denied reading community sub-channel", VerifiedSystemAdmin, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.CommunityChannel, OrgA, MembershipRole: null), false];
        yield return ["Unverified member denied reading official community channel", UnverifiedMember, ResourceAction.ConversationRead, new ResourceContext(ResourceKind.CommunityChannel, OrgA, ResourceRole.Member, IsOfficial: true), false];

        yield return ["Ordinary member denied posting in admins_only channel", VerifiedMember, ResourceAction.MessageSend, new ResourceContext(ResourceKind.CommunityChannel, OrgA, ResourceRole.Member, PostingPolicy: PostingPolicy.AdminsOnly), false];
        yield return ["Community Admin allowed posting in admins_only channel", VerifiedMember, ResourceAction.MessageSend, new ResourceContext(ResourceKind.CommunityChannel, OrgA, ResourceRole.Admin, PostingPolicy: PostingPolicy.AdminsOnly), true];
        yield return ["Unverified Admin denied posting in official admins_only community", UnverifiedMember, ResourceAction.MessageSend, new ResourceContext(ResourceKind.Community, OrgA, ResourceRole.Admin, IsOfficial: true, PostingPolicy: PostingPolicy.AdminsOnly), false];

        yield return ["Verified Community Admin can use @channel/@all", VerifiedMember, ResourceAction.MentionChannelOrAll, new ResourceContext(ResourceKind.CommunityChannel, OrgA, ResourceRole.Admin), true];
        yield return ["Unverified Community Owner denied @channel/@all (SEC-20b)", UnverifiedMember, ResourceAction.MentionChannelOrAll, new ResourceContext(ResourceKind.CommunityChannel, OrgA, ResourceRole.Owner), false];
        yield return ["Verified ordinary member denied @channel/@all", VerifiedMember, ResourceAction.MentionChannelOrAll, new ResourceContext(ResourceKind.CommunityChannel, OrgA, ResourceRole.Member), false];

        yield return ["SystemAdmin allowed emergency ownership transfer", VerifiedSystemAdmin, ResourceAction.CommunityEmergencyTransferOwnership, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: false), true];
        yield return ["CommsAdmin allowed emergency transfer for Official community", VerifiedCommsAdmin, ResourceAction.CommunityEmergencyTransferOwnership, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: true), true];
        yield return ["CommsAdmin denied emergency transfer for Social community", VerifiedCommsAdmin, ResourceAction.CommunityEmergencyTransferOwnership, new ResourceContext(ResourceKind.Community, OrgA, IsOfficial: false), false];

        // --- Organization Administration ---
        yield return ["HRAdmin can import directory", VerifiedHrAdmin, ResourceAction.AdminImportDirectory, new ResourceContext(ResourceKind.Organization, OrgA), true];
        yield return ["SystemAdmin denied HR directory import", VerifiedSystemAdmin, ResourceAction.AdminImportDirectory, new ResourceContext(ResourceKind.Organization, OrgA), false];
        yield return ["HRAdmin can verify account", VerifiedHrAdmin, ResourceAction.AdminVerifyAccount, new ResourceContext(ResourceKind.UserAccount, OrgA), true];
        yield return ["SystemAdmin can disable another account", VerifiedSystemAdmin, ResourceAction.AdminDisableOrEnableAccount, new ResourceContext(ResourceKind.UserAccount, OrgA, TargetIsSelf: false), true];
        yield return ["SystemAdmin cannot disable self", VerifiedSystemAdmin, ResourceAction.AdminDisableOrEnableAccount, new ResourceContext(ResourceKind.UserAccount, OrgA, TargetIsSelf: true), false];
        yield return ["HRAdmin denied disabling account", VerifiedHrAdmin, ResourceAction.AdminDisableOrEnableAccount, new ResourceContext(ResourceKind.UserAccount, OrgA, TargetIsSelf: false), false];
        yield return ["SystemAdmin can view full audit log", VerifiedSystemAdmin, ResourceAction.AdminViewAuditLog, new ResourceContext(ResourceKind.AuditLog, OrgA, IsHrAuditScope: false), true];
        yield return ["HRAdmin can view HR-scoped audit log", VerifiedHrAdmin, ResourceAction.AdminViewAuditLog, new ResourceContext(ResourceKind.AuditLog, OrgA, IsHrAuditScope: true), true];
        yield return ["HRAdmin denied non-HR audit log", VerifiedHrAdmin, ResourceAction.AdminViewAuditLog, new ResourceContext(ResourceKind.AuditLog, OrgA, IsHrAuditScope: false), false];

        // --- Default deny on unmapped / mismatched combinations ---
        yield return ["Default deny: unknown action enum value", VerifiedSystemAdmin, (ResourceAction)9999, new ResourceContext(ResourceKind.Organization, OrgA), false];
        yield return ["Default deny: AdminImportDirectory against Dm resource", VerifiedHrAdmin, ResourceAction.AdminImportDirectory, new ResourceContext(ResourceKind.Dm, OrgA, ResourceRole.Owner), false];
    }

    [Theory]
    [MemberData(nameof(MatrixCases))]
    public void Can_MatchesSecurityMatrix(
        string scenario,
        ActorContext? actor,
        ResourceAction action,
        ResourceContext resource,
        bool expected)
    {
        var actual = _authz.Can(actor, action, resource);
        Assert.True(actual == expected, $"Scenario '{scenario}' expected {expected} but got {actual}.");
    }
}
