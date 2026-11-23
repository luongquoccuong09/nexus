namespace Nexus.Web.Infrastructure.Security;

public sealed class ActiveUserMiddleware
{
    private readonly RequestDelegate _next;

    public ActiveUserMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAccountStatusLookup statusLookup)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!context.User.TryGetUserId(out var userId))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { code = "account_inactive" }, context.RequestAborted);
                return;
            }

            var runtime = await statusLookup.GetRuntimeStateAsync(userId, context.RequestAborted);
            if (runtime is null || runtime.Status != AccountStatus.Active)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { code = "account_inactive" }, context.RequestAborted);
                return;
            }

            var claimStamp = context.User.GetSecurityStamp();
            if (!string.IsNullOrEmpty(claimStamp) &&
                !string.IsNullOrEmpty(runtime.SecurityStamp) &&
                !string.Equals(claimStamp, runtime.SecurityStamp, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { code = "session_revoked" }, context.RequestAborted);
                return;
            }
        }

        await _next(context);
    }
}
