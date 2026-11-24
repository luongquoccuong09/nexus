using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nexus.Web.Infrastructure.Observability;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapNexusOperationalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
            ResponseWriter = WriteHealthJsonAsync
        }).AllowAnonymous();

        app.MapHealthChecks("/readyz", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthJsonAsync
        }).AllowAnonymous();

        app.MapGet("/api/version", (IConfiguration config) => new
        {
            version = typeof(NexusHost).Assembly.GetName().Version!.ToString(3),
            commit = config["NexusBuild:Commit"] is { Length: 40 } sha && sha.All(Uri.IsHexDigit) ? sha : null
        }).AllowAnonymous();

        return app;
    }

    private static Task WriteHealthJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString()
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload), context.RequestAborted);
    }
}
