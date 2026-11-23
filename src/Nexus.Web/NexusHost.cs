using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Serilog.Core;

namespace Nexus.Web;

public static class NexusHost
{
    public static WebApplication Build(
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? configureApp = null,
        ILogEventSink? additionalLogSink = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddNexusObservability(additionalLogSink);
        builder.Services.AddNexusSecurityDefaults(builder.Configuration);
        builder.Services.AddNexusStorage(builder.Configuration);

        configureBuilder?.Invoke(builder);

        var app = builder.Build();

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseNexusRequestLogging();
        app.UseStaticFiles();
        app.UseNexusSecurityDefaults();

        app.MapNexusOperationalEndpoints();

        configureApp?.Invoke(app);

        app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
        app.MapFallbackToFile("index.html").AllowAnonymous();
        return app;
    }
}
