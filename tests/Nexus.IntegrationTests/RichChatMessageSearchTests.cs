using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Messaging;
using Npgsql;

namespace Nexus.IntegrationTests;

[Collection("RichChatPostgres")]
public sealed class RichChatMessageSearchTests(PostgresContainerFixture pg)
{
    [Fact]
    public async Task ForwardCopiesOnlyAuthorizedContentAndReservesDestinationQuotaOnce()
    {
        var pdfBytes = "%PDF-1.4\nForward fixture payload\n%%EOF"u8.ToArray();
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=600",
            $"--NexusFiles:PerConversationQuotaBytes={pdfBytes.Length * 2}",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ]);
        await app.StartAsync();
        var origin = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        using var owner = Client(origin);
        using var peer = Client(origin);
        using var recipient = Client(origin);
        var ownerId = (await Register(owner, "Forward sender")).GetProperty("id").GetGuid();
        var partner = await Register(peer, "Forward source partner");
        var receiver = await Register(recipient, "Forward destination reader");
        var peerId = partner.GetProperty("id").GetGuid();
        var receiverId = receiver.GetProperty("id").GetGuid();
        foreach (var participant in new[] { (Client: peer, Account: partner), (Client: recipient, Account: receiver) })
        {
            await Post(owner, "/api/personal/contacts/requests", new { username = participant.Account.GetProperty("username").GetString() }, HttpStatusCode.Accepted);
            var pending = Assert.Single((await Get(participant.Client, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
            await Call(participant.Client, HttpMethod.Put, $"/api/personal/contacts/requests/{pending.GetProperty("id").GetGuid()}",
                HttpStatusCode.NoContent, new { accept = true });
        }
        const string personal = "/api/personal";
        var sourceDm = await Post(owner, personal + "/conversations/dm", new { targetUserId = peerId }, HttpStatusCode.OK);
        var sourceConversationId = sourceDm.GetProperty("id").GetGuid();
        var sourcePath = $"{personal}/conversations/{sourceConversationId}";
        var group = await Call(owner, HttpMethod.Post, personal + "/conversations/groups", HttpStatusCode.Created,
            new { title = "Forwarded copies", memberUserIds = new[] { receiverId } }, idempotency: true);
        var destination = $"{personal}/conversations/{group.GetProperty("id").GetGuid()}";
        using var upload = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(pdfBytes);
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        upload.Add(pdf, "file", "forward-source.pdf");
        upload.Add(new StringContent(Guid.NewGuid().ToString("N")), "clientUploadId");
        using var uploaded = await owner.PostAsync(sourcePath + "/attachments", upload);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var originalFile = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("attachment");
        var originalId = originalFile.GetProperty("id").GetGuid();
        var originalUrl = originalFile.GetProperty("downloadUrl").GetString()!;
        var source = await Send(owner, sourcePath, "Authorized copied text", attachments: [originalId]);
        var sourceId = source.GetProperty("id").GetGuid();
        var token = ContentLinkCodec.Encode("m", null, sourceConversationId, sourceId);
        var clientMsgId = Guid.NewGuid().ToString("N");
        Task<JsonElement> Forward(HttpClient client, string path, HttpStatusCode expected, string? link = null, string? key = null) =>
            Call(client, HttpMethod.Post, path + "/forward", expected, new { sourceLink = link ?? token, clientMsgId = key ?? Guid.NewGuid().ToString("N") });

        await Forward(recipient, destination, HttpStatusCode.NotFound);
        var csrf = (await Get(owner, "/api/auth/csrf")).GetProperty("token").GetString();
        async Task<(HttpStatusCode Status, JsonElement Body)> ConcurrentForward()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, destination + "/forward")
            {
                Content = JsonContent.Create(new { sourceLink = token, clientMsgId })
            };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            using var response = await owner.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }
        var raced = await Task.WhenAll(ConcurrentForward(), ConcurrentForward());
        Assert.Single(raced, item => item.Status == HttpStatusCode.Created);
        Assert.Single(raced, item => item.Status == HttpStatusCode.OK);
        var copy = raced[0].Body;
        Assert.Equal(copy.GetProperty("id").GetGuid(), raced[1].Body.GetProperty("id").GetGuid());
        var replay = await Forward(owner, destination, HttpStatusCode.OK, key: clientMsgId);
        Assert.Equal(copy.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid());
        await Forward(owner, sourcePath, HttpStatusCode.Conflict, key: clientMsgId);
        var copied = Assert.Single((await Get(recipient, destination + "/messages")).GetProperty("items").EnumerateArray());
        Assert.True(copied.GetProperty("isForwarded").GetBoolean());
        Assert.Equal("Authorized copied text", copied.GetProperty("body").GetString());
        var copiedFile = Assert.Single(copied.GetProperty("attachments").EnumerateArray());
        Assert.NotEqual(originalId, copiedFile.GetProperty("id").GetGuid());
        var copyUrl = copiedFile.GetProperty("downloadUrl").GetString()!;
        Assert.Equal(pdfBytes, await recipient.GetByteArrayAsync(copyUrl));
        await Call(recipient, HttpMethod.Get, originalUrl, HttpStatusCode.Forbidden);
        await Forward(owner, destination, HttpStatusCode.Created);
        await Forward(owner, destination, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(2, (await Get(recipient, destination + "/messages")).GetProperty("items").GetArrayLength());

        var orgA = await CreateOrg(owner, "Forward workspace A");
        var orgB = await CreateOrg(recipient, "Forward workspace B");
        var orgPath = $"/api/organizations/{orgA}";
        var community = await Call(owner, HttpMethod.Post, orgPath + "/communities", HttpStatusCode.Created,
            new { name = "Forward receiving community", visibility = "Public" }, idempotency: true);
        var communityId = community.GetProperty("id").GetGuid();
        var channel = await Call(owner, HttpMethod.Post, $"{orgPath}/communities/{communityId}/channels", HttpStatusCode.Created,
            new { name = "Forward receiving channel" }, idempotency: true);
        var channelId = channel.GetProperty("id").GetGuid();
        var channelPath = $"{orgPath}/conversations/{channelId}";
        var channelCopy = await Forward(owner, channelPath, HttpStatusCode.Created);
        var channelPosts = await Get(owner, $"{orgPath}/channels/{channelId}/posts");
        var channelPost = Assert.Single(channelPosts.GetProperty("items").EnumerateArray());
        Assert.True(channelPost.GetProperty("isForwarded").GetBoolean());
        Assert.Equal(channelCopy.GetProperty("id").GetGuid(), channelPost.GetProperty("id").GetGuid());
        Assert.StartsWith(orgPath, channelPost.GetProperty("attachments")[0].GetProperty("downloadUrl").GetString(), StringComparison.Ordinal);
        await Forward(recipient, $"/api/organizations/{orgB}/conversations/{channelId}", HttpStatusCode.NotFound);
        await Call(owner, HttpMethod.Post, $"{orgPath}/communities/{communityId}/channels/{channelId}/archive", HttpStatusCode.OK, new { });
        await Forward(owner, channelPath, HttpStatusCode.Conflict);
        var notes = await Post(owner, personal + "/conversations/self", new { }, HttpStatusCode.OK);
        var notesPath = $"{personal}/conversations/{notes.GetProperty("id").GetGuid()}";
        var postLink = ContentLinkCodec.Encode("p", orgA, channelId, channelPost.GetProperty("id").GetGuid());
        await Forward(owner, notesPath, HttpStatusCode.Created, postLink);

        var destinationDm = await Post(owner, personal + "/conversations/dm", new { targetUserId = receiverId }, HttpStatusCode.OK);
        await Call(recipient, HttpMethod.Put, $"{personal}/people/blocks/{ownerId}", HttpStatusCode.NoContent);
        await Forward(owner, $"{personal}/conversations/{destinationDm.GetProperty("id").GetGuid()}", HttpStatusCode.Forbidden);
        await Call(recipient, HttpMethod.Delete, $"{personal}/people/blocks/{ownerId}", HttpStatusCode.NoContent);

        var historyGroup = await Call(owner, HttpMethod.Post, personal + "/conversations/groups", HttpStatusCode.Created,
            new { title = "Private forward history", memberUserIds = new[] { receiverId } }, idempotency: true);
        var historyId = historyGroup.GetProperty("id").GetGuid();
        var historyPath = $"{personal}/conversations/{historyId}";
        var hidden = await Send(owner, historyPath, "Past content cannot be forwarded");
        await Call(owner, HttpMethod.Delete, historyPath + $"/members/{receiverId}", HttpStatusCode.NoContent);
        await Call(owner, HttpMethod.Post, historyPath + "/members", HttpStatusCode.OK, new { userId = receiverId });
        var hiddenReply = await Send(owner, historyPath, "Reply to a hidden root", hidden.GetProperty("id").GetGuid());
        await Forward(recipient, destination, HttpStatusCode.NotFound,
            ContentLinkCodec.Encode("m", null, historyId, hiddenReply.GetProperty("id").GetGuid()));

        await Call(owner, HttpMethod.Delete, $"{sourcePath}/messages/{sourceId}", HttpStatusCode.OK,
            ifMatch: source.GetProperty("version").GetInt64());
        await Forward(owner, destination, HttpStatusCode.NotFound);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Attachments.Where(item => item.Id == originalId).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.State, AttachmentState.Rejected)
                .SetProperty(item => item.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)));
            var cleanup = scope.ServiceProvider.GetRequiredService<OrphanAttachmentCleanupService>();
            Assert.True(await cleanup.CleanupOrphansAsync(DateTimeOffset.UtcNow.AddDays(-1)) >= 1);
        }
        Assert.Equal(pdfBytes, await recipient.GetByteArrayAsync(copyUrl));
    }

    [Fact]
    public async Task ConversationNameFilterFindsOnlyCurrentMemberships()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=600",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ]);
        await app.StartAsync();
        var origin = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        using var owner = Client(origin);
        using var peer = Client(origin);
        using var outsider = Client(origin);
        await Register(owner, "Switcher owner");
        var partner = await Register(peer, "Nguyễn Văn Hòa");
        var peerId = partner.GetProperty("id").GetGuid();
        await Register(outsider, "Switcher outsider");
        await Post(owner, "/api/personal/contacts/requests", new { username = partner.GetProperty("username").GetString() }, HttpStatusCode.Accepted);
        var contact = Assert.Single((await Get(peer, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await Call(peer, HttpMethod.Put, $"/api/personal/contacts/requests/{contact.GetProperty("id").GetGuid()}",
            HttpStatusCode.NoContent, new { accept = true });
        var username = Uri.EscapeDataString(partner.GetProperty("username").GetString()!);
        var candidates = (await Get(owner, $"/api/personal/people/chat-candidates?q={username}")).GetProperty("items");
        Assert.Equal(peerId, Assert.Single(candidates.EnumerateArray()).GetProperty("id").GetGuid());
        const string personal = "/api/personal/conversations";
        var group = await Call(owner, HttpMethod.Post, personal + "/groups", HttpStatusCode.Created,
            new { title = "Kế hoạch quý 4", memberUserIds = new[] { peerId } }, idempotency: true);
        const string filter = "?q=ke%20hoach&limit=1";
        var hits = (await Get(peer, personal + filter)).GetProperty("items");
        Assert.Equal(group.GetProperty("id").GetGuid(), Assert.Single(hits.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty((await Get(outsider, personal + filter)).GetProperty("items").EnumerateArray());
        await Call(owner, HttpMethod.Delete, $"{personal}/{group.GetProperty("id").GetGuid()}/members/{peerId}", HttpStatusCode.NoContent);
        Assert.Empty((await Get(peer, personal + filter)).GetProperty("items").EnumerateArray());

        var orgId = await CreateOrg(owner, "Switcher workspace");
        var orgPath = $"/api/organizations/{orgId}";
        var invite = await Post(owner, orgPath + "/invitations", new { }, HttpStatusCode.OK);
        await Post(peer, "/api/organizations/invitations/redeem", new { token = invite.GetProperty("token").GetString() }, HttpStatusCode.OK);
        var dm = await Post(owner, $"{orgPath}/conversations/dm", new { targetUserId = peerId }, HttpStatusCode.OK);
        var dmHits = (await Get(owner, $"{orgPath}/conversations?q=nguyen%20van")).GetProperty("items");
        Assert.Equal(dm.GetProperty("id").GetGuid(), Assert.Single(dmHits.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty((await Get(owner, personal + "?q=nguyen%20van")).GetProperty("items").EnumerateArray());
        await Call(outsider, HttpMethod.Get, $"{orgPath}/conversations?q=nguyen%20van", HttpStatusCode.Forbidden);
        var otherOrg = await CreateOrg(outsider, "Switcher workspace two");
        Assert.Empty((await Get(outsider, $"/api/organizations/{otherOrg}/conversations?q=nguyen%20van")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task SearchFindsVietnameseAndFiltersUnreadableMessagesAndChannelsInsideScope()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=600",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ]);
        await app.StartAsync();
        var origin = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        using var owner = Client(origin);
        using var peer = Client(origin);
        using var outsider = Client(origin);
        var ownerId = (await Register(owner, "Nguyễn Minh Anh")).GetProperty("id").GetGuid();
        var partner = await Register(peer, "Search peer");
        var peerId = partner.GetProperty("id").GetGuid();
        await Register(outsider, "Search outsider");
        await Post(owner, "/api/personal/contacts/requests", new { username = partner.GetProperty("username").GetString() }, HttpStatusCode.Accepted);
        var request = Assert.Single((await Get(peer, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await Call(peer, HttpMethod.Put, $"/api/personal/contacts/requests/{request.GetProperty("id").GetGuid()}",
            HttpStatusCode.NoContent, new { accept = true });
        const string personal = "/api/personal";
        var dm = await Post(owner, personal + "/conversations/dm", new { targetUserId = peerId }, HttpStatusCode.OK);
        var dmId = dm.GetProperty("id").GetGuid();
        var dmPath = $"{personal}/conversations/{dmId}";
        using var upload = new MultipartFormDataContent();
        var pdf = new ByteArrayContent("%PDF-1.4\nSearch fixture\n%%EOF"u8.ToArray());
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        upload.Add(pdf, "file", "search-fixture.pdf");
        upload.Add(new StringContent(Guid.NewGuid().ToString("N")), "clientUploadId");
        using var uploaded = await owner.PostAsync(dmPath + "/attachments", upload);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var attachment = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var attachmentId = (attachment.TryGetProperty("attachment", out var wrapped) ? wrapped : attachment).GetProperty("id").GetGuid();
        var varied = await Send(owner, dmPath, "Đường phố Hà Nội có cà phê", attachments: [attachmentId]);
        var variedId = varied.GetProperty("id").GetGuid();
        var recalled = await Send(owner, dmPath, "Đường phố Hà Nội bản đã thu hồi");
        await Call(owner, HttpMethod.Delete, $"{dmPath}/messages/{recalled.GetProperty("id").GetGuid()}", HttpStatusCode.OK,
            ifMatch: recalled.GetProperty("version").GetInt64());

        var filters = $"q=ha%20noi%20duong&senderId={ownerId}&sender=nguyen&conversationId={dmId}&hasAttachment=true";
        var match = Assert.Single((await Search(peer, personal, filters)).GetProperty("items").EnumerateArray());
        Assert.Equal(variedId, match.GetProperty("id").GetGuid());
        Assert.True(match.GetProperty("hasAttachment").GetBoolean());
        var token = Uri.EscapeDataString(match.GetProperty("sourceLink").GetString()!);
        Assert.Equal(variedId, (await Get(peer, $"/api/links/resolve?target={token}")).GetProperty("messageId").GetGuid());
        Assert.Empty((await Search(outsider, personal, "q=Ha%20Noi")).GetProperty("items").EnumerateArray());
        var noAttachment = await Search(peer, personal, "q=Ha%20Noi&hasAttachment=false");
        Assert.Empty(noAttachment.GetProperty("items").EnumerateArray());
        Assert.Empty((await Search(peer, personal, $"q=Ha%20Noi&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}"))
            .GetProperty("items").EnumerateArray());

        var group = await Call(owner, HttpMethod.Post, personal + "/conversations/groups", HttpStatusCode.Created,
            new { title = "Search history group", memberUserIds = new[] { peerId } }, idempotency: true);
        var groupId = group.GetProperty("id").GetGuid();
        var groupPath = $"{personal}/conversations/{groupId}";
        var hidden = await Send(owner, groupPath, "past visibility search marker");
        await Call(owner, HttpMethod.Delete, groupPath + $"/members/{peerId}", HttpStatusCode.NoContent);
        await Post(owner, groupPath + "/members", new { userId = peerId }, HttpStatusCode.OK);
        await Send(owner, groupPath, "past visibility search marker reply", hidden.GetProperty("id").GetGuid());
        Assert.Empty((await Search(peer, personal, $"q=visibility%20marker&conversationId={groupId}")).GetProperty("items").EnumerateArray());

        var orgA = await CreateOrg(owner, "Search workspace A");
        var orgB = await CreateOrg(outsider, "Search workspace B");
        var routeA = $"/api/organizations/{orgA}";
        var routeB = $"/api/organizations/{orgB}";
        var invite = await Post(owner, routeA + "/invitations", new { }, HttpStatusCode.OK);
        await Post(peer, "/api/organizations/invitations/redeem", new { token = invite.GetProperty("token").GetString() }, HttpStatusCode.OK);
        var community = await Call(owner, HttpMethod.Post, routeA + "/communities", HttpStatusCode.Created,
            new { name = "Search community", visibility = "Public" }, idempotency: true);
        var channelId = community.GetProperty("mainFeedConversationId").GetGuid();
        var communityId = community.GetProperty("id").GetGuid();
        var post = await Call(owner, HttpMethod.Post, $"{routeA}/channels/{channelId}/posts", HttpStatusCode.Created,
            new { title = "Lịch họp", body = "Thông báo kế hoạch dự án." }, idempotency: true);
        Assert.Empty((await Search(peer, routeA, "q=ke%20hoach")).GetProperty("items").EnumerateArray());
        await Post(peer, $"{routeA}/communities/{communityId}/join", new { }, HttpStatusCode.OK);
        var hit = Assert.Single((await Search(peer, routeA, $"q=lich%20hop&conversationId={channelId}")).GetProperty("items").EnumerateArray());
        Assert.Equal(post.GetProperty("id").GetGuid(), hit.GetProperty("id").GetGuid());
        Assert.Equal("post", hit.GetProperty("kind").GetString());
        Assert.Empty((await Search(outsider, routeB, "q=ke%20hoach")).GetProperty("items").EnumerateArray());
        await Call(outsider, HttpMethod.Get, routeA + "/messages/search?q=ke%20hoach", HttpStatusCode.Forbidden);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Communities.Where(item => item.Id == communityId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsOfficial, true));
            await db.OrganizationMemberships.Where(item => item.OrganizationId == orgA && item.Id == ownerId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.TrustLevel, TrustLevel.Verified));
            await db.OrganizationMemberships.Where(item => item.OrganizationId == orgA && item.Id == peerId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.TrustLevel, TrustLevel.Unverified));
            var sameTime = DateTimeOffset.UtcNow;
            for (var index = 0; index < 2800; index++)
            {
                db.Messages.Add(new MessageEntity
                {
                    Id = Guid.NewGuid(), ConversationId = dmId, Seq = index + 10, SenderId = ownerId,
                    ClientMsgId = $"search-page-{Guid.NewGuid():N}", PayloadSha256 = new string('a', 64),
                    Body = index < 51 ? "Giá vàng quý hiếm" : "Common everyday test text", CreatedAt = sameTime
                });
            }
            await db.SaveChangesAsync();
        }
        Assert.Empty((await Search(peer, routeA, "q=ke%20hoach")).GetProperty("items").EnumerateArray());
        Assert.Single((await Search(owner, routeA, $"q=ke%20hoach&conversationId={channelId}")).GetProperty("items").EnumerateArray());

        var page = await Search(peer, personal, "q=vang%20quy&limit=50");
        var ids = page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Equal(50, ids.Count);
        var cursor = Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!);
        var next = await Search(peer, personal, $"q=vang%20quy&limit=50&cursor={cursor}");
        Assert.DoesNotContain(Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid(), ids);
        await Call(peer, HttpMethod.Get, personal + "/messages/search?limit=51", HttpStatusCode.BadRequest);

        await using var conn = new NpgsqlConnection(pg.MigrationConnectionString);
        await conn.OpenAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE messages", conn);
        await analyze.ExecuteNonQueryAsync();
        await using var explain = new NpgsqlCommand("""
            EXPLAIN (ANALYZE, BUFFERS) SELECT id FROM messages
            WHERE deleted_at IS NULL
              AND (to_tsvector('simple'::regconfig, public.immutable_unaccent(body)) @@ plainto_tsquery('simple'::regconfig, 'vang quy')
                   OR public.immutable_unaccent(lower(body)) LIKE '%vang quy%')
            """, conn);
        await using var plan = await explain.ExecuteReaderAsync();
        var usedGin = false;
        while (await plan.ReadAsync()) usedGin |= plan.GetString(0).Contains("ix_messages_search_", StringComparison.Ordinal);
        Assert.True(usedGin, "Expected PostgreSQL to use a GIN index for the rare Vietnamese search terms.");
    }

    private static HttpClient Client(Uri origin) => new(new HttpClientHandler
    { CookieContainer = new CookieContainer(), CheckCertificateRevocationList = true }) { BaseAddress = origin };

    private static Task<JsonElement> Register(HttpClient client, string displayName) => Post(client, "/api/auth/register",
        new { username = "m66_" + Guid.NewGuid().ToString("N")[..12], displayName, password = "Search-fixture-Password!", termsAccepted = true },
        HttpStatusCode.Created);

    private static async Task<Guid> CreateOrg(HttpClient client, string name) =>
        (await Post(client, "/api/organizations", new { name }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static Task<JsonElement> Send(HttpClient client, string path, string body, Guid? parentMessageId = null, Guid[]? attachments = null) =>
        Post(client, path + "/messages", new { clientMsgId = Guid.NewGuid().ToString("N"), body, parentMessageId, attachmentIds = attachments }, HttpStatusCode.Created);

    private static Task<JsonElement> Search(HttpClient client, string prefix, string query) => Get(client, prefix + "/messages/search?" + query);

    private static Task<JsonElement> Get(HttpClient client, string path) => Call(client, HttpMethod.Get, path, HttpStatusCode.OK);

    private static Task<JsonElement> Post(HttpClient client, string path, object body, HttpStatusCode expected) =>
        Call(client, HttpMethod.Post, path, expected, body);

    private static async Task<JsonElement> Call(HttpClient client, HttpMethod method, string path, HttpStatusCode expected,
        object? body = null, bool idempotency = false, long? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get && path != "/api/auth/register")
        {
            var csrf = await Get(client, "/api/auth/csrf");
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        }
        if (idempotency) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        if (ifMatch.HasValue) request.Headers.Add("If-Match", $"\"{ifMatch.Value}\"");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {path} returned {response.StatusCode}, expected {expected}. {text}");
        return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }
}
