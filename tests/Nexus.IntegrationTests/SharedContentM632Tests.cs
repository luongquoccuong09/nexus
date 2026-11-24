using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Files;
using SkiaSharp;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class SharedContentM632Tests(PostgresContainerFixture pg, ITestOutputHelper output)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task ChatSharedTabsFilesMediaLinksFilterSec10AndSourceLinkResolution()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m632-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost($"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];

            using var alice = Client(baseUri, out _);
            using var bob = Client(baseUri, out _);
            _ = await RegisterAsync(alice, "sh_alice_" + suffix);
            var bobName = "sh_bob_" + suffix;
            var bobId = await RegisterAsync(bob, bobName);

            await ConnectContactsAsync(alice, bob, bobName);
            var dmId = (await PostAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK))
                .GetProperty("id").GetGuid();

            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");
            var jpegBytes = BuildJpeg(320, 240);
            var pdfId = await StagePersonalFileAsync(alice, dmId, "quarterly-report.pdf", "application/pdf", pdfBytes);
            var imgId = await StagePersonalFileAsync(alice, dmId, "site-photo.jpg", "image/jpeg", jpegBytes);

            var msg = await PostAsync(alice, $"/api/personal/conversations/{dmId}/messages", new
            {
                body = "Check [Nexus Docs](https://docs.example.com/guide) and https://status.example.org/ok. Ignore [bad](javascript:alert(1)) and data:text/html,<script>1</script>",
                clientMsgId = "sh-msg-" + suffix,
                attachmentIds = new[] { pdfId, imgId }
            }, HttpStatusCode.Created);
            var msgId = msg.GetProperty("id").GetGuid();

            // Validation errors
            using (var badTab = await bob.GetAsync($"/api/personal/conversations/{dmId}/shared?tab=unknown"))
                Assert.Equal(HttpStatusCode.BadRequest, badTab.StatusCode);
            using (var badLimit = await bob.GetAsync($"/api/personal/conversations/{dmId}/shared?tab=files&limit=51"))
                Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);

            // 1. Files tab returns both PDF & JPEG + quota + sourceLink; q filters by filename
            var filesTab = await GetAsync(bob, $"/api/personal/conversations/{dmId}/shared?tab=files");
            Assert.Equal("files", filesTab.GetProperty("tab").GetString());
            Assert.Equal(2, filesTab.GetProperty("items").GetArrayLength());
            Assert.Equal(JsonValueKind.Object, filesTab.GetProperty("quota").ValueKind);

            var filteredFiles = await GetAsync(bob, $"/api/personal/conversations/{dmId}/shared?tab=files&q=quarterly");
            var onlyPdf = Assert.Single(filteredFiles.GetProperty("items").EnumerateArray());
            Assert.Equal(pdfId, onlyPdf.GetProperty("id").GetGuid());
            var fileSourceLink = onlyPdf.GetProperty("sourceLink").GetString()!;

            // 2. Media tab returns ONLY JPEG with thumbnail/preview
            var mediaTab = await GetAsync(bob, $"/api/personal/conversations/{dmId}/shared?tab=media");
            var onlyImg = Assert.Single(mediaTab.GetProperty("items").EnumerateArray());
            Assert.Equal(imgId, onlyImg.GetProperty("id").GetGuid());
            Assert.True(onlyImg.GetProperty("hasThumbnail").GetBoolean());
            Assert.Equal(JsonValueKind.String, onlyImg.GetProperty("previewUrl").ValueKind);
            var mediaSourceLink = onlyImg.GetProperty("sourceLink").GetString()!;

            // 3. Links tab returns ONLY safe http(s) links (SEC-10 rejects javascript:/data:) + q filter
            var linksTab = await GetAsync(bob, $"/api/personal/conversations/{dmId}/shared?tab=links");
            var linkItems = linksTab.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, linkItems.Count);
            Assert.All(linkItems, item => Assert.StartsWith("https://", item.GetProperty("url").GetString()!, StringComparison.OrdinalIgnoreCase));
            var docsLink = linkItems.Single(l => l.GetProperty("host").GetString() == "docs.example.com");
            Assert.Equal("Nexus Docs", docsLink.GetProperty("label").GetString());
            var statusLink = linkItems.Single(l => l.GetProperty("host").GetString() == "status.example.org");
            Assert.Equal("https://status.example.org/ok", statusLink.GetProperty("url").GetString());
            var linkSourceLink = docsLink.GetProperty("sourceLink").GetString()!;

            var filteredLinks = await GetAsync(bob, $"/api/personal/conversations/{dmId}/shared?tab=links&q=docs");
            Assert.Single(filteredLinks.GetProperty("items").EnumerateArray());

            // 4. All 3 sourceLinks resolve via M6.25 /api/links/resolve to the exact origin message
            foreach (var link in new[] { fileSourceLink, mediaSourceLink, linkSourceLink })
            {
                var resolved = await GetAsync(bob, $"/api/links/resolve?target={Uri.EscapeDataString(link)}");
                Assert.Equal("message", resolved.GetProperty("kind").GetString());
                Assert.Equal(msgId, resolved.GetProperty("messageId").GetGuid());
            }

            // 5. Self Chat Shared view works for owner and is strictly 403 for bob
            var selfId = (await ProtectedAsync(alice, "/api/personal/conversations/self", null, HttpStatusCode.OK))
                .GetProperty("id").GetGuid();
            await PostAsync(alice, $"/api/personal/conversations/{selfId}/messages", new
            {
                body = "Private bookmark https://private.example.com/notes",
                clientMsgId = "sh-self-" + suffix
            }, HttpStatusCode.Created);

            var selfLinks = await GetAsync(alice, $"/api/personal/conversations/{selfId}/shared?tab=links");
            Assert.Single(selfLinks.GetProperty("items").EnumerateArray());

            using (var bobSelf = await bob.GetAsync($"/api/personal/conversations/{selfId}/shared?tab=links"))
                Assert.Equal(HttpStatusCode.Forbidden, bobSelf.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task GroupRejoinSec18VisibleFromSeqAndSoftDeletedMessagesHideFromAllThreeTabs()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m632-grp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost($"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];

            using var alice = Client(baseUri, out _);
            using var bob = Client(baseUri, out _);
            _ = await RegisterAsync(alice, "shg_alice_" + suffix);
            var bobName = "shg_bob_" + suffix;
            var bobId = await RegisterAsync(bob, bobName);

            await ConnectContactsAsync(alice, bob, bobName);
            var groupId = (await PostWithIdempotencyAsync(alice, "/api/personal/conversations/groups",
                new { title = "Shared Group " + suffix, memberUserIds = new[] { bobId } }, HttpStatusCode.Created))
                .GetProperty("id").GetGuid();

            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");
            var jpegBytes = BuildJpeg(200, 150);

            // Message 1 sent BEFORE bob is removed & re-added
            var prePdf = await StagePersonalFileAsync(alice, groupId, "prejoin.pdf", "application/pdf", pdfBytes);
            var preImg = await StagePersonalFileAsync(alice, groupId, "prejoin.jpg", "image/jpeg", jpegBytes);
            await PostAsync(alice, $"/api/personal/conversations/{groupId}/messages", new
            {
                body = "Pre-join link https://prejoin.example.com/secret",
                clientMsgId = "pre-" + suffix,
                attachmentIds = new[] { prePdf, preImg }
            }, HttpStatusCode.Created);

            // Remove bob and re-add bob -> VisibleFromSeq = 1 (SEC-18)
            using (var rem = await alice.DeleteAsync($"/api/personal/conversations/{groupId}/members/{bobId}"))
                Assert.Equal(HttpStatusCode.NoContent, rem.StatusCode);
            await PostAsync(alice, $"/api/personal/conversations/{groupId}/members", new { userId = bobId }, HttpStatusCode.OK);

            // Message 2 sent AFTER bob rejoined
            var postPdf = await StagePersonalFileAsync(alice, groupId, "postjoin.pdf", "application/pdf", pdfBytes);
            var postImg = await StagePersonalFileAsync(alice, groupId, "postjoin.jpg", "image/jpeg", jpegBytes);
            var msg2 = await PostAsync(alice, $"/api/personal/conversations/{groupId}/messages", new
            {
                body = "Post-join link https://postjoin.example.com/visible",
                clientMsgId = "post-" + suffix,
                attachmentIds = new[] { postPdf, postImg }
            }, HttpStatusCode.Created);
            var msg2Id = msg2.GetProperty("id").GetGuid();

            // Bob sees ONLY Message 2's items across files, media, and links (never pre-join Message 1!)
            var bobFiles = await GetAsync(bob, $"/api/personal/conversations/{groupId}/shared?tab=files");
            Assert.Equal(2, bobFiles.GetProperty("items").GetArrayLength());
            Assert.All(bobFiles.GetProperty("items").EnumerateArray(), i => Assert.Equal(msg2Id, i.GetProperty("messageId").GetGuid()));

            var bobMedia = await GetAsync(bob, $"/api/personal/conversations/{groupId}/shared?tab=media");
            var bobOnlyMedia = Assert.Single(bobMedia.GetProperty("items").EnumerateArray());
            Assert.Equal(postImg, bobOnlyMedia.GetProperty("id").GetGuid());

            var bobLinks = await GetAsync(bob, $"/api/personal/conversations/{groupId}/shared?tab=links");
            var bobOnlyLink = Assert.Single(bobLinks.GetProperty("items").EnumerateArray());
            Assert.Equal("postjoin.example.com", bobOnlyLink.GetProperty("host").GetString());

            // Soft-delete Message 2 -> vanishes from all 3 tabs immediately for bob!
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Messages.Where(m => m.Id == msg2Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow));
            }

            foreach (var tab in new[] { "files", "media", "links" })
            {
                var afterDelete = await GetAsync(bob, $"/api/personal/conversations/{groupId}/shared?tab={tab}");
                Assert.Empty(afterDelete.GetProperty("items").EnumerateArray());
            }
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task ChannelSharedViewPaginationOver50ArchivedReadOnlyRevocationSec20bAndOrgBIsolation()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m632-ch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost($"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];

            using var owner = Client(baseUri, out _);
            using var member = Client(baseUri, out _);
            using var orgBUser = Client(baseUri, out _);
            var ownerId = await RegisterAsync(owner, "shc_own_" + suffix);
            var memberId = await RegisterAsync(member, "shc_mem_" + suffix);
            _ = await RegisterAsync(orgBUser, "shc_orgb_" + suffix);

            var orgAId = await CreateOrganizationAsync(owner, "Shared OrgA " + suffix);
            await InviteAndJoinAsync(owner, member, orgAId);
            _ = await CreateOrganizationAsync(orgBUser, "Shared OrgB " + suffix);

            var comm = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
                new { name = "Shared Comm " + suffix, visibility = "Public" }, HttpStatusCode.Created);
            var commId = comm.GetProperty("id").GetGuid();
            await PostAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);
            var channelId = (await PostAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels",
                new { name = "Resources" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");
            var jpegBytes = BuildJpeg(240, 180);
            var pdfId = await StageOrgFileAsync(owner, orgAId, channelId, "handbook.pdf", "application/pdf", pdfBytes);
            var imgId = await StageOrgFileAsync(owner, orgAId, channelId, "banner.jpg", "image/jpeg", jpegBytes);

            var post = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{channelId}/posts", new
            {
                title = "Onboarding Resources",
                body = "Read [Handbook Portal](https://portal.example.com/handbook)",
                attachmentIds = new[] { pdfId, imgId }
            }, HttpStatusCode.Created);
            var postId = post.GetProperty("id").GetGuid();

            // Seed 51 additional tied-timestamp shared_links rows (= 52 total) to test > 50 pagination
            var tiedAt = new DateTimeOffset(2026, 11, 24, 2, 0, 0, TimeSpan.Zero);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                for (var i = 1; i <= 51; i++)
                {
                    db.SharedLinks.Add(new SharedLinkEntity
                    {
                        Id = Guid.CreateVersion7(),
                        OrganizationId = orgAId,
                        ConversationId = channelId,
                        PostId = postId,
                        SenderUserId = ownerId,
                        Url = $"https://docs.example.com/item-{i}",
                        Host = "docs.example.com",
                        Label = $"Item {i}",
                        CreatedAt = tiedAt
                    });
                }
                await db.SaveChangesAsync();
            }

            // 1. Links pagination > 50 tied rows + sourceLink resolution
            var linksP1 = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/shared?tab=links&limit=50");
            Assert.Equal(50, linksP1.GetProperty("items").GetArrayLength());
            Assert.True(linksP1.GetProperty("hasMore").GetBoolean());
            var nextBefore = Uri.EscapeDataString(linksP1.GetProperty("nextBefore").GetString()!);
            var nextBeforeId = linksP1.GetProperty("nextBeforeId").GetGuid();

            var linksP2 = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/shared?tab=links&limit=50&before={nextBefore}&beforeId={nextBeforeId}");
            Assert.Equal(2, linksP2.GetProperty("items").GetArrayLength());
            Assert.False(linksP2.GetProperty("hasMore").GetBoolean());

            var firstLinkSource = linksP1.GetProperty("items")[0].GetProperty("sourceLink").GetString()!;
            var resolved = await GetAsync(member, $"/api/links/resolve?target={Uri.EscapeDataString(firstLinkSource)}");
            Assert.Equal("post", resolved.GetProperty("kind").GetString());
            Assert.Equal(postId, resolved.GetProperty("postId").GetGuid());

            // 2. Archived channel stays readable across all 3 tabs (isArchived: true)
            await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/archive", null, HttpStatusCode.OK, HttpMethod.Post);
            foreach (var tab in new[] { "files", "media", "links" })
            {
                var arch = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/shared?tab={tab}");
                Assert.True(arch.GetProperty("isArchived").GetBoolean());
                Assert.NotEmpty(arch.GetProperty("items").EnumerateArray());
            }

            // 3. OrgB outsider gets 403 on all 3 tabs
            foreach (var tab in new[] { "files", "media", "links" })
            {
                using var r = await orgBUser.GetAsync($"/api/organizations/{orgAId}/channels/{channelId}/shared?tab={tab}");
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            }

            // 4. SEC-20b Official Verified blocks Unverified member
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Communities.Where(c => c.Id == commId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsOfficial, true));
            }
            using (var unv = await member.GetAsync($"/api/organizations/{orgAId}/channels/{channelId}/shared?tab=links"))
                Assert.Equal(HttpStatusCode.Forbidden, unv.StatusCode);

            // Restore non-Official, then revoke member -> immediate 403
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Communities.Where(c => c.Id == commId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsOfficial, false));
            }
            using (var rem = await owner.DeleteAsync($"/api/organizations/{orgAId}/communities/{commId}/members/{memberId}"))
                Assert.Equal(HttpStatusCode.NoContent, rem.StatusCode);
            using (var rev = await member.GetAsync($"/api/organizations/{orgAId}/channels/{channelId}/shared?tab=files"))
                Assert.Equal(HttpStatusCode.Forbidden, rev.StatusCode);

            // 5. Soft-deleted post disappears from all 3 tabs for owner
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.ChannelPosts.Where(p => p.Id == postId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
            }
            foreach (var tab in new[] { "files", "media", "links" })
            {
                var empty = await GetAsync(owner, $"/api/organizations/{orgAId}/channels/{channelId}/shared?tab={tab}");
                Assert.Empty(empty.GetProperty("items").EnumerateArray());
            }
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    private static byte[] BuildJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor(15, 118, 178));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return encoded.ToArray();
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildHost(params string[] extraArgs)
    {
        var args = new List<string>
        {
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        };
        args.AddRange(extraArgs);
        return NexusHost.Build(args.ToArray(), additionalLogSink: new ErrorSink(output));
    }

    private static Uri GetBaseUri(Microsoft.AspNetCore.Builder.WebApplication app) =>
        new(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));

    private static HttpClient Client(Uri uri, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new HttpClientHandler { CookieContainer = cookies, CheckCertificateRevocationList = true }) { BaseAddress = uri };
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, string username) =>
        (await PostAsync(client, "/api/auth/register", new
        {
            username,
            displayName = username,
            password = "Shared-view-password-2026!",
            termsAccepted = true
        }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<Guid> CreateOrganizationAsync(HttpClient owner, string name) =>
        (await ProtectedAsync(owner, "/api/organizations", new { name }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task InviteAndJoinAsync(HttpClient owner, HttpClient joiner, Guid orgId)
    {
        var invite = await ProtectedAsync(owner, $"/api/organizations/{orgId}/invitations", new { }, HttpStatusCode.OK);
        var token = invite.GetProperty("token").GetString()!;
        await ProtectedAsync(joiner, "/api/organizations/invitations/redeem", new { token }, HttpStatusCode.OK);
    }

    private static async Task ConnectContactsAsync(HttpClient requester, HttpClient acceptor, string acceptorUsername)
    {
        await ProtectedAsync(requester, "/api/personal/contacts/requests", new { username = acceptorUsername }, HttpStatusCode.Accepted);
        var reqs = await GetAsync(acceptor, "/api/personal/contacts/requests");
        var reqId = reqs.GetProperty("items").EnumerateArray().First().GetProperty("id").GetGuid();
        await ProtectedAsync(acceptor, $"/api/personal/contacts/requests/{reqId}", new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);
    }

    private static async Task<Guid> StagePersonalFileAsync(HttpClient client, Guid conversationId, string fileName, string contentType, byte[] content) =>
        await StageFileInternalAsync(client, $"/api/personal/conversations/{conversationId}/attachments", fileName, contentType, content);

    private static async Task<Guid> StageOrgFileAsync(HttpClient client, Guid orgId, Guid conversationId, string fileName, string contentType, byte[] content) =>
        await StageFileInternalAsync(client, $"/api/organizations/{orgId}/conversations/{conversationId}/attachments", fileName, contentType, content);

    private static async Task<Guid> StageFileInternalAsync(HttpClient client, string path, string fileName, string contentType, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(Guid.NewGuid().ToString("N")), "clientUploadId");
        using var resp = await client.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("attachment", out var att) ? att.GetProperty("id").GetGuid() : body.GetProperty("id").GetGuid();
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

    private static async Task<JsonElement> PostWithIdempotencyAsync(HttpClient client, string path, object body, HttpStatusCode expected)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var res = await client.SendAsync(req);
        Assert.Equal(expected, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ProtectedAsync(HttpClient client, string path, object? body, HttpStatusCode expected, HttpMethod? method = null)
    {
        var token = (await GetAsync(client, "/api/auth/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return expected is HttpStatusCode.NoContent or HttpStatusCode.Accepted or HttpStatusCode.Forbidden
            ? default
            : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed class ErrorSink(ITestOutputHelper output) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Exception is not null) output.WriteLine(logEvent.Exception.ToString());
        }
    }
}
