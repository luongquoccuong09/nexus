using System.Collections.Concurrent;
using System.Net;
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
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

[Collection("RichChatPostgres")]
public sealed class RichChatM61Tests(PostgresContainerFixture pg, ITestOutputHelper output)
{
    private const string TestPassword = "RichChat-Strong-Password-2026!";

    [Fact]
    public async Task PersonalDmGroupSelfChatReactions_Idempotency_Batching_SignalRDelta_Csrf_Validation_And_Isolation()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out var bobCookies);
        using var carol = Client(baseUri, out var carolCookies);
        using var sysAdmin = Client(baseUri, out var sysAdminCookies);
        using var orgBUser = Client(baseUri, out var orgBCookies);

        var aliceName = "m61_alice_" + suffix;
        var bobName = "m61_bob_" + suffix;
        var carolName = "m61_carol_" + suffix;
        var adminName = "m61_admin_" + suffix;
        var orgBName = "m61_orgb_" + suffix;

        var aliceId = await RegisterAsync(alice, aliceName);
        var bobId = await RegisterAsync(bob, bobName);
        var carolId = await RegisterAsync(carol, carolName);
        var sysAdminId = await RegisterAsync(sysAdmin, adminName);
        var orgBUserId = await RegisterAsync(orgBUser, orgBName);

        // Setup 2 isolated Organizations + SystemAdmin non-member + Personal contacts
        var orgAId = await CreateOrganizationAsync(alice, "M61 OrgA " + suffix);
        var orgBId = await CreateOrganizationAsync(orgBUser, "M61 OrgB " + suffix);
        await InviteAndJoinAsync(alice, bob, orgAId);
        await InviteAndJoinAsync(alice, carol, orgAId);
        await InviteAndJoinAsync(alice, sysAdmin, orgAId);
        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/members/{sysAdminId}/administrator",
            new { administrator = true }, HttpStatusCode.NoContent, HttpMethod.Put);

        await ConnectContactsAsync(alice, bob, bobName);
        await ConnectContactsAsync(alice, carol, carolName);

        // Connect SignalR hubs for Bob (Personal), Carol (Personal outsider to DM/Group), SysAdmin (OrgA), and OrgBUser (OrgB + Personal)
        var bobPersonalEvents = new ConcurrentQueue<JsonElement>();
        var carolEvents = new ConcurrentQueue<JsonElement>();
        var sysAdminEvents = new ConcurrentQueue<JsonElement>();
        var orgBEvents = new ConcurrentQueue<JsonElement>();

        await using var bobHub = BuildHub(baseUri, OrganizationScope.PersonalHub, bobCookies);
        await using var carolHub = BuildHub(baseUri, OrganizationScope.PersonalHub, carolCookies);
        await using var sysAdminHub = BuildHub(baseUri, $"/hubs/organizations/{orgAId}/messaging", sysAdminCookies);
        await using var orgBHub = BuildHub(baseUri, $"/hubs/organizations/{orgBId}/messaging", orgBCookies);

        bobHub.On<JsonElement>("ConversationChanged", p => bobPersonalEvents.Enqueue(p.Clone()));
        carolHub.On<JsonElement>("ConversationChanged", p => carolEvents.Enqueue(p.Clone()));
        sysAdminHub.On<JsonElement>("ConversationChanged", p => sysAdminEvents.Enqueue(p.Clone()));
        orgBHub.On<JsonElement>("ConversationChanged", p => orgBEvents.Enqueue(p.Clone()));

        await bobHub.StartAsync();
        await carolHub.StartAsync();
        await sysAdminHub.StartAsync();
        await orgBHub.StartAsync();
        var presence = app.Services.GetRequiredService<IPresenceTracker>();
        await WaitUntilAsync(() =>
            presence.IsOnline(Guid.Empty, bobId) &&
            presence.IsOnline(Guid.Empty, carolId) &&
            presence.IsOnline(orgAId, sysAdminId) &&
            presence.IsOnline(orgBId, orgBUserId));

        // 1. Personal DM setup + 2 messages
        var dm = await ProtectedAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK);
        var dmId = dm.GetProperty("id").GetGuid();

        var m1 = await ProtectedAsync(alice, $"/api/personal/conversations/{dmId}/messages", new
        {
            clientMsgId = $"m61-dm-1-{suffix}",
            body = "First DM message"
        }, HttpStatusCode.Created);
        var m1Id = m1.GetProperty("id").GetGuid();
        Assert.Empty(m1.GetProperty("reactions").EnumerateArray());

        var m2 = await ProtectedAsync(alice, $"/api/personal/conversations/{dmId}/messages", new
        {
            clientMsgId = $"m61-dm-2-{suffix}",
            body = "Second DM message"
        }, HttpStatusCode.Created);
        var m2Id = m2.GetProperty("id").GetGuid();

        var thumbsUp = Uri.EscapeDataString("👍");
        var heart = Uri.EscapeDataString("❤️");
        var party = Uri.EscapeDataString("🎉");
        var eyes = Uri.EscapeDataString("👀");

        // 2. CSRF enforcement on PUT and DELETE
        using (var noCsrfPut = await alice.PutAsync($"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCsrfPut.StatusCode);
            var err = await noCsrfPut.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_csrf", err.GetProperty("code").GetString());
        }

        using (var noCsrfDel = await alice.DeleteAsync($"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCsrfDel.StatusCode);
            var err = await noCsrfDel.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_csrf", err.GetProperty("code").GetString());
        }

        // 3. Invalid emoji validation -> 400 invalid_emoji
        foreach (var badEmoji in new[] { "%20", new string('x', 33), "a%250Ab" })
        {
            var err = await ProtectedAsync(alice, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{badEmoji}",
                null, HttpStatusCode.BadRequest, HttpMethod.Put);
            Assert.Equal("invalid_emoji", err.GetProperty("code").GetString());
        }

        // 4. Add reaction + SignalR direct delta + strict idempotency (no duplicate conversation_events or SignalR)
        var reactionPath = $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}";
        var concurrentPuts = await Task.WhenAll(
            ProtectedAsync(alice, reactionPath, null, HttpStatusCode.OK, HttpMethod.Put),
            ProtectedAsync(alice, reactionPath, null, HttpStatusCode.OK, HttpMethod.Put));
        var put1 = Assert.Single(concurrentPuts, response => response.GetProperty("changed").GetBoolean());
        Assert.Equal(m1Id, put1.GetProperty("messageId").GetGuid());
        Assert.Equal(1L, put1.GetProperty("seq").GetInt64());
        Assert.Equal("👍", put1.GetProperty("emoji").GetString());
        Assert.True(put1.GetProperty("reacted").GetBoolean());
        Assert.True(put1.GetProperty("changed").GetBoolean());
        var cs1 = put1.GetProperty("changeSeq").GetInt64();
        Assert.All(concurrentPuts, response => Assert.Equal(cs1, response.GetProperty("changeSeq").GetInt64()));

        await WaitUntilAsync(() => bobPersonalEvents.Any(e =>
            e.GetProperty("conversationId").GetGuid() == dmId &&
            e.GetProperty("changeSeq").GetInt64() == cs1 &&
            e.TryGetProperty("delta", out var d) &&
            d.ValueKind == JsonValueKind.Object));

        var bobDeltaEvt = bobPersonalEvents.First(e =>
            e.GetProperty("conversationId").GetGuid() == dmId &&
            e.GetProperty("changeSeq").GetInt64() == cs1);
        Assert.Equal(JsonValueKind.Null, bobDeltaEvt.GetProperty("message").ValueKind);
        var delta = bobDeltaEvt.GetProperty("delta");
        Assert.Equal("reaction", delta.GetProperty("type").GetString());
        Assert.Equal(m1Id, delta.GetProperty("messageId").GetGuid());
        Assert.Equal(1L, delta.GetProperty("seq").GetInt64());
        Assert.Equal("👍", delta.GetProperty("emoji").GetString());
        Assert.Equal(aliceId, delta.GetProperty("userId").GetGuid());
        Assert.True(delta.GetProperty("reacted").GetBoolean());

        int eventCountBeforeDup;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            eventCountBeforeDup = await db.ConversationEvents.CountAsync(e => e.ConversationId == dmId);
        }

        var bobCountBeforeDup = bobPersonalEvents.Count(e => e.GetProperty("conversationId").GetGuid() == dmId);

        // Idempotent duplicate PUT -> changed = false, same changeSeq, 0 new conversation_events, 0 new SignalR
        var putDup = await ProtectedAsync(alice, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Put);
        Assert.False(putDup.GetProperty("changed").GetBoolean());
        Assert.Equal(cs1, putDup.GetProperty("changeSeq").GetInt64());

        await Task.Delay(150);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.Equal(eventCountBeforeDup, await db.ConversationEvents.CountAsync(e => e.ConversationId == dmId));
        }
        Assert.Equal(bobCountBeforeDup, bobPersonalEvents.Count(e => e.GetProperty("conversationId").GetGuid() == dmId));

        // 5. Additional reactions across both users & both messages -> verify GET /messages batch summary & ordering
        await ProtectedAsync(bob, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Put);
        await ProtectedAsync(bob, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{heart}",
            null, HttpStatusCode.OK, HttpMethod.Put);
        await ProtectedAsync(alice, $"/api/personal/conversations/{dmId}/messages/{m2Id}/reactions/{party}",
            null, HttpStatusCode.OK, HttpMethod.Put);

        var aliceMsgs = await GetAsync(alice, $"/api/personal/conversations/{dmId}/messages");
        var aliceItems = aliceMsgs.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, aliceItems.Count);
        var aliceM1Reactions = aliceItems[0].GetProperty("reactions").EnumerateArray().ToList();
        Assert.Equal(2, aliceM1Reactions.Count);
        Assert.Equal("👍", aliceM1Reactions[0].GetProperty("emoji").GetString());
        Assert.Equal(2, aliceM1Reactions[0].GetProperty("count").GetInt32());
        Assert.True(aliceM1Reactions[0].GetProperty("reactedByMe").GetBoolean());
        Assert.Equal("❤️", aliceM1Reactions[1].GetProperty("emoji").GetString());
        Assert.Equal(1, aliceM1Reactions[1].GetProperty("count").GetInt32());
        Assert.False(aliceM1Reactions[1].GetProperty("reactedByMe").GetBoolean());

        var aliceM2Reaction = Assert.Single(aliceItems[1].GetProperty("reactions").EnumerateArray());
        Assert.Equal("🎉", aliceM2Reaction.GetProperty("emoji").GetString());
        Assert.Equal(1, aliceM2Reaction.GetProperty("count").GetInt32());
        Assert.True(aliceM2Reaction.GetProperty("reactedByMe").GetBoolean());

        var bobMsgs = await GetAsync(bob, $"/api/personal/conversations/{dmId}/messages");
        var bobM1Reactions = bobMsgs.GetProperty("items").EnumerateArray().First().GetProperty("reactions").EnumerateArray().ToList();
        Assert.True(bobM1Reactions[0].GetProperty("reactedByMe").GetBoolean());
        Assert.True(bobM1Reactions[1].GetProperty("reactedByMe").GetBoolean());

        // 6. DELETE reaction + SignalR removal delta + idempotent second DELETE
        var del1 = await ProtectedAsync(alice, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Delete);
        Assert.False(del1.GetProperty("reacted").GetBoolean());
        Assert.True(del1.GetProperty("changed").GetBoolean());
        var delCs = del1.GetProperty("changeSeq").GetInt64();

        await WaitUntilAsync(() => bobPersonalEvents.Any(e =>
            e.GetProperty("conversationId").GetGuid() == dmId &&
            e.GetProperty("changeSeq").GetInt64() == delCs &&
            e.TryGetProperty("delta", out var d) &&
            d.ValueKind == JsonValueKind.Object &&
            d.GetProperty("reacted").GetBoolean() == false));

        int eventCountBeforeDupDel;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            eventCountBeforeDupDel = await db.ConversationEvents.CountAsync(e => e.ConversationId == dmId);
        }

        var delDup = await ProtectedAsync(alice, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Delete);
        Assert.False(delDup.GetProperty("changed").GetBoolean());
        Assert.Equal(delCs, delDup.GetProperty("changeSeq").GetInt64());

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.Equal(eventCountBeforeDupDel, await db.ConversationEvents.CountAsync(e => e.ConversationId == dmId));
        }

        // Bob removes the last 👍 -> pill disappears completely
        await ProtectedAsync(bob, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Delete);
        var afterLastRemoved = await GetAsync(alice, $"/api/personal/conversations/{dmId}/messages");
        var remainingM1 = Assert.Single(afterLastRemoved.GetProperty("items").EnumerateArray().First().GetProperty("reactions").EnumerateArray());
        Assert.Equal("❤️", remainingM1.GetProperty("emoji").GetString());

        // 7. Personal Group reaction + SignalR delta
        var group = await ProtectedAsync(alice, "/api/personal/conversations/groups",
            new { title = "M61 Personal Group", memberUserIds = new[] { bobId } }, HttpStatusCode.Created, idempotencyKey: $"m61-pgrp-{suffix}");
        var groupId = group.GetProperty("id").GetGuid();
        var grpMsg = await ProtectedAsync(alice, $"/api/personal/conversations/{groupId}/messages", new
        {
            clientMsgId = $"m61-grp-{suffix}",
            body = "Hello group"
        }, HttpStatusCode.Created);
        var grpMsgId = grpMsg.GetProperty("id").GetGuid();

        var grpReact = await ProtectedAsync(alice, $"/api/personal/conversations/{groupId}/messages/{grpMsgId}/reactions/{eyes}",
            null, HttpStatusCode.OK, HttpMethod.Put);
        var grpCs = grpReact.GetProperty("changeSeq").GetInt64();
        await WaitUntilAsync(() => bobPersonalEvents.Any(e =>
            e.GetProperty("conversationId").GetGuid() == groupId &&
            e.GetProperty("changeSeq").GetInt64() == grpCs &&
            e.TryGetProperty("delta", out var d) &&
            d.ValueKind == JsonValueKind.Object &&
            d.GetProperty("emoji").GetString() == "👀"));

        // 8. SelfChat reaction + owner-only isolation
        var self = await ProtectedAsync(alice, "/api/personal/conversations/self", null, HttpStatusCode.OK);
        var selfId = self.GetProperty("id").GetGuid();
        var selfMsg = await ProtectedAsync(alice, $"/api/personal/conversations/{selfId}/messages", new
        {
            clientMsgId = $"m61-self-{suffix}",
            body = "Self note"
        }, HttpStatusCode.Created);
        var selfMsgId = selfMsg.GetProperty("id").GetGuid();

        var selfReact = await ProtectedAsync(alice, $"/api/personal/conversations/{selfId}/messages/{selfMsgId}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Put);
        Assert.True(selfReact.GetProperty("changed").GetBoolean());

        var selfMsgs = await GetAsync(alice, $"/api/personal/conversations/{selfId}/messages");
        var selfOnlyReaction = Assert.Single(Assert.Single(selfMsgs.GetProperty("items").EnumerateArray()).GetProperty("reactions").EnumerateArray());
        Assert.Equal("👍", selfOnlyReaction.GetProperty("emoji").GetString());
        Assert.Equal(1, selfOnlyReaction.GetProperty("count").GetInt32());
        Assert.True(selfOnlyReaction.GetProperty("reactedByMe").GetBoolean());

        await ProtectedAsync(bob, $"/api/personal/conversations/{selfId}/messages/{selfMsgId}/reactions/{thumbsUp}",
            null, HttpStatusCode.Forbidden, HttpMethod.Put);

        // 9. SEC-03 / SEC-05: Non-members (Carol, OrgA SystemAdmin, OrgBUser) denied HTTP + zero SignalR leaks
        foreach (var outsider in new[] { carol, sysAdmin, orgBUser })
        {
            await ProtectedAsync(outsider, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{thumbsUp}",
                null, HttpStatusCode.Forbidden, HttpMethod.Put);
            await ProtectedAsync(outsider, $"/api/personal/conversations/{dmId}/messages/{m1Id}/reactions/{heart}",
                null, HttpStatusCode.Forbidden, HttpMethod.Delete);
            await ProtectedAsync(outsider, $"/api/personal/conversations/{groupId}/messages/{grpMsgId}/reactions/{eyes}",
                null, HttpStatusCode.Forbidden, HttpMethod.Put);
        }

        await Task.Delay(150);
        Assert.Empty(carolEvents);
        Assert.Empty(sysAdminEvents);
        Assert.Empty(orgBEvents);
        _ = carolId;
    }

    [Fact]
    public async Task OrgIsolation_ChangesReplay_Sec18VisibleFromSeq_DmBlock_Channel404_Recalled409_And_CheckConstraint()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        using var carol = Client(baseUri, out var carolCookies);
        using var sysAdmin = Client(baseUri, out _);
        using var orgBUser = Client(baseUri, out var orgBCookies);

        var aliceId = await RegisterAsync(alice, "o61_alice_" + suffix);
        var bobId = await RegisterAsync(bob, "o61_bob_" + suffix);
        var carolId = await RegisterAsync(carol, "o61_carol_" + suffix);
        var sysAdminId = await RegisterAsync(sysAdmin, "o61_admin_" + suffix);
        var orgBUserId = await RegisterAsync(orgBUser, "o61_orgb_" + suffix);

        var orgAId = await CreateOrganizationAsync(alice, "O61 OrgA " + suffix);
        var orgBId = await CreateOrganizationAsync(orgBUser, "O61 OrgB " + suffix);

        await InviteAndJoinAsync(alice, bob, orgAId);
        await InviteAndJoinAsync(alice, carol, orgAId);
        await InviteAndJoinAsync(alice, sysAdmin, orgAId);
        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/members/{sysAdminId}/administrator",
            new { administrator = true }, HttpStatusCode.NoContent, HttpMethod.Put);

        await using var orgBHub = BuildHub(baseUri, $"/hubs/organizations/{orgBId}/messaging", orgBCookies);
        var orgBEvents = new ConcurrentQueue<JsonElement>();
        var carolEvents = new ConcurrentQueue<JsonElement>();
        await using var carolHub = BuildHub(baseUri, $"/hubs/organizations/{orgAId}/messaging", carolCookies);
        carolHub.On<JsonElement>("ConversationChanged", p => carolEvents.Enqueue(p.Clone()));
        orgBHub.On<JsonElement>("ConversationChanged", p => orgBEvents.Enqueue(p.Clone()));
        await orgBHub.StartAsync();
        await carolHub.StartAsync();
        var presence = app.Services.GetRequiredService<IPresenceTracker>();
        await WaitUntilAsync(() => presence.IsOnline(orgBId, orgBUserId) && presence.IsOnline(orgAId, carolId));

        var thumbsUp = Uri.EscapeDataString("👍");
        var heart = Uri.EscapeDataString("❤️");
        var party = Uri.EscapeDataString("🎉");
        var eyes = Uri.EscapeDataString("👀");

        // 1. OrgA DM + Cross-Org Isolation + SystemAdmin non-member denial + DM block denial
        var orgDm = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/dm",
            new { targetUserId = bobId }, HttpStatusCode.OK);
        var orgDmId = orgDm.GetProperty("id").GetGuid();

        var dmMsg = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgDmId}/messages", new
        {
            clientMsgId = $"o61-dm-{suffix}",
            body = "OrgA DM secret"
        }, HttpStatusCode.Created);
        var dmMsgId = dmMsg.GetProperty("id").GetGuid();

        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgDmId}/messages/{dmMsgId}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Put);

        // OrgB user denied via OrgB prefix, OrgA prefix, and Personal prefix; SystemAdmin non-member denied too
        await ProtectedAsync(orgBUser, $"/api/organizations/{orgBId}/conversations/{orgDmId}/messages/{dmMsgId}/reactions/{thumbsUp}",
            null, HttpStatusCode.Forbidden, HttpMethod.Put);
        await ProtectedAsync(orgBUser, $"/api/organizations/{orgAId}/conversations/{orgDmId}/messages/{dmMsgId}/reactions/{thumbsUp}",
            null, HttpStatusCode.Forbidden, HttpMethod.Put);
        await ProtectedAsync(orgBUser, $"/api/personal/conversations/{orgDmId}/messages/{dmMsgId}/reactions/{thumbsUp}",
            null, HttpStatusCode.Forbidden, HttpMethod.Put);
        await ProtectedAsync(sysAdmin, $"/api/organizations/{orgAId}/conversations/{orgDmId}/messages/{dmMsgId}/reactions/{thumbsUp}",
            null, HttpStatusCode.Forbidden, HttpMethod.Put);

        // DM block denies reactions from both sides
        await ProtectedAsync(bob, $"/api/organizations/{orgAId}/people/blocks/{aliceId}",
            null, HttpStatusCode.NoContent, HttpMethod.Put);
        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgDmId}/messages/{dmMsgId}/reactions/{heart}",
            null, HttpStatusCode.Forbidden, HttpMethod.Put);
        await ProtectedAsync(bob, $"/api/organizations/{orgAId}/conversations/{orgDmId}/messages/{dmMsgId}/reactions/{heart}",
            null, HttpStatusCode.Forbidden, HttpMethod.Put);

        // Unblock restores reaction capability
        await ProtectedAsync(bob, $"/api/organizations/{orgAId}/people/blocks/{aliceId}",
            null, HttpStatusCode.NoContent, HttpMethod.Delete);
        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgDmId}/messages/{dmMsgId}/reactions/{heart}",
            null, HttpStatusCode.OK, HttpMethod.Put);

        // 2. Channel conversations reject /messages/.../reactions with 404
        var comm = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/communities",
            new { name = "O61 Comm " + suffix, visibility = "Public" }, HttpStatusCode.Created, idempotencyKey: $"o61-comm-{suffix}");
        var channelId = comm.GetProperty("mainFeedConversationId").GetGuid();
        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{channelId}/messages/{Guid.NewGuid()}/reactions/{thumbsUp}",
            null, HttpStatusCode.NotFound, HttpMethod.Put);

        // 3. OrgA Group: /changes?after= replay, member removal, SEC-18 VisibleFromSeq, and recalled message 409
        var orgGrp = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/groups",
            new { title = "O61 Group", memberUserIds = new[] { bobId, carolId } }, HttpStatusCode.Created, idempotencyKey: $"o61-grp-{suffix}");
        var orgGrpId = orgGrp.GetProperty("id").GetGuid();

        var g1 = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages", new
        {
            clientMsgId = $"o61-g1-{suffix}",
            body = "Pre-removal #1"
        }, HttpStatusCode.Created);
        var g1Id = g1.GetProperty("id").GetGuid();

        var g2 = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages", new
        {
            clientMsgId = $"o61-g2-{suffix}",
            body = "Pre-removal #2"
        }, HttpStatusCode.Created);
        var g2Id = g2.GetProperty("id").GetGuid();

        // Remove Carol -> immediate 403 on reactions
        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/members/{carolId}",
            null, HttpStatusCode.NoContent, HttpMethod.Delete);
        await ProtectedAsync(carol, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g2Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.Forbidden, HttpMethod.Put);

        // While Carol is out: Alice reacts on g1 (seq 1) and sends g3 (seq 3) + reacts on g3
        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g1Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Put);
        var g3 = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages", new
        {
            clientMsgId = $"o61-g3-{suffix}",
            body = "While Carol away #3"
        }, HttpStatusCode.Created);
        var g3Id = g3.GetProperty("id").GetGuid();
        await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g3Id}/reactions/{party}",
            null, HttpStatusCode.OK, HttpMethod.Put);

        // Re-add Carol -> VisibleFromSeq = 3 (SEC-18)
        var reAdd = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/members",
            new { userId = carolId }, HttpStatusCode.OK);
        Assert.Equal(3L, reAdd.GetProperty("visibleFromSeq").GetInt64());

        // An active member who rejoined must not receive a live delta for the old history.
        var hiddenReaction = await ProtectedAsync(alice,
            $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g1Id}/reactions/{heart}",
            null, HttpStatusCode.OK, HttpMethod.Put);
        var hiddenChangeSeq = hiddenReaction.GetProperty("changeSeq").GetInt64();

        // Carol cannot react to pre-join messages (seq <= VisibleFromSeq) or non-existent messages -> identical 404 message_not_found
        foreach (var hiddenId in new[] { g1Id, g3Id, Guid.NewGuid() })
        {
            var hiddenResp = await ProtectedAsync(carol, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{hiddenId}/reactions/{thumbsUp}",
                null, HttpStatusCode.NotFound, HttpMethod.Put);
            Assert.Equal("message_not_found", hiddenResp.GetProperty("code").GetString());
        }

        // Send g4 (seq 4) after Carol rejoined, then Bob adds and removes 👀
        var g4 = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages", new
        {
            clientMsgId = $"o61-g4-{suffix}",
            body = "Visible after rejoin #4"
        }, HttpStatusCode.Created);
        var g4Id = g4.GetProperty("id").GetGuid();

        var addEyes = await ProtectedAsync(bob, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g4Id}/reactions/{eyes}",
            null, HttpStatusCode.OK, HttpMethod.Put);
        var addEyesCs = addEyes.GetProperty("changeSeq").GetInt64();
        var remEyes = await ProtectedAsync(bob, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g4Id}/reactions/{eyes}",
            null, HttpStatusCode.OK, HttpMethod.Delete);
        var remEyesCs = remEyes.GetProperty("changeSeq").GetInt64();
        await WaitUntilAsync(() => carolEvents.Any(e => e.GetProperty("conversationId").GetGuid() == orgGrpId &&
            e.GetProperty("changeSeq").GetInt64() == remEyesCs));
        Assert.DoesNotContain(carolEvents, e => e.GetProperty("conversationId").GetGuid() == orgGrpId &&
            e.GetProperty("changeSeq").GetInt64() == hiddenChangeSeq);

        // /changes?after=0 for Carol hides pre-join message.reaction.* (seq <= 3) and replays post-join reaction events!
        var carolChanges = await GetAsync(carol, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/changes?after=0");
        var carolItems = carolChanges.GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(carolItems, e =>
            e.GetProperty("eventType").GetString()!.StartsWith("message.", StringComparison.Ordinal) &&
            e.GetProperty("payload").GetProperty("seq").GetInt64() <= 3);

        var addedEvt = Assert.Single(carolItems, e =>
            e.GetProperty("changeSeq").GetInt64() == addEyesCs &&
            e.GetProperty("eventType").GetString() == "message.reaction.added");
        Assert.Equal(g4Id, addedEvt.GetProperty("payload").GetProperty("messageId").GetGuid());
        Assert.Equal(4L, addedEvt.GetProperty("payload").GetProperty("seq").GetInt64());
        Assert.Equal("👀", addedEvt.GetProperty("payload").GetProperty("emoji").GetString());
        Assert.Equal(bobId, addedEvt.GetProperty("payload").GetProperty("userId").GetGuid());
        Assert.True(addedEvt.GetProperty("payload").GetProperty("reacted").GetBoolean());

        var removedEvt = Assert.Single(carolItems, e =>
            e.GetProperty("changeSeq").GetInt64() == remEyesCs &&
            e.GetProperty("eventType").GetString() == "message.reaction.removed");
        Assert.Equal(g4Id, removedEvt.GetProperty("payload").GetProperty("messageId").GetGuid());
        Assert.False(removedEvt.GetProperty("payload").GetProperty("reacted").GetBoolean());

        // Incremental /changes?after=addEyesCs returns only remEyesCs
        var incremental = await GetAsync(carol, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/changes?after={addEyesCs}");
        var incOnly = Assert.Single(incremental.GetProperty("items").EnumerateArray());
        Assert.Equal(remEyesCs, incOnly.GetProperty("changeSeq").GetInt64());
        Assert.Equal("message.reaction.removed", incOnly.GetProperty("eventType").GetString());

        // Carol reacts on visible g4 -> 200 OK
        await ProtectedAsync(carol, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g4Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.OK, HttpMethod.Put);

        // 4. Recalled message -> 409 message_recalled on PUT & DELETE, and GET /messages returns reactions: []
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Messages.Where(m => m.Id == g4Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow));
        }

        var recalledPut = await ProtectedAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g4Id}/reactions/{heart}",
            null, HttpStatusCode.Conflict, HttpMethod.Put);
        Assert.Equal("message_recalled", recalledPut.GetProperty("code").GetString());

        var recalledDel = await ProtectedAsync(carol, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages/{g4Id}/reactions/{thumbsUp}",
            null, HttpStatusCode.Conflict, HttpMethod.Delete);
        Assert.Equal("message_recalled", recalledDel.GetProperty("code").GetString());

        var msgsAfterRecall = await GetAsync(carol, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages");
        var recalledDto = Assert.Single(msgsAfterRecall.GetProperty("items").EnumerateArray());
        Assert.Equal(g4Id, recalledDto.GetProperty("id").GetGuid());
        Assert.Empty(recalledDto.GetProperty("reactions").EnumerateArray());

        var aliceMsgsAfterRecall = await GetAsync(alice, $"/api/organizations/{orgAId}/conversations/{orgGrpId}/messages");
        var aliceGrpItems = aliceMsgsAfterRecall.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, aliceGrpItems.Count);
        Assert.Collection(aliceGrpItems[0].GetProperty("reactions").EnumerateArray(),
            reaction => Assert.Equal("👍", reaction.GetProperty("emoji").GetString()),
            reaction => Assert.Equal("❤️", reaction.GetProperty("emoji").GetString()));
        Assert.Empty(aliceGrpItems[1].GetProperty("reactions").EnumerateArray());
        Assert.Equal("🎉", Assert.Single(aliceGrpItems[2].GetProperty("reactions").EnumerateArray()).GetProperty("emoji").GetString());
        Assert.Empty(aliceGrpItems[3].GetProperty("reactions").EnumerateArray());

        // 5. Verify PostgreSQL CHECK constraint ck_message_reactions_emoji_non_empty directly under nexus_runtime
        await using (var conn = new NpgsqlConnection(pg.RuntimeConnectionString))
        {
            await conn.OpenAsync();
            foreach (var bad in new[] { "", "   " })
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO message_reactions (message_id, user_id, emoji, created_at)
                    VALUES (@mid, @uid, @emoji, NOW())
                    """, conn);
                cmd.Parameters.AddWithValue("mid", g1Id);
                cmd.Parameters.AddWithValue("uid", aliceId);
                cmd.Parameters.AddWithValue("emoji", bad);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
                Assert.Equal("ck_message_reactions_emoji_non_empty", ex.ConstraintName);
            }
        }

        await Task.Delay(150);
        Assert.Empty(orgBEvents);
    }

    private WebApplication BuildHost(params string[] extraArgs)
    {
        var args = new List<string>
        {
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true",
            "--NexusSecurity:RateLimitPermitsPerMinute=500",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        };
        args.AddRange(extraArgs);
        return NexusHost.Build([.. args], additionalLogSink: new ErrorSink(output));
    }

    private static Uri GetBaseUri(WebApplication app) =>
        new(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));

    private static HttpClient Client(Uri uri, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new HttpClientHandler { CookieContainer = cookies, CheckCertificateRevocationList = true }) { BaseAddress = uri };
    }

    private static HubConnection BuildHub(Uri baseUri, string hubPath, CookieContainer cookies) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, hubPath.TrimStart('/')), opts =>
            {
                opts.Cookies = cookies;
                opts.Headers["Origin"] = baseUri.GetLeftPart(UriPartial.Authority);
            })
            .Build();

    private static async Task<Guid> RegisterAsync(HttpClient client, string username) =>
        (await PostAsync(client, "/api/auth/register", new
        {
            username,
            displayName = username,
            password = TestPassword,
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

    private static async Task<JsonElement> ProtectedAsync(HttpClient client, string path, object? body, HttpStatusCode expected, HttpMethod? method = null, string? idempotencyKey = null)
    {
        var token = (await GetAsync(client, "/api/auth/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(raw) ? default : JsonSerializer.Deserialize<JsonElement>(raw);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 6000)
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

    private sealed class ErrorSink(ITestOutputHelper output) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Exception is not null) output.WriteLine(logEvent.Exception.ToString());
        }
    }
}
