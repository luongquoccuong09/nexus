using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Modules.Messaging;

namespace Nexus.IntegrationTests;

[Collection("RichChatPostgres")]
public sealed class RichChatManualUnreadTests(PostgresContainerFixture pg)
{
    private const string Password = "Manual-unread-test-password!";

    [Fact]
    public async Task ManualUnreadUsesRootHistory_AndOnlyAnObservedVersionClearsIt()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=600",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ]);
        await app.StartAsync();
        var origin = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        using var owner = Client(origin, out _);
        using var otherDevice = Client(origin, out var otherCookies);
        using var peer = Client(origin, out var peerCookies);
        using var stranger = Client(origin, out _);
        var account = await Register(owner);
        var partner = await Register(peer);
        await Register(stranger);
        var ownerId = account.GetProperty("id").GetGuid();
        var peerId = partner.GetProperty("id").GetGuid();
        await Call(otherDevice, HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK,
            new { username = account.GetProperty("username").GetString(), password = Password });
        await Call(owner, HttpMethod.Post, "/api/personal/contacts/requests", HttpStatusCode.Accepted,
            new { username = partner.GetProperty("username").GetString() });
        var request = Assert.Single((await Get(peer, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await Call(peer, HttpMethod.Put, $"/api/personal/contacts/requests/{request.GetProperty("id").GetGuid()}",
            HttpStatusCode.NoContent, new { accept = true });
        var group = await Call(owner, HttpMethod.Post, "/api/personal/conversations/groups", HttpStatusCode.Created,
            new { title = "Unread test group", memberUserIds = new[] { peerId } }, idempotency: true);
        var conversationId = group.GetProperty("id").GetGuid();
        var path = $"/api/personal/conversations/{conversationId}";
        var root = await Send(owner, path);
        var reply = await Send(peer, path, root.GetProperty("id").GetGuid());
        var last = await Send(owner, path);
        var firstSeq = root.GetProperty("seq").GetInt64();
        var replySeq = reply.GetProperty("seq").GetInt64();
        var lastSeq = last.GetProperty("seq").GetInt64();
        await Call(owner, HttpMethod.Put, path + "/read", HttpStatusCode.OK, new { lastReadSeq = lastSeq });
        await Call(peer, HttpMethod.Put, path + "/read", HttpStatusCode.OK, new { lastReadSeq = lastSeq });

        await using var otherHub = Hub(origin, otherCookies);
        await using var peerHub = Hub(origin, peerCookies);
        var ownUpdates = new ConcurrentQueue<JsonElement>();
        var peerUpdates = new ConcurrentQueue<JsonElement>();
        otherHub.On<JsonElement>("ReadStateUpdated", state => ownUpdates.Enqueue(state.Clone()));
        peerHub.On<JsonElement>("ReadStateUpdated", state => peerUpdates.Enqueue(state.Clone()));
        await otherHub.StartAsync();
        await peerHub.StartAsync();
        var presence = app.Services.GetRequiredService<IPresenceTracker>();
        for (var i = 0; i < 100 && !(presence.IsOnline(Guid.Empty, ownerId) && presence.IsOnline(Guid.Empty, peerId)); ++i)
            await Task.Delay(25);

        await Call(stranger, HttpMethod.Put, path + "/unread", HttpStatusCode.Forbidden, new { fromSeq = firstSeq });
        await Call(owner, HttpMethod.Put, path + "/unread", HttpStatusCode.NotFound, new { fromSeq = replySeq });
        var mark = await Call(owner, HttpMethod.Put, path + "/unread", HttpStatusCode.OK, new { fromSeq = firstSeq });
        var initialVersion = mark.GetProperty("manualUnreadVersion").GetInt64();
        Assert.Equal(firstSeq, mark.GetProperty("manualUnreadFromSeq").GetInt64());
        Assert.Equal(lastSeq, mark.GetProperty("lastReadSeq").GetInt64());
        Assert.Equal(2, mark.GetProperty("unreadCount").GetInt64());

        for (var i = 0; i < 100 && ownUpdates.IsEmpty; ++i) await Task.Delay(25);
        Assert.Equal(initialVersion, Assert.Single(ownUpdates).GetProperty("manualUnreadVersion").GetInt64());
        Assert.Empty(peerUpdates);
        const string filter = "/api/personal/conversations?unreadOnly=true";
        Assert.Single((await Get(otherDevice, filter)).GetProperty("items").EnumerateArray());
        Assert.Empty((await Get(peer, filter)).GetProperty("items").EnumerateArray());

        var remark = await Call(owner, HttpMethod.Put, path + "/unread", HttpStatusCode.OK, new { fromSeq = lastSeq });
        var currentVersion = remark.GetProperty("manualUnreadVersion").GetInt64();
        var staleRead = await Call(otherDevice, HttpMethod.Put, path + "/read", HttpStatusCode.OK,
            new { lastReadSeq = lastSeq, manualUnreadVersion = initialVersion });
        Assert.Equal(lastSeq, staleRead.GetProperty("manualUnreadFromSeq").GetInt64());
        var oldRead = await Call(otherDevice, HttpMethod.Put, path + "/read", HttpStatusCode.OK, new { lastReadSeq = firstSeq });
        Assert.Equal(lastSeq, oldRead.GetProperty("lastReadSeq").GetInt64());
        Assert.Equal(1, oldRead.GetProperty("unreadCount").GetInt64());
        var cleared = await Call(otherDevice, HttpMethod.Put, path + "/read", HttpStatusCode.OK,
            new { lastReadSeq = lastSeq, manualUnreadVersion = currentVersion });
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("manualUnreadFromSeq").ValueKind);
        Assert.Equal(lastSeq, cleared.GetProperty("lastReadSeq").GetInt64());
        Assert.Empty((await Get(owner, filter)).GetProperty("items").EnumerateArray());

        await Call(peer, HttpMethod.Put, path + "/unread", HttpStatusCode.OK, new { fromSeq = lastSeq });
        await Call(owner, HttpMethod.Delete, path + $"/members/{peerId}", HttpStatusCode.NoContent);
        await Call(owner, HttpMethod.Post, path + "/members", HttpStatusCode.OK, new { userId = peerId });
        await Call(peer, HttpMethod.Put, path + "/unread", HttpStatusCode.NotFound, new { fromSeq = lastSeq });
        Assert.Empty((await Get(peer, filter)).GetProperty("items").EnumerateArray());

        var orgA = await Organization(owner, "Unread Org A");
        var orgB = await Organization(stranger, "Unread Org B");
        Assert.NotEqual(orgA, orgB);
        var routeA = $"/api/organizations/{orgA}";
        var invitation = await Call(owner, HttpMethod.Post, routeA + "/invitations", HttpStatusCode.OK, new { });
        await Call(peer, HttpMethod.Post, "/api/organizations/invitations/redeem", HttpStatusCode.OK,
            new { token = invitation.GetProperty("token").GetString() });
        var dm = await Call(owner, HttpMethod.Post, routeA + "/conversations/dm", HttpStatusCode.OK, new { targetUserId = peerId });
        var orgChat = $"{routeA}/conversations/{dm.GetProperty("id").GetGuid()}";
        await Send(owner, orgChat);
        Assert.Equal(1, (await Call(owner, HttpMethod.Put, orgChat + "/unread", HttpStatusCode.OK, new { }))
            .GetProperty("unreadCount").GetInt64());
        await Call(stranger, HttpMethod.Put, orgChat + "/unread", HttpStatusCode.Forbidden, new { });
        Assert.Empty((await Get(stranger, $"/api/organizations/{orgB}/conversations?unreadOnly=true"))
            .GetProperty("items").EnumerateArray());
    }

    private static HttpClient Client(Uri origin, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new HttpClientHandler { CookieContainer = cookies, CheckCertificateRevocationList = true })
        { BaseAddress = origin };
    }

    private static HubConnection Hub(Uri origin, CookieContainer cookies) => new HubConnectionBuilder()
        .WithUrl(new Uri(origin, "/hubs/personal/messaging"), options =>
        {
            options.Cookies = cookies;
            options.Headers["Origin"] = origin.GetLeftPart(UriPartial.Authority);
        }).Build();

    private static Task<JsonElement> Register(HttpClient client)
    {
        var username = "m65_" + Guid.NewGuid().ToString("N")[..12];
        return Call(client, HttpMethod.Post, "/api/auth/register", HttpStatusCode.Created,
            new { username, displayName = username, password = Password, termsAccepted = true });
    }

    private static async Task<Guid> Organization(HttpClient client, string name) =>
        (await Call(client, HttpMethod.Post, "/api/organizations", HttpStatusCode.Created, new { name })).GetProperty("id").GetGuid();

    private static Task<JsonElement> Send(HttpClient client, string path, Guid? parentMessageId = null) =>
        Call(client, HttpMethod.Post, path + "/messages", HttpStatusCode.Created,
            new { clientMsgId = Guid.NewGuid().ToString("N"), body = "Unread fixture", parentMessageId });

    private static Task<JsonElement> Get(HttpClient client, string path) => Call(client, HttpMethod.Get, path, HttpStatusCode.OK);

    private static async Task<JsonElement> Call(HttpClient client, HttpMethod method, string path, HttpStatusCode expected,
        object? body = null, bool idempotency = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get && !path.StartsWith("/api/auth/", StringComparison.Ordinal))
        {
            var csrf = await Get(client, "/api/auth/csrf");
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        }
        if (idempotency) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {path} returned {response.StatusCode}, expected {expected}. {text}");
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }
}
