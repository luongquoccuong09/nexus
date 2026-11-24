using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Messaging;

namespace Nexus.IntegrationTests;

[Collection("RichChatPostgres")]
public sealed class RichChatBookmarkTests(PostgresContainerFixture pg)
{
    [Fact]
    public async Task PinsAreShared_SavesArePrivate_RejoinAndRecallHideBoth()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=600",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ]);
        await app.StartAsync();
        var origin = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        using var owner = Client(origin, out var ownerCookies);
        using var member = Client(origin, out var memberCookies);
        using var outsider = Client(origin, out _);
        var ownerId = (await Register(owner)).GetProperty("id").GetGuid();
        var memberAccount = await Register(member);
        var memberId = memberAccount.GetProperty("id").GetGuid();
        await Register(outsider);
        await Call(owner, HttpMethod.Post, "/api/personal/contacts/requests", HttpStatusCode.Accepted,
            new { username = memberAccount.GetProperty("username").GetString() });
        var contact = Assert.Single((await Get(member, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await Call(member, HttpMethod.Put, $"/api/personal/contacts/requests/{contact.GetProperty("id").GetGuid()}",
            HttpStatusCode.NoContent, new { accept = true });

        const string personal = "/api/personal";
        var group = await Call(owner, HttpMethod.Post, personal + "/conversations/groups", HttpStatusCode.Created,
            new { title = "Bookmark group", memberUserIds = new[] { memberId } }, idempotency: true);
        var groupId = group.GetProperty("id").GetGuid();
        var path = $"{personal}/conversations/{groupId}";
        var original = await Send(owner, path);
        var id = original.GetProperty("id").GetGuid();
        var message = $"{path}/messages/{id}";

        await using var ownerHub = Hub(origin, ownerCookies);
        await using var memberHub = Hub(origin, memberCookies);
        var memberEvents = new ConcurrentQueue<JsonElement>();
        var ownerSaves = new ConcurrentQueue<JsonElement>();
        var memberSaves = new ConcurrentQueue<JsonElement>();
        memberHub.On<JsonElement>("ConversationChanged", item => memberEvents.Enqueue(item.Clone()));
        ownerHub.On<JsonElement>("SavedMessageChanged", item => ownerSaves.Enqueue(item.Clone()));
        memberHub.On<JsonElement>("SavedMessageChanged", item => memberSaves.Enqueue(item.Clone()));
        await ownerHub.StartAsync();
        await memberHub.StartAsync();
        var presence = app.Services.GetRequiredService<IPresenceTracker>();
        await WaitFor(() => presence.IsOnline(Guid.Empty, ownerId) && presence.IsOnline(Guid.Empty, memberId));

        await Call(member, HttpMethod.Put, message + "/pin", HttpStatusCode.Forbidden);
        await Call(outsider, HttpMethod.Put, message + "/save", HttpStatusCode.Forbidden);
        var pin = await Call(owner, HttpMethod.Put, message + "/pin", HttpStatusCode.OK);
        var pinSeq = pin.GetProperty("changeSeq").GetInt64();
        var duplicate = await Call(owner, HttpMethod.Put, message + "/pin", HttpStatusCode.OK);
        Assert.Equal(pinSeq, duplicate.GetProperty("changeSeq").GetInt64());
        await WaitFor(() => memberEvents.Any(item => item.GetProperty("changeSeq").GetInt64() == pinSeq));
        Assert.Equal(id, Assert.Single((await Get(member, path + "/pins")).GetProperty("items").EnumerateArray())
            .GetProperty("messageId").GetGuid());

        await Call(owner, HttpMethod.Put, message + "/save", HttpStatusCode.OK);
        await WaitFor(() => ownerSaves.Any(item => item.GetProperty("messageId").GetGuid() == id));
        Assert.Empty(memberSaves);
        var saved = Assert.Single((await Get(owner, personal + "/saved-messages")).GetProperty("items").EnumerateArray());
        var token = Uri.EscapeDataString(saved.GetProperty("sourceLink").GetString()!);
        Assert.Equal(id, (await Get(owner, $"/api/links/resolve?target={token}")).GetProperty("messageId").GetGuid());
        Assert.Empty((await Get(member, personal + "/saved-messages")).GetProperty("items").EnumerateArray());
        var memberSavedState = Assert.Single((await Get(member, $"{path}/saved-state?ids={id}")).GetProperty("items").EnumerateArray());
        Assert.False(memberSavedState.GetProperty("saved").GetBoolean());

        await Call(owner, HttpMethod.Delete, message + "/pin", HttpStatusCode.OK);
        Assert.Empty((await Get(member, path + "/pins")).GetProperty("items").EnumerateArray());
        await Call(owner, HttpMethod.Put, message + "/pin", HttpStatusCode.OK);
        var unsaved = await Call(owner, HttpMethod.Delete, message + "/save", HttpStatusCode.OK);
        Assert.False(unsaved.GetProperty("saved").GetBoolean());
        Assert.Empty((await Get(owner, personal + "/saved-messages")).GetProperty("items").EnumerateArray());
        await Call(owner, HttpMethod.Put, message + "/save", HttpStatusCode.OK);

        await Call(member, HttpMethod.Put, message + "/save", HttpStatusCode.OK);
        await Call(owner, HttpMethod.Delete, path + $"/members/{memberId}", HttpStatusCode.NoContent);
        await Call(member, HttpMethod.Get, path + "/pins", HttpStatusCode.Forbidden);
        await Call(owner, HttpMethod.Post, path + "/members", HttpStatusCode.OK, new { userId = memberId });
        Assert.Empty((await Get(member, path + "/pins")).GetProperty("items").EnumerateArray());
        Assert.Empty((await Get(member, personal + "/saved-messages")).GetProperty("items").EnumerateArray());
        await Call(member, HttpMethod.Put, message + "/save", HttpStatusCode.NotFound);

        await Call(owner, HttpMethod.Delete, message, HttpStatusCode.OK, version: original.GetProperty("version").GetInt64());
        Assert.Empty((await Get(owner, path + "/pins")).GetProperty("items").EnumerateArray());
        Assert.Empty((await Get(owner, personal + "/saved-messages")).GetProperty("items").EnumerateArray());
        await Call(owner, HttpMethod.Put, message + "/save", HttpStatusCode.Conflict);

        var orgA = (await Call(owner, HttpMethod.Post, "/api/organizations", HttpStatusCode.Created,
            new { name = "Bookmark workspace A" })).GetProperty("id").GetGuid();
        var orgB = (await Call(outsider, HttpMethod.Post, "/api/organizations", HttpStatusCode.Created,
            new { name = "Bookmark workspace B" })).GetProperty("id").GetGuid();
        Assert.NotEqual(orgA, orgB);
        var orgAApi = $"/api/organizations/{orgA}";
        var invite = await Call(owner, HttpMethod.Post, orgAApi + "/invitations", HttpStatusCode.OK, new { });
        await Call(member, HttpMethod.Post, "/api/organizations/invitations/redeem", HttpStatusCode.OK,
            new { token = invite.GetProperty("token").GetString() });
        var dm = await Call(owner, HttpMethod.Post, orgAApi + "/conversations/dm", HttpStatusCode.OK,
            new { targetUserId = memberId });
        var dmPath = $"{orgAApi}/conversations/{dm.GetProperty("id").GetGuid()}";
        var orgMessage = await Send(owner, dmPath);
        var orgTarget = $"{dmPath}/messages/{orgMessage.GetProperty("id").GetGuid()}";
        await Call(owner, HttpMethod.Put, orgTarget + "/save", HttpStatusCode.OK);
        Assert.Empty((await Get(owner, personal + "/saved-messages")).GetProperty("items").EnumerateArray());
        Assert.Empty((await Get(outsider, $"/api/organizations/{orgB}/saved-messages")).GetProperty("items").EnumerateArray());
        await Call(outsider, HttpMethod.Put, orgTarget + "/pin", HttpStatusCode.Forbidden);
        await Call(owner, HttpMethod.Get, orgAApi + "/saved-messages?limit=51", HttpStatusCode.BadRequest);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var sameTime = DateTimeOffset.UtcNow;
            for (var index = 0; index < 51; index++)
            {
                var item = new MessageEntity
                {
                    Id = Guid.NewGuid(), ConversationId = groupId, Seq = index + 2, SenderId = ownerId,
                    ClientMsgId = $"saved-page-{Guid.NewGuid():N}", PayloadSha256 = new string('a', 64),
                    Body = "Saved page fixture", CreatedAt = sameTime
                };
                db.Messages.Add(item);
                db.SavedMessages.Add(new SavedMessageEntity
                {
                    UserId = ownerId, MessageId = item.Id, IsSaved = true, Version = 1, SavedAt = sameTime
                });
            }
            await db.SaveChangesAsync();
        }

        var savedList = personal + "/saved-messages";
        var first = await Get(owner, savedList + "?limit=50");
        var firstIds = first.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("messageId").GetGuid()).ToHashSet();
        Assert.Equal(50, firstIds.Count);
        var cursor = first.GetProperty("nextBefore");
        var beforeAt = Uri.EscapeDataString(cursor.GetProperty("beforeAt").GetString()!);
        var next = await Get(owner, $"{savedList}?limit=50&beforeAt={beforeAt}&beforeId={cursor.GetProperty("beforeId").GetGuid()}");
        var last = Assert.Single(next.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(last.GetProperty("messageId").GetGuid(), firstIds);
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
        var username = "m64_" + Guid.NewGuid().ToString("N")[..12];
        return Call(client, HttpMethod.Post, "/api/auth/register", HttpStatusCode.Created,
            new { username, displayName = username, password = "Bookmark-Fixture-password!", termsAccepted = true });
    }

    private static Task<JsonElement> Send(HttpClient client, string path) => Call(client, HttpMethod.Post, path + "/messages",
        HttpStatusCode.Created, new { clientMsgId = Guid.NewGuid().ToString("N"), body = "Bookmarked message" });

    private static Task<JsonElement> Get(HttpClient client, string path) => Call(client, HttpMethod.Get, path, HttpStatusCode.OK);

    private static async Task<JsonElement> Call(HttpClient client, HttpMethod method, string path, HttpStatusCode expected,
        object? body = null, long? version = null, bool idempotency = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get && path != "/api/auth/register")
        {
            var csrf = await Get(client, "/api/auth/csrf");
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        }
        if (version.HasValue) request.Headers.Add("If-Match", $"\"{version.Value}\"");
        if (idempotency) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {path} returned {response.StatusCode}, expected {expected}. {text}");
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static async Task WaitFor(Func<bool> expected)
    {
        for (var i = 0; i < 100 && !expected(); i++) await Task.Delay(25);
        Assert.True(expected());
    }
}
