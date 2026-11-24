using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class VoiceNotesM634Tests(PostgresContainerFixture pg, ITestOutputHelper output)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task VoiceNotesUploadValidationPlaybackRangeHeadersSharedAndAuthz()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m634-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);
        try
        {
            await using var app = BuildHost(
                "--NexusStorage:Provider=Local",
                $"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();

            // Verify MIME parameter normalization helper directly
            Assert.Equal("audio/webm", FileContentInspector.NormalizeContentType("audio/webm;codecs=opus"));
            Assert.Equal("audio/ogg", FileContentInspector.NormalizeContentType("audio/ogg; codecs=opus"));
            Assert.Equal("audio/mp4", FileContentInspector.NormalizeContentType("AUDIO/MP4;codecs=mp4a.40.2"));

            var suffix = Guid.NewGuid().ToString("N")[..6];
            const string pw = "VoiceM634-Strong-Password-2026!";

            // 1. Register users: Alice (OrgA owner), Bob (OrgA member), Carol (OrgA Unverified), Dave (OrgA group rejoin), Eve (OrgB owner)
            var (alice, aliceId) = await RegisterAndLoginAsync(app, $"va_{suffix}", pw, "Alice Voice");
            var (bob, bobId) = await RegisterAndLoginAsync(app, $"vb_{suffix}", pw, "Bob Voice");
            var (carol, carolId) = await RegisterAndLoginAsync(app, $"vc_{suffix}", pw, "Carol Unverified");
            var (dave, daveId) = await RegisterAndLoginAsync(app, $"vd_{suffix}", pw, "Dave Rejoin");
            var (eve, _) = await RegisterAndLoginAsync(app, $"ve_{suffix}", pw, "Eve OrgB");

            // Create OrgA and OrgB
            var orgAId = await CreateOrgAsync(alice, $"OrgA Voice {suffix}");
            var orgBId = await CreateOrgAsync(eve, $"OrgB Voice {suffix}");

            await InviteAndAcceptOrgAsync(alice, bob, orgAId);
            await InviteAndAcceptOrgAsync(alice, carol, orgAId);
            await InviteAndAcceptOrgAsync(alice, dave, orgAId);

            // Mark Bob as Verified and Carol as Unverified in OrgA for SEC-20b
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.OrganizationMemberships
                    .Where(m => m.OrganizationId == orgAId && m.Id == bobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.TrustLevel, TrustLevel.Verified));
                await db.OrganizationMemberships
                    .Where(m => m.OrganizationId == orgAId && m.Id == carolId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.TrustLevel, TrustLevel.Unverified));
            }

            // Personal DM (Alice <-> Bob) + Self Chat (Alice)
            var personalDmId = await CreatePersonalDmAsync(alice, bobId);
            var selfChatId = await GetSelfChatIdAsync(alice);

            // Build valid synthetic voice-note payloads
            var webmBytes = BuildValidWebm(128);
            var oggBytes = BuildValidOggOpus(128);
            var mp4Bytes = BuildValidMp4M4a(128);

            // 2. Reject spoofed / disguised payloads (400 invalid_file_type)
            using (var exeAsWebm = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"spoof-exe-{suffix}", "voice.webm", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00], "audio/webm", "5000"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, exeAsWebm.StatusCode);
            }

            using (var htmlAsOgg = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"spoof-html-{suffix}", "voice.ogg", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), "audio/ogg", "5000"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, htmlAsOgg.StatusCode);
            }

            // Bare EBML without "webm" DocType
            using (var badEbml = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"spoof-ebml-{suffix}", "voice.webm", [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0x82, 0x84, 0x65, 0x76, 0x69, 0x6C], "audio/webm", "5000"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, badEbml.StatusCode);
            }

            // OggS without OpusHead/Vorbis header
            var fakeOgg = new byte[48];
            Encoding.ASCII.GetBytes("OggS").CopyTo(fakeOgg, 0);
            Encoding.ASCII.GetBytes("TheoraFake").CopyTo(fakeOgg, 28);
            using (var badOgg = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"spoof-ogg-{suffix}", "voice.ogg", fakeOgg, "audio/ogg", "5000"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, badOgg.StatusCode);
            }

            // MP4 with non-audio brand "qt  "
            var fakeMp4 = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(fakeMp4.AsSpan(0, 4), 24u);
            Encoding.ASCII.GetBytes("ftypqt  ").CopyTo(fakeMp4, 4);
            using (var badMp4 = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"spoof-mp4-{suffix}", "voice.m4a", fakeMp4, "audio/mp4", "5000"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, badMp4.StatusCode);
            }

            // 3. Reject invalid durations (<=0, >120000, non-numeric) -> 400 invalid_voice_note_duration
            foreach (var badDur in new[] { "0", "-10", "120001", "notanumber" })
            {
                using var durResp = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{personalDmId}/attachments",
                    $"bad-dur-{badDur}-{suffix}", "voice.webm", webmBytes, "audio/webm;codecs=opus", badDur);
                Assert.Equal(HttpStatusCode.BadRequest, durResp.StatusCode);
                var errJson = JsonDocument.Parse(await durResp.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal("invalid_voice_note_duration", errJson.GetProperty("code").GetString());
            }

            // 4. Reject oversized voice note (> 5 MB) -> 413 voice_note_too_large
            var oversizedWebm = BuildValidWebm(5 * 1024 * 1024 + 1);
            using (var bigResp = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"big-voice-{suffix}", "voice.webm", oversizedWebm, "audio/webm;codecs=opus", "15000"))
            {
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bigResp.StatusCode);
                var bigJson = JsonDocument.Parse(await bigResp.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal("voice_note_too_large", bigJson.GetProperty("code").GetString());
            }

            // 5. Valid uploads for all 3 formats (audio/webm, audio/ogg, audio/mp4) with MIME params normalized
            var (webmId, webmAudioUrl, webmDownloadUrl) = await UploadAndAssertAudioAsync(
                alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"ok-webm-{suffix}", "note1.webm", webmBytes, "audio/webm;codecs=opus", "14500", "audio/webm", 14500);
            var (oggId, _, _) = await UploadAndAssertAudioAsync(
                alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"ok-ogg-{suffix}", "note2.ogg", oggBytes, "audio/ogg;codecs=opus", "23000", "audio/ogg", 23000);
            var (mp4Id, _, _) = await UploadAndAssertAudioAsync(
                alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"ok-mp4-{suffix}", "note3.m4a", mp4Bytes, "audio/mp4;codecs=mp4a.40.2", "120000", "audio/mp4", 120000);

            // Verify DB CHECK constraint ck_attachments_duration_ms directly on PG 17
            await using (var conn = new NpgsqlConnection(pg.AdminConnectionString))
            {
                await conn.OpenAsync();
                await using var badZero = new NpgsqlCommand("UPDATE attachments SET duration_ms = 0 WHERE id = @id", conn);
                badZero.Parameters.AddWithValue("id", webmId);
                await Assert.ThrowsAsync<PostgresException>(() => badZero.ExecuteNonQueryAsync());

                await using var badHigh = new NpgsqlCommand("UPDATE attachments SET duration_ms = 120001 WHERE id = @id", conn);
                badHigh.Parameters.AddWithValue("id", webmId);
                await Assert.ThrowsAsync<PostgresException>(() => badHigh.ExecuteNonQueryAsync());
            }

            // Attach the 3 voice notes to a DM message
            var dmMsgId = await SendMessageAsync(alice, $"/api/personal/conversations/{personalDmId}/messages",
                $"dm-voice-{suffix}", "", [webmId, oggId, mp4Id]);

            // Upload a plain text file in the same DM to verify non-audio is rejected on /audio
            Guid txtAttId;
            using (var txtResp = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{personalDmId}/attachments",
                $"txt-{suffix}", "notes.txt", Encoding.UTF8.GetBytes("hello voice test"), "text/plain", null))
            {
                Assert.Equal(HttpStatusCode.Created, txtResp.StatusCode);
                var txtJson = JsonDocument.Parse(await txtResp.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment");
                txtAttId = txtJson.GetProperty("id").GetGuid();
                Assert.Equal(JsonValueKind.Null, txtJson.GetProperty("audioUrl").ValueKind);
                Assert.Equal(JsonValueKind.Null, txtJson.GetProperty("durationMs").ValueKind);
            }
            await SendMessageAsync(alice, $"/api/personal/conversations/{personalDmId}/messages", $"dm-txt-{suffix}", "txt", [txtAttId]);

            // 6. Authorized playback: 200 OK + inline + nosniff + no-store + CSP media-src 'self' blob:
            using (var playResp = await bob.GetAsync(webmAudioUrl))
            {
                Assert.Equal(HttpStatusCode.OK, playResp.StatusCode);
                Assert.Equal("audio/webm", playResp.Content.Headers.ContentType?.MediaType);
                Assert.Contains("inline", playResp.Content.Headers.ContentDisposition?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.Equal("nosniff", playResp.Headers.GetValues("X-Content-Type-Options").Single());
                Assert.Contains("no-store", playResp.Headers.CacheControl?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                var csp = playResp.Headers.GetValues("Content-Security-Policy").Single();
                Assert.Contains("media-src 'self' blob:", csp, StringComparison.Ordinal);
                var returned = await playResp.Content.ReadAsByteArrayAsync();
                Assert.Equal(webmBytes, returned);
            }

            // Range seeking: bytes=0-15 -> 206 Partial Content
            using (var rangeReq = new HttpRequestMessage(HttpMethod.Get, webmAudioUrl))
            {
                rangeReq.Headers.Range = new RangeHeaderValue(0, 15);
                using var rangeResp = await bob.SendAsync(rangeReq);
                Assert.Equal(HttpStatusCode.PartialContent, rangeResp.StatusCode);
                Assert.Equal(16, (await rangeResp.Content.ReadAsByteArrayAsync()).Length);
                Assert.NotNull(rangeResp.Content.Headers.ContentRange);
            }

            // Download endpoint stays strictly attachment + nosniff (SEC-11)
            using (var dlResp = await bob.GetAsync(webmDownloadUrl))
            {
                Assert.Equal(HttpStatusCode.OK, dlResp.StatusCode);
                Assert.Contains("attachment", dlResp.Content.Headers.ContentDisposition?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.Equal("nosniff", dlResp.Headers.GetValues("X-Content-Type-Options").Single());
            }

            // Non-audio attachment on /audio -> 400 audio_only
            using (var nonAudioResp = await bob.GetAsync($"/api/personal/files/{txtAttId}/audio"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, nonAudioResp.StatusCode);
                var nonAudioJson = JsonDocument.Parse(await nonAudioResp.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal("audio_only", nonAudioJson.GetProperty("code").GetString());
            }

            // Shared View (Media tab includes audio with durationMs & audioUrl)
            using (var sharedResp = await bob.GetAsync($"/api/personal/conversations/{personalDmId}/shared?tab=media&limit=50"))
            {
                Assert.Equal(HttpStatusCode.OK, sharedResp.StatusCode);
                var sharedItems = JsonDocument.Parse(await sharedResp.Content.ReadAsStringAsync()).RootElement.GetProperty("items");
                Assert.Equal(3, sharedItems.GetArrayLength());
                Assert.Contains(sharedItems.EnumerateArray(), item =>
                    item.GetProperty("id").GetGuid() == webmId &&
                    item.GetProperty("durationMs").GetInt32() == 14500 &&
                    item.GetProperty("audioUrl").GetString() == webmAudioUrl);
            }

            // 7. Self Chat owner-only boundary
            var (selfVoiceId, selfAudioUrl, _) = await UploadAndAssertAudioAsync(
                alice, $"/api/personal/conversations/{selfChatId}/attachments",
                $"self-voice-{suffix}", "memo.webm", webmBytes, "audio/webm", "4200", "audio/webm", 4200);
            await SendMessageAsync(alice, $"/api/personal/conversations/{selfChatId}/messages", $"self-msg-{suffix}", "", [selfVoiceId]);
            Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(selfAudioUrl)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(selfAudioUrl)).StatusCode);

            // 8. Soft-deleted message voice note -> 403 even when VisibleFromSeq == 0
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Messages
                    .Where(m => m.Id == dmMsgId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow));
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(webmAudioUrl)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(webmDownloadUrl)).StatusCode);

            // 9. SEC-18 VisibleFromSeq on OrgA Group Chat: Dave leaves & rejoins -> pre-join voice note denied (403)
            var groupId = await CreateOrgGroupAsync(alice, orgAId, $"Voice Group {suffix}", [bobId, daveId]);
            var (preJoinVoiceId, preJoinAudioUrl, _) = await UploadAndAssertAudioAsync(
                alice, $"/api/organizations/{orgAId}/conversations/{groupId}/attachments",
                $"prejoin-{suffix}", "prejoin.webm", webmBytes, "audio/webm", "7000", "audio/webm", 7000);
            await SendMessageAsync(alice, $"/api/organizations/{orgAId}/conversations/{groupId}/messages",
                $"prejoin-msg-{suffix}", "", [preJoinVoiceId]);
            Assert.Equal(HttpStatusCode.OK, (await dave.GetAsync(preJoinAudioUrl)).StatusCode);

            // Remove Dave and re-add Dave -> VisibleFromSeq advances past pre-join message
            using (var remDave = await SendAuthedAsync(alice, HttpMethod.Delete,
                $"/api/organizations/{orgAId}/conversations/{groupId}/members/{daveId}", null))
            {
                Assert.True(remDave.IsSuccessStatusCode);
            }
            using (var addDave = await SendAuthedAsync(alice, HttpMethod.Post,
                $"/api/organizations/{orgAId}/conversations/{groupId}/members", new { userId = daveId }))
            {
                Assert.True(addDave.IsSuccessStatusCode);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await dave.GetAsync(preJoinAudioUrl)).StatusCode);

            // 10. OrgA Official Community Channel: SEC-20b Unverified denied, OrgB outsider denied, archived channel (409 upload / 200 playback), Org removal revocation
            var (commId, channelId) = await CreateOfficialCommunityAndChannelAsync(alice, orgAId, $"Official Voice {suffix}");
            await AddCommunityMemberAsync(alice, orgAId, commId, bobId);
            await AddCommunityMemberAsync(alice, orgAId, commId, carolId);

            // Designate community Official so Carol (Unverified, still a CommunityMember) is blocked live by SEC-20b
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Communities
                    .Where(c => c.Id == commId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsOfficial, true));
            }

            var (chVoiceId, chAudioUrl, _) = await UploadAndAssertAudioAsync(
                alice, $"/api/organizations/{orgAId}/conversations/{channelId}/attachments",
                $"ch-voice-{suffix}", "announcement.ogg", oggBytes, "audio/ogg", "18000", "audio/ogg", 18000);
            using (var postResp = await SendAuthedAsync(alice, HttpMethod.Post,
                $"/api/organizations/{orgAId}/channels/{channelId}/posts",
                new { title = "Voice update", body = "Listen below", attachmentIds = new[] { chVoiceId } }))
            {
                Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
            }

            Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync(chAudioUrl)).StatusCode);
            // SEC-20b: Unverified Carol cannot play Official community channel audio
            Assert.Equal(HttpStatusCode.Forbidden, (await carol.GetAsync(chAudioUrl)).StatusCode);
            // OrgB outsider Eve denied
            Assert.Equal(HttpStatusCode.Forbidden, (await eve.GetAsync(chAudioUrl)).StatusCode);

            // Archive channel -> upload returns 409 channel_archived, playback still 200
            using (var archResp = await SendAuthedAsync(alice, HttpMethod.Post,
                $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/archive", null))
            {
                Assert.Equal(HttpStatusCode.OK, archResp.StatusCode);
            }
            using (var archivedUpload = await UploadVoiceNoteAsync(alice, $"/api/organizations/{orgAId}/conversations/{channelId}/attachments",
                $"arch-voice-{suffix}", "blocked.webm", webmBytes, "audio/webm", "3000"))
            {
                Assert.Equal(HttpStatusCode.Conflict, archivedUpload.StatusCode);
                var archJson = JsonDocument.Parse(await archivedUpload.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal("channel_archived", archJson.GetProperty("code").GetString());
            }
            Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync(chAudioUrl)).StatusCode);

            // Remove Bob from OrgA -> immediate revocation (403)
            using (var removeBobOrg = await SendAuthedAsync(alice, HttpMethod.Delete,
                $"/api/organizations/{orgAId}/members/{bobId}", null))
            {
                Assert.True(removeBobOrg.IsSuccessStatusCode);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(chAudioUrl)).StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task VoiceNotesClamAvFailClosedQuarantine()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m634-clam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clamPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var serverTask = RunMockClamdAsync(listener, cts.Token);

        try
        {
            await using var app = BuildHost(
                "--NexusStorage:Provider=Local",
                $"--NexusStorage:LocalRootPath={tempStorage}",
                "--NexusFiles:FailClosedScanning=true",
                "--NexusFiles:ClamAvHost=127.0.0.1",
                $"--NexusFiles:ClamAvPort={clamPort}");
            await app.StartAsync();

            var suffix = Guid.NewGuid().ToString("N")[..6];
            const string pw = "VoiceClam-Strong-Password-2026!";
            var (alice, _) = await RegisterAndLoginAsync(app, $"vc1_{suffix}", pw, "Alice Clam");
            var selfChatId = await GetSelfChatIdAsync(alice);

            // 1. Clean voice note passes ClamAV with FailClosedScanning=true
            var cleanWebm = BuildValidWebm(128);
            using (var cleanResp = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{selfChatId}/attachments",
                $"clam-ok-{suffix}", "clean.webm", cleanWebm, "audio/webm", "5000"))
            {
                Assert.Equal(HttpStatusCode.Created, cleanResp.StatusCode);
                var att = JsonDocument.Parse(await cleanResp.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment");
                Assert.Equal("clean", att.GetProperty("scanStatus").GetString());
            }

            // 2. Infected voice note quarantined -> 422 UnprocessableEntity
            var infectedWebm = BuildValidWebm(128);
            Encoding.ASCII.GetBytes("MOCK_VIRUS_SIGNATURE").CopyTo(infectedWebm, 64);
            using (var infResp = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{selfChatId}/attachments",
                $"clam-bad-{suffix}", "infected.webm", infectedWebm, "audio/webm", "5000"))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, infResp.StatusCode);
                var err = JsonDocument.Parse(await infResp.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal("file_rejected_by_scanner", err.GetProperty("code").GetString());
            }

            // 3. Stop ClamAV -> fail-closed returns 503 ServiceUnavailable
            cts.Cancel();
            listener.Stop();
            await serverTask;

            using (var downResp = await UploadVoiceNoteAsync(alice, $"/api/personal/conversations/{selfChatId}/attachments",
                $"clam-down-{suffix}", "down.webm", cleanWebm, "audio/webm", "5000"))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, downResp.StatusCode);
            }
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    private WebApplication BuildHost(params string[] extraArgs)
    {
        var baseArgs = new List<string>
        {
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}",
            "--Nexus:Cors:AllowedOrigins:0=http://localhost:5173"
        };
        baseArgs.AddRange(extraArgs);
        return NexusHost.Build(baseArgs.ToArray(), additionalLogSink: new ErrorSink(output));
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            CheckCertificateRevocationList = true
        };
        return new HttpClient(handler) { BaseAddress = new Uri(address) };
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        using var resp = await client.GetAsync("/api/auth/csrf");
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAuthedAsync(HttpClient client, HttpMethod method, string url, object? body)
    {
        var token = await GetCsrfTokenAsync(client);
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-CSRF-TOKEN", token);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return await client.SendAsync(req);
    }

    private static async Task<(HttpClient Client, Guid UserId)> RegisterAndLoginAsync(WebApplication app, string username, string password, string displayName)
    {
        var client = CreateClient(app);
        using var reg = await client.PostAsJsonAsync("/api/auth/register", new { username, password, displayName, termsAccepted = true });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var userId = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        return (client, userId);
    }

    private static async Task<Guid> CreateOrgAsync(HttpClient client, string name)
    {
        using var resp = await SendAuthedAsync(client, HttpMethod.Post, "/api/organizations", new { name });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task InviteAndAcceptOrgAsync(HttpClient owner, HttpClient invitee, Guid orgId)
    {
        using var invResp = await SendAuthedAsync(owner, HttpMethod.Post, $"/api/organizations/{orgId}/invitations", new { });
        Assert.Equal(HttpStatusCode.OK, invResp.StatusCode);
        var token = JsonDocument.Parse(await invResp.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

        using var accResp = await SendAuthedAsync(invitee, HttpMethod.Post, "/api/organizations/invitations/redeem", new { token });
        Assert.Equal(HttpStatusCode.OK, accResp.StatusCode);
    }

    private static async Task<Guid> CreatePersonalDmAsync(HttpClient client, Guid targetUserId)
    {
        using var resp = await SendAuthedAsync(client, HttpMethod.Post, "/api/personal/conversations/dm", new { targetUserId });
        Assert.True(resp.IsSuccessStatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> GetSelfChatIdAsync(HttpClient client)
    {
        using var resp = await SendAuthedAsync(client, HttpMethod.Post, "/api/personal/conversations/self", null);
        Assert.True(resp.IsSuccessStatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateOrgGroupAsync(HttpClient owner, Guid orgId, string title, Guid[] memberUserIds)
    {
        var token = await GetCsrfTokenAsync(owner);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/organizations/{orgId}/conversations/groups")
        {
            Content = JsonContent.Create(new { title, memberUserIds })
        };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var resp = await owner.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(Guid CommunityId, Guid ChannelId)> CreateOfficialCommunityAndChannelAsync(HttpClient owner, Guid orgId, string name)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/organizations/{orgId}/communities")
        {
            Content = JsonContent.Create(new { name, description = "Official voice community", visibility = "Public" })
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var commResp = await owner.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, commResp.StatusCode);
        var commJson = JsonDocument.Parse(await commResp.Content.ReadAsStringAsync()).RootElement;
        var commId = commJson.GetProperty("id").GetGuid();

        using var chResp = await SendAuthedAsync(owner, HttpMethod.Post,
            $"/api/organizations/{orgId}/communities/{commId}/channels",
            new { name = "voice-updates", description = "Secondary archivable channel" });
        Assert.Equal(HttpStatusCode.Created, chResp.StatusCode);
        var channelId = JsonDocument.Parse(await chResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        return (commId, channelId);
    }

    private sealed class ErrorSink(ITestOutputHelper output) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Exception is not null) output.WriteLine(logEvent.Exception.ToString());
        }
    }

    private static async Task AddCommunityMemberAsync(HttpClient owner, Guid orgId, Guid communityId, Guid userId)
    {
        using var resp = await SendAuthedAsync(owner, HttpMethod.Post,
            $"/api/organizations/{orgId}/communities/{communityId}/members", new { userId });
        Assert.True(resp.IsSuccessStatusCode);
    }

    private static async Task<HttpResponseMessage> UploadVoiceNoteAsync(
        HttpClient client,
        string url,
        string clientUploadId,
        string fileName,
        byte[] bytes,
        string contentType,
        string? durationMs)
    {
        var token = await GetCsrfTokenAsync(client);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(clientUploadId), "clientUploadId");
        if (durationMs is not null)
        {
            form.Add(new StringContent(durationMs), "durationMs");
        }

        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(fileContent, "file", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        req.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(req);
    }

    private static async Task<(Guid Id, string AudioUrl, string DownloadUrl)> UploadAndAssertAudioAsync(
        HttpClient client,
        string url,
        string clientUploadId,
        string fileName,
        byte[] bytes,
        string clientContentType,
        string durationMs,
        string expectedCanonicalMime,
        int expectedDurationMs)
    {
        using var resp = await UploadVoiceNoteAsync(client, url, clientUploadId, fileName, bytes, clientContentType, durationMs);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var att = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment");
        Assert.Equal(expectedCanonicalMime, att.GetProperty("contentType").GetString());
        Assert.Equal(expectedDurationMs, att.GetProperty("durationMs").GetInt32());
        var audioUrl = att.GetProperty("audioUrl").GetString();
        var downloadUrl = att.GetProperty("downloadUrl").GetString();
        Assert.False(string.IsNullOrWhiteSpace(audioUrl));
        Assert.False(string.IsNullOrWhiteSpace(downloadUrl));
        return (att.GetProperty("id").GetGuid(), audioUrl!, downloadUrl!);
    }

    private static async Task<Guid> SendMessageAsync(HttpClient client, string url, string clientMsgId, string body, Guid[] attachmentIds)
    {
        using var resp = await SendAuthedAsync(client, HttpMethod.Post, url, new { clientMsgId, body, attachmentIds });
        Assert.True(resp.IsSuccessStatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static byte[] BuildValidWebm(int totalLength)
    {
        var bytes = new byte[Math.Max(64, totalLength)];
        // EBML magic 1A 45 DF A3 + DocType "webm"
        bytes[0] = 0x1A; bytes[1] = 0x45; bytes[2] = 0xDF; bytes[3] = 0xA3;
        bytes[4] = 0x9F; bytes[5] = 0x42; bytes[6] = 0x82; bytes[7] = 0x84;
        Encoding.ASCII.GetBytes("webm").CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] BuildValidOggOpus(int totalLength)
    {
        var bytes = new byte[Math.Max(64, totalLength)];
        Encoding.ASCII.GetBytes("OggS").CopyTo(bytes, 0);
        bytes[4] = 0x00; // version 0
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(bytes, 28);
        return bytes;
    }

    private static byte[] BuildValidMp4M4a(int totalLength)
    {
        var bytes = new byte[Math.Max(64, totalLength)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 24u);
        Encoding.ASCII.GetBytes("ftypM4A ").CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes("isom").CopyTo(bytes, 16);
        return bytes;
    }

    private static async Task RunMockClamdAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break;
            }

            using (client)
            await using (var stream = client.GetStream())
            {
                var cmd = new byte[10];
                await stream.ReadExactlyAsync(cmd, ct);

                using var ms = new MemoryStream();
                var lenBuf = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(lenBuf, ct);
                    var chunkLen = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
                    if (chunkLen == 0) break;
                    var data = new byte[chunkLen];
                    await stream.ReadExactlyAsync(data, ct);
                    ms.Write(data, 0, data.Length);
                }

                var payload = Encoding.UTF8.GetString(ms.ToArray());
                var reply = payload.Contains("MOCK_VIRUS_SIGNATURE", StringComparison.Ordinal)
                    ? "stream: Mock.Virus.Test FOUND\0"
                    : "stream: OK\0";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), ct);
                await stream.FlushAsync(ct);
            }
        }
    }
}
