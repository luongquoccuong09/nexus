using Microsoft.AspNetCore.SignalR;

namespace Nexus.Web.Infrastructure.Security;

public sealed class ActiveUserHubFilter : IHubFilter
{
    private readonly IAccountStatusLookup _statusLookup;

    public ActiveUserHubFilter(IAccountStatusLookup statusLookup)
    {
        _statusLookup = statusLookup;
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await EnsureActiveAsync(context.Context);
        await next(context);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await EnsureActiveAsync(invocationContext.Context);
        return await next(invocationContext);
    }

    private async Task EnsureActiveAsync(HubCallerContext callerContext)
    {
        if (!callerContext.User.TryGetUserId(out var userId))
        {
            callerContext.Abort();
            throw new HubException("Account is not active.");
        }

        var status = await _statusLookup.GetStatusAsync(userId, callerContext.ConnectionAborted);
        if (status != AccountStatus.Active)
        {
            callerContext.Abort();
            throw new HubException("Account is not active.");
        }
    }
}
