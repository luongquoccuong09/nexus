using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;
using Nexus.Web.Modules.People;
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
        builder.Services.AddNexusDatabase(builder.Configuration);
        builder.Services.AddNexusStorage(builder.Configuration);
        builder.Services.AddFilesModule(builder.Configuration);
        builder.Services.AddIdentityModule(builder.Configuration);
        builder.Services.AddSingleton<IPresenceTracker, InMemoryPresenceTracker>();
        builder.Services.AddScoped<MessagingFanoutService>();

        configureBuilder?.Invoke(builder);

        var app = builder.Build();

        var securityOptions = app.Services.GetRequiredService<IOptions<NexusSecurityOptions>>().Value;
        if (securityOptions.TrustForwardedHeaders)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 2
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            app.UseForwardedHeaders(forwarded);
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseNexusRequestLogging();
        app.UseStaticFiles();
        app.UseNexusSecurityDefaults();

        app.MapNexusOperationalEndpoints();
        app.MapIdentityEndpoints();
        app.MapOrgEndpoints();
        app.MapPeopleEndpoints();
        app.MapPeopleSafetyEndpoints();
        app.MapConversationEndpoints();
        app.MapCommunityEndpoints();
        app.MapMessagingEndpoints();
        app.MapCommunityFeedEndpoints();
        app.MapFilesEndpoints();
        app.MapHub<MessagingHub>("/hubs/messaging");

        configureApp?.Invoke(app);

        app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
        app.MapFallbackToFile("index.html").AllowAnonymous();
        return app;
    }
}
