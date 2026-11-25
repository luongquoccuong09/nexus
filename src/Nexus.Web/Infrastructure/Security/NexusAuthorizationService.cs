namespace Nexus.Web.Infrastructure.Security;

/// <summary>
/// Centralized deny-by-default resource authorization service implementing the P0.2 matrix
/// documented in docs/SECURITY_AND_AUTHZ.md.
/// </summary>
public sealed class NexusAuthorizationService : IAuthorizationService
{
    public bool Can(ActorContext? actor, ResourceAction action, ResourceContext resource)
    {
        if (actor is null || actor.Status != AccountStatus.Active)
        {
            return false;
        }

        if (resource.OrganizationId != Guid.Empty && resource.OrganizationId != actor.OrganizationId)
        {
            return false;
        }

        return action switch
        {
            ResourceAction.MonitorRead => resource.Kind == ResourceKind.Monitor &&
                resource.OrganizationId == Guid.Empty && actor.CanMonitor,
            ResourceAction.MonitorExecute => resource.Kind == ResourceKind.Monitor &&
                resource.OrganizationId == Guid.Empty && actor.CanMonitor && actor.CanRunLoadTests,

            ResourceAction.DirectorySearch =>
                resource.Kind == ResourceKind.Organization,

            ResourceAction.CalendarRead or ResourceAction.CalendarCreate =>
                CanAccessCalendar(actor, resource),
            ResourceAction.CalendarEdit =>
                CanAccessCalendar(actor, resource) && resource.TargetIsOwner,
            ResourceAction.CalendarRespond =>
                CanAccessCalendar(actor, resource) && resource.TargetIsSelf,

            ResourceAction.DmOpen =>
                resource.Kind == ResourceKind.Dm &&
                resource.TargetIsActive &&
                !resource.TargetIsSelf &&
                !resource.IsBlocked &&
                (actor.TrustLevel == TrustLevel.Verified || !resource.DailyNewDmQuotaExceeded),

            ResourceAction.DmBlockOrReport =>
                resource.Kind == ResourceKind.Dm &&
                resource.TargetIsActive &&
                !resource.TargetIsSelf,

            ResourceAction.ManageOwnBlockList or ResourceAction.ManageOwnDmReadReceipts =>
                resource.Kind == ResourceKind.UserAccount && resource.TargetIsSelf,

            ResourceAction.GroupCreate =>
                resource.Kind == ResourceKind.GroupChat,

            ResourceAction.ConversationRead or
            ResourceAction.MessageSearch or
            ResourceAction.FileDownload or
            ResourceAction.DeltaSync =>
                CanReadConversation(actor, resource),

            ResourceAction.MessageSend or
            ResourceAction.FileUpload =>
                CanWriteConversation(actor, resource),

            ResourceAction.MessagePin =>
                resource.Kind is ResourceKind.Dm or ResourceKind.GroupChat or ResourceKind.SelfChat &&
                CanWriteConversation(actor, resource) &&
                (resource.Kind != ResourceKind.GroupChat || IsResourceManager(resource.MembershipRole)),

            ResourceAction.GroupAddMember or
            ResourceAction.GroupManageAdmins =>
                resource.Kind == ResourceKind.GroupChat &&
                IsResourceManager(resource.MembershipRole),

            ResourceAction.GroupRemoveMember =>
                resource.Kind == ResourceKind.GroupChat &&
                IsResourceManager(resource.MembershipRole) &&
                !resource.TargetIsOwner &&
                !resource.TargetIsSelf,

            ResourceAction.GroupTransferOwnership =>
                resource.Kind == ResourceKind.GroupChat &&
                resource.MembershipRole == ResourceRole.Owner &&
                !resource.TargetIsSelf &&
                resource.TargetIsActive,

            ResourceAction.GroupLeave =>
                resource.Kind == ResourceKind.GroupChat &&
                resource.MembershipRole is not null &&
                (resource.MembershipRole != ResourceRole.Owner || !resource.HasOtherMembers),

            ResourceAction.CommunityCreate =>
                resource.Kind == ResourceKind.Community &&
                (!resource.IsOfficial || IsVerifiedRole(actor, OrgRole.CommsAdmin)),

            ResourceAction.CommunityDesignateOfficial =>
                resource.Kind == ResourceKind.Community &&
                IsVerifiedRole(actor, OrgRole.CommsAdmin),

            ResourceAction.CommunityDiscoverAndJoin =>
                resource.Kind == ResourceKind.Community &&
                resource.Visibility == Visibility.Public &&
                (!resource.IsOfficial || actor.TrustLevel == TrustLevel.Verified),

            ResourceAction.ChannelCreate or
            ResourceAction.CommunityManageSettings =>
                resource.Kind is ResourceKind.Community or ResourceKind.CommunityChannel &&
                IsResourceManager(resource.MembershipRole) &&
                (!resource.IsOfficial || actor.TrustLevel == TrustLevel.Verified),

            ResourceAction.MentionChannelOrAll =>
                resource.Kind is ResourceKind.Community or ResourceKind.CommunityChannel or ResourceKind.GroupChat &&
                actor.TrustLevel == TrustLevel.Verified &&
                IsResourceManager(resource.MembershipRole),

            ResourceAction.CommunityAddOrRemoveMember =>
                resource.Kind == ResourceKind.Community &&
                IsResourceManager(resource.MembershipRole) &&
                !resource.TargetIsOwner &&
                (!resource.IsOfficial || (actor.TrustLevel == TrustLevel.Verified && resource.TargetIsVerified)),

            ResourceAction.CommunityManageAdmins or
            ResourceAction.CommunityTransferOwnership =>
                resource.Kind == ResourceKind.Community &&
                resource.MembershipRole == ResourceRole.Owner &&
                !resource.TargetIsSelf &&
                resource.TargetIsActive &&
                (!resource.IsOfficial || (actor.TrustLevel == TrustLevel.Verified && resource.TargetIsVerified)),

            ResourceAction.CommunityEmergencyTransferOwnership =>
                resource.Kind == ResourceKind.Community &&
                actor.TrustLevel == TrustLevel.Verified &&
                (actor.OrgRole == OrgRole.SystemAdmin ||
                 (actor.OrgRole == OrgRole.CommsAdmin && resource.IsOfficial)),

            ResourceAction.AdminImportDirectory =>
                resource.Kind == ResourceKind.Organization &&
                IsVerifiedRole(actor, OrgRole.HRAdmin),

            ResourceAction.AdminVerifyAccount =>
                resource.Kind == ResourceKind.UserAccount &&
                actor.TrustLevel == TrustLevel.Verified &&
                actor.OrgRole is OrgRole.SystemAdmin or OrgRole.HRAdmin,

            ResourceAction.AdminDisableOrEnableAccount =>
                resource.Kind == ResourceKind.UserAccount &&
                IsVerifiedRole(actor, OrgRole.SystemAdmin) &&
                !resource.TargetIsSelf,

            ResourceAction.AdminManageRoles =>
                resource.Kind == ResourceKind.UserAccount &&
                IsVerifiedRole(actor, OrgRole.SystemAdmin),

            ResourceAction.AdminViewAuditLog =>
                resource.Kind == ResourceKind.AuditLog &&
                actor.TrustLevel == TrustLevel.Verified &&
                (actor.OrgRole == OrgRole.SystemAdmin ||
                 (actor.OrgRole == OrgRole.HRAdmin && resource.IsHrAuditScope)),

            _ => false
        };
    }

    private static bool CanReadConversation(ActorContext actor, ResourceContext resource)
    {
        if (resource.MembershipRole is null)
        {
            return false;
        }

        return resource.Kind switch
        {
            ResourceKind.Dm or ResourceKind.GroupChat => true,
            ResourceKind.SelfChat =>
                resource.OrganizationId == Guid.Empty &&
                resource.MembershipRole == ResourceRole.Owner &&
                resource.TargetIsSelf,
            ResourceKind.Community or ResourceKind.CommunityChannel =>
                !resource.IsOfficial || actor.TrustLevel == TrustLevel.Verified,
            _ => false
        };
    }

    private static bool CanWriteConversation(ActorContext actor, ResourceContext resource)
    {
        if (!CanReadConversation(actor, resource))
        {
            return false;
        }

        if (resource.IsArchived)
        {
            return false;
        }

        if (resource.Kind == ResourceKind.Dm)
        {
            return !resource.IsBlocked;
        }

        if (resource.Kind is ResourceKind.Community or ResourceKind.CommunityChannel &&
            resource.PostingPolicy == PostingPolicy.AdminsOnly)
        {
            return IsResourceManager(resource.MembershipRole);
        }

        return true;
    }

    private static bool IsResourceManager(ResourceRole? role) =>
        role is ResourceRole.Owner or ResourceRole.Admin;

    private static bool CanAccessCalendar(ActorContext actor, ResourceContext resource) =>
        resource.Kind == ResourceKind.CalendarEvent && resource.OrganizationId == actor.OrganizationId &&
        resource.MembershipRole is not null &&
        (!resource.IsOfficial || actor.TrustLevel == TrustLevel.Verified);

    private static bool IsVerifiedRole(ActorContext actor, OrgRole role) =>
        actor.TrustLevel == TrustLevel.Verified && actor.OrgRole == role;
}
