using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;

namespace Nexus.IntegrationTests;

public sealed class MessagingM2Tests(PostgresContainerFixture pg)
    : IClassFixture<PostgresContainerFixture>
{
    private static readonly long[] ExpectedSyncSeqs = [1L, 2L, 3L];

    [Fact]
    public async Task M2_2_SEC20b_DmOpenRaceIsAtomic_AndUnverifiedDailyLimitEnforced()
    {
        await using var app = BuildHost("--NexusIdentity:DailyNewDmLimitForUnverified=2");
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "Messaging-Strong-Password-2026!";

        var userA = await SeedUserAsync(app.Services, $"dma_{suffix}", pw, "Alice DM", OrgRole.Member, TrustLevel.Verified);
        var userB = await SeedUserAsync(app.Services, $"dmb_{suffix}", pw, "Bob DM", OrgRole.Member, TrustLevel.Verified);
        var userC = await SeedUserAsync(app.Services, $"dmc_{suffix}", pw, "Charlie DM", OrgRole.Member, TrustLevel.Verified);
        var userD = await SeedUserAsync(app.Services, $"dmd_{suffix}", pw, "Dana DM", OrgRole.Member, TrustLevel.Verified);

        var (_, clientA) = await LoginAsync(app, userA.UserName!, pw);
        var (_, clientB) = await LoginAsync(app, userB.UserName!, pw);

        // Concurrent DM open from both sides must resolve to exactly 1 conversation row (M2.2).
        var tasks = new[]
        {
            clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id }),
            clientB.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userA.Id }),
            clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id }),
            clientB.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userA.Id })
        };
        var responses = await Task.WhenAll(tasks);

        var ids = new HashSet<Guid>();
        foreach (var resp in responses)
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            ids.Add(doc.GetProperty("id").GetGuid());
            resp.Dispose();
        }

        Assert.Single(ids);

        // SEC-20b: Unverified account limited to 2 new DMs per UTC day.
        var (_, trialClient) = CreateCookieClient(app);
        using var regResp = await trialClient.PostAsJsonAsync("/api/auth/register", new
        {
            username = $"trialdm_{suffix}",
            password = pw,
            displayName = "Trial User",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Created, regResp.StatusCode);

        using var dm1 = await trialClient.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userA.Id });
        Assert.Equal(HttpStatusCode.OK, dm1.StatusCode);

        using var dm2 = await trialClient.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
        Assert.Equal(HttpStatusCode.OK, dm2.StatusCode);

        // 3rd brand-new DM exceeds DailyNewDmLimitForUnverified=2 -> 403 Forbidden.
        using var dm3 = await trialClient.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userC.Id });
        Assert.Equal(HttpStatusCode.Forbidden, dm3.StatusCode);

        // Re-opening existing DM with A still succeeds (200 OK).
        using var dm1Again = await trialClient.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userA.Id });
        Assert.Equal(HttpStatusCode.OK, dm1Again.StatusCode);

        // Opening DM with self is denied (403 Forbidden).
        using var selfDm = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userA.Id });
        Assert.Equal(HttpStatusCode.Forbidden, selfDm.StatusCode);
        _ = userD;
    }

    [Fact]
    public async Task M2_3_M2_4_M2_10_SEC03_SEC05_SEC07_SEC15_RealtimeIsolation_AndSystemAdminDenied()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "Realtime-Strong-Password-2026!";

        var userA = await SeedUserAsync(app.Services, $"rta_{suffix}", pw, "Alice RT", OrgRole.Member, TrustLevel.Verified);
        var userB = await SeedUserAsync(app.Services, $"rtb_{suffix}", pw, "Bob RT", OrgRole.Member, TrustLevel.Verified);
        var userC = await SeedUserAsync(app.Services, $"rtc_{suffix}", pw, "Charlie Outsider", OrgRole.Member, TrustLevel.Verified);
        var sysAdmin = await SeedUserAsync(app.Services, $"rtsys_{suffix}", pw, "SysAdmin Outsider", OrgRole.SystemAdmin, TrustLevel.Verified);

        var (cookiesA, clientA) = await LoginAsync(app, userA.UserName!, pw);
        var (cookiesB, clientB) = await LoginAsync(app, userB.UserName!, pw);
        var (cookiesC, clientC) = await LoginAsync(app, userC.UserName!, pw);
        var (cookiesAdmin, clientAdmin) = await LoginAndEnableMfaAsync(app, sysAdmin.UserName!, pw);

        var bEvents = new ConcurrentQueue<JsonElement>();
        var bTyping = new ConcurrentQueue<JsonElement>();
        var cEvents = new ConcurrentQueue<JsonElement>();
        var adminEvents = new ConcurrentQueue<JsonElement>();

        await using var hubA = BuildMessagingHub(app, cookiesA);
        await using var hubB = BuildMessagingHub(app, cookiesB);
        await using var hubC = BuildMessagingHub(app, cookiesC);
        await using var hubAdmin = BuildMessagingHub(app, cookiesAdmin);

        hubB.On<JsonElement>("ConversationChanged", payload => bEvents.Enqueue(payload.Clone()));
        hubB.On<JsonElement>("TypingIndicator", payload => bTyping.Enqueue(payload.Clone()));
        hubC.On<JsonElement>("ConversationChanged", payload => cEvents.Enqueue(payload.Clone()));
        hubC.On<JsonElement>("TypingIndicator", payload => cEvents.Enqueue(payload.Clone()));
        hubAdmin.On<JsonElement>("ConversationChanged", payload => adminEvents.Enqueue(payload.Clone()));
        hubAdmin.On<JsonElement>("TypingIndicator", payload => adminEvents.Enqueue(payload.Clone()));

        await hubA.StartAsync();
        await hubB.StartAsync();
        await hubC.StartAsync();
        await hubAdmin.StartAsync();

        // A opens DM with B and sends a message
        using var openResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
        Assert.Equal(HttpStatusCode.OK, openResp.StatusCode);
        var dmId = JsonDocument.Parse(await openResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var sendResp = await clientA.PostAsJsonAsync($"/api/conversations/{dmId}/messages", new
        {
            clientMsgId = $"msg-{suffix}-1",
            body = "Hello Bob from Alice!"
        });
        Assert.Equal(HttpStatusCode.Created, sendResp.StatusCode);
        var sentDoc = JsonDocument.Parse(await sendResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1L, sentDoc.GetProperty("seq").GetInt64());

        // B receives ConversationChanged for dmId via user:{id} SignalR group
        await WaitUntilAsync(() => bEvents.Any(e => e.GetProperty("conversationId").GetGuid() == dmId && e.GetProperty("changeSeq").GetInt64() >= 2));

        // M2.10: A sends typing twice rapidly -> B receives 1 throttled TypingIndicator
        await hubA.InvokeAsync("SendTyping", dmId);
        await hubA.InvokeAsync("SendTyping", dmId);
        await WaitUntilAsync(() => !bTyping.IsEmpty);
        await Task.Delay(150);
        Assert.Single(bTyping);

        // Presence check: A's conversation list reports peer B is online
        using var listA = await clientA.GetAsync("/api/conversations");
        Assert.Equal(HttpStatusCode.OK, listA.StatusCode);
        var listADoc = JsonDocument.Parse(await listA.Content.ReadAsStringAsync()).RootElement;
        var dmItem = Assert.Single(listADoc.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == dmId);
        Assert.True(dmItem.GetProperty("peer").GetProperty("isOnline").GetBoolean());

        // SEC-03, SEC-05, SEC-07, SEC-15: Non-member C and SystemAdmin are denied every HTTP and SignalR access
        foreach (var (outsiderClient, outsiderHub) in new[] { (clientC, hubC), (clientAdmin, hubAdmin) })
        {
            using var getMsgs = await outsiderClient.GetAsync($"/api/conversations/{dmId}/messages");
            Assert.Equal(HttpStatusCode.Forbidden, getMsgs.StatusCode);

            using var postMsg = await outsiderClient.PostAsJsonAsync($"/api/conversations/{dmId}/messages", new
            {
                clientMsgId = $"intruder-{Guid.NewGuid():N}",
                body = "Unauthorized send"
            });
            Assert.Equal(HttpStatusCode.Forbidden, postMsg.StatusCode);

            using var getChanges = await outsiderClient.GetAsync($"/api/conversations/{dmId}/changes?after=0");
            Assert.Equal(HttpStatusCode.Forbidden, getChanges.StatusCode);

            using var putRead = await outsiderClient.PutAsJsonAsync($"/api/conversations/{dmId}/read", new { lastReadSeq = 1 });
            Assert.Equal(HttpStatusCode.Forbidden, putRead.StatusCode);

            using var getMembers = await outsiderClient.GetAsync($"/api/conversations/{dmId}/members");
            Assert.Equal(HttpStatusCode.Forbidden, getMembers.StatusCode);

            await Assert.ThrowsAsync<HubException>(() => outsiderHub.InvokeAsync("SendTyping", dmId));
        }

        // Neither outsider C nor SystemAdmin received any SignalR notification for A<->B DM
        Assert.Empty(cEvents);
        Assert.Empty(adminEvents);
    }

    [Fact]
    public async Task M2_3_SEC19_ClientMsgIdIdempotency_ConcurrentRetry_AndConflict()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "Idempotent-Strong-Password-2026!";

        var userA = await SeedUserAsync(app.Services, $"idema_{suffix}", pw, "Alice Idem", OrgRole.Member, TrustLevel.Verified);
        var userB = await SeedUserAsync(app.Services, $"idemb_{suffix}", pw, "Bob Idem", OrgRole.Member, TrustLevel.Verified);
        var userC = await SeedUserAsync(app.Services, $"idemc_{suffix}", pw, "Charlie Idem", OrgRole.Member, TrustLevel.Verified);

        var (_, clientA) = await LoginAsync(app, userA.UserName!, pw);

        using var dmABResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
        var dmAB = JsonDocument.Parse(await dmABResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var dmACResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userC.Id });
        var dmAC = JsonDocument.Parse(await dmACResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var clientMsgId = $"client-retry-{suffix}";
        const string body = "Exactly once delivery";

        // Concurrent retries with identical (clientMsgId, body) produce 1 message row
        var concurrentCalls = Enumerable.Range(0, 4)
            .Select(_ => clientA.PostAsJsonAsync($"/api/conversations/{dmAB}/messages", new { clientMsgId, body }))
            .ToArray();
        var concurrentResponses = await Task.WhenAll(concurrentCalls);

        var msgIds = new HashSet<Guid>();
        var seqs = new HashSet<long>();
        foreach (var r in concurrentResponses)
        {
            Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"Unexpected status {r.StatusCode}");
            var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            msgIds.Add(doc.GetProperty("id").GetGuid());
            seqs.Add(doc.GetProperty("seq").GetInt64());
            r.Dispose();
        }

        Assert.Single(msgIds);
        Assert.Single(seqs);

        // Sequential retry with exact same payload returns 200 OK with same message id
        using var replay = await clientA.PostAsJsonAsync($"/api/conversations/{dmAB}/messages", new { clientMsgId, body });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayDoc = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(msgIds.Single(), replayDoc.GetProperty("id").GetGuid());

        // SEC-19: Reusing clientMsgId with different body -> 409 Conflict
        using var diffBody = await clientA.PostAsJsonAsync($"/api/conversations/{dmAB}/messages", new { clientMsgId, body = "Tampered body" });
        Assert.Equal(HttpStatusCode.Conflict, diffBody.StatusCode);

        // SEC-19: Reusing clientMsgId in a different conversation -> 409 Conflict
        using var diffConv = await clientA.PostAsJsonAsync($"/api/conversations/{dmAC}/messages", new { clientMsgId, body });
        Assert.Equal(HttpStatusCode.Conflict, diffConv.StatusCode);

        // Hard pagination cap (>50 rejected)
        using var overLimit = await clientA.GetAsync($"/api/conversations/{dmAB}/messages?limit=51");
        Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
    }

    [Fact]
    public async Task M2_5_M2_7_SEC17_DeltaSync_Reconcile_410Gone_AndMonotonicReadState()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "DeltaSync-Strong-Password-2026!";

        var userA = await SeedUserAsync(app.Services, $"synca_{suffix}", pw, "Alice Sync", OrgRole.Member, TrustLevel.Verified);
        var userB = await SeedUserAsync(app.Services, $"syncb_{suffix}", pw, "Bob Sync", OrgRole.Member, TrustLevel.Verified);

        var (_, clientA) = await LoginAsync(app, userA.UserName!, pw);
        var (cookiesB, clientB) = await LoginAsync(app, userB.UserName!, pw);

        using var dmResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
        var dmDoc = JsonDocument.Parse(await dmResp.Content.ReadAsStringAsync()).RootElement;
        var dmId = dmDoc.GetProperty("id").GetGuid();
        var initialChangeSeq = dmDoc.GetProperty("changeSeq").GetInt64();

        // While B is disconnected from SignalR, A sends 3 messages
        for (var i = 1; i <= 3; i++)
        {
            using var send = await clientA.PostAsJsonAsync($"/api/conversations/{dmId}/messages", new
            {
                clientMsgId = $"sync-{suffix}-{i}",
                body = $"Missed message #{i}"
            });
            Assert.Equal(HttpStatusCode.Created, send.StatusCode);
        }

        // B reconnects and calls /api/sync/reconcile + /changes?after=initialChangeSeq
        using var reconcileResp = await clientB.GetAsync("/api/sync/reconcile");
        Assert.Equal(HttpStatusCode.OK, reconcileResp.StatusCode);
        var reconcileDoc = JsonDocument.Parse(await reconcileResp.Content.ReadAsStringAsync()).RootElement;
        var convState = Assert.Single(reconcileDoc.GetProperty("items").EnumerateArray(), x => x.GetProperty("conversationId").GetGuid() == dmId);
        Assert.Equal(3L, convState.GetProperty("nextSeq").GetInt64());
        Assert.True(convState.GetProperty("changeSeq").GetInt64() > initialChangeSeq);

        using var changesResp = await clientB.GetAsync($"/api/conversations/{dmId}/changes?after={initialChangeSeq}");
        Assert.Equal(HttpStatusCode.OK, changesResp.StatusCode);
        var changesDoc = JsonDocument.Parse(await changesResp.Content.ReadAsStringAsync()).RootElement;
        var events = changesDoc.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal(ExpectedSyncSeqs, events.Select(e => e.GetProperty("payload").GetProperty("seq").GetInt64()).ToArray());

        // M2.7: Connect B to SignalR and verify monotonic GREATEST(last_read_seq) + ReadStateUpdated fan-out
        var readUpdates = new ConcurrentQueue<JsonElement>();
        await using var hubB = BuildMessagingHub(app, cookiesB);
        hubB.On<JsonElement>("ReadStateUpdated", payload => readUpdates.Enqueue(payload.Clone()));
        await hubB.StartAsync();

        using var read3 = await clientB.PutAsJsonAsync($"/api/conversations/{dmId}/read", new { lastReadSeq = 3 });
        Assert.Equal(HttpStatusCode.OK, read3.StatusCode);
        var read3Doc = JsonDocument.Parse(await read3.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3L, read3Doc.GetProperty("lastReadSeq").GetInt64());
        Assert.Equal(0L, read3Doc.GetProperty("unreadCount").GetInt64());

        await WaitUntilAsync(() => readUpdates.Any(u => u.GetProperty("conversationId").GetGuid() == dmId && u.GetProperty("lastReadSeq").GetInt64() == 3));

        // Stale lower lastReadSeq must NOT regress read state
        using var readStale = await clientB.PutAsJsonAsync($"/api/conversations/{dmId}/read", new { lastReadSeq = 1 });
        Assert.Equal(HttpStatusCode.OK, readStale.StatusCode);
        var readStaleDoc = JsonDocument.Parse(await readStale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3L, readStaleDoc.GetProperty("lastReadSeq").GetInt64());

        // M2.5 / SEC-17: Cursor older than min_available_change_seq - 1 returns 410 Gone (resync_required)
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Conversations
                .Where(c => c.Id == dmId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.MinAvailableChangeSeq, 4L));
        }

        using var staleResp = await clientB.GetAsync($"/api/conversations/{dmId}/changes?after=1");
        Assert.Equal(HttpStatusCode.Gone, staleResp.StatusCode);
        var staleDoc = JsonDocument.Parse(await staleResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("resync_required", staleDoc.GetProperty("code").GetString());
    }

    [Fact]
    public async Task M2_8_SEC18_GroupLifecycle_IdempotencyKey_RemovalRevokesAccess_RejoinHidesHistory_AndOwnershipTransfer()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "GroupLife-Strong-Password-2026!";

        var owner = await SeedUserAsync(app.Services, $"grpa_{suffix}", pw, "Owner Alice", OrgRole.Member, TrustLevel.Verified);
        var member = await SeedUserAsync(app.Services, $"grpb_{suffix}", pw, "Member Bob", OrgRole.Member, TrustLevel.Verified);

        var (_, ownerClient) = await LoginAsync(app, owner.UserName!, pw);
        var (_, memberClient) = await LoginAsync(app, member.UserName!, pw);

        // Create group with Idempotency-Key (replays return identical group; mismatched payload returns 409)
        var idemKey = $"create-group-{suffix}";
        using var createReq1 = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/groups")
        {
            Content = JsonContent.Create(new { title = "Engineering Team", memberUserIds = new[] { member.Id } })
        };
        createReq1.Headers.Add("Idempotency-Key", idemKey);
        using var createResp1 = await ownerClient.SendAsync(createReq1);
        Assert.Equal(HttpStatusCode.Created, createResp1.StatusCode);
        var groupId = JsonDocument.Parse(await createResp1.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var createReq2 = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/groups")
        {
            Content = JsonContent.Create(new { title = "Engineering Team", memberUserIds = new[] { member.Id } })
        };
        createReq2.Headers.Add("Idempotency-Key", idemKey);
        using var createResp2 = await ownerClient.SendAsync(createReq2);
        Assert.Equal(HttpStatusCode.Created, createResp2.StatusCode);
        Assert.Equal(groupId, JsonDocument.Parse(await createResp2.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());

        using var createMismatch = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/groups")
        {
            Content = JsonContent.Create(new { title = "Different Title", memberUserIds = new[] { member.Id } })
        };
        createMismatch.Headers.Add("Idempotency-Key", idemKey);
        using var mismatchResp = await ownerClient.SendAsync(createMismatch);
        Assert.Equal(HttpStatusCode.Conflict, mismatchResp.StatusCode);

        // Send messages 1 and 2 while Bob is a member
        for (var i = 1; i <= 2; i++)
        {
            using var s = await ownerClient.PostAsJsonAsync($"/api/conversations/{groupId}/messages", new
            {
                clientMsgId = $"grp-{suffix}-{i}",
                body = $"Before removal #{i}"
            });
            Assert.Equal(HttpStatusCode.Created, s.StatusCode);
        }

        // Promote Bob to Admin, then remove Bob
        using var promote = await ownerClient.PutAsJsonAsync($"/api/conversations/{groupId}/admins/{member.Id}", new { isAdmin = true });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        using var remove = await ownerClient.DeleteAsync($"/api/conversations/{groupId}/members/{member.Id}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        // SEC-18: Removed member immediately loses history, delta sync, read-state, and offline-queued send access
        using var removedMsgs = await memberClient.GetAsync($"/api/conversations/{groupId}/messages");
        Assert.Equal(HttpStatusCode.Forbidden, removedMsgs.StatusCode);

        using var removedChanges = await memberClient.GetAsync($"/api/conversations/{groupId}/changes?after=0");
        Assert.Equal(HttpStatusCode.Forbidden, removedChanges.StatusCode);

        using var removedOfflineSend = await memberClient.PostAsJsonAsync($"/api/conversations/{groupId}/messages", new
        {
            clientMsgId = $"grp-offline-{suffix}",
            body = "Queued while offline before removal"
        });
        Assert.Equal(HttpStatusCode.Forbidden, removedOfflineSend.StatusCode);

        // Owner sends secret message #3 while Bob is out
        using var secretSend = await ownerClient.PostAsJsonAsync($"/api/conversations/{groupId}/messages", new
        {
            clientMsgId = $"grp-{suffix}-3",
            body = "Secret while Bob was removed"
        });
        Assert.Equal(HttpStatusCode.Created, secretSend.StatusCode);

        // Re-add Bob -> visible_from_seq = 3 so messages 1..3 stay hidden across both /messages and /changes
        using var reAdd = await ownerClient.PostAsJsonAsync($"/api/conversations/{groupId}/members", new { userId = member.Id });
        Assert.Equal(HttpStatusCode.OK, reAdd.StatusCode);
        var reAddDoc = JsonDocument.Parse(await reAdd.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3L, reAddDoc.GetProperty("visibleFromSeq").GetInt64());

        using var bobMsgsAfterRejoin = await memberClient.GetAsync($"/api/conversations/{groupId}/messages");
        Assert.Equal(HttpStatusCode.OK, bobMsgsAfterRejoin.StatusCode);
        Assert.Empty(JsonDocument.Parse(await bobMsgsAfterRejoin.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());

        using var bobChangesAfterRejoin = await memberClient.GetAsync($"/api/conversations/{groupId}/changes?after=0");
        Assert.Equal(HttpStatusCode.OK, bobChangesAfterRejoin.StatusCode);
        var bobChangeItems = JsonDocument.Parse(await bobChangesAfterRejoin.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(bobChangeItems, e => e.GetProperty("eventType").GetString() == "message.created");

        // Send message #4 after Bob rejoined -> Bob sees ONLY #4
        using var postRejoinSend = await ownerClient.PostAsJsonAsync($"/api/conversations/{groupId}/messages", new
        {
            clientMsgId = $"grp-{suffix}-4",
            body = "Welcome back Bob"
        });
        Assert.Equal(HttpStatusCode.Created, postRejoinSend.StatusCode);

        using var bobMsgsNow = await memberClient.GetAsync($"/api/conversations/{groupId}/messages");
        Assert.Equal(HttpStatusCode.OK, bobMsgsNow.StatusCode);
        var visibleItems = JsonDocument.Parse(await bobMsgsNow.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().ToList();
        var onlyVisible = Assert.Single(visibleItems);
        Assert.Equal(4L, onlyVisible.GetProperty("seq").GetInt64());
        Assert.Equal("Welcome back Bob", onlyVisible.GetProperty("body").GetString());

        // Owner cannot leave while another member remains until transferring ownership
        using var ownerLeaveBlocked = await ownerClient.PostAsync($"/api/conversations/{groupId}/leave", null);
        Assert.Equal(HttpStatusCode.BadRequest, ownerLeaveBlocked.StatusCode);

        using var transfer = await ownerClient.PostAsJsonAsync($"/api/conversations/{groupId}/transfer-ownership", new { newOwnerUserId = member.Id });
        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);

        using var ownerLeaveAllowed = await ownerClient.PostAsync($"/api/conversations/{groupId}/leave", null);
        Assert.Equal(HttpStatusCode.NoContent, ownerLeaveAllowed.StatusCode);
    }

    [Fact]
    public async Task M2_11_BlockAndReportUser_EnforcedOnDmOpenAndSend()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "Safety-Strong-Password-2026!";

        var userA = await SeedUserAsync(app.Services, $"safa_{suffix}", pw, "Alice Safety", OrgRole.Member, TrustLevel.Verified);
        var userB = await SeedUserAsync(app.Services, $"safb_{suffix}", pw, "Bob Safety", OrgRole.Member, TrustLevel.Verified);

        var (_, clientA) = await LoginAsync(app, userA.UserName!, pw);
        var (_, clientB) = await LoginAsync(app, userB.UserName!, pw);

        using var dmResp = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
        Assert.Equal(HttpStatusCode.OK, dmResp.StatusCode);
        var dmId = JsonDocument.Parse(await dmResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // B blocks A
        using var blockResp = await clientB.PutAsync($"/api/people/blocks/{userA.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, blockResp.StatusCode);

        using var listBlocks = await clientB.GetAsync("/api/people/blocks");
        Assert.Equal(HttpStatusCode.OK, listBlocks.StatusCode);
        Assert.Contains(
            JsonDocument.Parse(await listBlocks.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray(),
            b => b.GetProperty("blockedUserId").GetGuid() == userA.Id);

        // A cannot send in existing DM or open DM with B while blocked
        using var blockedSend = await clientA.PostAsJsonAsync($"/api/conversations/{dmId}/messages", new
        {
            clientMsgId = $"blk-{suffix}-1",
            body = "Should be blocked"
        });
        Assert.Equal(HttpStatusCode.Forbidden, blockedSend.StatusCode);

        using var blockedOpen = await clientA.PostAsJsonAsync("/api/conversations/dm", new { targetUserId = userB.Id });
        Assert.Equal(HttpStatusCode.Forbidden, blockedOpen.StatusCode);

        // B unblocks A -> messaging works again
        using var unblockResp = await clientB.DeleteAsync($"/api/people/blocks/{userA.Id}");
        Assert.Equal(HttpStatusCode.NoContent, unblockResp.StatusCode);

        using var allowedSend = await clientA.PostAsJsonAsync($"/api/conversations/{dmId}/messages", new
        {
            clientMsgId = $"blk-{suffix}-2",
            body = "Unblocked now"
        });
        Assert.Equal(HttpStatusCode.Created, allowedSend.StatusCode);

        // B reports A -> persisted in user_reports + audit_logs
        using var reportResp = await clientB.PostAsJsonAsync("/api/people/reports", new
        {
            reportedUserId = userA.Id,
            conversationId = dmId,
            reason = "spam",
            details = "Repeated unsolicited messages"
        });
        Assert.Equal(HttpStatusCode.Created, reportResp.StatusCode);
        var reportId = JsonDocument.Parse(await reportResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        Assert.True(await db.UserReports.AnyAsync(r => r.Id == reportId && r.ReporterUserId == userB.Id && r.ReportedUserId == userA.Id));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "USER_REPORTED" && a.ActorUserId == userB.Id && a.ResourceId == userA.Id.ToString()));
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
            OrganizationId = org.Id,
            UserName = username,
            DisplayName = displayName,
            Status = AccountStatus.Active,
            TrustLevel = trustLevel,
            OrgRole = role,
            TermsVersion = opts.CurrentTermsVersion,
            TermsAcceptedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var res = await userManager.CreateAsync(user, password);
        Assert.True(res.Succeeded, string.Join("; ", res.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task<(CookieContainer Cookies, HttpClient Client)> LoginAsync(WebApplication app, string username, string password)
    {
        var (cookies, client) = CreateCookieClient(app);
        using var resp = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (cookies, client);
    }

    private static async Task<(CookieContainer Cookies, HttpClient Client)> LoginAndEnableMfaAsync(WebApplication app, string username, string password)
    {
        var (cookies, client) = await LoginAsync(app, username, password);
        using var setup = await client.PostAsync("/api/auth/mfa/setup", null);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var sharedKey = JsonDocument.Parse(await setup.Content.ReadAsStringAsync()).RootElement.GetProperty("sharedKey").GetString()!;

        using var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable", new { code = GenerateTotpCode(sharedKey) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        return (cookies, client);
    }

    private static (CookieContainer Cookies, HttpClient Client) CreateCookieClient(WebApplication app)
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(GetBaseAddress(app)) };
        return (cookies, client);
    }

    private static HubConnection BuildMessagingHub(WebApplication app, CookieContainer cookies)
    {
        var baseUri = new Uri(GetBaseAddress(app));
        return new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "hubs/messaging"), opts =>
            {
                opts.Cookies = cookies;
                opts.Headers.Add("Origin", baseUri.ToString().TrimEnd('/'));
            })
            .Build();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.True(predicate(), "Timed out waiting for expected SignalR event.");
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

#pragma warning disable CA5350 // RFC 6238 TOTP used by ASP.NET Core Identity requires HMAC-SHA1 by specification.
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
