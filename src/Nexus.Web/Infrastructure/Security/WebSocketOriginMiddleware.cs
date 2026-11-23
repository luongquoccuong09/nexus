using Microsoft.Extensions.Options;

namespace Nexus.Web.Infrastructure.Security;

public sealed class WebSocketOriginMiddleware
{
    private readonly RequestDelegate _next;
    private readonly NexusSecurityOptions _options;

    public WebSocketOriginMiddleware(RequestDelegate next, IOptions<NexusSecurityOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isHubRequest = !string.IsNullOrEmpty(_options.HubPathPrefix) &&
                           context.Request.Path.StartsWithSegments(_options.HubPathPrefix, StringComparison.OrdinalIgnoreCase);

        if (context.WebSockets.IsWebSocketRequest || isHubRequest)
        {
            if (context.Request.Headers.TryGetValue("Origin", out var originValues))
            {
                var origin = originValues.ToString();
                if (!_options.IsOriginAllowed(origin, context.Request.Scheme, context.Request.Host))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { code = "origin_not_allowed" }, context.RequestAborted);
                    return;
                }
            }
        }

        await _next(context);
    }
}
