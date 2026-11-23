using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexus.Web;
using Nexus.Web.Infrastructure.Observability;
using Nexus.Web.Infrastructure.Security;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Nexus.IntegrationTests;

public sealed class SecurityDefaultsAndObservabilityTests
{
    private const string TestScheme = "TestHeader";

    [Fact]
    public async Task OperationalEndpoints_AllowAnonymous_AndReturnSecurityHeaders()
    {
        await using var app = NexusHost.Build(["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync();
        using var client = CreateClient(app);

        foreach (var path in new[] { "/api/version", "/healthz", "/readyz" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.Contains("Content-Security-Policy"), $"Missing CSP on {path}");
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        }
    }

    [Fact]
    public async Task FallbackPolicy_AndActiveUserMiddleware_EnforceAuthenticationAndAccountStatus()
    {
        var userId = Guid.NewGuid();
        var statusLookup = new InMemoryAccountStatusLookup();
        statusLookup.SetStatus(userId, AccountStatus.Active);

        await using var app = NexusHost.Build(
            ["--urls", "http://127.0.0.1:0"],
            configureBuilder: b =>
            {
                b.Services.AddSingleton<IAccountStatusLookup>(statusLookup);
                b.Services.AddAuthentication(TestScheme)
                    .AddScheme<AuthenticationSchemeOptions, TestHeaderAuthHandler>(TestScheme, _ => { });
            },
            configureApp: a =>
            {
                a.MapGet("/api/protected-probe", () => Results.Ok(new { ok = true }));
            });

        await app.StartAsync();
        using var client = CreateClient(app);

        // 1. Unauthenticated -> 401 via FallbackPolicy
        using var anonResp = await client.GetAsync("/api/protected-probe");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. Active user -> 200
        using var activeReq = new HttpRequestMessage(HttpMethod.Get, "/api/protected-probe");
        activeReq.Headers.Add("X-Test-UserId", userId.ToString());
        using var activeResp = await client.SendAsync(activeReq);
        Assert.Equal(HttpStatusCode.OK, activeResp.StatusCode);

        // 3. Disabled user with existing session/credentials -> 403 immediately (SEC-08 / SEC-16)
        statusLookup.SetStatus(userId, AccountStatus.Disabled);
        using var disabledReq = new HttpRequestMessage(HttpMethod.Get, "/api/protected-probe");
        disabledReq.Headers.Add("X-Test-UserId", userId.ToString());
        using var disabledResp = await client.SendAsync(disabledReq);
        Assert.Equal(HttpStatusCode.Forbidden, disabledResp.StatusCode);
    }

    [Fact]
    public async Task SignalR_ActiveUserHubFilter_AndWebSocketOriginChecks_EnforceAccess()
    {
        var activeUserId = Guid.NewGuid();
        var disabledUserId = Guid.NewGuid();
        var statusLookup = new InMemoryAccountStatusLookup();
        statusLookup.SetStatus(activeUserId, AccountStatus.Active);
        statusLookup.SetStatus(disabledUserId, AccountStatus.Disabled);

        await using var app = NexusHost.Build(
            ["--urls", "http://127.0.0.1:0"],
            configureBuilder: b =>
            {
                b.Services.AddSingleton<IAccountStatusLookup>(statusLookup);
                b.Services.AddAuthentication(TestScheme)
                    .AddScheme<AuthenticationSchemeOptions, TestHeaderAuthHandler>(TestScheme, _ => { });
            },
            configureApp: a =>
            {
                a.MapHub<ProbeHub>("/hubs/probe");
            });

        await app.StartAsync();
        var baseAddress = GetBaseAddress(app);
        using var client = CreateClient(app);

        // 1. Cross-origin negotiate request -> 403 Forbidden (SEC-07)
        using var badOriginReq = new HttpRequestMessage(HttpMethod.Post, "/hubs/probe/negotiate?negotiateVersion=1");
        badOriginReq.Headers.Add("Origin", "https://evil.example");
        badOriginReq.Headers.Add("X-Test-UserId", activeUserId.ToString());
        using var badOriginResp = await client.SendAsync(badOriginReq);
        Assert.Equal(HttpStatusCode.Forbidden, badOriginResp.StatusCode);

        // 2. Same-origin negotiate request for Active user -> 200 OK
        using var goodOriginReq = new HttpRequestMessage(HttpMethod.Post, "/hubs/probe/negotiate?negotiateVersion=1");
        goodOriginReq.Headers.Add("Origin", baseAddress.TrimEnd('/'));
        goodOriginReq.Headers.Add("X-Test-UserId", activeUserId.ToString());
        using var goodOriginResp = await client.SendAsync(goodOriginReq);
        Assert.Equal(HttpStatusCode.OK, goodOriginResp.StatusCode);

        // 3. Disabled user cannot connect to hub (SEC-08 / SEC-16)
        await using var disabledConnection = new HubConnectionBuilder()
            .WithUrl($"{baseAddress}hubs/probe", opts =>
            {
                opts.Headers.Add("X-Test-UserId", disabledUserId.ToString());
                opts.Headers.Add("Origin", baseAddress.TrimEnd('/'));
            })
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => disabledConnection.StartAsync());

        // 4. Active user connects, invokes Ping, then is disabled mid-session -> next invoke rejected
        await using var activeConnection = new HubConnectionBuilder()
            .WithUrl($"{baseAddress}hubs/probe", opts =>
            {
                opts.Headers.Add("X-Test-UserId", activeUserId.ToString());
                opts.Headers.Add("Origin", baseAddress.TrimEnd('/'));
            })
            .Build();
        await activeConnection.StartAsync();
        var pong = await activeConnection.InvokeAsync<string>("Ping");
        Assert.Equal("pong", pong);

        statusLookup.SetStatus(activeUserId, AccountStatus.Disabled);
        await Assert.ThrowsAnyAsync<Exception>(() => activeConnection.InvokeAsync<string>("Ping"));
    }

    [Fact]
    public void CorsOptions_RejectsWildcardOriginAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NexusHost.Build(["--NexusSecurity:AllowedOrigins:0=*"]));

        Assert.Contains("Wildcard", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, true, 25)]
    [InlineData(1, true, 1)]
    [InlineData(50, true, 50)]
    [InlineData(0, false, 0)]
    [InlineData(-5, false, 0)]
    [InlineData(51, false, 0)]
    [InlineData(500, false, 0)]
    public void PaginationGuard_EnforcesMaxPageSizeOf50(int? requested, bool expectedOk, int expectedSize)
    {
        var ok = PaginationGuard.TryNormalize(requested, out var actualSize, out var error);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedSize, actualSize);
        if (!expectedOk)
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public async Task SerilogRedaction_NeverLogsBodiesPasswordsCookiesOrTokens()
    {
        var sink = new CapturingJsonLogSink();
        await using var app = NexusHost.Build(
            ["--urls", "http://127.0.0.1:0"],
            additionalLogSink: sink);

        var logger = app.Services.GetRequiredService<ILogger<SecurityDefaultsAndObservabilityTests>>();
        var safeUserId = Guid.NewGuid();
        var payload = new
        {
            UserId = safeUserId,
            Password = "SuperSecretPassword!123",
            MessageBody = "Confidential salary discussion",
            SessionCookie = "nexus_cookie_value_999",
            AccessToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
        };

        logger.LogInformation(
            "Processed request for {UserId} with {Password} {MessageBody} {Cookie} {Token} {@Payload}",
            safeUserId,
            "TopSecretPass#99",
            "Private DM body text",
            "cookie-secret-abc",
            "bearer-token-xyz",
            payload);

        var combinedJson = string.Join("\n", sink.Lines);
        Assert.Contains(safeUserId.ToString(), combinedJson);
        Assert.Contains(SensitiveDataRedactingEnricher.RedactedMarker, combinedJson);
        Assert.DoesNotContain("SuperSecretPassword!123", combinedJson);
        Assert.DoesNotContain("Confidential salary discussion", combinedJson);
        Assert.DoesNotContain("nexus_cookie_value_999", combinedJson);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", combinedJson);
        Assert.DoesNotContain("TopSecretPass#99", combinedJson);
        Assert.DoesNotContain("Private DM body text", combinedJson);
        Assert.DoesNotContain("cookie-secret-abc", combinedJson);
        Assert.DoesNotContain("bearer-token-xyz", combinedJson);
    }

    private static string GetBaseAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        return address.EndsWith('/') ? address : address + "/";
    }

    private static HttpClient CreateClient(WebApplication app) =>
        new() { BaseAddress = new Uri(GetBaseAddress(app)) };

    private sealed class ProbeHub : Hub
    {
        public string Ping() => "pong";
    }

    private sealed class TestHeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestHeaderAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-UserId", out var values) ||
                string.IsNullOrWhiteSpace(values.ToString()))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, values.ToString()) };
            var identity = new ClaimsIdentity(claims, TestScheme);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, TestScheme)));
        }
    }

    private sealed class CapturingJsonLogSink : ILogEventSink
    {
        private readonly CompactJsonFormatter _formatter = new();
        public ConcurrentQueue<string> Lines { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            using var writer = new StringWriter();
            _formatter.Format(logEvent, writer);
            Lines.Enqueue(writer.ToString());
        }
    }
}
