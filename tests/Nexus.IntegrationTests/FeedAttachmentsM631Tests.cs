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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using SkiaSharp;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class FeedAttachmentsM631Tests(PostgresContainerFixture pg, ITestOutputHelper output)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task PostAttachmentsStageBindDeliverViaSignalRAndListInChannelFilesWithSourceLinks()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m631-main-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost(extraArgs: [$"--NexusStorage:LocalRootPath={tempStorage}"]);
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];

            using var owner = Client(baseUri, out _);
            using var member = Client(baseUri, out var memberCookies);
            var ownerId = await RegisterAsync(owner, "fa_own_" + suffix);
            _ = await RegisterAsync(member, "fa_mem_" + suffix);

            var orgAId = await CreateOrganizationAsync(owner, "Files OrgA " + suffix);
            await InviteAndJoinAsync(owner, member, orgAId);

            var comm = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
                new { name = "Design Hub " + suffix, visibility = "Public" }, HttpStatusCode.Created);
            var commId = comm.GetProperty("id").GetGuid();
            await PostAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);

            var ch = await PostAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels",
                new { name = "Assets" }, HttpStatusCode.Created);
            var channelId = ch.GetProperty("id").GetGuid();

            var memberDeltas = Channel.CreateUnbounded<JsonElement>();
            await using var hubMember = await ConnectPersonalHubAsync(baseUri, memberCookies, memberDeltas);

            // 1. Stage PDF + 600x400 JPEG with embedded EXIF/COM metadata
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");
            var jpegBytes = BuildJpegWithComment(600, 400, "SECRET-CAMERA-EXIF-GPS");
            var pdfId = await StageFileAsync(owner, orgAId, channelId, "architecture.pdf", "application/pdf", pdfBytes);
            var imgId = await StageFileAsync(owner, orgAId, channelId, "diagram.jpg", "image/jpeg", jpegBytes);

            // 2. Bind both attachments to a post in a single transaction
            var created = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{channelId}/posts", new
            {
                title = "Q4 Architecture Deck",
                body = "Please review the attached PDF and diagram.",
                attachmentIds = new[] { pdfId, imgId }
            }, HttpStatusCode.Created);
            var postId = created.GetProperty("id").GetGuid();
            Assert.Equal(2, created.GetProperty("attachments").GetArrayLength());

            // SignalR post_created delta includes attachments[]
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var deltaEvt = await ReadDeltaTypeAsync(memberDeltas, "post_created", timeout.Token);
            var deltaPost = deltaEvt.GetProperty("delta").GetProperty("post");
            Assert.Equal(postId, deltaPost.GetProperty("id").GetGuid());
            Assert.Equal(2, deltaPost.GetProperty("attachments").GetArrayLength());

            // 3. Attachment-only post (empty body) succeeds
            var soloPdfId = await StageFileAsync(owner, orgAId, channelId, "solo.pdf", "application/pdf", pdfBytes);
            var soloPost = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{channelId}/posts", new
            {
                body = "",
                attachmentIds = new[] { soloPdfId }
            }, HttpStatusCode.Created);
            Assert.Single(soloPost.GetProperty("attachments").EnumerateArray());

            // 4. GET /channels/{id}/posts batch-loads attachments[]
            var feed = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/posts");
            var postItem = feed.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == postId);
            Assert.Equal(2, postItem.GetProperty("attachments").GetArrayLength());

            // 5. GET /channels/{id}/files returns postId, postTitle, uploaderDisplayName, thumbnailUrl, and M6.25 sourceLink
            var files = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/files?limit=50");
            Assert.False(files.GetProperty("isArchived").GetBoolean());
            var fileItems = files.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(3, fileItems.Count);

            var imgFile = fileItems.Single(f => f.GetProperty("id").GetGuid() == imgId);
            Assert.Equal(postId, imgFile.GetProperty("postId").GetGuid());
            Assert.Equal("Q4 Architecture Deck", imgFile.GetProperty("postTitle").GetString());
            Assert.True(imgFile.GetProperty("hasThumbnail").GetBoolean());
            Assert.Equal(JsonValueKind.String, imgFile.GetProperty("thumbnailUrl").ValueKind);
            var sourceLink = imgFile.GetProperty("sourceLink").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(sourceLink));

            // Source link resolves via M6.25 /api/links/resolve to the origin post
            var resolved = await GetAsync(member, $"/api/links/resolve?target={Uri.EscapeDataString(sourceLink)}");
            Assert.Equal("post", resolved.GetProperty("kind").GetString());
            Assert.Equal(postId, resolved.GetProperty("postId").GetGuid());
            Assert.Equal(channelId, resolved.GetProperty("conversationId").GetGuid());

            // Download PDF & preview/thumbnail JPEG; confirm EXIF comment stripped
            using (var dlPdf = await member.GetAsync(imgFile.GetProperty("downloadUrl").GetString()!))
            {
                Assert.Equal(HttpStatusCode.OK, dlPdf.StatusCode);
                var downloadedImg = await dlPdf.Content.ReadAsByteArrayAsync();
                Assert.DoesNotContain("SECRET-CAMERA-EXIF-GPS", Encoding.ASCII.GetString(downloadedImg), StringComparison.Ordinal);
            }
            using (var dlThumb = await member.GetAsync(imgFile.GetProperty("thumbnailUrl").GetString()!))
            {
                Assert.Equal(HttpStatusCode.OK, dlThumb.StatusCode);
            }

            // 6. Verify DB CHECK constraint ck_attachments_single_parent rejects both message_id and post_id
            await using (var conn = new NpgsqlConnection(pg.RuntimeConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "UPDATE attachments SET message_id = @mid WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("mid", Guid.CreateVersion7());
                cmd.Parameters.AddWithValue("id", pdfId);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.True(ex.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation);
            }

            _ = ownerId;
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidQuarantinedCrossChannelAndExcessAttachmentsRejectedWhilePreservingRecoveryAndCleanup()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m631-err-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost(
                configureBuilder: builder => builder.Services.Replace(ServiceDescriptor.Singleton<IFileScanner, KeywordTestScanner>()),
                extraArgs: [$"--NexusStorage:LocalRootPath={tempStorage}"]);
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];

            using var owner = Client(baseUri, out _);
            using var member = Client(baseUri, out _);
            _ = await RegisterAsync(owner, "fe_own_" + suffix);
            _ = await RegisterAsync(member, "fe_mem_" + suffix);

            var orgAId = await CreateOrganizationAsync(owner, "Err OrgA " + suffix);
            await InviteAndJoinAsync(owner, member, orgAId);

            var comm = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
                new { name = "Err Comm " + suffix, visibility = "Public" }, HttpStatusCode.Created);
            var commId = comm.GetProperty("id").GetGuid();
            await PostAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);

            var ch1 = (await PostAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels", new { name = "Ch1" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
            var ch2 = (await PostAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels", new { name = "Ch2" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");

            // 1. Infected file is quarantined by scanner (400/422 rejected)
            using (var infectedResp = await UploadFileRawAsync(owner, orgAId, ch1, "eicar-malware.pdf", "application/pdf", pdfBytes))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, infectedResp.StatusCode);
            }

            // 2. > 10 attachments rejected with 400 too_many_attachments
            var elevenIds = Enumerable.Range(0, 11).Select(_ => Guid.CreateVersion7()).ToArray();
            var tooMany = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{ch1}/posts", new
            {
                body = "Too many",
                attachmentIds = elevenIds
            }, HttpStatusCode.BadRequest);
            Assert.Equal("too_many_attachments", tooMany.GetProperty("code").GetString());

            // 3. Valid staged file in ch1 + invalid cross-channel file from ch2 -> 400 invalid_attachments, valid file stays Pending for recovery!
            var validCh1 = await StageFileAsync(owner, orgAId, ch1, "valid.pdf", "application/pdf", pdfBytes);
            var crossCh2 = await StageFileAsync(owner, orgAId, ch2, "cross.pdf", "application/pdf", pdfBytes);
            var otherUserCh1 = await StageFileAsync(member, orgAId, ch1, "member.pdf", "application/pdf", pdfBytes);

            var badCross = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{ch1}/posts", new
            {
                body = "Bad cross-channel",
                attachmentIds = new[] { validCh1, crossCh2 }
            }, HttpStatusCode.BadRequest);
            Assert.Equal("invalid_attachments", badCross.GetProperty("code").GetString());

            var badOtherUser = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{ch1}/posts", new
            {
                body = "Bad other user",
                attachmentIds = new[] { otherUserCh1 }
            }, HttpStatusCode.BadRequest);
            Assert.Equal("invalid_attachments", badOtherUser.GetProperty("code").GetString());

            // Recovery: validCh1 is still Pending and binds cleanly on retry!
            var recovered = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{ch1}/posts", new
            {
                body = "Recovered post",
                attachmentIds = new[] { validCh1 }
            }, HttpStatusCode.Created);
            Assert.Single(recovered.GetProperty("attachments").EnumerateArray());

            // Reusing already-attached validCh1 fails
            var reuse = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{ch1}/posts", new
            {
                body = "Reuse",
                attachmentIds = new[] { validCh1 }
            }, HttpStatusCode.BadRequest);
            Assert.Equal("invalid_attachments", reuse.GetProperty("code").GetString());

            // 4. OrphanAttachmentCleanupService deletes expired Pending upload while preserving Attached post file
            var orphanPendingId = await StageFileAsync(owner, orgAId, ch1, "orphan.pdf", "application/pdf", pdfBytes);
            var oldTime = DateTimeOffset.UtcNow.AddHours(-48);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Attachments.Where(a => a.Id == orphanPendingId || a.Id == validCh1)
                    .ExecuteUpdateAsync(set => set.SetProperty(a => a.CreatedAt, oldTime));
            }

            var cleanup = app.Services.GetRequiredService<OrphanAttachmentCleanupService>();
            var cleaned = await cleanup.CleanupOrphansAsync(DateTimeOffset.UtcNow.AddHours(-1));
            Assert.True(cleaned >= 1);

            await using (var verifyScope = app.Services.CreateAsyncScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<NexusDbContext>();
                Assert.False(await db.Attachments.AnyAsync(a => a.Id == orphanPendingId));
                Assert.True(await db.Attachments.AnyAsync(a => a.Id == validCh1 && a.State == AttachmentState.Attached));
            }
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task ChannelFilesPaginationRevocationSec20bOrgIsolationAndArchivedReadOnly()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m631-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost(extraArgs: [$"--NexusStorage:LocalRootPath={tempStorage}"]);
            await app.StartAsync();
            var baseUri = GetBaseUri(app);
            var suffix = Guid.NewGuid().ToString("N")[..6];

            using var owner = Client(baseUri, out _);
            using var member = Client(baseUri, out _);
            using var orgBUser = Client(baseUri, out _);
            var ownerId = await RegisterAsync(owner, "fpg_own_" + suffix);
            var memberId = await RegisterAsync(member, "fpg_mem_" + suffix);
            _ = await RegisterAsync(orgBUser, "fpg_orgb_" + suffix);

            var orgAId = await CreateOrganizationAsync(owner, "Page OrgA " + suffix);
            await InviteAndJoinAsync(owner, member, orgAId);
            _ = await CreateOrganizationAsync(orgBUser, "Page OrgB " + suffix);

            var comm = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
                new { name = "Paged Files Comm " + suffix, visibility = "Public" }, HttpStatusCode.Created);
            var commId = comm.GetProperty("id").GetGuid();
            await PostAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);
            var channelId = (await PostAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels",
                new { name = "Docs" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

            // Upload 1 real PDF attached to post 1, plus seed 51 tied-timestamp attached post files (= 52 total)
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");
            var realPdfId = await StageFileAsync(owner, orgAId, channelId, "real.pdf", "application/pdf", pdfBytes);
            var realPost = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{channelId}/posts", new
            {
                title = "Real Doc",
                body = "Attached real PDF",
                attachmentIds = new[] { realPdfId }
            }, HttpStatusCode.Created);
            var realPostId = realPost.GetProperty("id").GetGuid();

            var tiedTime = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
            var seededIds = new List<Guid> { realPdfId };
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                for (var i = 1; i <= 51; i++)
                {
                    var id = Guid.CreateVersion7();
                    seededIds.Add(id);
                    db.Attachments.Add(new AttachmentEntity
                    {
                        Id = id,
                        OrganizationId = orgAId,
                        ConversationId = channelId,
                        PostId = realPostId,
                        UploaderUserId = ownerId,
                        ClientUploadId = $"seed-{suffix}-{i}",
                        FileName = $"doc-{i}.pdf",
                        ContentType = "application/pdf",
                        SizeBytes = 128,
                        Sha256Hex = new string('a', 64),
                        ObjectKey = $"seeded/{id:N}",
                        State = AttachmentState.Attached,
                        ScanStatus = "clean",
                        CreatedAt = tiedTime,
                        AttachedAt = tiedTime
                    });
                }
                await db.SaveChangesAsync();
            }

            // 1. Pagination > 50 tied rows + hard cap 51 rejected
            using (var badLimit = await member.GetAsync($"/api/organizations/{orgAId}/channels/{channelId}/files?limit=51"))
                Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);

            var page1 = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/files?limit=50");
            Assert.Equal(50, page1.GetProperty("items").GetArrayLength());
            Assert.True(page1.GetProperty("hasMore").GetBoolean());
            var nextBefore = Uri.EscapeDataString(page1.GetProperty("nextBefore").GetString()!);
            var nextBeforeId = page1.GetProperty("nextBeforeId").GetGuid();

            var page2 = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/files?limit=50&before={nextBefore}&beforeId={nextBeforeId}");
            Assert.Equal(2, page2.GetProperty("items").GetArrayLength());
            Assert.False(page2.GetProperty("hasMore").GetBoolean());

            var allPagedIds = page1.GetProperty("items").EnumerateArray().Concat(page2.GetProperty("items").EnumerateArray())
                .Select(i => i.GetProperty("id").GetGuid()).Order().ToList();
            Assert.Equal(seededIds.Order().ToList(), allPagedIds);

            // 2. Archived channel: upload blocked (409), Channel Files list & download remain 200 OK
            await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/channels/{channelId}/archive", null, HttpStatusCode.OK, HttpMethod.Post);
            using (var archUpload = await UploadFileRawAsync(owner, orgAId, channelId, "blocked.pdf", "application/pdf", pdfBytes))
                Assert.Equal(HttpStatusCode.Conflict, archUpload.StatusCode);

            var archFiles = await GetAsync(member, $"/api/organizations/{orgAId}/channels/{channelId}/files?limit=10");
            Assert.True(archFiles.GetProperty("isArchived").GetBoolean());
            using (var archDl = await member.GetAsync($"/api/organizations/{orgAId}/files/{realPdfId}/download"))
                Assert.Equal(HttpStatusCode.OK, archDl.StatusCode);

            // 3. OrgB outsider gets 403 on Channel Files & download
            using (var orgBFiles = await orgBUser.GetAsync($"/api/organizations/{orgAId}/channels/{channelId}/files"))
                Assert.Equal(HttpStatusCode.Forbidden, orgBFiles.StatusCode);
            using (var orgBDl = await orgBUser.GetAsync($"/api/organizations/{orgAId}/files/{realPdfId}/download"))
                Assert.Equal(HttpStatusCode.Forbidden, orgBDl.StatusCode);

            // 4. SEC-20b: Official community blocks Unverified member from Channel Files & download
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Communities.Where(c => c.Id == commId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsOfficial, true));
            }
            using (var unvFiles = await member.GetAsync($"/api/organizations/{orgAId}/channels/{channelId}/files"))
                Assert.Equal(HttpStatusCode.Forbidden, unvFiles.StatusCode);
            using (var unvDl = await member.GetAsync($"/api/organizations/{orgAId}/files/{realPdfId}/download"))
                Assert.Equal(HttpStatusCode.Forbidden, unvDl.StatusCode);

            // Restore non-Official, then revoke member -> immediate 403
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.Communities.Where(c => c.Id == commId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsOfficial, false));
            }
            using (var removeResp = await owner.DeleteAsync($"/api/organizations/{orgAId}/communities/{commId}/members/{memberId}"))
                Assert.Equal(HttpStatusCode.NoContent, removeResp.StatusCode);

            using (var revFiles = await member.GetAsync($"/api/organizations/{orgAId}/channels/{channelId}/files"))
                Assert.Equal(HttpStatusCode.Forbidden, revFiles.StatusCode);
            using (var revDl = await member.GetAsync($"/api/organizations/{orgAId}/files/{realPdfId}/download"))
                Assert.Equal(HttpStatusCode.Forbidden, revDl.StatusCode);

            // 5. Soft-deleted post hides its attachments from Channel Files & direct download
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.ChannelPosts.Where(p => p.Id == realPostId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
            }
            var afterDeleteFiles = await GetAsync(owner, $"/api/organizations/{orgAId}/channels/{channelId}/files");
            Assert.Empty(afterDeleteFiles.GetProperty("items").EnumerateArray());
            using (var delDl = await owner.GetAsync($"/api/organizations/{orgAId}/files/{realPdfId}/download"))
                Assert.Equal(HttpStatusCode.Forbidden, delDl.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    private sealed class KeywordTestScanner : IFileScanner
    {
        public Task<FileScanResult> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(fileName.Contains("eicar", StringComparison.OrdinalIgnoreCase)
                ? new FileScanResult(FileScanVerdict.Infected, "quarantined", "Test EICAR signature")
                : new FileScanResult(FileScanVerdict.Clean, "clean", null));
    }

    private static byte[] BuildJpegWithComment(int width, int height, string comment)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(15, 118, 178));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        var raw = encoded.ToArray();
        var commentBytes = Encoding.ASCII.GetBytes(comment);
        var segLen = commentBytes.Length + 2;
        using var ms = new MemoryStream();
        ms.Write(raw, 0, 2); // SOI
        ms.WriteByte(0xFF);
        ms.WriteByte(0xFE); // COM marker
        ms.WriteByte((byte)(segLen >> 8));
        ms.WriteByte((byte)(segLen & 0xFF));
        ms.Write(commentBytes, 0, commentBytes.Length);
        ms.Write(raw, 2, raw.Length - 2);
        return ms.ToArray();
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildHost(
        Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configureBuilder = null,
        string[]? extraArgs = null)
    {
        var args = new List<string>
        {
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        };
        if (extraArgs is not null) args.AddRange(extraArgs);
        return NexusHost.Build(args.ToArray(), configureBuilder: configureBuilder, additionalLogSink: new ErrorSink(output));
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
            password = "Feed-attachments-2026!",
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

    private static async Task<HttpResponseMessage> UploadFileRawAsync(HttpClient client, Guid orgId, Guid conversationId, string fileName, string contentType, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(Guid.NewGuid().ToString("N")), "clientUploadId");
        return await client.PostAsync($"/api/organizations/{orgId}/conversations/{conversationId}/attachments", form);
    }

    private static async Task<Guid> StageFileAsync(HttpClient client, Guid orgId, Guid conversationId, string fileName, string contentType, byte[] content)
    {
        using var resp = await UploadFileRawAsync(client, orgId, conversationId, fileName, contentType, content);
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
