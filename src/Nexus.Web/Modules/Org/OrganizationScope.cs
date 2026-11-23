using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Nexus.Web.Modules.Org;

public sealed class OrganizationScope(RequestDelegate next)
{
    public const string Api = "/api/organizations/{organizationId:guid}";
    public const string Hub = "/hubs/organizations/{organizationId:guid}/messaging";
    public const string PersonalApi = "/api/personal";
    public const string PersonalHub = "/hubs/personal/messaging";

    public static string ApiPath(Guid? organizationId) => organizationId is null ? PersonalApi : $"/api/organizations/{organizationId}";

    public static bool IsPersonal(HttpContext? context) => context is not null &&
        (context.Request.Path.StartsWithSegments(PersonalApi) || context.Request.Path.StartsWithSegments("/hubs/personal"));

    public static Guid GetId(HttpContext? context) =>
        Guid.TryParse(context?.GetRouteValue("organizationId")?.ToString(), out var id) ? id : Guid.Empty;

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPersonal(context))
        {
            if (!context.User.TryGetUserId(out var userId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var db = context.RequestServices.GetRequiredService<NexusDbContext>();
            db.IsPersonalScope = true;
            db.OrganizationActor = await db.FindPersonalActorAsync(userId, context.RequestAborted);
            if (db.OrganizationActor is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else if (context.GetRouteValue("organizationId") is not null)
        {
            if (!context.User.TryGetUserId(out var userId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var db = context.RequestServices.GetRequiredService<NexusDbContext>();
            db.OrganizationScopeId = GetId(context);
            if (db.OrganizationScopeId == Guid.Empty || await db.FindOrganizationActorAsync(userId, context.RequestAborted) is not { } actor)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { code = "organization_access_revoked" }, context.RequestAborted);
                return;
            }
            db.OrganizationActor = actor;
        }

        await next(context);
    }
}
