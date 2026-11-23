using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using SkiaSharp;

namespace Nexus.IntegrationTests;

public sealed class FilesM3Tests(PostgresContainerFixture pg)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task M3_1_M3_2_M3_4_TwoStageUpload_MagicBytes_ImageReencodeThumbnail_AndOrphanCleanup()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m3-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);
        try
        {
            await using var app = BuildHost(
                "--NexusStorage:Provider=Local",
                $"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();

            var suffix = Guid.NewGuid().ToString("N")[..6];
            const string pw = "FilesM3-Strong-Password-2026!";

            var userA = await SeedUserAsync(app.Services, $"fa_{suffix}", pw, "Alice Files", OrgRole.Member);
            var userB = await SeedUserAsync(app.Services, $"fb_{suffix}", pw, "Bob Files", OrgRole.Member);
            var userC = await SeedUserAsync(app.Services, $"fc_{suffix}", pw, "Charlie Files", OrgRole.Member);

            var clientA = await LoginAsync(app, userA.UserName!, pw);
            var clientB = await LoginAsync(app, userB.UserName!, pw);

            using var dmABResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
            var dmAB = JsonDocument.Parse(await dmABResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

            using var dmACResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userC.Id });
            var dmAC = JsonDocument.Parse(await dmACResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

            // M3.2: Reject spoofed executable (MZ header disguised as .png) and HTML/SVG
            using var exeResp = await UploadMultipartAsync(clientA, dmAB, $"exe-{suffix}", "evil.png", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]);
            Assert.Equal(HttpStatusCode.BadRequest, exeResp.StatusCode);

            using var svgResp = await UploadMultipartAsync(clientA, dmAB, $"svg-{suffix}", "vector.svg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"));
            Assert.Equal(HttpStatusCode.BadRequest, svgResp.StatusCode);

            using var htmlTxtResp = await UploadMultipartAsync(clientA, dmAB, $"html-{suffix}", "notes.txt", Encoding.UTF8.GetBytes("<html><body><script>alert(1)</script></body></html>"));
            Assert.Equal(HttpStatusCode.BadRequest, htmlTxtResp.StatusCode);

            // M3.1: Stage 1 upload valid PDF + idempotent retry + cross-conversation 409 conflict
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n%%EOF\n");
            var pdfUploadId = $"pdf-{suffix}";
            using var pdfResp1 = await UploadMultipartAsync(clientA, dmAB, pdfUploadId, "report.pdf", pdfBytes);
            Assert.Equal(HttpStatusCode.Created, pdfResp1.StatusCode);
            var pdfDoc1 = JsonDocument.Parse(await pdfResp1.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment");
            var pdfId = pdfDoc1.GetProperty("id").GetGuid();
            Assert.Equal("pending", pdfDoc1.GetProperty("state").GetString());
            Assert.Equal("application/pdf", pdfDoc1.GetProperty("contentType").GetString());

            using var pdfRetry = await UploadMultipartAsync(clientA, dmAB, pdfUploadId, "report.pdf", pdfBytes);
            Assert.Equal(HttpStatusCode.OK, pdfRetry.StatusCode);
            Assert.Equal(pdfId, JsonDocument.Parse(await pdfRetry.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment").GetProperty("id").GetGuid());

            using var pdfConflict = await UploadMultipartAsync(clientA, dmAC, pdfUploadId, "report.pdf", pdfBytes);
            Assert.Equal(HttpStatusCode.Conflict, pdfConflict.StatusCode);

            // M3.4: Upload 600x300 PNG with injected metadata -> re-encoded (metadata stripped) + thumbnail <= 400px (UX-08)
            const string exifSecret = "NEXUS_EXIF_GPS_SECRET_MARKER";
            var rawPngWithMeta = CreatePngWithTextChunk(600, 300, exifSecret);
            Assert.Contains(exifSecret, Encoding.Latin1.GetString(rawPngWithMeta));

            using var imgResp = await UploadMultipartAsync(clientA, dmAB, $"img-{suffix}", "screenshot.png", rawPngWithMeta);
            Assert.Equal(HttpStatusCode.Created, imgResp.StatusCode);
            var imgAtt = JsonDocument.Parse(await imgResp.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment");
            var imgId = imgAtt.GetProperty("id").GetGuid();
            Assert.Equal(600, imgAtt.GetProperty("imageWidth").GetInt32());
            Assert.Equal(300, imgAtt.GetProperty("imageHeight").GetInt32());
            var thumbUrl = imgAtt.GetProperty("thumbnailUrl").GetString();
            Assert.False(string.IsNullOrEmpty(thumbUrl));

            // Verify re-encoded download stripped metadata and thumbnail is <= 400px wide
            using var imgDownload = await clientA.GetAsync($"/api/files/{imgId}/download");
            Assert.Equal(HttpStatusCode.OK, imgDownload.StatusCode);
            var downloadedPng = await imgDownload.Content.ReadAsByteArrayAsync();
            Assert.DoesNotContain(exifSecret, Encoding.Latin1.GetString(downloadedPng));

            using var thumbResp = await clientA.GetAsync(thumbUrl);
            Assert.Equal(HttpStatusCode.OK, thumbResp.StatusCode);
            Assert.Equal("nosniff", Assert.Single(thumbResp.Headers.GetValues("X-Content-Type-Options")));
            var thumbBytes = await thumbResp.Content.ReadAsByteArrayAsync();
            using (var thumbBmp = SKBitmap.Decode(thumbBytes))
            {
                Assert.NotNull(thumbBmp);
                Assert.Equal(400, thumbBmp!.Width);
                Assert.Equal(200, thumbBmp.Height);
            }

            // Full-resolution inline preview works for verified PNG/JPEG, rejects PDF (SEC-11)
            using var previewImgResp = await clientA.GetAsync($"/api/files/{imgId}/preview");
            Assert.Equal(HttpStatusCode.OK, previewImgResp.StatusCode);
            Assert.Equal("nosniff", Assert.Single(previewImgResp.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("inline", previewImgResp.Content.Headers.ContentDisposition?.DispositionType);

            using var previewPdfResp = await clientA.GetAsync($"/api/files/{pdfId}/preview");
            Assert.Equal(HttpStatusCode.BadRequest, previewPdfResp.StatusCode);

            // Unicode (CJK + Vietnamese) filenames must download cleanly with RFC 5987 filename*=UTF-8''...
            using var unicodePdfResp = await UploadMultipartAsync(clientA, dmAB, $"uni-{suffix}", "20251503_基01_Báo cáo.pdf", pdfBytes);
            Assert.Equal(HttpStatusCode.Created, unicodePdfResp.StatusCode);
            var unicodePdfId = JsonDocument.Parse(await unicodePdfResp.Content.ReadAsStringAsync()).RootElement
                .GetProperty("attachment").GetProperty("id").GetGuid();

            using var unicodeDownload = await clientA.GetAsync($"/api/files/{unicodePdfId}/download");
            Assert.Equal(HttpStatusCode.OK, unicodeDownload.StatusCode);
            Assert.Equal("attachment", unicodeDownload.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("20251503_基01_Báo cáo.pdf", unicodeDownload.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Equal(pdfBytes, await unicodeDownload.Content.ReadAsByteArrayAsync());

            // Stage 2: Attach both files to an attachment-only message (empty caption allowed!)
            using var sendResp = await clientA.PostAsJsonAsync($"/api/conversations/{dmAB}/messages", new
            {
                clientMsgId = $"att-msg-{suffix}",
                body = "",
                attachmentIds = new[] { pdfId, imgId }
            });
            Assert.Equal(HttpStatusCode.Created, sendResp.StatusCode);
            var sentMsg = JsonDocument.Parse(await sendResp.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(2, sentMsg.GetProperty("attachments").GetArrayLength());

            // B sees attachments hydrated in GET /messages and GET /files
            using var msgsB = await clientB.GetAsync($"/api/conversations/{dmAB}/messages");
            Assert.Equal(HttpStatusCode.OK, msgsB.StatusCode);
            var bMsg = Assert.Single(JsonDocument.Parse(await msgsB.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(2, bMsg.GetProperty("attachments").GetArrayLength());

            using var filesB = await clientB.GetAsync($"/api/conversations/{dmAB}/files");
            Assert.Equal(HttpStatusCode.OK, filesB.StatusCode);
            Assert.Equal(2, JsonDocument.Parse(await filesB.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());

            // M3.1: Orphan cleanup removes unattached Pending objects older than TTL
            using var orphanResp = await UploadMultipartAsync(clientA, dmAB, $"orphan-{suffix}", "abandoned.txt", Encoding.UTF8.GetBytes("abandoned file"));
            Assert.Equal(HttpStatusCode.Created, orphanResp.StatusCode);
            var orphanId = JsonDocument.Parse(await orphanResp.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment").GetProperty("id").GetGuid();

            string orphanKey;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                var row = await db.Attachments.SingleAsync(a => a.Id == orphanId);
                orphanKey = row.ObjectKey;
                row.CreatedAt = DateTimeOffset.UtcNow.AddHours(-48);
                await db.SaveChangesAsync();
            }

            var cleanup = app.Services.GetRequiredService<OrphanAttachmentCleanupService>();
            var cleanedCount = await cleanup.CleanupOrphansAsync(DateTimeOffset.UtcNow.AddHours(-24));
            Assert.True(cleanedCount >= 1);

            var storage = app.Services.GetRequiredService<IFileStorage>();
            Assert.Null(await storage.OpenReadAsync(orphanKey));
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                Assert.False(await db.Attachments.AnyAsync(a => a.Id == orphanId));
            }
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task M3_3_SEC04_SEC11_AuthorizedDownloads_SafeHeaders_SignedUrlCap_AndRemovalIsolation()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m3-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);
        try
        {
            await using var app = BuildHost(
                "--NexusStorage:Provider=Local",
                $"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();

            var suffix = Guid.NewGuid().ToString("N")[..6];
            const string pw = "FilesSec-Strong-Password-2026!";

            var owner = await SeedUserAsync(app.Services, $"sa_{suffix}", pw, "Owner Alice", OrgRole.Member);
            var member = await SeedUserAsync(app.Services, $"sb_{suffix}", pw, "Member Bob", OrgRole.Member);
            var outsider = await SeedUserAsync(app.Services, $"sc_{suffix}", pw, "Outsider Charlie", OrgRole.Member);
            var sysAdmin = await SeedUserAsync(app.Services, $"sadm_{suffix}", pw, "SysAdmin Outsider", OrgRole.SystemAdmin);

            var ownerClient = await LoginAsync(app, owner.UserName!, pw);
            var memberClient = await LoginAsync(app, member.UserName!, pw);
            var outsiderClient = await LoginAsync(app, outsider.UserName!, pw);
            var adminClient = await LoginAndEnableMfaAsync(app, sysAdmin.UserName!, pw);

            // Create group with owner + member
            using var grpReq = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/groups")
            {
                Content = JsonContent.Create(new { title = "Finance Docs", memberUserIds = new[] { member.Id } })
            };
            grpReq.Headers.Add("Idempotency-Key", $"grp-sec-{suffix}");
            using var grpResp = await ownerClient.SendAsync(grpReq);
            Assert.Equal(HttpStatusCode.Created, grpResp.StatusCode);
            var groupId = JsonDocument.Parse(await grpResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n% Confidential finance payload\n%%EOF\n");
            using var upResp = await UploadMultipartAsync(ownerClient, groupId, $"secpdf-{suffix}", "q4-budget.pdf", pdfBytes);
            Assert.Equal(HttpStatusCode.Created, upResp.StatusCode);
            var attId = JsonDocument.Parse(await upResp.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment").GetProperty("id").GetGuid();

            // Pending attachment cannot be downloaded by other members before it is attached
            using var pendingByB = await memberClient.GetAsync($"/api/files/{attId}/download");
            Assert.Equal(HttpStatusCode.Forbidden, pendingByB.StatusCode);

            using var attachMsg = await ownerClient.PostAsJsonAsync($"/api/conversations/{groupId}/messages", new
            {
                clientMsgId = $"sec-msg-{suffix}",
                body = "Budget attached",
                attachmentIds = new[] { attId }
            });
            Assert.Equal(HttpStatusCode.Created, attachMsg.StatusCode);

            // Member B downloads cleanly with SEC-11 safe headers (Content-Disposition: attachment + nosniff)
            using var dlB = await memberClient.GetAsync($"/api/files/{attId}/download");
            Assert.Equal(HttpStatusCode.OK, dlB.StatusCode);
            Assert.Equal("nosniff", Assert.Single(dlB.Headers.GetValues("X-Content-Type-Options")));
            var disposition = dlB.Content.Headers.ContentDisposition;
            Assert.NotNull(disposition);
            Assert.Equal("attachment", disposition!.DispositionType);
            Assert.Equal(pdfBytes, await dlB.Content.ReadAsByteArrayAsync());

            // Signed URL endpoint enforces <= 60s cap (SEC-04)
            using var signedOk = await memberClient.PostAsJsonAsync($"/api/files/{attId}/signed-url", new { ttlSeconds = 60 });
            Assert.Equal(HttpStatusCode.OK, signedOk.StatusCode);
            Assert.Equal(60, JsonDocument.Parse(await signedOk.Content.ReadAsStringAsync()).RootElement.GetProperty("expiresInSeconds").GetInt32());

            using var signedTooLong = await memberClient.PostAsJsonAsync($"/api/files/{attId}/signed-url", new { ttlSeconds = 61 });
            Assert.Equal(HttpStatusCode.BadRequest, signedTooLong.StatusCode);

            // SEC-04: Outsider C and non-member SystemAdmin are denied download, signed-url, and files tab
            foreach (var deniedClient in new[] { outsiderClient, adminClient })
            {
                using var d1 = await deniedClient.GetAsync($"/api/files/{attId}/download");
                Assert.Equal(HttpStatusCode.Forbidden, d1.StatusCode);

                using var d2 = await deniedClient.PostAsJsonAsync($"/api/files/{attId}/signed-url", new { ttlSeconds = 30 });
                Assert.Equal(HttpStatusCode.Forbidden, d2.StatusCode);

                using var d3 = await deniedClient.GetAsync($"/api/conversations/{groupId}/files");
                Assert.Equal(HttpStatusCode.Forbidden, d3.StatusCode);
            }

            // Removing member B revokes download immediately; re-adding B (visible_from_seq = 1) still blocks pre-join file!
            using var rem = await ownerClient.DeleteAsync($"/api/conversations/{groupId}/members/{member.Id}");
            Assert.Equal(HttpStatusCode.NoContent, rem.StatusCode);

            using var dlAfterRemove = await memberClient.GetAsync($"/api/files/{attId}/download");
            Assert.Equal(HttpStatusCode.Forbidden, dlAfterRemove.StatusCode);

            using var reAdd = await ownerClient.PostAsJsonAsync($"/api/conversations/{groupId}/members", new { userId = member.Id });
            Assert.Equal(HttpStatusCode.OK, reAdd.StatusCode);

            using var dlAfterRejoin = await memberClient.GetAsync($"/api/files/{attId}/download");
            Assert.Equal(HttpStatusCode.Forbidden, dlAfterRejoin.StatusCode);

            using var filesAfterRejoin = await memberClient.GetAsync($"/api/conversations/{groupId}/files");
            Assert.Equal(HttpStatusCode.OK, filesAfterRejoin.StatusCode);
            Assert.Empty(JsonDocument.Parse(await filesAfterRejoin.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task M3_5_QuotasAndWarnings_EnforcedPerUserAndPerConversation()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m3-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);
        try
        {
            await using var app = BuildHost(
                "--NexusStorage:Provider=Local",
                $"--NexusStorage:LocalRootPath={tempStorage}",
                "--NexusFiles:PerConversationQuotaBytes=4096",
                "--NexusFiles:PerUserQuotaBytes=8192",
                "--NexusFiles:QuotaWarningRatio=0.5");
            await app.StartAsync();

            var suffix = Guid.NewGuid().ToString("N")[..6];
            const string pw = "QuotaM3-Strong-Password-2026!";

            var userA = await SeedUserAsync(app.Services, $"qa_{suffix}", pw, "Alice Quota", OrgRole.Member);
            var userB = await SeedUserAsync(app.Services, $"qb_{suffix}", pw, "Bob Quota", OrgRole.Member);
            var clientA = await LoginAsync(app, userA.UserName!, pw);

            using var dmResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
            var dmId = JsonDocument.Parse(await dmResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

            var chunk = Encoding.UTF8.GetBytes(new string('A', 2500));
            using var up1 = await UploadMultipartAsync(clientA, dmId, $"q1-{suffix}", "part1.txt", chunk);
            Assert.Equal(HttpStatusCode.Created, up1.StatusCode);
            var q1 = JsonDocument.Parse(await up1.Content.ReadAsStringAsync()).RootElement.GetProperty("quota");
            Assert.True(q1.GetProperty("conversationWarning").GetBoolean());

            using var up2 = await UploadMultipartAsync(clientA, dmId, $"q2-{suffix}", "part2.txt", chunk);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, up2.StatusCode);
            Assert.Equal("conversation_quota_exceeded", JsonDocument.Parse(await up2.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task M3_6_ClamAvInstreamProtocol_AndFailClosedQuarantine()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m3-clam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        // Start a mock ClamAV clamd TCP server speaking zINSTREAM\0 protocol
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clamPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource();
        var mockClamdTask = RunMockClamdAsync(listener, serverCts.Token);

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
            const string pw = "ClamAvM3-Strong-Password-2026!";

            var userA = await SeedUserAsync(app.Services, $"ca_{suffix}", pw, "Alice Clam", OrgRole.Member);
            var userB = await SeedUserAsync(app.Services, $"cb_{suffix}", pw, "Bob Clam", OrgRole.Member);
            var clientA = await LoginAsync(app, userA.UserName!, pw);

            using var dmResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
            var dmId = JsonDocument.Parse(await dmResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

            // 1. Clean file passes ClamAV with FailClosedScanning=true
            using var cleanResp = await UploadMultipartAsync(clientA, dmId, $"clean-{suffix}", "hello.txt", Encoding.UTF8.GetBytes("Safe business memo"));
            Assert.Equal(HttpStatusCode.Created, cleanResp.StatusCode);
            var cleanAtt = JsonDocument.Parse(await cleanResp.Content.ReadAsStringAsync()).RootElement.GetProperty("attachment");
            Assert.Equal("clean", cleanAtt.GetProperty("scanStatus").GetString());

            // 2. Infected payload (contains MOCK_VIRUS marker) -> 422 Unprocessable Entity + FILE_SCAN_REJECTED audit log
            using var virusResp = await UploadMultipartAsync(clientA, dmId, $"virus-{suffix}", "infected.txt", Encoding.UTF8.GetBytes("PAYLOAD_MOCK_VIRUS_SIGNATURE"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, virusResp.StatusCode);
            var virusDoc = JsonDocument.Parse(await virusResp.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("infected", virusDoc.GetProperty("scanStatus").GetString());

            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "FILE_SCAN_REJECTED" && a.ActorUserId == userA.Id));
            }

            // 3. Stop ClamAV -> fail-closed returns 503 Service Unavailable and refuses to store unscanned bytes
            await serverCts.CancelAsync();
            listener.Stop();

            using var outageResp = await UploadMultipartAsync(clientA, dmId, $"outage-{suffix}", "during-outage.txt", Encoding.UTF8.GetBytes("Should fail closed"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, outageResp.StatusCode);
            var outageDoc = JsonDocument.Parse(await outageResp.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("failed_closed", outageDoc.GetProperty("scanStatus").GetString());
        }
        finally
        {
            await serverCts.CancelAsync();
            try { listener.Stop(); } catch { }
            try { await mockClamdTask; } catch { }
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
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
                // Read "zINSTREAM\0" (10 bytes)
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

    private static async Task<HttpResponseMessage> UploadMultipartAsync(
        HttpClient client,
        Guid conversationId,
        string clientUploadId,
        string fileName,
        byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(clientUploadId), "clientUploadId");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        return await client.PostAsync($"/api/conversations/{conversationId}/attachments", form);
    }

    private static byte[] CreatePngWithTextChunk(int width, int height, string textPayload)
    {
        using var bmp = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(30, 112, 184));
        }
        using var img = SKImage.FromBitmap(bmp);
        using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();

        // Insert a PNG tEXt chunk right before IEND (last 12 bytes)
        var iendPos = png.Length - 12;
        var chunkData = Encoding.Latin1.GetBytes("Comment\0" + textPayload);
        using var ms = new MemoryStream();
        ms.Write(png, 0, iendPos);
        var lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, chunkData.Length);
        ms.Write(lenBytes);
        var typeBytes = "tEXt"u8.ToArray();
        ms.Write(typeBytes);
        ms.Write(chunkData);
        ms.Write(new byte[4]); // dummy CRC (SkiaSharp still decodes pixel chunks cleanly)
        ms.Write(png, iendPos, 12);
        return ms.ToArray();
    }

    private WebApplication BuildHost(params string[] extraArgs)
    {
        var args = new List<string>
        {
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        };
        args.AddRange(extraArgs);
        return NexusHost.Build([.. args]);
    }

    private static async Task<NexusUser> SeedUserAsync(
        IServiceProvider services,
        string username,
        string password,
        string displayName,
        OrgRole role)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NexusUser>>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<IdentityModuleOptions>>().Value;
        var org = await IdentityBootstrapService.EnsureDefaultOrganizationAsync(db, opts);

        var user = new NexusUser
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = org.Id,
            UserName = username,
            DisplayName = displayName,
            Status = AccountStatus.Active,
            TrustLevel = TrustLevel.Verified,
            OrgRole = role,
            TermsVersion = opts.CurrentTermsVersion,
            TermsAcceptedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var res = await userManager.CreateAsync(user, password);
        Assert.True(res.Succeeded, string.Join("; ", res.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task<HttpClient> LoginAsync(WebApplication app, string username, string password)
    {
        var client = CreateCookieClient(app);
        using var resp = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return client;
    }

    private static async Task<HttpClient> LoginAndEnableMfaAsync(WebApplication app, string username, string password)
    {
        var client = await LoginAsync(app, username, password);
        using var setup = await client.PostAsync("/api/auth/mfa/setup", null);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var sharedKey = JsonDocument.Parse(await setup.Content.ReadAsStringAsync()).RootElement.GetProperty("sharedKey").GetString()!;
        using var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable", new { code = GenerateTotpCode(sharedKey) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        return client;
    }

    private static HttpClient CreateCookieClient(WebApplication app)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            CheckCertificateRevocationList = true
        };
        return new HttpClient(handler) { BaseAddress = new Uri(GetBaseAddress(app)) };
    }

    private static string GetBaseAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        return address.EndsWith('/') ? address : address + "/";
    }

    private static string GenerateTotpCode(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleaned = base32Key.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var c in cleaned)
        {
            var idx = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (idx < 0) continue;
            buffer = (buffer << 5) | idx;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

#pragma warning disable CA5350 // RFC 6238 TOTP requires HMAC-SHA1 by specification.
        using var hmac = new HMACSHA1([.. output]);
#pragma warning restore CA5350
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6");
    }
}
