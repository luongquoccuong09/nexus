using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Modules.Messaging;

namespace Nexus.IntegrationTests;

[Collection("RichChatPostgres")]
public sealed class RichChatThreadTests(PostgresContainerFixture pg)
{
    [Fact]
    public async Task PersonalDm_ThreadReplyIsIdempotentAndPrivateReadStateStaysSeparateFromTimeline()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        using var alice = Client(app, out var aliceCookies);
        using var bob = Client(app, out var bobCookies);
        var bobAccount = await RegisterAsync(bob);
        var aliceAccount = await RegisterAsync(alice);
        await SendAsync(alice, "/api/personal/contacts/requests", new { username = bobAccount.Username }, HttpStatusCode.Accepted);
        var contacts = await GetAsync(bob, "/api/personal/contacts/requests");
        var requestId = Assert.Single(contacts.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        await SendAsync(bob, $"/api/personal/contacts/requests/{requestId}", new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);

        var dm = await SendAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobAccount.Id });
        var conversationId = dm.GetProperty("id").GetGuid();
        var conversation = $"/api/personal/conversations/{conversationId}";
        var parent = await SendMessageAsync(alice, conversation, "Main timeline parent");
        var parentId = parent.GetProperty("id").GetGuid();
        var thread = $"{conversation}/messages/{parentId}/thread";
        var anchor = (await GetAsync(bob, $"{conversation}/messages")).GetProperty("changeSeq").GetInt64();
        await SendAsync(bob, $"{conversation}/read", new { lastReadSeq = parent.GetProperty("seq").GetInt64() }, method: HttpMethod.Put);

        await using var bobHub = BuildHub(bob, bobCookies, "/hubs/personal/messaging");
        await using var aliceHub = BuildHub(alice, aliceCookies, "/hubs/personal/messaging");
        var bobDeltas = new ConcurrentQueue<JsonElement>();
        var aliceStates = new ConcurrentQueue<JsonElement>();
        var bobStates = new ConcurrentQueue<JsonElement>();
        bobHub.On<JsonElement>("ConversationChanged", evt => bobDeltas.Enqueue(evt));
        bobHub.On<JsonElement>("ThreadStateUpdated", evt => bobStates.Enqueue(evt));
        aliceHub.On<JsonElement>("ThreadStateUpdated", evt => aliceStates.Enqueue(evt));
        await bobHub.StartAsync();
        await aliceHub.StartAsync();
        var presence = app.Services.GetRequiredService<IPresenceTracker>();
        await WaitUntilAsync(() => presence.IsOnline(Guid.Empty, aliceAccount.Id) && presence.IsOnline(Guid.Empty, bobAccount.Id));

        var followed = await SendAsync(bob, $"{thread}/follow", method: HttpMethod.Put);
        Assert.True(followed.GetProperty("isFollowing").GetBoolean());
        var followVersion = followed.GetProperty("version").GetInt64();
        var followAgain = await SendAsync(bob, $"{thread}/follow", method: HttpMethod.Put);
        Assert.Equal(followVersion, followAgain.GetProperty("version").GetInt64());
        await WaitUntilAsync(() => !bobStates.IsEmpty);
        var initialState = Assert.Single(bobStates);
        Assert.Equal(parentId, initialState.GetProperty("parentMessageId").GetGuid());
        Assert.Equal(followVersion, initialState.GetProperty("version").GetInt64());
        using var noCsrf = await bob.PutAsJsonAsync($"{thread}/read", new { lastReadReplySeq = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);

        var concurrent = await Task.WhenAll(
            SendMessageAsync(alice, conversation, "First reply", parentId),
            SendMessageAsync(alice, conversation, "Second reply", parentId));
        Assert.All(concurrent, reply => Assert.Equal(parentId, reply.GetProperty("parentMessageId").GetGuid()));
        var newest = concurrent.MaxBy(r => r.GetProperty("seq").GetInt64());
        var newestSeq = newest.GetProperty("seq").GetInt64();
        var replayId = newest.GetProperty("clientMsgId").GetString();
        var replay = await SendMessageAsync(alice, conversation, newest.GetProperty("body").GetString()!, parentId, replayId, HttpStatusCode.OK);
        Assert.Equal(newest.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid());
        await SendMessageAsync(alice, conversation, newest.GetProperty("body").GetString()!, clientMsgId: replayId, expected: HttpStatusCode.Conflict);
        var nested = await SendMessageAsync(alice, conversation, "Nested reply", newest.GetProperty("id").GetGuid(), expected: HttpStatusCode.BadRequest);
        Assert.Equal("nested_thread_not_allowed", nested.GetProperty("code").GetString());

        await WaitUntilAsync(() => bobDeltas.Count(evt => evt.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.Object && delta.GetProperty("type").GetString() == "thread_reply_created") == 2);
        var newestEvent = Assert.Single(bobDeltas, evt => evt.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.Object && delta.GetProperty("lastReplySeq").GetInt64() == newestSeq);
        Assert.Equal(2, newestEvent.GetProperty("delta").GetProperty("replyCount").GetInt32());

        var response = await GetAsync(bob, $"{conversation}/messages");
        var root = Assert.Single(response.GetProperty("items").EnumerateArray());
        Assert.Equal(parentId, root.GetProperty("id").GetGuid());
        Assert.Equal(2, root.GetProperty("replyCount").GetInt32());
        Assert.Equal(newestSeq, root.GetProperty("lastReplySeq").GetInt64());
        Assert.True(root.GetProperty("isFollowing").GetBoolean());
        Assert.True(root.GetProperty("hasUnread").GetBoolean());
        var unread = await GetAsync(bob, "/api/personal/conversations?unreadOnly=true");
        Assert.DoesNotContain(unread.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == conversationId);
        var list = await GetAsync(bob, "/api/personal/conversations");
        var dmItem = Assert.Single(list.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == conversationId);
        Assert.Equal(0, dmItem.GetProperty("unreadCount").GetInt32());
        Assert.Equal("Main timeline parent", dmItem.GetProperty("lastMessagePreview").GetProperty("bodyPreview").GetString());

        var changes = await GetAsync(bob, $"{conversation}/changes?after={anchor}");
        var replyEvents = changes.GetProperty("items").EnumerateArray()
            .Where(evt => evt.GetProperty("payload").TryGetProperty("type", out var kind) && kind.GetString() == "thread_reply_created")
            .ToArray();
        Assert.Equal(2, replyEvents.Length);

        var state = (await GetAsync(bob, thread)).GetProperty("state");
        Assert.True(state.GetProperty("hasUnread").GetBoolean());
        var read = await SendAsync(bob, $"{thread}/read", new { lastReadReplySeq = long.MaxValue }, method: HttpMethod.Put);
        Assert.Equal(newestSeq, read.GetProperty("lastReadReplySeq").GetInt64());
        Assert.False(read.GetProperty("hasUnread").GetBoolean());
        var lower = await SendAsync(bob, $"{thread}/read", new { lastReadReplySeq = 0 }, method: HttpMethod.Put);
        Assert.Equal(newestSeq, lower.GetProperty("lastReadReplySeq").GetInt64());
        Assert.Equal(read.GetProperty("version").GetInt64(), lower.GetProperty("version").GetInt64());
        var unfollowed = await SendAsync(bob, $"{thread}/follow", method: HttpMethod.Delete);
        Assert.False(unfollowed.GetProperty("isFollowing").GetBoolean());
        Assert.Equal(newestSeq, unfollowed.GetProperty("lastReadReplySeq").GetInt64());
        var unfollowVersion = unfollowed.GetProperty("version").GetInt64();
        await WaitUntilAsync(() => bobStates.Any(evt => evt.GetProperty("version").GetInt64() == unfollowVersion));
        Assert.Empty(aliceStates);
        Assert.Equal(0, (await GetAsync(alice, thread)).GetProperty("state").GetProperty("lastReadReplySeq").GetInt64());
    }

    [Fact]
    public async Task OrganizationGroup_ThreadPageCapAndCurrentMemberHistoryBoundary()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        using var owner = Client(app, out _);
        using var member = Client(app, out _);
        using var other = Client(app, out _);
        await RegisterAsync(owner);
        var memberId = (await RegisterAsync(member)).Id;
        await RegisterAsync(other);
        var orgId = await CreateOrganizationAsync(owner);
        var otherOrgId = await CreateOrganizationAsync(other);
        var org = $"/api/organizations/{orgId}";
        var invite = await SendAsync(owner, $"{org}/invitations", new { });
        await SendAsync(member, "/api/organizations/invitations/redeem", new { token = invite.GetProperty("token").GetString() });

        var group = await SendAsync(owner, $"{org}/conversations/groups", new { title = "Thread test", memberUserIds = new[] { memberId } },
            HttpStatusCode.Created, idempotencyKey: Guid.NewGuid().ToString("N"));
        var conversationId = group.GetProperty("id").GetGuid();
        var conversation = $"{org}/conversations/{conversationId}";
        var parentId = (await SendMessageAsync(owner, conversation, "Group thread root")).GetProperty("id").GetGuid();
        var thread = $"{conversation}/messages/{parentId}/thread";
        for (var i = 0; i < 51; i++)
            await SendMessageAsync(owner, conversation, $"Group reply {i + 1}", parentId);

        var first = await GetAsync(member, $"{thread}?limit=50");
        var items = first.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(50, items.Length);
        Assert.Equal(51, first.GetProperty("parent").GetProperty("replyCount").GetInt32());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.False(first.GetProperty("hasNewer").GetBoolean());
        var before = first.GetProperty("nextBeforeSeq").GetInt64();
        var older = await GetAsync(member, $"{thread}?limit=50&beforeSeq={before}");
        var oldest = Assert.Single(older.GetProperty("items").EnumerateArray());
        Assert.True(oldest.GetProperty("seq").GetInt64() < before);
        Assert.True(older.GetProperty("hasNewer").GetBoolean());
        Assert.False(older.GetProperty("hasMore").GetBoolean());
        await GetAsync(member, $"{thread}?limit=51", HttpStatusCode.BadRequest);
        await GetAsync(member, $"{thread}?beforeSeq={before}&aroundSeq={before}", HttpStatusCode.BadRequest);
        await GetAsync(other, thread, HttpStatusCode.Forbidden);
        await GetAsync(other, $"/api/organizations/{otherOrgId}/conversations/{conversationId}/messages/{parentId}/thread", HttpStatusCode.Forbidden);
        await GetAsync(member, $"/api/personal/conversations/{conversationId}/messages/{parentId}/thread", HttpStatusCode.Forbidden);

        await SendAsync(owner, $"{conversation}/members/{memberId}", expected: HttpStatusCode.NoContent, method: HttpMethod.Delete);
        await GetAsync(member, thread, HttpStatusCode.Forbidden);
        await SendAsync(member, $"{thread}/follow", expected: HttpStatusCode.Forbidden, method: HttpMethod.Put);
        await SendAsync(owner, $"{conversation}/members", new { userId = memberId });
        var rejoinCursor = (await GetAsync(owner, $"{conversation}/messages")).GetProperty("changeSeq").GetInt64();
        var postRejoin = await SendMessageAsync(owner, conversation, "New reply on hidden root", parentId);
        var response = await GetAsync(member, thread, HttpStatusCode.NotFound);
        Assert.Equal("thread_not_found", response.GetProperty("code").GetString());
        await SendAsync(member, $"{thread}/read", new { lastReadReplySeq = postRejoin.GetProperty("seq").GetInt64() }, HttpStatusCode.NotFound, HttpMethod.Put);
        var replay = await GetAsync(member, $"{conversation}/changes?after={rejoinCursor}&limit=50");
        Assert.Empty(replay.GetProperty("items").EnumerateArray());
        var visibleParentId = (await SendMessageAsync(owner, conversation, "Visible new root")).GetProperty("id").GetGuid();
        var empty = await GetAsync(member, $"{conversation}/messages/{visibleParentId}/thread");
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
    }

    private WebApplication BuildHost() => NexusHost.Build([
        "--urls", "http://127.0.0.1:0",
        "--NexusDatabase:RequireTls=false",
        "--NexusOrganizations:AllowCreation=true",
        "--NexusSecurity:RateLimitPermitsPerMinute=500",
        $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
    ]);

    private static HttpClient Client(WebApplication app, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new HttpClientHandler { CookieContainer = cookies, CheckCertificateRevocationList = true })
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses))
        };
    }

    private static HubConnection BuildHub(HttpClient client, CookieContainer cookies, string path) =>
        new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, path), options =>
        {
            options.Cookies = cookies;
            options.Headers["Origin"] = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        }).Build();

    private static async Task<(Guid Id, string Username)> RegisterAsync(HttpClient client)
    {
        var username = $"thread_{Guid.NewGuid():N}"[..25];
        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username, displayName = username, password = "Rich-Chat-Thread-Test_2026!", termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (account.GetProperty("id").GetGuid(), username);
    }

    private static async Task<Guid> CreateOrganizationAsync(HttpClient client) =>
        (await SendAsync(client, "/api/organizations", new { name = $"Thread test {Guid.NewGuid():N}" }, HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    private static Task<JsonElement> SendMessageAsync(HttpClient client, string conversation, string body,
        Guid? parentMessageId = null, string? clientMsgId = null, HttpStatusCode expected = HttpStatusCode.Created) =>
        SendAsync(client, $"{conversation}/messages", new { clientMsgId = clientMsgId ?? Guid.NewGuid().ToString("N"), body, parentMessageId }, expected);

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(path);
        return await VerifyResponseAsync(response, expected);
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, string path, object? body = null,
        HttpStatusCode expected = HttpStatusCode.OK, HttpMethod? method = null, string? idempotencyKey = null)
    {
        var csrf = (await GetAsync(client, "/api/auth/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request);
        return await VerifyResponseAsync(response, expected);
    }

    private static async Task<JsonElement> VerifyResponseAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, got {(int)response.StatusCode} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}.");
        return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(predicate(), "The expected SignalR event did not arrive.");
    }
}
