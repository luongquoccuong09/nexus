using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class SelfChatM629Tests(PostgresContainerFixture pg, ITestOutputHelper output)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task ZeroOrgAccountCreatesIdempotentSelfChatUnderConcurrencyAndEnforcesDbConstraints()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var solo = Client(baseUri, out _);
        var soloId = await RegisterAsync(solo, "self_solo_" + suffix);

        // Confirm account has 0 Organizations
        var orgs = await GetAsync(solo, "/api/organizations");
        Assert.Empty(orgs.GetProperty("items").EnumerateArray());

        // CSRF is required
        using (var noCsrf = await solo.PostAsync("/api/personal/conversations/self", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            var err = await noCsrf.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_csrf", err.GetProperty("code").GetString());
        }

        // Org-scoped /self route does not exist
        var dummyOrgId = Guid.CreateVersion7();
        using (var orgRoute = await solo.PostAsync($"/api/organizations/{dummyOrgId}/conversations/self", null))
        {
            Assert.Equal(HttpStatusCode.NotFound, orgRoute.StatusCode);
        }

        // 8 concurrent creations must be strictly idempotent and return the exact same conversation ID
        var csrfToken = (await GetAsync(solo, "/api/auth/csrf")).GetProperty("token").GetString()!;
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/personal/conversations/self");
            req.Headers.Add("X-CSRF-TOKEN", csrfToken);
            using var resp = await solo.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return await resp.Content.ReadFromJsonAsync<JsonElement>();
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        var distinctIds = results.Select(r => r.GetProperty("id").GetGuid()).Distinct().ToList();
        var selfId = Assert.Single(distinctIds);
        Assert.All(results, r =>
        {
            Assert.Equal("self", r.GetProperty("kind").GetString());
            Assert.Equal(ConversationEndpoints.SelfChatDefaultTitle, r.GetProperty("title").GetString());
        });

        // Verify PostgreSQL has exactly 1 Self conversation row and CHECK constraints hold
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var count = await db.Conversations.CountAsync(c => c.CreatedByUserId == soloId && c.Kind == ConversationKind.Self);
            Assert.Equal(1, count);
        }

        await using (var conn = new NpgsqlConnection(pg.RuntimeConnectionString))
        {
            await conn.OpenAsync();
            await using var badSelf = new NpgsqlCommand("""
                INSERT INTO conversations (id, organization_id, kind, community_id, is_community_main_feed, posting_policy, title, dm_key, next_seq, change_seq, min_available_change_seq, created_by_user_id, created_at, updated_at)
                VALUES (@id, @orgId, 'Self', NULL, false, 'Members', 'Invalid', @dmKey, 0, 1, 1, @userId, NOW(), NOW())
                """, conn);
            badSelf.Parameters.AddWithValue("id", Guid.CreateVersion7());
            badSelf.Parameters.AddWithValue("orgId", Guid.CreateVersion7());
            badSelf.Parameters.AddWithValue("dmKey", $"self:{soloId:N}");
            badSelf.Parameters.AddWithValue("userId", soloId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => badSelf.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }

        // Subsequent call returns created = false with same ID
        var second = await ProtectedAsync(solo, "/api/personal/conversations/self", null, HttpStatusCode.OK);
        Assert.Equal(selfId, second.GetProperty("id").GetGuid());
        Assert.False(second.GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task SelfChatSupportsMultiDeviceSyncFilesContentLinksAndSidebarOrganization()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m629-self-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost($"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var username = "self_sync_" + suffix;

            using var dev1 = Client(baseUri, out var cookies1);
            using var dev2 = Client(baseUri, out var cookies2);
            var userId = await RegisterAsync(dev1, username);
            await PostAsync(dev2, "/api/auth/login", new { username, password = "Self-chat-password-2026!" }, HttpStatusCode.OK);

            var dev2Changes = Channel.CreateUnbounded<JsonElement>();
            var dev2Reads = Channel.CreateUnbounded<JsonElement>();
            await using var hub2 = new HubConnectionBuilder()
                .WithUrl(new Uri(baseUri, OrganizationScope.PersonalHub), opts =>
                {
                    opts.Cookies = cookies2;
                    opts.Headers["Origin"] = baseUri.GetLeftPart(UriPartial.Authority);
                })
                .Build();
            hub2.On<JsonElement>("ConversationChanged", payload => dev2Changes.Writer.TryWrite(payload));
            hub2.On<JsonElement>("ReadStateUpdated", payload => dev2Reads.Writer.TryWrite(payload));
            await hub2.StartAsync();

            // 1. Device 1 opens Self Chat -> Device 2 receives realtime ConversationChanged
            var opened = await ProtectedAsync(dev1, "/api/personal/conversations/self", null, HttpStatusCode.OK);
            var selfId = opened.GetProperty("id").GetGuid();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var createdEvt = await dev2Changes.Reader.ReadAsync(timeout.Token);
            Assert.Equal(selfId, createdEvt.GetProperty("conversationId").GetGuid());

            // 2. Device 1 stages an M3 PDF attachment and sends a note mentioning @self
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");
            var attachmentId = await StagePdfAsync(dev1, selfId, "private-note.pdf", pdfBytes);

            var clientMsgId = "self-note-1-" + suffix;
            var sent = await PostAsync(dev1, $"/api/personal/conversations/{selfId}/messages", new
            {
                body = $"Remember @{username} groceries",
                clientMsgId,
                attachmentIds = new[] { attachmentId },
                preserveReadState = true
            }, HttpStatusCode.Created);
            var msgId = sent.GetProperty("id").GetGuid();
            var msgSeq = sent.GetProperty("seq").GetInt64();
            Assert.Equal(1L, msgSeq);

            // Idempotent replay returns same message
            var replay = await PostAsync(dev1, $"/api/personal/conversations/{selfId}/messages", new
            {
                body = $"Remember @{username} groceries",
                clientMsgId,
                attachmentIds = new[] { attachmentId },
                preserveReadState = true
            }, HttpStatusCode.OK);
            Assert.Equal(msgId, replay.GetProperty("id").GetGuid());

            // Device 2 receives message delta via SignalR
            var msgEvt = await dev2Changes.Reader.ReadAsync(timeout.Token);
            Assert.Equal(selfId, msgEvt.GetProperty("conversationId").GetGuid());
            Assert.Equal(msgId, msgEvt.GetProperty("message").GetProperty("id").GetGuid());

            // 3. Device 2 lists conversations, reads history, downloads file, resolves M6.25 link, and marks read
            var convs = await GetAsync(dev2, "/api/personal/conversations");
            var selfConv = Assert.Single(convs.GetProperty("items").EnumerateArray());
            Assert.Equal(selfId, selfConv.GetProperty("id").GetGuid());
            Assert.Equal("self", selfConv.GetProperty("kind").GetString());
            Assert.Equal(ConversationEndpoints.SelfChatDefaultTitle, selfConv.GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, selfConv.GetProperty("peer").ValueKind);
            Assert.Equal(1, selfConv.GetProperty("memberCount").GetInt32());

            var files = await GetAsync(dev2, $"/api/personal/conversations/{selfId}/files");
            var fileItem = Assert.Single(files.GetProperty("items").EnumerateArray());
            Assert.Equal(attachmentId, fileItem.GetProperty("id").GetGuid());

            using (var dl = await dev2.GetAsync($"/api/personal/files/{attachmentId}/download"))
            {
                Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
                var bytes = await dl.Content.ReadAsByteArrayAsync();
                Assert.Equal(pdfBytes, bytes);
            }

            var token = ContentLinkCodec.Encode("m", null, selfId, msgId);
            var resolved = await GetAsync(dev2, $"/api/links/resolve?target={Uri.EscapeDataString(token)}");
            Assert.Equal("message", resolved.GetProperty("kind").GetString());
            Assert.Equal(selfId, resolved.GetProperty("conversationId").GetGuid());
            Assert.Equal(msgId, resolved.GetProperty("messageId").GetGuid());

            await ProtectedAsync(dev1, $"/api/personal/conversations/{selfId}/read", new { lastReadSeq = msgSeq }, HttpStatusCode.OK, HttpMethod.Put);
            var readEvt = await dev2Reads.Reader.ReadAsync(timeout.Token);
            Assert.Equal(selfId, readEvt.GetProperty("conversationId").GetGuid());
            Assert.Equal(msgSeq, readEvt.GetProperty("lastReadSeq").GetInt64());

            // 4. M6.26 sidebar Favorites & custom section work on Device 1 and reflect on Device 2
            var section = await ProtectedAsync(dev1, "/api/sidebar/sections", new { surface = "chat", name = "Scratchpad" }, HttpStatusCode.Created);
            var sectionId = section.GetProperty("id").GetGuid();
            await ProtectedAsync(dev1, $"/api/sidebar/items/{selfId}", new { surface = "chat", isFavorite = true, sectionId }, HttpStatusCode.OK, HttpMethod.Put);
            var sidebar2 = await GetAsync(dev2, "/api/sidebar?surface=chat");
            var sidebarItem = Assert.Single(sidebar2.GetProperty("items").EnumerateArray());
            Assert.Equal(selfId, sidebarItem.GetProperty("targetId").GetGuid());
            Assert.True(sidebarItem.GetProperty("isFavorite").GetBoolean());
            Assert.Equal(sectionId, sidebarItem.GetProperty("sectionId").GetGuid());

            // 5. Confirm zero self-mention notification spam
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var outboxCount = await db.OutboxMessages.CountAsync(o => o.ResourceId == msgId);
            Assert.Equal(0, outboxCount);
            _ = userId;
        }
        finally
        {
            if (Directory.Exists(tempStorage))
            {
                Directory.Delete(tempStorage, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OtherUsersIncludingSharedOrgAdminAreStrictlyDeniedSelfChatAccessAndMutations()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m629-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost($"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];

            using var alice = Client(baseUri, out _);
            using var bob = Client(baseUri, out _);
            using var outsider = Client(baseUri, out _);

            var aliceId = await RegisterAsync(alice, "sec_alice_" + suffix);
            var bobName = "sec_bob_" + suffix;
            var bobId = await RegisterAsync(bob, bobName);
            _ = await RegisterAsync(outsider, "sec_out_" + suffix);

            // Bob is BOTH alice's Personal contact AND SystemAdmin in OrgA
            var orgAId = await CreateOrganizationAsync(alice, "Sec OrgA " + suffix);
            await InviteAndJoinAsync(alice, bob, orgAId);
            await ProtectedAsync(alice, $"/api/organizations/{orgAId}/members/{bobId}/administrator", new { administrator = true }, HttpStatusCode.NoContent, HttpMethod.Put);
            await ConnectContactsAsync(alice, bob, bobName);

            // Alice creates Self Chat, uploads a file, sends a note, and encodes a content link
            var self = await ProtectedAsync(alice, "/api/personal/conversations/self", null, HttpStatusCode.OK);
            var selfId = self.GetProperty("id").GetGuid();
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");
            var attachmentId = await StagePdfAsync(alice, selfId, "secret.pdf", pdfBytes);
            var msg = await PostAsync(alice, $"/api/personal/conversations/{selfId}/messages", new
            {
                body = "Private diary entry",
                clientMsgId = "sec-note-" + suffix,
                attachmentIds = new[] { attachmentId }
            }, HttpStatusCode.Created);
            var msgId = msg.GetProperty("id").GetGuid();
            var msgLink = ContentLinkCodec.Encode("m", null, selfId, msgId);

            // 1. Neither bob (Personal contact + OrgA SystemAdmin) nor outsider can read, write, sync, download, resolve link, or pin self chat
            foreach (var intruder in new[] { bob, outsider })
            {
                using (var r = await intruder.GetAsync($"/api/personal/conversations/{selfId}/messages"))
                    Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

                using (var r = await intruder.PostAsJsonAsync($"/api/personal/conversations/{selfId}/messages", new { body = "intrude", clientMsgId = Guid.NewGuid().ToString("N") }))
                    Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

                using (var r = await intruder.GetAsync($"/api/personal/conversations/{selfId}/changes?after=0"))
                    Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

                using (var r = await intruder.GetAsync($"/api/personal/conversations/{selfId}/members"))
                    Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

                using (var r = await intruder.PutAsJsonAsync($"/api/personal/conversations/{selfId}/read", new { lastReadSeq = 1 }))
                    Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

                using (var r = await intruder.GetAsync($"/api/personal/conversations/{selfId}/files"))
                    Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

                using (var r = await intruder.GetAsync($"/api/personal/files/{attachmentId}/download"))
                    Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

                using (var r = await intruder.GetAsync($"/api/links/resolve?target={Uri.EscapeDataString(msgLink)}"))
                {
                    Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
                    Assert.Equal("link_unavailable", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
                }

                await ProtectedAsync(intruder, $"/api/sidebar/items/{selfId}", new { surface = "chat", isFavorite = true }, HttpStatusCode.Forbidden, HttpMethod.Put);
            }

            // 2. Owner cannot add anyone, leave, remove self, transfer ownership, set admin, or block/report self
            using (var r = await alice.PostAsJsonAsync($"/api/personal/conversations/{selfId}/members", new { userId = bobId }))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            using (var r = await alice.DeleteAsync($"/api/personal/conversations/{selfId}/members/{aliceId}"))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            using (var r = await alice.PostAsync($"/api/personal/conversations/{selfId}/leave", null))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            using (var r = await alice.PostAsJsonAsync($"/api/personal/conversations/{selfId}/transfer-ownership", new { newOwnerUserId = bobId }))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            using (var r = await alice.PutAsJsonAsync($"/api/personal/conversations/{selfId}/admins/{bobId}", new { isAdmin = true }))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            using (var r = await alice.PutAsync($"/api/personal/people/blocks/{aliceId}", null))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            using (var r = await alice.PostAsJsonAsync("/api/personal/people/reports", new { reportedUserId = aliceId, reason = "self" }))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            // 3. Disabled owner loses access to Self Chat immediately
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Users.Where(u => u.Id == aliceId)
                    .ExecuteUpdateAsync(set => set.SetProperty(u => u.Status, AccountStatus.Disabled));
            }

            using (var disabledResp = await alice.GetAsync($"/api/personal/conversations/{selfId}/messages"))
                Assert.Equal(HttpStatusCode.Forbidden, disabledResp.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempStorage))
            {
                Directory.Delete(tempStorage, recursive: true);
            }
        }
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
            password = "Self-chat-password-2026!",
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

    private static async Task<Guid> StagePdfAsync(HttpClient client, Guid conversationId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(Guid.NewGuid().ToString("N")), "clientUploadId");
        using var resp = await client.PostAsync($"/api/personal/conversations/{conversationId}/attachments", form);
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
