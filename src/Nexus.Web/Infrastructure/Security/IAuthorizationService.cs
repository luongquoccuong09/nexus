namespace Nexus.Web.Infrastructure.Security;

public interface IAuthorizationService
{
    bool Can(ActorContext? actor, ResourceAction action, ResourceContext resource);
}
