using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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
using Nexus.Web.Modules.Messaging;

namespace Nexus.IntegrationTests;

[Collection("RichChatPostgres")]
public sealed class RichChatEditRecallTests(PostgresContainerFixture pg)
{
    private const string Password = "Rich-Chat-Edit-Fixture_2026!";
    private const string OriginalBody = "secret-original-message https://example.test/original-recall-link";
    private const string FirstEdit = "edited-candidate-one https://example.test/first-edit-link";
    private const string SecondEdit = "edited-candidate-two https://example.test/second-edit-link";

    [Fact]
    public async Task PersonalDm_ConcurrentVersionedEdits_RecallRedactsHistoryFilesPreviewAndLinks()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        using var author = Client(app, out _);
        using var secondDevice = Client(app, out _);
        using var peer = Client(app, out var peerCookies);
        using var outsider = Client(app, out var outsiderCookies);
        var authorAccount = await RegisterAsync(author);
        var peerAccount = await RegisterAsync(peer);
        var outsiderAccount = await RegisterAsync(outsider);
        await RequestAsync(secondDevice, HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK,
            new { username = authorAccount.Username, password = Password });
        await RequestAsync(author, HttpMethod.Post, "/api/personal/contacts/requests", HttpStatusCode.Accepted,
            new { username = peerAccount.Username });
        var request = Assert.Single((await GetAsync(peer, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await RequestAsync(peer, HttpMethod.Put, $"/api/personal/contacts/requests/{request.GetProperty("id").GetGuid()}",
            HttpStatusCode.NoContent, new { accept = true });

        var conversationId = (await RequestAsync(author, HttpMethod.Post, "/api/personal/conversations/dm", HttpStatusCode.OK,
            new { targetUserId = peerAccount.Id })).GetProperty("id").GetGuid();
        var chat = $"/api/personal/conversations/{conversationId}";
        var file = await StageFileAsync(author, chat);
        var filePath = $"/api/personal/files/{file}/download";
        var clientMsgId = Guid.NewGuid().ToString("N");
        var sendRequest = new { clientMsgId, body = OriginalBody, attachmentIds = new[] { file } };
        var sent = await RequestAsync(author, HttpMethod.Post, $"{chat}/messages", HttpStatusCode.Created, sendRequest);
        var messageId = sent.GetProperty("id").GetGuid();
        var path = $"{chat}/messages/{messageId}";
        var resolvePath = "/api/links/resolve?target=" + ContentLinkCodec.Encode("m", null, conversationId, messageId);
        Assert.Equal(1, sent.GetProperty("version").GetInt32());
        Assert.Equal(messageId, (await GetAsync(peer, resolvePath)).GetProperty("messageId").GetGuid());
        Assert.Single((await GetAsync(peer, $"{chat}/shared?tab=links")).GetProperty("items").EnumerateArray());
        Assert.Single((await GetAsync(peer, $"{chat}/files")).GetProperty("items").EnumerateArray());
        using (var download = await peer.GetAsync(filePath)) Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        await using var peerHub = Hub(peer, peerCookies, "/hubs/personal/messaging");
        await using var outsiderHub = Hub(outsider, outsiderCookies, "/hubs/personal/messaging");
        var peerEvents = new ConcurrentQueue<JsonElement>();
        var outsiderEvents = new ConcurrentQueue<JsonElement>();
        peerHub.On<JsonElement>("ConversationChanged", item => peerEvents.Enqueue(item));
        outsiderHub.On<JsonElement>("ConversationChanged", item => outsiderEvents.Enqueue(item));
        await peerHub.StartAsync();
        await outsiderHub.StartAsync();
        var presence = app.Services.GetRequiredService<IPresenceTracker>();
        await WaitUntilAsync(() => presence.IsOnline(Guid.Empty, peerAccount.Id) && presence.IsOnline(Guid.Empty, outsiderAccount.Id));

        await MutationAsync(peer, HttpMethod.Patch, path, 1, new { body = "Peer cannot edit" }, HttpStatusCode.Forbidden);
        await MutationAsync(outsider, HttpMethod.Delete, path, 1, expected: HttpStatusCode.Forbidden);
        var missing = await MutationAsync(author, HttpMethod.Patch, path, null, new { body = FirstEdit }, (HttpStatusCode)428);
        Assert.Equal("precondition_required", missing.Body.GetProperty("code").GetString());
        var malformed = await MutationAsync(author, HttpMethod.Patch, path, null, new { body = FirstEdit }, HttpStatusCode.BadRequest, rawIfMatch: "*");
        Assert.Equal("invalid_if_match", malformed.Body.GetProperty("code").GetString());
        await MutationAsync(author, HttpMethod.Patch, path, 1, new { body = FirstEdit }, HttpStatusCode.BadRequest, csrf: false);
        await MutationAsync(author, HttpMethod.Delete, path, 1, expected: HttpStatusCode.BadRequest, csrf: false);

        var edits = await Task.WhenAll(
            MutationAsync(author, HttpMethod.Patch, path, 1, new { body = FirstEdit }),
            MutationAsync(secondDevice, HttpMethod.Patch, path, 1, new { body = SecondEdit }));
        var winner = Assert.Single(edits, result => result.Status == HttpStatusCode.OK);
        var stale = Assert.Single(edits, result => result.Status == HttpStatusCode.PreconditionFailed);
        Assert.Equal("\"2\"", winner.ETag);
        Assert.Equal("\"2\"", stale.ETag);
        Assert.Equal("message_version_conflict", stale.Body.GetProperty("code").GetString());
        var edited = winner.Body.GetProperty("body").GetString()!;
        Assert.True(edited is FirstEdit or SecondEdit);
        await WaitUntilAsync(() => peerEvents.Any(evt => HasMessageDelta(evt, messageId, "message_edited")));
        var editDelta = Assert.Single(peerEvents, evt => HasMessageDelta(evt, messageId, "message_edited")).GetProperty("delta");
        Assert.Equal(edited, editDelta.GetProperty("body").GetString());
        Assert.Equal(2, editDelta.GetProperty("version").GetInt32());
        var active = Assert.Single((await GetAsync(peer, $"{chat}/messages")).GetProperty("items").EnumerateArray());
        Assert.Equal(edited, active.GetProperty("body").GetString());
        Assert.Single(active.GetProperty("attachments").EnumerateArray());
        var updatedLink = Assert.Single((await GetAsync(peer, $"{chat}/shared?tab=links")).GetProperty("items").EnumerateArray());
        Assert.DoesNotContain("original-recall-link", updatedLink.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(edited[(edited.IndexOf("https://", StringComparison.Ordinal) + "https://".Length)..], updatedLink.GetRawText(), StringComparison.Ordinal);
        await RequestAsync(peer, HttpMethod.Put, $"{path}/reactions/{Uri.EscapeDataString("👍")}", HttpStatusCode.OK);

        var staleRecall = await MutationAsync(secondDevice, HttpMethod.Delete, path, 1, expected: HttpStatusCode.PreconditionFailed);
        Assert.Equal("\"2\"", staleRecall.ETag);
        var recall = await MutationAsync(author, HttpMethod.Delete, path, 2, expected: HttpStatusCode.OK);
        Assert.Equal("\"3\"", recall.ETag);
        Assert.Equal(string.Empty, recall.Body.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, recall.Body.GetProperty("deletedAt").ValueKind);
        var repeated = await MutationAsync(secondDevice, HttpMethod.Delete, path, 2, expected: HttpStatusCode.Conflict);
        Assert.Equal("message_recalled", repeated.Body.GetProperty("code").GetString());
        await WaitUntilAsync(() => peerEvents.Any(evt => HasMessageDelta(evt, messageId, "message_recalled")));
        var recalledDelta = Assert.Single(peerEvents, evt => HasMessageDelta(evt, messageId, "message_recalled")).GetProperty("delta");
        Assert.Equal(3, recalledDelta.GetProperty("version").GetInt32());
        Assert.Equal(string.Empty, recalledDelta.GetProperty("body").GetString());
        Assert.Empty(outsiderEvents);

        var marker = Assert.Single((await GetAsync(peer, $"{chat}/messages")).GetProperty("items").EnumerateArray());
        AssertRecalled(marker);
        AssertRecalled(await RequestAsync(author, HttpMethod.Post, $"{chat}/messages", HttpStatusCode.OK, sendRequest));
        var conversations = await GetAsync(peer, "/api/personal/conversations");
        var summary = Assert.Single(conversations.GetProperty("items").EnumerateArray(), row => row.GetProperty("id").GetGuid() == conversationId);
        Assert.Equal("This message was recalled", summary.GetProperty("lastMessagePreview").GetProperty("bodyPreview").GetString()?.TrimEnd('.'));
        var history = await GetAsync(peer, $"{chat}/changes?after=0&limit=50");
        AssertNoOldBody(history.GetRawText());
        var creation = Assert.Single(history.GetProperty("items").EnumerateArray(), item => item.GetProperty("eventType").GetString() == "message.created");
        AssertRecalled(creation.GetProperty("payload"));
        Assert.Empty((await GetAsync(peer, $"{chat}/shared?tab=links")).GetProperty("items").EnumerateArray());
        Assert.Empty((await GetAsync(peer, $"{chat}/shared?tab=files")).GetProperty("items").EnumerateArray());
        Assert.Empty((await GetAsync(peer, $"{chat}/files")).GetProperty("items").EnumerateArray());
        var unavailable = await GetAsync(peer, resolvePath, HttpStatusCode.NotFound);
        Assert.Equal("link_unavailable", unavailable.GetProperty("code").GetString());
        using (var download = await peer.GetAsync(filePath)) Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var stored = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId);
        Assert.Equal(string.Empty, stored.Body);
        Assert.Equal(3, stored.Version);
        var persisted = await db.ConversationEvents.AsNoTracking().Where(e => e.ConversationId == conversationId).Select(e => e.PayloadJson).ToListAsync();
        Assert.All(persisted, AssertNoOldBody);
        Assert.False(await db.MessageReactions.AnyAsync(r => r.MessageId == messageId));
        Assert.False(await db.SharedLinks.AnyAsync(link => link.MessageId == messageId));
    }

    [Fact]
    public async Task OrganizationScopes_ThreadReplyVisibilityAndTimeWindowUseCurrentAuthorization()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        using var author = Client(app, out _);
        using var member = Client(app, out var memberCookies);
        using var other = Client(app, out _);
        var authorId = (await RegisterAsync(author)).Id;
        var memberId = (await RegisterAsync(member)).Id;
        await RegisterAsync(other);
        var org = await CreateOrganizationAsync(author);
        var foreignOrg = await CreateOrganizationAsync(other);
        var basePath = $"/api/organizations/{org}";
        var token = (await RequestAsync(author, HttpMethod.Post, $"{basePath}/invitations", HttpStatusCode.OK, new { })).GetProperty("token").GetString();
        await RequestAsync(member, HttpMethod.Post, "/api/organizations/invitations/redeem", HttpStatusCode.OK, new { token });
        var chatId = (await RequestAsync(author, HttpMethod.Post, $"{basePath}/conversations/groups", HttpStatusCode.Created,
            new { title = "Scoped edit fixture", memberUserIds = new[] { memberId } }, idempotencyKey: Guid.NewGuid().ToString("N"))).GetProperty("id").GetGuid();
        var chat = $"{basePath}/conversations/{chatId}";
        var root = await SendMessageAsync(author, chat, "Parent visible on join");
        var rootId = root.GetProperty("id").GetGuid();
        var ownRoot = await SendMessageAsync(member, chat, "Member's old root");
        var ownRootId = ownRoot.GetProperty("id").GetGuid();
        var firstReply = await SendMessageAsync(author, chat, "Hidden thread reply before rejoin", rootId);
        var replyId = firstReply.GetProperty("id").GetGuid();
        var replyPath = $"{chat}/messages/{replyId}";

        await MutationAsync(other, HttpMethod.Patch, replyPath, 1, new { body = "Wrong organization" }, HttpStatusCode.Forbidden);
        await MutationAsync(other, HttpMethod.Delete, $"/api/organizations/{foreignOrg}/conversations/{chatId}/messages/{replyId}", 1,
            expected: HttpStatusCode.Forbidden);
        await MutationAsync(author, HttpMethod.Delete, $"/api/personal/conversations/{chatId}/messages/{replyId}", 1,
            expected: HttpStatusCode.Forbidden);

        await RequestAsync(author, HttpMethod.Delete, $"{chat}/members/{memberId}", HttpStatusCode.NoContent);
        await MutationAsync(member, HttpMethod.Patch, $"{chat}/messages/{ownRootId}", 1, new { body = "Revoked author" }, HttpStatusCode.Forbidden);
        await RequestAsync(author, HttpMethod.Post, $"{chat}/members", HttpStatusCode.OK, new { userId = memberId });

        await using var memberHub = Hub(member, memberCookies, $"/hubs/organizations/{org}/messaging");
        var memberEvents = new ConcurrentQueue<JsonElement>();
        memberHub.On<JsonElement>("ConversationChanged", item => memberEvents.Enqueue(item));
        await memberHub.StartAsync();
        var presence = app.Services.GetRequiredService<IPresenceTracker>();
        await WaitUntilAsync(() => presence.IsOnline(org, memberId));
        var postRejoinReply = await SendMessageAsync(author, chat, "New reply on a hidden parent", rootId);
        var hiddenId = postRejoinReply.GetProperty("id").GetGuid();
        var hiddenPath = $"{chat}/messages/{hiddenId}";
        var cursor = (await GetAsync(author, $"{chat}/messages")).GetProperty("changeSeq").GetInt64();
        await MutationAsync(author, HttpMethod.Patch, hiddenPath, 1, new { body = "Edited on hidden parent" }, HttpStatusCode.OK);
        await MutationAsync(author, HttpMethod.Delete, hiddenPath, 2, expected: HttpStatusCode.OK);
        Assert.Empty((await GetAsync(member, $"{chat}/changes?after={cursor}")).GetProperty("items").EnumerateArray());
        await GetAsync(member, $"{chat}/messages/{rootId}/thread", HttpStatusCode.NotFound);
        var visibleRoot = await SendMessageAsync(author, chat, "New visible root after rejoin");
        var visibleRootId = visibleRoot.GetProperty("id").GetGuid();
        var visibleReply = await SendMessageAsync(author, chat, "Visible thread reply", visibleRootId);
        var visibleReplyId = visibleReply.GetProperty("id").GetGuid();
        await MutationAsync(author, HttpMethod.Patch, $"{chat}/messages/{visibleReplyId}", 1, new { body = "Edited visible reply" }, HttpStatusCode.OK);
        await WaitUntilAsync(() => memberEvents.Any(item => HasMessageDelta(item, visibleReplyId, "message_edited")));
        Assert.DoesNotContain(memberEvents, item => HasMessageDelta(item, hiddenId, "message_edited") || HasMessageDelta(item, hiddenId, "message_recalled"));
        await MutationAsync(author, HttpMethod.Delete, $"{chat}/messages/{visibleReplyId}", 2, expected: HttpStatusCode.OK);
        var thread = await GetAsync(member, $"{chat}/messages/{visibleRootId}/thread");
        AssertRecalled(Assert.Single(thread.GetProperty("items").EnumerateArray()));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var old = DateTimeOffset.UtcNow.AddMinutes(-16);
            await db.Messages.Where(message => message.Id == visibleRootId).ExecuteUpdateAsync(set => set.SetProperty(m => m.CreatedAt, old));
        }
        var expiredEdit = await MutationAsync(author, HttpMethod.Patch, $"{chat}/messages/{visibleRootId}", 1, new { body = "Expired edit" }, HttpStatusCode.Conflict);
        Assert.Equal("message_edit_window_expired", expiredEdit.Body.GetProperty("code").GetString());
        var expiredRecall = await MutationAsync(author, HttpMethod.Delete, $"{chat}/messages/{visibleRootId}", 1, expected: HttpStatusCode.Conflict);
        Assert.Equal("message_edit_window_expired", expiredRecall.Body.GetProperty("code").GetString());

        var dmId = (await RequestAsync(author, HttpMethod.Post, $"{basePath}/conversations/dm", HttpStatusCode.OK,
            new { targetUserId = memberId })).GetProperty("id").GetGuid();
        var dm = $"{basePath}/conversations/{dmId}";
        var dmMessageId = (await SendMessageAsync(author, dm, "Blocked DM edit fixture")).GetProperty("id").GetGuid();
        await RequestAsync(member, HttpMethod.Put, $"{basePath}/people/blocks/{authorId}", HttpStatusCode.NoContent);
        await MutationAsync(author, HttpMethod.Patch, $"{dm}/messages/{dmMessageId}", 1, new { body = "Blocked edit" }, HttpStatusCode.Forbidden);
        await MutationAsync(author, HttpMethod.Delete, $"{dm}/messages/{dmMessageId}", 1, expected: HttpStatusCode.Forbidden);
    }

    private static void AssertRecalled(JsonElement item)
    {
        Assert.Equal(string.Empty, item.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("deletedAt").ValueKind);
        Assert.Empty(item.GetProperty("attachments").EnumerateArray());
        Assert.Empty(item.GetProperty("reactions").EnumerateArray());
    }

    private static void AssertNoOldBody(string payload)
    {
        Assert.DoesNotContain("secret-original-message", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("edited-candidate-one", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("edited-candidate-two", payload, StringComparison.Ordinal);
    }

    private static bool HasMessageDelta(JsonElement evt, Guid messageId, string type) =>
        evt.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
        delta.TryGetProperty("type", out var kind) && kind.GetString() == type &&
        delta.GetProperty("messageId").GetGuid() == messageId;

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

    private static HubConnection Hub(HttpClient client, CookieContainer cookies, string path) =>
        new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, path), options =>
        {
            options.Cookies = cookies;
            options.Headers["Origin"] = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        }).Build();

    private static async Task<(Guid Id, string Username)> RegisterAsync(HttpClient client)
    {
        var username = $"edit_{Guid.NewGuid():N}"[..25];
        var account = await RequestAsync(client, HttpMethod.Post, "/api/auth/register", HttpStatusCode.Created,
            new { username, displayName = username, password = Password, termsAccepted = true }, csrf: false);
        return (account.GetProperty("id").GetGuid(), username);
    }

    private static async Task<Guid> CreateOrganizationAsync(HttpClient client) =>
        (await RequestAsync(client, HttpMethod.Post, "/api/organizations", HttpStatusCode.Created,
            new { name = $"Edit fixture {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

    private static async Task<Guid> StageFileAsync(HttpClient client, string chat)
    {
        using var form = new MultipartFormDataContent();
        var pdf = new ByteArrayContent("%PDF-1.4\nRecall fixture\n%%EOF"u8.ToArray());
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "recall-fixture.pdf");
        form.Add(new StringContent(Guid.NewGuid().ToString("N")), "clientUploadId");
        using var response = await client.PostAsync($"{chat}/attachments", form);
        var uploaded = await ReadResponseAsync(response, HttpStatusCode.Created);
        return uploaded.TryGetProperty("attachment", out var attachment)
            ? attachment.GetProperty("id").GetGuid() : uploaded.GetProperty("id").GetGuid();
    }

    private static Task<JsonElement> SendMessageAsync(HttpClient client, string chat, string body, Guid? parentMessageId = null) =>
        RequestAsync(client, HttpMethod.Post, $"{chat}/messages", HttpStatusCode.Created,
            new { clientMsgId = Guid.NewGuid().ToString("N"), body, parentMessageId });

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(path);
        return await ReadResponseAsync(response, expected);
    }

    private static async Task<JsonElement> RequestAsync(HttpClient client, HttpMethod method, string path, HttpStatusCode expected,
        object? body = null, bool csrf = true, string? idempotencyKey = null)
    {
        using var request = await PrepareRequestAsync(client, method, path, body, csrf);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request);
        return await ReadResponseAsync(response, expected);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string? ETag)> MutationAsync(HttpClient client,
        HttpMethod method, string path, int? version, object? body = null, HttpStatusCode? expected = null, bool csrf = true,
        string? rawIfMatch = null)
    {
        using var request = await PrepareRequestAsync(client, method, path, body, csrf);
        if (rawIfMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", rawIfMatch);
        else if (version is not null) request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        using var response = await client.SendAsync(request);
        var json = await ReadResponseAsync(response, expected ?? response.StatusCode);
        return (response.StatusCode, json, response.Headers.ETag?.Tag);
    }

    private static async Task<HttpRequestMessage> PrepareRequestAsync(HttpClient client, HttpMethod method, string path,
        object? body, bool csrf)
    {
        var token = csrf ? (await GetAsync(client, "/api/auth/csrf")).GetProperty("token").GetString() : null;
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return request;
    }

    private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, got {(int)response.StatusCode} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}.");
        var body = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(predicate(), "The expected SignalR update did not arrive.");
    }
}
