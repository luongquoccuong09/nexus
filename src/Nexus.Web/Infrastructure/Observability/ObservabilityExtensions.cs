using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Nexus.Web.Modules.Meetings;
using Nexus.Web.Modules.Notifications;

namespace Nexus.Web.Infrastructure.Observability;

public static class ObservabilityExtensions
{
    public const string ServiceName = "Nexus.Web";

    public static WebApplicationBuilder AddNexusObservability(
        this WebApplicationBuilder builder,
        ILogEventSink? additionalSink = null)
    {
        builder.Services.AddSerilog((services, loggerConfig) =>
        {
            loggerConfig
                .ReadFrom.Configuration(builder.Configuration)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", ServiceName)
                .Enrich.With<SensitiveDataRedactingEnricher>()
                .WriteTo.Console(new CompactJsonFormatter());

            if (additionalSink is not null)
            {
                loggerConfig.WriteTo.Sink(additionalSink);
            }
        });

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live", "ready"]);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: ServiceName,
                serviceVersion: typeof(NexusHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = ctx =>
                        !ctx.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) &&
                        !ctx.Request.Path.Equals("/readyz", StringComparison.OrdinalIgnoreCase) &&
                        !ctx.Request.Path.StartsWithSegments(MediaSignalGateway.SignalPath, StringComparison.OrdinalIgnoreCase);
                })
                .AddHttpClientInstrumentation(options =>
                    options.FilterHttpRequestMessage = request => !request.Options.TryGetValue(WebPushTransport.PrivateEndpoint, out _)))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
                    .AddMeter("System.Runtime", "Npgsql", NexusMeters.Name);
                if (builder.Configuration.GetValue<bool>("NexusObservability:ExportMetrics"))
                    metrics.AddOtlpExporter();
            });

        return builder;
    }

    public static IApplicationBuilder UseNexusRequestLogging(this IApplicationBuilder app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (httpContext, _, ex) =>
            {
                if (ex is not null || httpContext.Response.StatusCode >= 500)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
                    httpContext.Request.Path.Equals("/readyz", StringComparison.OrdinalIgnoreCase))
                {
                    return LogEventLevel.Debug;
                }

                return LogEventLevel.Information;
            };
        });

        return app;
    }
}
