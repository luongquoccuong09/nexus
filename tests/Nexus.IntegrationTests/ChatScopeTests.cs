using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class ChatScopeTests(PostgresContainerFixture pg, ITestOutputHelper output) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task PersonalConsentOrganizationInvitationAndRevocationEnforceScope()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];
        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out var bobCookies);
        using var other = Client(baseUri, out _);
        var aliceId = await RegisterAsync(alice, "scopealice_" + suffix);
        var bobUser = "scopebob_" + suffix;
        var bobId = await RegisterAsync(bob, bobUser);
        var otherId = await RegisterAsync(other, "scopeother_" + suffix);

        Assert.Empty((await GetAsync(alice, "/api/organizations")).GetProperty("items").EnumerateArray());
        Assert.Empty((await GetAsync(alice, "/api/personal/people/directory")).GetProperty("items").EnumerateArray());
        using var blockedDm = await alice.PostAsJsonAsync("/api/personal/conversations/dm", new { targetUserId = bobId });
        Assert.Equal(HttpStatusCode.NotFound, blockedDm.StatusCode);
        await ProtectedAsync(alice, "/api/personal/contacts/requests", new { username = bobUser }, HttpStatusCode.Accepted);
        var request = Assert.Single((await GetAsync(bob, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await ProtectedAsync(bob, $"/api/personal/contacts/requests/{request.GetProperty("id").GetGuid()}", new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);

        var dm = await PostAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK);
        var conversation = dm.GetProperty("id").GetGuid();
        var personalPath = $"/api/personal/conversations/{conversation}";
        await PostAsync(alice, $"{personalPath}/messages", new { body = "Personal fixture", clientMsgId = "personal-first" }, HttpStatusCode.Created);
        Assert.Single((await GetAsync(bob, $"{personalPath}/messages")).GetProperty("items").EnumerateArray());
        using var outsider = await other.GetAsync($"{personalPath}/messages");
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);

        var contactLink = (await ProtectedAsync(alice, "/api/personal/contacts/links", new { }, HttpStatusCode.OK)).GetProperty("token").GetString();
        await ProtectedAsync(other, "/api/personal/contacts/links/redeem", new { token = contactLink }, HttpStatusCode.Accepted);
        using var pendingDm = await alice.PostAsJsonAsync("/api/personal/conversations/dm", new { targetUserId = otherId });
        Assert.Equal(HttpStatusCode.NotFound, pendingDm.StatusCode);
        var pending = Assert.Single((await GetAsync(alice, "/api/personal/contacts/requests?limit=1")).GetProperty("items").EnumerateArray());
        await ProtectedAsync(alice, $"/api/personal/contacts/requests/{pending.GetProperty("id").GetGuid()}", new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);
        var contacts = await GetAsync(alice, "/api/personal/people/directory?limit=1");
        Assert.Single(contacts.GetProperty("items").EnumerateArray());
        Assert.Single((await GetAsync(alice, $"/api/personal/people/directory?limit=1&after={contacts.GetProperty("nextAfter").GetGuid()}")).GetProperty("items").EnumerateArray());

        using var groupRequest = new HttpRequestMessage(HttpMethod.Post, "/api/personal/conversations/groups")
        {
            Content = JsonContent.Create(new { title = "Personal group", memberUserIds = new[] { bobId } })
        };
        groupRequest.Headers.Add("Idempotency-Key", "personal-group");
        using var groupResponse = await alice.SendAsync(groupRequest);
        Assert.Equal(HttpStatusCode.Created, groupResponse.StatusCode);
        var groupId = (await groupResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(2, (await GetAsync(bob, $"/api/personal/conversations/{groupId}/members")).GetProperty("items").GetArrayLength());

        await EnrollAsync(alice);
        var org = (await ProtectedAsync(alice, "/api/organizations", new { name = "Scope fixture" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var orgPath = $"/api/organizations/{org}";
        using var missingMembership = await bob.GetAsync($"{orgPath}/people/directory");
        Assert.Equal(HttpStatusCode.Forbidden, missingMembership.StatusCode);
        var token = (await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK)).GetProperty("token").GetString()!;
        var preview = await ProtectedAsync(bob, "/api/organizations/invitations/preview", new { token }, HttpStatusCode.OK);
        Assert.Equal("Scope fixture", preview.GetProperty("name").GetString());
        Assert.Empty((await GetAsync(bob, "/api/organizations")).GetProperty("items").EnumerateArray());
        var competing = await Task.WhenAll(RedeemAsync(bob, token), RedeemAsync(other, token));
        Assert.Single(competing, status => status == HttpStatusCode.OK);
        Assert.Single(competing, status => status == HttpStatusCode.NotFound);
        if (competing[0] != HttpStatusCode.OK)
        {
            var bobToken = (await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK)).GetProperty("token").GetString()!;
            Assert.Equal(HttpStatusCode.OK, await RedeemAsync(bob, bobToken));
        }
        Assert.Contains((await GetAsync(bob, $"{orgPath}/people/directory")).GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == aliceId);
        using var crossScope = await bob.GetAsync($"{orgPath}/conversations/{conversation}/messages");
        Assert.Equal(HttpStatusCode.Forbidden, crossScope.StatusCode);

        await EnrollAsync(bob);
        var secondOrg = (await ProtectedAsync(bob, "/api/organizations", new { name = "Independent scope" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var secondPath = $"/api/organizations/{secondOrg}";
        Assert.Equal("Member", (await GetAsync(bob, $"{orgPath}/me")).GetProperty("orgRole").GetString());
        Assert.True((await GetAsync(bob, $"{secondPath}/me")).GetProperty("isOwner").GetBoolean());
        var page = await GetAsync(bob, "/api/organizations?limit=1");
        Assert.Single(page.GetProperty("items").EnumerateArray());
        var next = await GetAsync(bob, $"/api/organizations?limit=1&after={page.GetProperty("nextAfter").GetGuid()}");
        Assert.Single(next.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextAfter").ValueKind);
        Assert.DoesNotContain((await GetAsync(bob, $"{secondPath}/people/directory")).GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == aliceId);

        var revokedInvite = await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK);
        await ProtectedAsync(alice, $"{orgPath}/invitations/{revokedInvite.GetProperty("id").GetGuid()}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NotFound, await RedeemAsync(other, revokedInvite.GetProperty("token").GetString()!));
        var expiredInvite = await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var invitationId = expiredInvite.GetProperty("id").GetGuid();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.OrganizationInvitations.Where(i => i.Id == invitationId)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.NotFound, await RedeemAsync(other, expiredInvite.GetProperty("token").GetString()!));

        var organizationDm = (await PostAsync(alice, $"{orgPath}/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK))
            .GetProperty("id").GetGuid();
        var organizationDmPath = $"{orgPath}/conversations/{organizationDm}";
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(baseUri, $"/hubs/organizations/{org}/messaging"), o => o.Cookies = bobCookies).Build();
        await using var secondHub = new HubConnectionBuilder().WithUrl(new Uri(baseUri, $"/hubs/organizations/{secondOrg}/messaging"), o => o.Cookies = bobCookies).Build();
        var firstScopeDelivery = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondScopeDeliveries = new ConcurrentQueue<Guid>();
        var secondScopeDelivery = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstSubscription = hub.On<JsonElement>("ConversationChanged", payload =>
            firstScopeDelivery.TrySetResult(payload.GetProperty("conversationId").GetGuid()));
        using var secondSubscription = secondHub.On<JsonElement>("ConversationChanged", payload =>
        {
            var id = payload.GetProperty("conversationId").GetGuid();
            secondScopeDeliveries.Enqueue(id);
            if (id != organizationDm) secondScopeDelivery.TrySetResult(id);
        });
        await hub.StartAsync();
        await secondHub.StartAsync();
        await PostAsync(alice, $"{organizationDmPath}/messages", new { body = "Organization fixture", clientMsgId = "organization-first" }, HttpStatusCode.Created);
        Assert.Equal(organizationDm, await firstScopeDelivery.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var unread = Assert.Single((await GetAsync(bob, $"{orgPath}/conversations")).GetProperty("items").EnumerateArray());
        Assert.Equal(1, unread.GetProperty("unreadCount").GetInt64());
        Assert.Empty((await GetAsync(bob, $"{secondPath}/conversations")).GetProperty("items").EnumerateArray());
        Assert.Single((await GetAsync(bob, $"{orgPath}/sync/reconcile")).GetProperty("items").EnumerateArray());
        Assert.Empty((await GetAsync(bob, $"{secondPath}/sync/reconcile")).GetProperty("items").EnumerateArray());
        using var crossOrgSync = await bob.GetAsync($"{secondPath}/conversations/{organizationDm}/changes");
        Assert.Equal(HttpStatusCode.Forbidden, crossOrgSync.StatusCode);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await ProtectedAsync(alice, $"{orgPath}/members/{bobId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var revoked = await bob.GetAsync($"{orgPath}/people/directory");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var revokedSync = await bob.GetAsync($"{orgPath}/sync/reconcile");
        Assert.Equal(HttpStatusCode.Forbidden, revokedSync.StatusCode);
        Assert.Equal(HubConnectionState.Connected, secondHub.State);
        using var secondGroupRequest = new HttpRequestMessage(HttpMethod.Post, $"{secondPath}/conversations/groups")
        {
            Content = JsonContent.Create(new { title = "Second Organization group" })
        };
        secondGroupRequest.Headers.Add("Idempotency-Key", "second-org-group");
        using var secondGroupResponse = await bob.SendAsync(secondGroupRequest);
        Assert.Equal(HttpStatusCode.Created, secondGroupResponse.StatusCode);
        var secondGroupId = (await secondGroupResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(secondGroupId, await secondScopeDelivery.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(organizationDm, secondScopeDeliveries);
        Assert.True((await GetAsync(bob, $"{secondPath}/me")).GetProperty("isOwner").GetBoolean());
        Assert.Single((await GetAsync(bob, $"{personalPath}/messages")).GetProperty("items").EnumerateArray());
    }

    private static HttpClient Client(Uri uri, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new HttpClientHandler { CookieContainer = cookies, CheckCertificateRevocationList = true }) { BaseAddress = uri };
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, string username) =>
        (await PostAsync(client, "/api/auth/register", new { username, displayName = username,
            password = "Chat-scope-password-2026!", termsAccepted = true }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task EnrollAsync(HttpClient client)
    {
        using var setup = await client.PostAsync("/api/auth/mfa/setup", null);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        await PostAsync(client, "/api/auth/mfa/enable", new { code = IdentityM1aTests.GenerateTotpCode(key) }, HttpStatusCode.OK);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body, HttpStatusCode expected)
    {
        using var res = await client.PostAsJsonAsync(path, body);
        Assert.Equal(expected, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpStatusCode> RedeemAsync(HttpClient client, string token)
    {
        using var request = await ProtectedMessageAsync(client, "/api/organizations/invitations/redeem", new { token }, HttpMethod.Post);
        using var result = await client.SendAsync(request);
        return result.StatusCode;
    }

    private static async Task<JsonElement> ProtectedAsync(HttpClient client, string path, object? body, HttpStatusCode expected, HttpMethod? method = null)
    {
        using var request = await ProtectedMessageAsync(client, path, body, method ?? HttpMethod.Post);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return expected == HttpStatusCode.NoContent || expected == HttpStatusCode.Accepted ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpRequestMessage> ProtectedMessageAsync(HttpClient client, string path, object? body, HttpMethod method)
    {
        var token = (await GetAsync(client, "/api/auth/csrf")).GetProperty("token").GetString();
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return request;
    }

    private sealed class ErrorSink(ITestOutputHelper output) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Exception is not null) output.WriteLine(logEvent.Exception.ToString());
        }
    }
}
