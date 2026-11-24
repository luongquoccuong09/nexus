using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Notifications;
using Nexus.Web.Modules.Org;

namespace Nexus.IntegrationTests;

public sealed class CommunitiesM5Tests(PostgresContainerFixture pg)
    : IClassFixture<PostgresContainerFixture>
{
    private static readonly string[] SingleChoiceOptions = ["Option Alpha", "Option Beta", "Option Gamma"];

    [Fact]
    public async Task M5_0_M5_1_CreateCommunity_MainFeedAndOptionalSubChannels_IsolationAndRemoval()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m5-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);
        try
        {
            await using var app = BuildHost(
                "--NexusStorage:Provider=Local",
                $"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();

            var suffix = Guid.NewGuid().ToString("N")[..6];
            const string pw = "CommunitiesM5-Strong-Password-2026!";

            var owner = await SeedUserAsync(app.Services, $"cown_{suffix}", pw, "Owner Alice", OrgRole.Member, TrustLevel.Verified);
            var member = await SeedUserAsync(app.Services, $"cmem_{suffix}", pw, "Member Bob", OrgRole.Member, TrustLevel.Verified);
            var outsider = await SeedUserAsync(app.Services, $"cout_{suffix}", pw, "Outsider Charlie", OrgRole.Member, TrustLevel.Verified);
            var sysAdmin = await SeedUserAsync(app.Services, $"csys_{suffix}", pw, "SysAdmin Eve", OrgRole.SystemAdmin, TrustLevel.Verified);

            var ownerClient = await LoginAsync(app, owner.UserName!, pw);
            var memberClient = await LoginAsync(app, member.UserName!, pw);
            var outsiderClient = await LoginAsync(app, outsider.UserName!, pw);
            var sysAdminClient = await LoginAndEnableMfaAsync(app, sysAdmin.UserName!, pw);

            // M5.0 / P0-D04: Create Community with Idempotency-Key -> main feed directly, ZERO default sub-channels
            var idemKey = $"comm-create-{suffix}";
            using var createReq1 = new HttpRequestMessage(HttpMethod.Post, "/api/communities")
            {
                Content = JsonContent.Create(new { name = $"Engineering {suffix}", description = "Core eng space", visibility = "Private" })
            };
            createReq1.Headers.Add("Idempotency-Key", idemKey);
            using var createResp1 = await ownerClient.SendAsync(createReq1);
            Assert.Equal(HttpStatusCode.Created, createResp1.StatusCode);

            var comm1 = JsonDocument.Parse(await createResp1.Content.ReadAsStringAsync()).RootElement;
            var communityId = comm1.GetProperty("id").GetGuid();
            var mainFeedId = comm1.GetProperty("mainFeedConversationId").GetGuid();
            Assert.Equal(0, comm1.GetProperty("channels").GetArrayLength());

            // Idempotent replay with same key & body returns exact same Community ID
            using var createReq2 = new HttpRequestMessage(HttpMethod.Post, "/api/communities")
            {
                Content = JsonContent.Create(new { name = $"Engineering {suffix}", description = "Core eng space", visibility = "Private" })
            };
            createReq2.Headers.Add("Idempotency-Key", idemKey);
            using var createResp2 = await ownerClient.SendAsync(createReq2);
            Assert.Equal(HttpStatusCode.Created, createResp2.StatusCode);
            Assert.Equal(communityId, JsonDocument.Parse(await createResp2.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());

            // Same Idempotency-Key with mismatched payload -> 409 Conflict
            using var createReqConflict = new HttpRequestMessage(HttpMethod.Post, "/api/communities")
            {
                Content = JsonContent.Create(new { name = $"Different {suffix}", description = "Other", visibility = "Private" })
            };
            createReqConflict.Headers.Add("Idempotency-Key", idemKey);
            using var createRespConflict = await ownerClient.SendAsync(createReqConflict);
            Assert.Equal(HttpStatusCode.Conflict, createRespConflict.StatusCode);

            // Chat conversation list excludes Community Channel feeds
            using var chatListResp = await ownerClient.GetAsync("/api/conversations");
            Assert.Equal(HttpStatusCode.OK, chatListResp.StatusCode);
            var chatConvs = JsonDocument.Parse(await chatListResp.Content.ReadAsStringAsync()).RootElement.GetProperty("items");
            Assert.DoesNotContain(chatConvs.EnumerateArray(), item => item.GetProperty("id").GetGuid() == mainFeedId);

            // M5.0: Owner adds optional sub-channel on demand
            using var chResp = await ownerClient.PostAsJsonAsync($"/api/communities/{communityId}/channels", new
            {
                name = "releases",
                postingPolicy = "Members"
            });
            Assert.Equal(HttpStatusCode.Created, chResp.StatusCode);
            var subChannelId = JsonDocument.Parse(await chResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

            // Add Member B to Community -> inherits access to both main feed and sub-channel
            using var addMemResp = await ownerClient.PostAsJsonAsync($"/api/communities/{communityId}/members", new { userId = member.Id });
            Assert.Equal(HttpStatusCode.OK, addMemResp.StatusCode);

            // Member B posts in main feed & sub-channel, and uploads a file in sub-channel
            using var mainPostResp = await memberClient.PostAsJsonAsync($"/api/channels/{mainFeedId}/posts", new
            {
                title = "Main feed hello",
                body = "Hello community!"
            });
            Assert.Equal(HttpStatusCode.Created, mainPostResp.StatusCode);

            using var subPostResp = await memberClient.PostAsJsonAsync($"/api/channels/{subChannelId}/posts", new
            {
                title = "Release v1",
                body = "Notes for release v1."
            });
            Assert.Equal(HttpStatusCode.Created, subPostResp.StatusCode);

            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n%%EOF\n");
            using var fileResp = await UploadMultipartAsync(memberClient, subChannelId, $"chfile-{suffix}", "spec.pdf", pdfBytes);
            Assert.Equal(HttpStatusCode.Created, fileResp.StatusCode);
            var attachmentId = JsonDocument.Parse(await fileResp.Content.ReadAsStringAsync()).RootElement
                .GetProperty("attachment").GetProperty("id").GetGuid();

            // Outsider C and non-member SystemAdmin are denied (403) across main feed, sub-channel, and channel files
            Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/channels/{mainFeedId}/posts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/channels/{subChannelId}/posts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/files/{attachmentId}/download")).StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await sysAdminClient.GetAsync($"/api/channels/{mainFeedId}/posts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await sysAdminClient.GetAsync($"/api/channels/{subChannelId}/posts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await sysAdminClient.GetAsync($"/api/files/{attachmentId}/download")).StatusCode);

            // Member B can currently download the channel file
            Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync($"/api/files/{attachmentId}/download")).StatusCode);

            // M5.1: Remove Member B from Community -> IMMEDIATELY revokes access to main feed, sub-channel, AND files
            using var removeResp = await ownerClient.DeleteAsync($"/api/communities/{communityId}/members/{member.Id}");
            Assert.Equal(HttpStatusCode.NoContent, removeResp.StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync($"/api/channels/{mainFeedId}/posts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync($"/api/channels/{subChannelId}/posts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync($"/api/files/{attachmentId}/download")).StatusCode);

            var transferPath = $"/api/communities/{communityId}/transfer-ownership";
            var transferRequest = new { newOwnerUserId = member.Id };
            using var revokedTarget = await ownerClient.PostAsJsonAsync(transferPath, transferRequest);
            Assert.Equal(HttpStatusCode.Forbidden, revokedTarget.StatusCode);
            using var rejoin = await ownerClient.PostAsJsonAsync($"/api/communities/{communityId}/members", new { userId = member.Id });
            Assert.Equal(HttpStatusCode.OK, rejoin.StatusCode);
            using var unauthorized = await outsiderClient.PostAsJsonAsync(transferPath, transferRequest);
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);

            var transfers = await Task.WhenAll(
                ownerClient.PostAsJsonAsync(transferPath, transferRequest),
                ownerClient.PostAsJsonAsync(transferPath, transferRequest));
            using var firstTransfer = transfers[0];
            using var secondTransfer = transfers[1];
            Assert.Single(transfers, result => result.StatusCode == HttpStatusCode.OK);
            Assert.Single(transfers, result => result.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);
            var roles = (await memberClient.GetFromJsonAsync<JsonElement>($"/api/communities/{communityId}/members"))
                .GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("userId").GetGuid(),
                    item => item.GetProperty("role").GetString());
            Assert.Equal("Admin", roles[owner.Id]);
            Assert.Equal("Owner", roles[member.Id]);
            using var staleRemove = await ownerClient.DeleteAsync($"/api/communities/{communityId}/members/{member.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, staleRemove.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempStorage)) Directory.Delete(tempStorage, recursive: true);
        }
    }

    [Fact]
    public async Task M5_2_M5_3_OfficialDesignation_UnverifiedRestriction_AdminsOnly_AcknowledgmentsCsvAndRemind()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "OfficialM5-Strong-Password-2026!";

        var owner = await SeedUserAsync(app.Services, $"oown_{suffix}", pw, "Owner Dana", OrgRole.Member, TrustLevel.Verified);
        var verifiedMember = await SeedUserAsync(app.Services, $"over_{suffix}", pw, "Verified Frank", OrgRole.Member, TrustLevel.Verified);
        var unverifiedUser = await SeedUserAsync(app.Services, $"ounv_{suffix}", pw, "Unverified Grace", OrgRole.Member, TrustLevel.Unverified);
        var commsAdmin = await SeedUserAsync(app.Services, $"ocom_{suffix}", pw, "Comms Admin Helen", OrgRole.CommsAdmin, TrustLevel.Verified);
        var sysAdmin = await SeedUserAsync(app.Services, $"osys_{suffix}", pw, "SysAdmin Ivan", OrgRole.SystemAdmin, TrustLevel.Verified);

        var ownerClient = await LoginAsync(app, owner.UserName!, pw);
        var verifiedClient = await LoginAsync(app, verifiedMember.UserName!, pw);
        var unverifiedClient = await LoginAsync(app, unverifiedUser.UserName!, pw);
        var commsClient = await LoginAndEnableMfaAsync(app, commsAdmin.UserName!, pw);
        var sysAdminClient = await LoginAndEnableMfaAsync(app, sysAdmin.UserName!, pw);

        // Create Public Community
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/communities")
        {
            Content = JsonContent.Create(new
            {
                name = $"Company Wide {suffix}",
                description = "Official notices",
                visibility = "Public"
            })
        };
        createReq.Headers.Add("Idempotency-Key", $"off-create-{suffix}");
        using var createResp = await ownerClient.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var communityId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // Non-CommsAdmin (Owner, SysAdmin) cannot designate Official
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PostAsJsonAsync($"/api/communities/{communityId}/official", new { isOfficial = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sysAdminClient.PostAsJsonAsync($"/api/communities/{communityId}/official", new { isOfficial = true })).StatusCode);

        // Verified CommsAdmin with MFA designates Official -> 200 & audited
        using var officialResp = await commsClient.PostAsJsonAsync($"/api/communities/{communityId}/official", new { isOfficial = true });
        Assert.Equal(HttpStatusCode.OK, officialResp.StatusCode);

        // Unverified user cannot discover or join Official community (SEC-20b)
        using var unvDiscover = await unverifiedClient.GetAsync("/api/communities?discover=true");
        Assert.Equal(HttpStatusCode.OK, unvDiscover.StatusCode);
        var unvItems = JsonDocument.Parse(await unvDiscover.Content.ReadAsStringAsync()).RootElement.GetProperty("items");
        Assert.DoesNotContain(unvItems.EnumerateArray(), c => c.GetProperty("id").GetGuid() == communityId);

        Assert.Equal(HttpStatusCode.Forbidden, (await unverifiedClient.PostAsync($"/api/communities/{communityId}/join", null)).StatusCode);

        // Verified member CAN join Official Public community
        Assert.Equal(HttpStatusCode.OK, (await verifiedClient.PostAsync($"/api/communities/{communityId}/join", null)).StatusCode);

        // Owner creates an AdminsOnly announcement sub-channel
        using var chResp = await ownerClient.PostAsJsonAsync($"/api/communities/{communityId}/channels", new
        {
            name = "announcements",
            postingPolicy = "AdminsOnly"
        });
        Assert.Equal(HttpStatusCode.Created, chResp.StatusCode);
        var annChannelId = JsonDocument.Parse(await chResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // Regular Verified member cannot post in AdminsOnly channel
        using var memberBlockedPost = await verifiedClient.PostAsJsonAsync($"/api/channels/{annChannelId}/posts", new
        {
            title = "Unauthorized",
            body = "Should be blocked"
        });
        Assert.Equal(HttpStatusCode.Forbidden, memberBlockedPost.StatusCode);

        // Owner publishes announcement requiring acknowledgment
        using var annResp = await ownerClient.PostAsJsonAsync($"/api/channels/{annChannelId}/posts", new
        {
            title = "Security Policy Update",
            body = "Please read and acknowledge the Q4 policy.",
            isAnnouncement = true,
            requiresAcknowledgment = true
        });
        Assert.Equal(HttpStatusCode.Created, annResp.StatusCode);
        var postId = JsonDocument.Parse(await annResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // Verified member acknowledges twice -> idempotent, count stays 1
        using var ack1 = await verifiedClient.PutAsync($"/api/posts/{postId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, ack1.StatusCode);
        using var ack2 = await verifiedClient.PutAsync($"/api/posts/{postId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, ack2.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await ack2.Content.ReadAsStringAsync()).RootElement.GetProperty("acknowledgedCount").GetInt32());

        // Owner views JSON acknowledgment status & exports CSV with safe download headers
        using var ackJsonResp = await ownerClient.GetAsync($"/api/posts/{postId}/acknowledgments?format=json");
        Assert.Equal(HttpStatusCode.OK, ackJsonResp.StatusCode);
        var ackJson = JsonDocument.Parse(await ackJsonResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, ackJson.GetProperty("totalRecipients").GetInt32());
        Assert.Equal(1, ackJson.GetProperty("acknowledgedCount").GetInt32());
        Assert.Equal(1, ackJson.GetProperty("outstandingCount").GetInt32());

        using var ackCsvResp = await ownerClient.GetAsync($"/api/posts/{postId}/acknowledgments?format=csv");
        Assert.Equal(HttpStatusCode.OK, ackCsvResp.StatusCode);
        Assert.Equal("attachment", ackCsvResp.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(ackCsvResp.Headers.GetValues("X-Content-Type-Options")));
        var csvBody = await ackCsvResp.Content.ReadAsStringAsync();
        Assert.Contains(",acknowledged,", csvBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(",outstanding,", csvBody, StringComparison.OrdinalIgnoreCase);

        // Owner triggers Reminder -> audited
        using var remindResp = await ownerClient.PostAsync($"/api/posts/{postId}/remind", null);
        Assert.Equal(HttpStatusCode.OK, remindResp.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await remindResp.Content.ReadAsStringAsync()).RootElement.GetProperty("remindedCount").GetInt32());

        var mainFeedId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()).RootElement
            .GetProperty("mainFeedConversationId").GetGuid();
        using var memberMention = await verifiedClient.PostAsJsonAsync($"/api/channels/{mainFeedId}/posts", new { body = "Text with @all" });
        Assert.Equal(HttpStatusCode.Created, memberMention.StatusCode);
        var memberMentionId = (await memberMention.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var ownerMention = await ownerClient.PostAsJsonAsync($"/api/channels/{annChannelId}/posts", new
        {
            body = $"@channel @all @{verifiedMember.UserName} @{unverifiedUser.UserName}"
        });
        Assert.Equal(HttpStatusCode.Created, ownerMention.StatusCode);
        var ownerMentionId = (await ownerMention.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // SystemAdmin (with MFA) performs emergency ownership transfer -> 200 & audited
        using var transferResp = await sysAdminClient.PostAsJsonAsync($"/api/communities/{communityId}/emergency-transfer-ownership", new
        {
            newOwnerUserId = verifiedMember.Id
        });
        Assert.Equal(HttpStatusCode.OK, transferResp.StatusCode);

        // Verify all 3 audit log entries were persisted
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var auditActions = await db.AuditLogs.AsNoTracking()
            .Where(a => a.ResourceId == communityId.ToString() || a.ResourceId == postId.ToString())
            .Select(a => a.Action)
            .ToListAsync();

        Assert.Contains("COMMUNITY_OFFICIAL_CHANGED", auditActions);
        Assert.Contains("ANNOUNCEMENT_REMINDER_SENT", auditActions);
        Assert.Contains("COMMUNITY_EMERGENCY_OWNERSHIP_TRANSFER", auditActions);

        // The hosted worker delivers only to the outstanding member of the Official community.
        using var dispatchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await db.OutboxMessages.AnyAsync(
                   message => message.ResourceId == postId && message.CompletedAt != null, dispatchTimeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), dispatchTimeout.Token);
        }

        var notification = Assert.Single(await db.Notifications.AsNoTracking()
            .Where(item => item.ResourceId == postId).ToListAsync(dispatchTimeout.Token));
        Assert.Equal(owner.Id, notification.RecipientId);

        // Replaying the original event does not deliver a second notification.
        await db.OutboxMessages.Where(message => message.Id == notification.OutboxMessageId)
            .ExecuteUpdateAsync(updates => updates.SetProperty(message => message.CompletedAt, (DateTimeOffset?)null),
                dispatchTimeout.Token);
        while (!await db.OutboxMessages.AnyAsync(message => message.Id == notification.OutboxMessageId &&
                   message.CompletedAt != null && message.Attempts == 2, dispatchTimeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), dispatchTimeout.Token);
        }

        Assert.Equal(1, await db.Notifications.CountAsync(item => item.ResourceId == postId, dispatchTimeout.Token));

        while (!await db.OutboxMessages.AnyAsync(
                   item => item.ResourceId == ownerMentionId && item.CompletedAt != null, dispatchTimeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), dispatchTimeout.Token);
        }

        Assert.False(await db.OutboxMessages.AnyAsync(item => item.ResourceId == memberMentionId, dispatchTimeout.Token));
        var broadcast = Assert.Single(await db.Notifications.Where(item => item.ResourceId == ownerMentionId).ToListAsync(dispatchTimeout.Token));
        Assert.Equal(verifiedMember.Id, broadcast.RecipientId);
        var mentionsPath = $"/api/notifications/mentions/unread?conversationId={annChannelId}";
        Assert.Equal(1, (await verifiedClient.GetFromJsonAsync<JsonElement>(mentionsPath, dispatchTimeout.Token))
            .GetProperty("unreadMentionCount").GetInt32());
        Assert.Equal(0, (await unverifiedClient.GetFromJsonAsync<JsonElement>(mentionsPath, dispatchTimeout.Token))
            .GetProperty("unreadMentionCount").GetInt32());

        db.Notifications.AddRange(Enumerable.Range(0, 50).Select(_ => new NotificationEntity
        {
            Id = Guid.NewGuid(), OutboxMessageId = Guid.NewGuid(), RecipientId = verifiedMember.Id,
            OrganizationId = broadcast.OrganizationId, ConversationId = annChannelId,
            ResourceId = ownerMentionId, Kind = broadcast.Kind, CreatedAt = broadcast.CreatedAt
        }));
        await db.SaveChangesAsync(dispatchTimeout.Token);
        var firstPage = await verifiedClient.GetFromJsonAsync<JsonElement>("/api/notifications?limit=50", dispatchTimeout.Token);
        var before = firstPage.GetProperty("nextBefore");
        Assert.Equal(50, firstPage.GetProperty("items").GetArrayLength());
        var beforeAt = Uri.EscapeDataString(before.GetProperty("createdAt").GetString()!);
        var secondPage = await verifiedClient.GetFromJsonAsync<JsonElement>(
            $"/api/notifications?limit=50&beforeAt={beforeAt}&beforeId={before.GetProperty("id").GetGuid()}", dispatchTimeout.Token);
        var lastId = Assert.Single(secondPage.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.DoesNotContain(firstPage.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == lastId);

        await db.CommunityMembers.Where(item => item.CommunityId == communityId && item.UserId == verifiedMember.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeftAt, DateTimeOffset.UtcNow), dispatchTimeout.Token);
        var afterRevocation = await verifiedClient.GetFromJsonAsync<JsonElement>("/api/notifications", dispatchTimeout.Token);
        Assert.Empty(afterRevocation.GetProperty("items").EnumerateArray());
        using var revokedSource = await verifiedClient.GetAsync($"/api/notifications/{broadcast.Id}/source", dispatchTimeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, revokedSource.StatusCode);
    }

    [Fact]
    public async Task M5_4_M5_5_M5_6_DepartmentSyncIdempotent_CommentsReactionsPinning_AndPollVoteUniqueness()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "SyncPollsM5-Strong-Password-2026!";

        var owner = await SeedUserAsync(app.Services, $"sown_{suffix}", pw, "Owner Kim", OrgRole.Member, TrustLevel.Verified);
        var deptUser1 = await SeedUserAsync(app.Services, $"sdu1_{suffix}", pw, "Dept Liam", OrgRole.Member, TrustLevel.Verified);
        var deptUser2 = await SeedUserAsync(app.Services, $"sdu2_{suffix}", pw, "Dept Mia", OrgRole.Member, TrustLevel.Verified);

        // Seed an HR Department and assign deptUser1 & deptUser2 to it
        var orgId = await OrganizationFixtureHandler.GetIdAsync(app.Services);
        Guid deptId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            deptId = Guid.CreateVersion7();
            db.Departments.Add(new DepartmentEntity
            {
                Id = deptId,
                OrganizationId = orgId,
                Code = $"ENG-{suffix}",
                Name = $"Engineering {suffix}",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            await db.OrganizationMemberships
                .Where(u => u.OrganizationId == orgId && (u.Id == deptUser1.Id || u.Id == deptUser2.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, deptId));
        }

        var ownerClient = await LoginAsync(app, owner.UserName!, pw);
        var memberClient = await LoginAsync(app, deptUser1.UserName!, pw);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/communities")
        {
            Content = JsonContent.Create(new
            {
                name = $"Eng Dept {suffix}",
                visibility = "Private"
            })
        };
        createReq.Headers.Add("Idempotency-Key", $"sync-create-{suffix}");
        using var createResp = await ownerClient.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var comm = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()).RootElement;
        var communityId = comm.GetProperty("id").GetGuid();
        var mainFeedId = comm.GetProperty("mainFeedConversationId").GetGuid();

        // M5.4 (CM-02): Map department & sync twice -> 1st adds 2, 2nd adds 0 & unchanged 2 (idempotent)
        using var mapResp = await ownerClient.PutAsJsonAsync($"/api/communities/{communityId}/department-mapping", new { departmentId = deptId });
        Assert.Equal(HttpStatusCode.OK, mapResp.StatusCode);

        using var sync1 = await ownerClient.PostAsync($"/api/communities/{communityId}/sync-department", null);
        Assert.Equal(HttpStatusCode.OK, sync1.StatusCode);
        var s1 = JsonDocument.Parse(await sync1.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, s1.GetProperty("addedCount").GetInt32());

        using var sync2 = await ownerClient.PostAsync($"/api/communities/{communityId}/sync-department", null);
        Assert.Equal(HttpStatusCode.OK, sync2.StatusCode);
        var s2 = JsonDocument.Parse(await sync2.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, s2.GetProperty("addedCount").GetInt32());
        Assert.Equal(2, s2.GetProperty("unchangedCount").GetInt32());

        // M5.5 & M5.6: Create 2 posts (first with single-choice poll, second plain)
        using var pollPostResp = await ownerClient.PostAsJsonAsync($"/api/channels/{mainFeedId}/posts", new
        {
            title = "Team Lunch",
            body = "Vote for Friday lunch!",
            poll = new
            {
                question = "Where should we eat?",
                options = SingleChoiceOptions,
                allowMultiple = false
            }
        });
        Assert.Equal(HttpStatusCode.Created, pollPostResp.StatusCode);
        var pollPostId = JsonDocument.Parse(await pollPostResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var post2Resp = await memberClient.PostAsJsonAsync($"/api/channels/{mainFeedId}/posts", new
        {
            title = "Second post",
            body = "Newest unpinned post"
        });
        Assert.Equal(HttpStatusCode.Created, post2Resp.StatusCode);

        // Pinning: regular member forbidden (403), Owner succeeds (200) -> pinned post sorts first
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PutAsJsonAsync($"/api/posts/{pollPostId}/pin", new { isPinned = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync($"/api/posts/{pollPostId}/pin", new { isPinned = true })).StatusCode);

        // Comments & idempotent reactions
        using var commentResp = await memberClient.PostAsJsonAsync($"/api/posts/{pollPostId}/comments", new { body = "Count me in!" });
        Assert.Equal(HttpStatusCode.Created, commentResp.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await memberClient.PutAsync($"/api/posts/{pollPostId}/reactions/%F0%9F%91%8D", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await memberClient.PutAsync($"/api/posts/{pollPostId}/reactions/%F0%9F%91%8D", null)).StatusCode);

        // Fetch feed -> pinned poll post is first, has comment & 1 reaction, get poll ID and option IDs
        using var feedResp1 = await memberClient.GetAsync($"/api/channels/{mainFeedId}/posts");
        Assert.Equal(HttpStatusCode.OK, feedResp1.StatusCode);
        var feedItems1 = JsonDocument.Parse(await feedResp1.Content.ReadAsStringAsync()).RootElement.GetProperty("items");
        var firstPost = feedItems1[0];
        Assert.Equal(pollPostId, firstPost.GetProperty("id").GetGuid());
        Assert.True(firstPost.GetProperty("isPinned").GetBoolean());
        Assert.Equal(1, firstPost.GetProperty("comments").GetArrayLength());
        Assert.Equal(1, firstPost.GetProperty("reactions")[0].GetProperty("count").GetInt32());

        var poll = firstPost.GetProperty("poll");
        var pollId = poll.GetProperty("id").GetGuid();
        var opt1Id = poll.GetProperty("options")[0].GetProperty("id").GetString()!;
        var opt2Id = poll.GetProperty("options")[1].GetProperty("id").GetString()!;

        // M5.6: Single-choice poll rejects 2 simultaneous options (400)
        using var multiReject = await memberClient.PutAsJsonAsync($"/api/polls/{pollId}/vote", new { optionIds = new[] { opt1Id, opt2Id } });
        Assert.Equal(HttpStatusCode.BadRequest, multiReject.StatusCode);

        // Member votes option 1, then changes vote to option 2 -> totalVoters stays 1, only option 2 has 1 vote
        using var vote1 = await memberClient.PutAsJsonAsync($"/api/polls/{pollId}/vote", new { optionIds = new[] { opt1Id } });
        Assert.Equal(HttpStatusCode.OK, vote1.StatusCode);

        using var vote2 = await memberClient.PutAsJsonAsync($"/api/polls/{pollId}/vote", new { optionIds = new[] { opt2Id } });
        Assert.Equal(HttpStatusCode.OK, vote2.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await vote2.Content.ReadAsStringAsync()).RootElement.GetProperty("totalVoters").GetInt32());

        // Remove reaction -> 204 NoContent
        Assert.Equal(HttpStatusCode.NoContent, (await memberClient.DeleteAsync($"/api/posts/{pollPostId}/reactions/%F0%9F%91%8D")).StatusCode);

        using var feedResp2 = await memberClient.GetAsync($"/api/channels/{mainFeedId}/posts");
        var updatedPoll = JsonDocument.Parse(await feedResp2.Content.ReadAsStringAsync()).RootElement
            .GetProperty("items")[0].GetProperty("poll");
        Assert.Equal(0, updatedPoll.GetProperty("options")[0].GetProperty("votes").GetInt32());
        Assert.Equal(1, updatedPoll.GetProperty("options")[1].GetProperty("votes").GetInt32());
        Assert.Equal(1, updatedPoll.GetProperty("totalVoters").GetInt32());
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
        OrgRole role,
        TrustLevel trustLevel)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NexusUser>>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<IdentityModuleOptions>>().Value;
        var org = await IdentityBootstrapService.EnsureDefaultOrganizationAsync(db, opts);

        var user = new NexusUser
        {
            Id = Guid.CreateVersion7(),
            UserName = username,
            DisplayName = displayName,
            Status = AccountStatus.Active,
            TermsVersion = opts.CurrentTermsVersion,
            TermsAcceptedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var res = await userManager.CreateAsync(user, password);
        Assert.True(res.Succeeded, string.Join("; ", res.Errors.Select(e => e.Description)));
        OrganizationFixtureHandler.Add(db, org.Id, user, role, trustLevel);
        await db.SaveChangesAsync();
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
        return new HttpClient(new OrganizationFixtureHandler(app.Services, handler)) { BaseAddress = new Uri(GetBaseAddress(app)) };
    }

    private static string GetBaseAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        return address.EndsWith('/') ? address : address + "/";
    }

    private static async Task<HttpResponseMessage> UploadMultipartAsync(
        HttpClient client,
        Guid conversationId,
        string clientUploadId,
        string fileName,
        byte[] content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(clientUploadId), "clientUploadId");
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        return await client.PostAsync($"/api/conversations/{conversationId}/attachments", form);
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
