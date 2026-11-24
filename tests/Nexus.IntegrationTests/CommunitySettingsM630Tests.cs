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
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class CommunitySettingsM630Tests(PostgresContainerFixture pg, ITestOutputHelper output)
    : IClassFixture<PostgresContainerFixture>
{
    private static readonly string[] PollOptions = ["Yes", "No"];
    private static readonly string[] PollVoteOpt1 = ["opt_1"];
    [Fact]
    public async Task CommunityAndChannelMetadataEditingPreservesIdsEmitsSignalRDeltasAndEnforcesAuthzAndCsrf()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var owner = Client(baseUri, out _);
        using var admin = Client(baseUri, out _);
        using var member = Client(baseUri, out var memberCookies);
        using var nonMember = Client(baseUri, out _);
        using var orgBUser = Client(baseUri, out var orgBCookies);

        _ = await RegisterAsync(owner, "m630_own_" + suffix);
        var adminId = await RegisterAsync(admin, "m630_adm_" + suffix);
        _ = await RegisterAsync(member, "m630_mem_" + suffix);
        _ = await RegisterAsync(nonMember, "m630_non_" + suffix);
        _ = await RegisterAsync(orgBUser, "m630_orgb_" + suffix);

        var orgAId = await CreateOrganizationAsync(owner, "Settings OrgA " + suffix);
        await InviteAndJoinAsync(owner, admin, orgAId);
        await InviteAndJoinAsync(owner, member, orgAId);
        await InviteAndJoinAsync(owner, nonMember, orgAId);
        _ = await CreateOrganizationAsync(orgBUser, "Settings OrgB " + suffix);

        // Create Community + join admin & member + promote admin
        var comm = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
            new { name = "Initial Community " + suffix, description = "Original desc", visibility = "Public" }, HttpStatusCode.Created);
        var commId = comm.GetProperty("id").GetGuid();
        var mainFeedId = comm.GetProperty("mainFeedConversationId").GetGuid();
        await PostAsync(admin, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);
        await PostAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);
        await PutAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/admins/{adminId}", new { isAdmin = true }, HttpStatusCode.OK);

        // Connect SignalR for member (OrgA) and orgBUser (OrgB)
        var memberDeltas = Channel.CreateUnbounded<JsonElement>();
        var orgBDeltas = Channel.CreateUnbounded<JsonElement>();
        await using var hubMember = await ConnectPersonalHubAsync(baseUri, memberCookies, memberDeltas);
        await using var hubOrgB = await ConnectPersonalHubAsync(baseUri, orgBCookies, orgBDeltas);

        // 1. CSRF required on PUT /communities/{id}
        using (var noCsrf = await owner.PutAsJsonAsync($"/api/organizations/{orgAId}/communities/{commId}", new { name = "No CSRF", description = "x" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            Assert.Equal("invalid_csrf", (await noCsrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // Validation bounds
        await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}", new { name = "   ", description = "x" }, HttpStatusCode.BadRequest, HttpMethod.Put);
        await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}", new { name = "Valid", description = new string('d', 501) }, HttpStatusCode.BadRequest, HttpMethod.Put);

        // Ordinary member, non-member, and OrgB outsider are denied (403)
        await ProtectedAsync(member, $"/api/organizations/{orgAId}/communities/{commId}", new { name = "Hacked", description = "x" }, HttpStatusCode.Forbidden, HttpMethod.Put);
        await ProtectedAsync(nonMember, $"/api/organizations/{orgAId}/communities/{commId}", new { name = "Hacked", description = "x" }, HttpStatusCode.Forbidden, HttpMethod.Put);
        await ProtectedAsync(orgBUser, $"/api/organizations/{orgAId}/communities/{commId}", new { name = "Hacked", description = "x" }, HttpStatusCode.Forbidden, HttpMethod.Put);

        // 2. Owner edits Community metadata -> stable IDs, main feed title synced, audit log, SignalR delta
        var updatedComm = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}",
            new { name = "Renamed Community " + suffix, description = "Updated community description" }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal(commId, updatedComm.GetProperty("id").GetGuid());
        Assert.Equal("Renamed Community " + suffix, updatedComm.GetProperty("name").GetString());
        Assert.Equal("Updated community description", updatedComm.GetProperty("description").GetString());

        // Verify persistence via fresh GET /communities (regression check against detached AsNoTracking entity)
        var reloadedComms = await GetAsync(member, $"/api/organizations/{orgAId}/communities");
        var reloadedComm = reloadedComms.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == commId);
        Assert.Equal("Renamed Community " + suffix, reloadedComm.GetProperty("name").GetString());
        Assert.Equal("Updated community description", reloadedComm.GetProperty("description").GetString());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commEvt = await ReadDeltaTypeAsync(memberDeltas, "community_updated", timeout.Token);
        Assert.Equal(commId, commEvt.GetProperty("communityId").GetGuid());
        Assert.Equal("Renamed Community " + suffix, commEvt.GetProperty("delta").GetProperty("name").GetString());
        await Task.Delay(120);
        Assert.False(orgBDeltas.Reader.TryRead(out _));

        // 3. Admin creates sub-channel with description, then edits it -> stable ID, SignalR delta
        var createdCh = await PostAsync(admin, $"/api/organizations/{orgAId}/communities/{commId}/channels",
            new { name = "Design Ops", description = "Initial channel desc" }, HttpStatusCode.Created);
        var channelId = createdCh.GetProperty("id").GetGuid();
        Assert.Equal("Initial channel desc", createdCh.GetProperty("description").GetString());
        Assert.False(createdCh.GetProperty("isArchived").GetBoolean());

        // Main feed cannot be edited via /channels/{mainFeedId}
        var mainEdit = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{mainFeedId}",
            new { name = "Main", description = "x" }, HttpStatusCode.Conflict, HttpMethod.Put);
        Assert.Equal("main_feed_managed_by_community", mainEdit.GetProperty("code").GetString());

        // Member denied editing channel
        await ProtectedAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}",
            new { name = "Nope", description = "x" }, HttpStatusCode.Forbidden, HttpMethod.Put);

        var updatedCh = await ProtectedAsync(admin, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}",
            new { name = "Design Systems", description = "Updated channel description" }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal(channelId, updatedCh.GetProperty("id").GetGuid());
        Assert.Equal("Design Systems", updatedCh.GetProperty("name").GetString());
        Assert.Equal("Updated channel description", updatedCh.GetProperty("description").GetString());

        var reloadedAfterCh = await GetAsync(member, $"/api/organizations/{orgAId}/communities");
        var reloadedCh = reloadedAfterCh.GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == commId)
            .GetProperty("channels").EnumerateArray()
            .Single(ch => ch.GetProperty("id").GetGuid() == channelId);
        Assert.Equal("Design Systems", reloadedCh.GetProperty("name").GetString());
        Assert.Equal("Updated channel description", reloadedCh.GetProperty("description").GetString());

        var chEvt = await ReadDeltaTypeAsync(memberDeltas, "channel_updated", timeout.Token);
        Assert.Equal(channelId, chEvt.GetProperty("delta").GetProperty("channelId").GetGuid());
        Assert.Equal("Design Systems", chEvt.GetProperty("delta").GetProperty("name").GetString());

        // 4. SEC-20b: Mark community Official -> Unverified admin is denied CommunityManageSettings (403)
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Communities.Where(c => c.Id == commId)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.IsOfficial, true));
            var audits = await db.AuditLogs.AsNoTracking()
                .Where(a => a.OrganizationId == orgAId && (a.Action == "COMMUNITY_SETTINGS_UPDATED" || a.Action == "CHANNEL_SETTINGS_UPDATED"))
                .Select(a => a.Action)
                .ToListAsync();
            Assert.Contains("COMMUNITY_SETTINGS_UPDATED", audits);
            Assert.Contains("CHANNEL_SETTINGS_UPDATED", audits);
        }

        await ProtectedAsync(admin, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}",
            new { name = "Unverified Official Edit", description = "blocked" }, HttpStatusCode.Forbidden, HttpMethod.Put);
        await ProtectedAsync(admin, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/archive",
            null, HttpStatusCode.Forbidden, HttpMethod.Post);
    }

    [Fact]
    public async Task ChannelArchiveAndRestoreEnforceReadOnlyWritesPreserveReadsFilesAndRejectInvalidTransitions()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m630-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost($"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];

            using var owner = Client(baseUri, out _);
            using var member = Client(baseUri, out var memberCookies);
            using var outsider = Client(baseUri, out _);

            var ownerId = await RegisterAsync(owner, "arch_own_" + suffix);
            _ = await RegisterAsync(member, "arch_mem_" + suffix);
            _ = await RegisterAsync(outsider, "arch_out_" + suffix);

            var orgAId = await CreateOrganizationAsync(owner, "Archive OrgA " + suffix);
            await InviteAndJoinAsync(owner, member, orgAId);
            _ = await CreateOrganizationAsync(outsider, "Archive OrgB " + suffix);

            var comm = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
                new { name = "Archive Comm " + suffix, visibility = "Public" }, HttpStatusCode.Created);
            var commId = comm.GetProperty("id").GetGuid();
            var mainFeedId = comm.GetProperty("mainFeedConversationId").GetGuid();
            await PostAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);

            var ch = await PostAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels",
                new { name = "Legacy Project", description = "Historical archive" }, HttpStatusCode.Created);
            var channelId = ch.GetProperty("id").GetGuid();

            // Seed announcement + poll + existing PDF file in sub-channel before archiving
            var post = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{channelId}/posts", new
            {
                title = "Final Milestone",
                body = "Project completed.",
                isAnnouncement = true,
                requiresAcknowledgment = true,
                poll = new { question = "Retro?", options = PollOptions }
            }, HttpStatusCode.Created);
            var postId = post.GetProperty("id").GetGuid();
            var pollId = post.GetProperty("pollId").GetGuid();
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");
            var attachmentId = await StagePdfAsync(owner, orgAId, channelId, "spec.pdf", pdfBytes);
            await using (var attachScope = app.Services.CreateAsyncScope())
            {
                var attachDb = attachScope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await attachDb.Attachments.Where(a => a.Id == attachmentId)
                    .ExecuteUpdateAsync(set => set.SetProperty(a => a.State, Nexus.Web.Modules.Files.AttachmentState.Attached));
            }
            var postLink = ContentLinkCodec.Encode("p", orgAId, channelId, postId);

            // DB CHECK constraint blocks archiving main feed directly
            await using (var conn = new NpgsqlConnection(pg.RuntimeConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "UPDATE conversations SET archived_at = NOW(), archived_by_user_id = @uid WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("uid", ownerId);
                cmd.Parameters.AddWithValue("id", mainFeedId);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
            }

            // API rejects archiving main feed & restoring non-archived channel
            var mainArch = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{mainFeedId}/archive",
                null, HttpStatusCode.Conflict, HttpMethod.Post);
            Assert.Equal("cannot_archive_main_feed", mainArch.GetProperty("code").GetString());

            var notArch = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/restore",
                null, HttpStatusCode.Conflict, HttpMethod.Post);
            Assert.Equal("channel_not_archived", notArch.GetProperty("code").GetString());

            // Connect SignalR for member
            var memberDeltas = Channel.CreateUnbounded<JsonElement>();
            await using var hubMember = await ConnectPersonalHubAsync(baseUri, memberCookies, memberDeltas);

            // 1. Archive channel
            var archived = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/archive",
                null, HttpStatusCode.OK, HttpMethod.Post);
            Assert.True(archived.GetProperty("isArchived").GetBoolean());
            Assert.Equal(ownerId, archived.GetProperty("archivedByUserId").GetGuid());

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var archEvt = await ReadDeltaTypeAsync(memberDeltas, "channel_archived", timeout.Token);
            Assert.Equal(channelId, archEvt.GetProperty("delta").GetProperty("channelId").GetGuid());

            // Double-archive -> 409 channel_already_archived
            var dblArch = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/archive",
                null, HttpStatusCode.Conflict, HttpMethod.Post);
            Assert.Equal("channel_already_archived", dblArch.GetProperty("code").GetString());

            // 2. Exhaustive 409 channel_archived checks on ALL mutations for authorized members/owner
            async Task AssertArchivedConflict(HttpResponseMessage resp)
            {
                Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("channel_archived", json.GetProperty("code").GetString());
            }

            using (var r = await owner.PostAsJsonAsync($"/api/organizations/{orgAId}/channels/{channelId}/posts", new { body = "Blocked" }))
                await AssertArchivedConflict(r);
            using (var r = await owner.PutAsJsonAsync($"/api/organizations/{orgAId}/posts/{postId}/pin", new { isPinned = true }))
                await AssertArchivedConflict(r);
            using (var r = await member.PostAsJsonAsync($"/api/organizations/{orgAId}/posts/{postId}/comments", new { body = "Blocked comment" }))
                await AssertArchivedConflict(r);
            using (var r = await member.PutAsync($"/api/organizations/{orgAId}/posts/{postId}/reactions/%F0%9F%91%8D", null))
                await AssertArchivedConflict(r);
            using (var r = await member.DeleteAsync($"/api/organizations/{orgAId}/posts/{postId}/reactions/%F0%9F%91%8D"))
                await AssertArchivedConflict(r);
            using (var r = await member.PutAsync($"/api/organizations/{orgAId}/posts/{postId}/acknowledge", null))
                await AssertArchivedConflict(r);
            using (var r = await owner.PostAsync($"/api/organizations/{orgAId}/posts/{postId}/remind", null))
                await AssertArchivedConflict(r);
            using (var r = await member.PutAsJsonAsync($"/api/organizations/{orgAId}/polls/{pollId}/vote", new { optionIds = PollVoteOpt1 }))
                await AssertArchivedConflict(r);
            using (var r = await UploadPdfRawAsync(owner, orgAId, channelId, "blocked.pdf", pdfBytes))
                await AssertArchivedConflict(r);
            await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}",
                new { name = "Blocked Edit", description = "x" }, HttpStatusCode.Conflict, HttpMethod.Put);

            // Outsider gets 403 (never 409!)
            using (var r = await outsider.PostAsJsonAsync($"/api/organizations/{orgAId}/channels/{channelId}/posts", new { body = "Outsider" }))
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

            // 3. Reads, existing files, acknowledgment report, and content links remain accessible!
            var listComms = await GetAsync(member, $"/api/organizations/{orgAId}/communities");
            var commItem = listComms.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == commId);
            var chItem = commItem.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == channelId);
            Assert.True(chItem.GetProperty("isArchived").GetBoolean());
            Assert.Equal("Historical archive", chItem.GetProperty("description").GetString());

            var feed = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/posts");
            Assert.True(feed.GetProperty("isArchived").GetBoolean());
            Assert.False(feed.GetProperty("canPost").GetBoolean());
            Assert.Equal("Historical archive", feed.GetProperty("channelDescription").GetString());
            Assert.Single(feed.GetProperty("items").EnumerateArray());

            using (var dl = await member.GetAsync($"/api/organizations/{orgAId}/files/{attachmentId}/download"))
            {
                Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
                Assert.Equal(pdfBytes, await dl.Content.ReadAsByteArrayAsync());
            }

            var resolved = await GetAsync(member, $"/api/links/resolve?target={Uri.EscapeDataString(postLink)}");
            Assert.Equal(postId, resolved.GetProperty("postId").GetGuid());
            _ = await GetAsync(owner, $"/api/organizations/{orgAId}/posts/{postId}/acknowledgments");

            // 4. Restore channel -> SignalR delta, double-restore 409, writes work again
            var restored = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/restore",
                null, HttpStatusCode.OK, HttpMethod.Post);
            Assert.False(restored.GetProperty("isArchived").GetBoolean());
            Assert.Equal(JsonValueKind.Null, restored.GetProperty("archivedAt").ValueKind);

            var restoreEvt = await ReadDeltaTypeAsync(memberDeltas, "channel_restored", timeout.Token);
            Assert.Equal(channelId, restoreEvt.GetProperty("delta").GetProperty("channelId").GetGuid());

            var dblRestore = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/restore",
                null, HttpStatusCode.Conflict, HttpMethod.Post);
            Assert.Equal("channel_not_archived", dblRestore.GetProperty("code").GetString());

            // Writes work immediately after restore
            await PostAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/posts", new { body = "Back online!" }, HttpStatusCode.Created);
            await PostAsync(member, $"/api/organizations/{orgAId}/posts/{postId}/comments", new { body = "Restored comment!" }, HttpStatusCode.Created);

            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var actions = await db.AuditLogs.AsNoTracking()
                .Where(a => a.OrganizationId == orgAId && (a.Action == "CHANNEL_ARCHIVED" || a.Action == "CHANNEL_RESTORED"))
                .Select(a => a.Action)
                .ToListAsync();
            Assert.Contains("CHANNEL_ARCHIVED", actions);
            Assert.Contains("CHANNEL_RESTORED", actions);
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
            password = "Settings-password-2026!",
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

    private static async Task<HubConnection> ConnectPersonalHubAsync(Uri baseUri, CookieContainer cookies, Channel<JsonElement> sink)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, OrganizationScope.PersonalHub), opts =>
            {
                opts.Cookies = cookies;
                opts.Headers["Origin"] = baseUri.GetLeftPart(UriPartial.Authority);
            })
            .Build();
        hub.On<JsonElement>("CommunityFeedChanged", payload => sink.Writer.TryWrite(payload));
        await hub.StartAsync();
        return hub;
    }

    private static async Task<JsonElement> ReadDeltaTypeAsync(Channel<JsonElement> ch, string expectedType, CancellationToken ct)
    {
        while (true)
        {
            var item = await ch.Reader.ReadAsync(ct);
            if (item.TryGetProperty("delta", out var delta) &&
                delta.ValueKind == JsonValueKind.Object &&
                delta.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() == expectedType)
            {
                return item;
            }
        }
    }

    private static async Task<HttpResponseMessage> UploadPdfRawAsync(HttpClient client, Guid orgId, Guid conversationId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(Guid.NewGuid().ToString("N")), "clientUploadId");
        return await client.PostAsync($"/api/organizations/{orgId}/conversations/{conversationId}/attachments", form);
    }

    private static async Task<Guid> StagePdfAsync(HttpClient client, Guid orgId, Guid conversationId, string fileName, byte[] content)
    {
        using var resp = await UploadPdfRawAsync(client, orgId, conversationId, fileName, content);
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

    private static async Task<JsonElement> PutAsync(HttpClient client, string path, object body, HttpStatusCode expected)
    {
        using var res = await client.PutAsJsonAsync(path, body);
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
