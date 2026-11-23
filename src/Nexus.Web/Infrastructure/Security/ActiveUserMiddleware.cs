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

            var status = await statusLookup.GetStatusAsync(userId, context.RequestAborted);
            if (status != AccountStatus.Active)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { code = "account_inactive" }, context.RequestAborted);
                return;
            }
        }

        await _next(context);
    }
}
