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
using Nexus.Web.Modules.Monitor;
using Nexus.Web.Modules.Notifications;
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
        builder.AddMonitor();
        builder.Services.AddNexusSecurityDefaults(builder.Configuration, builder.Environment.IsDevelopment());
        builder.Services.AddNexusDatabase(builder.Configuration);
        builder.Services.AddNexusStorage(builder.Configuration);
        builder.Services.AddFilesModule(builder.Configuration);
        builder.Services.AddIdentityModule(builder.Configuration);
        builder.Services.AddSingleton<IPresenceTracker, InMemoryPresenceTracker>();
        builder.Services.AddScoped<MessagingFanoutService>();
        builder.Services.AddScoped<OutboxProcessor>();
        builder.Services.AddKeyedScoped<IOutboxHandler, AnnouncementReminderHandler>(AnnouncementReminderHandler.Kind);
        builder.Services.AddKeyedScoped<IOutboxHandler, MentionOutboxHandler>(MentionOutbox.MessageKind);
        builder.Services.AddKeyedScoped<IOutboxHandler, MentionOutboxHandler>(MentionOutbox.PostKind);
        builder.Services.AddKeyedScoped<IOutboxHandler, MentionOutboxHandler>(MentionOutbox.CommentKind);
        builder.Services.AddKeyedScoped<IOutboxHandler, MentionOutboxHandler>(MentionOutbox.MessageActivity);
        builder.Services.AddKeyedScoped<IOutboxHandler, MentionOutboxHandler>(MentionOutbox.PostActivity);
        builder.Services.AddKeyedScoped<IOutboxHandler, MentionOutboxHandler>(MentionOutbox.CommentActivity);
        builder.Services.AddKeyedScoped<IOutboxHandler, PushOutboxHandler>(PushOutboxHandler.Kind);
        builder.Services.AddKeyedScoped<IOutboxHandler, EmailDigestHandler>(EmailDigestHandler.Kind);
        builder.Services.AddHostedService<OutboxBackgroundService>();
        builder.Services.AddSingleton<WebPushTransport>();
        builder.Services.AddSingleton<IEmailDigestTransport, EmailDigestTransport>();
        builder.Services.AddHttpClient(WebPushTransport.ClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true,
                UseCookies = false
            }).RemoveAllLoggers();

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
        app.UseMiddleware<OrganizationScope>();

        app.MapNexusOperationalEndpoints();
        app.MapMonitor();
        app.MapIdentityEndpoints();
        app.MapContentLinkEndpoints();
        app.MapNotificationEndpoints();
        app.MapOrganizations();
        app.MapPersonalConnections();
        app.MapOrgEndpoints();
        app.MapPeopleEndpoints();
        app.MapPeopleTagEndpoints();
        app.MapPeopleSafetyEndpoints();
        app.MapConversationEndpoints();
        app.MapSidebarEndpoints();
        app.MapCommunityEndpoints();
        app.MapMessagingEndpoints();
        app.MapMessageReactionsEndpoints();
        app.MapMessageThreadEndpoints();
        app.MapMessageMutationEndpoints();
        app.MapCommunityFeedEndpoints();
        app.MapFilesEndpoints();
        app.MapSharedContentEndpoints();
        app.MapPeopleSafetyEndpoints(OrganizationScope.PersonalApi);
        app.MapConversationEndpoints(OrganizationScope.PersonalApi);
        app.MapMessagingEndpoints(OrganizationScope.PersonalApi);
        app.MapMessageReactionsEndpoints(OrganizationScope.PersonalApi);
        app.MapMessageThreadEndpoints(OrganizationScope.PersonalApi);
        app.MapMessageMutationEndpoints(OrganizationScope.PersonalApi);
        app.MapFilesEndpoints(OrganizationScope.PersonalApi);
        app.MapSharedContentEndpoints(OrganizationScope.PersonalApi);
        app.MapHub<MessagingHub>(OrganizationScope.Hub);
        app.MapHub<MessagingHub>(OrganizationScope.PersonalHub);

        configureApp?.Invoke(app);

        app.MapGet("/favicon.ico", () => Results.NoContent()).AllowAnonymous();
        app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
        app.MapFallbackToFile("index.html").AllowAnonymous();
        return app;
    }
}
