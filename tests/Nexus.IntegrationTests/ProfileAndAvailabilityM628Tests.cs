using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Notifications;
using Nexus.Web.Modules.Org;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class ProfileAndAvailabilityM628Tests(PostgresContainerFixture pg, ITestOutputHelper output)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task SchemaConstraintsAndTimedAvailabilityLifecyclePropagateAndExpireWithoutPolling()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        var aliceId = await RegisterAsync(alice, "avail_alice_" + suffix);
        _ = await RegisterAsync(bob, "avail_bob_" + suffix);

        // 1. Verify PostgreSQL CHECK constraints reject invalid state and Available+expires_at
        await using (var conn = new NpgsqlConnection(pg.RuntimeConnectionString))
        {
            await conn.OpenAsync();
            await using (var badState = new NpgsqlCommand(
                "UPDATE users SET availability_state = 'Invisible' WHERE \"Id\" = @id", conn))
            {
                badState.Parameters.AddWithValue("id", aliceId);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => badState.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
            }

            await using (var badExpiry = new NpgsqlCommand(
                "UPDATE users SET availability_state = 'Available', availability_expires_at = NOW() + INTERVAL '1 hour' WHERE \"Id\" = @id", conn))
            {
                badExpiry.Parameters.AddWithValue("id", aliceId);
                var ex = await Assert.ThrowsAsync<PostgresException>(() => badExpiry.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
            }
        }

        // 2. CSRF protection & validation rules on PUT /api/people/me/availability
        using (var noCsrf = await alice.PutAsJsonAsync("/api/people/me/availability", new { state = "Busy" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            var body = await noCsrf.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_csrf", body.GetProperty("code").GetString());
        }

        await ProtectedAsync(alice, "/api/people/me/availability", new { state = "OutOfOffice" }, HttpStatusCode.BadRequest, HttpMethod.Put);
        await ProtectedAsync(alice, "/api/people/me/availability", new { state = "Available", durationMinutes = 30 }, HttpStatusCode.BadRequest, HttpMethod.Put);
        await ProtectedAsync(alice, "/api/people/me/availability", new { state = "Busy", durationMinutes = 0 }, HttpStatusCode.BadRequest, HttpMethod.Put);
        await ProtectedAsync(alice, "/api/people/me/availability", new { state = "Busy", durationMinutes = 10081 }, HttpStatusCode.BadRequest, HttpMethod.Put);
        await ProtectedAsync(alice, "/api/people/me/availability", new { state = "Busy", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }, HttpStatusCode.BadRequest, HttpMethod.Put);
        await ProtectedAsync(alice, "/api/people/me/availability", new { state = "Busy", expiresAt = DateTimeOffset.UtcNow.AddDays(8) }, HttpStatusCode.BadRequest, HttpMethod.Put);
        await ProtectedAsync(alice, "/api/people/me/availability", new
        {
            state = "Busy",
            durationMinutes = 30,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        }, HttpStatusCode.BadRequest, HttpMethod.Put);

        // Create OrgA, Personal contact, Personal DM, and Community so we can inspect every enriched payload
        var orgAId = await CreateOrganizationAsync(alice, "Avail Org " + suffix);
        await InviteAndJoinAsync(alice, bob, orgAId);
        await ConnectContactsAsync(alice, bob, "avail_bob_" + suffix);
        var dm = await PostAsync(bob, "/api/personal/conversations/dm", new { targetUserId = aliceId }, HttpStatusCode.OK);
        var dmId = dm.GetProperty("id").GetGuid();
        var comm = await PostWithIdempotencyAsync(alice, $"/api/organizations/{orgAId}/communities",
            new { name = "Avail Comm " + suffix, visibility = "Public" }, HttpStatusCode.Created);
        var commId = comm.GetProperty("id").GetGuid();
        await PostAsync(bob, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);

        // 3. Set Busy for 60 minutes (exercises nexus_runtime DML)
        var setBusy = await ProtectedAsync(alice, "/api/people/me/availability",
            new { state = "Busy", durationMinutes = 60 }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal("Busy", setBusy.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, setBusy.GetProperty("expiresAt").ValueKind);
        Assert.False(setBusy.GetProperty("suppressChime").GetBoolean());

        // Check /api/auth/me and /api/organizations/{orgA}/me
        var authMe = await GetAsync(alice, "/api/auth/me");
        Assert.Equal("Busy", authMe.GetProperty("availability").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, authMe.GetProperty("availability").GetProperty("expiresAt").ValueKind);
        Assert.False(authMe.GetProperty("suppressChime").GetBoolean());

        var orgMe = await GetAsync(alice, $"/api/organizations/{orgAId}/me");
        Assert.Equal("Busy", orgMe.GetProperty("availability").GetProperty("state").GetString());
        Assert.False(orgMe.GetProperty("suppressChime").GetBoolean());

        // Check enriched directory, chat candidates, conversations peer/members, and community members
        var orgDir = await GetAsync(bob, $"/api/organizations/{orgAId}/people/directory");
        var aliceOrgDir = orgDir.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == aliceId);
        Assert.Equal("Busy", aliceOrgDir.GetProperty("availability").GetProperty("state").GetString());

        var personalDir = await GetAsync(bob, "/api/personal/people/directory");
        var alicePersonalDir = personalDir.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == aliceId);
        Assert.Equal("Busy", alicePersonalDir.GetProperty("availability").GetProperty("state").GetString());

        var candidates = await GetAsync(bob, "/api/personal/people/chat-candidates");
        var aliceCandidate = candidates.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == aliceId);
        Assert.Equal("Busy", aliceCandidate.GetProperty("availability").GetProperty("state").GetString());

        var convs = await GetAsync(bob, "/api/personal/conversations");
        var dmItem = convs.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == dmId);
        Assert.Equal("Busy", dmItem.GetProperty("peer").GetProperty("availability").GetProperty("state").GetString());

        var dmMembers = await GetAsync(bob, $"/api/personal/conversations/{dmId}/members");
        var aliceMember = dmMembers.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("userId").GetGuid() == aliceId);
        Assert.Equal("Busy", aliceMember.GetProperty("availability").GetProperty("state").GetString());

        var commMembers = await GetAsync(bob, $"/api/organizations/{orgAId}/communities/{commId}/members");
        var aliceCommMember = commMembers.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("userId").GetGuid() == aliceId);
        Assert.Equal("Busy", aliceCommMember.GetProperty("availability").GetProperty("state").GetString());

        // 4. Simulate expiration in PostgreSQL -> zero-polling resolution immediately falls back to Available
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Users.Where(u => u.Id == aliceId)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.AvailabilityExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-10)));
        }

        var expiredMe = await GetAsync(alice, "/api/auth/me");
        Assert.Equal("Available", expiredMe.GetProperty("availability").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, expiredMe.GetProperty("availability").GetProperty("expiresAt").ValueKind);

        var expiredCard = await GetAsync(bob, $"/api/organizations/{orgAId}/people/{aliceId}/profile-card");
        Assert.Equal("Available", expiredCard.GetProperty("availability").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, expiredCard.GetProperty("availability").GetProperty("expiresAt").ValueKind);

        // 5. Set DoNotDisturb -> suppressChime true; clear to Available -> expiresAt null & suppressChime false
        var dnd = await ProtectedAsync(alice, "/api/people/me/availability",
            new { state = "DoNotDisturb", durationMinutes = 15 }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal("DoNotDisturb", dnd.GetProperty("state").GetString());
        Assert.True(dnd.GetProperty("suppressChime").GetBoolean());

        var cleared = await ProtectedAsync(alice, "/api/people/me/availability",
            new { state = "Available" }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal("Available", cleared.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("expiresAt").ValueKind);
        Assert.False(cleared.GetProperty("suppressChime").GetBoolean());
    }

    [Fact]
    public async Task ProfileCardsEnforceStrictOrgVsPersonalIsolationSec20bBlocksAndDisabledAccounts()
    {
        await using var app = BuildHost(configureBuilder: builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NexusIdentity:DailyNewDmLimitForUnverified"] = "1"
            });
        });
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        using var carol = Client(baseUri, out _);
        using var dave = Client(baseUri, out _);
        using var disabledClient = Client(baseUri, out _);
        using var unverifiedQuota = Client(baseUri, out _);

        var aliceId = await RegisterAsync(alice, "pc_alice_" + suffix);
        var bobId = await RegisterAsync(bob, "pc_bob_" + suffix);
        _ = await RegisterAsync(carol, "pc_carol_" + suffix);
        var daveId = await RegisterAsync(dave, "pc_dave_" + suffix);
        var disabledId = await RegisterAsync(disabledClient, "pc_dis_" + suffix);
        _ = await RegisterAsync(unverifiedQuota, "pc_unv_" + suffix);

        // OrgA: alice, bob, disabledUser, unverifiedQuotaUser
        var orgAId = await CreateOrganizationAsync(alice, "Alpha Org " + suffix);
        await InviteAndJoinAsync(alice, bob, orgAId);
        await InviteAndJoinAsync(alice, disabledClient, orgAId);
        await InviteAndJoinAsync(alice, unverifiedQuota, orgAId);

        // OrgB: carol, dave (completely isolated from OrgA)
        var orgBId = await CreateOrganizationAsync(carol, "Beta Org " + suffix);
        await InviteAndJoinAsync(carol, dave, orgBId);

        // Personal contact between alice and carol (NO shared org)
        await ConnectContactsAsync(alice, carol, "pc_carol_" + suffix);

        // Seed OrgA Department, Branch, CustomStatus, and AvatarObjectKey for alice; disable disabledId
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var now = DateTimeOffset.UtcNow;
            var dept = new DepartmentEntity { Id = Guid.CreateVersion7(), OrganizationId = orgAId, Name = "Engineering", Code = "ENG-" + suffix, CreatedAt = now };
            var branch = new BranchEntity { Id = Guid.CreateVersion7(), OrganizationId = orgAId, Name = "Hanoi HQ", Code = "HN-" + suffix, CreatedAt = now };
            db.Departments.Add(dept);
            db.Branches.Add(branch);
            await db.SaveChangesAsync();

            await db.OrganizationMemberships
                .Where(m => m.OrganizationId == orgAId && m.Id == aliceId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(m => m.DepartmentId, dept.Id)
                    .SetProperty(m => m.BranchId, branch.Id)
                    .SetProperty(m => m.CustomStatus, "On site at HQ")
                    .SetProperty(m => m.AvatarObjectKey, "avatars/alice-org-a.png"));

            await db.Users
                .Where(u => u.Id == disabledId)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.Status, AccountStatus.Disabled));
        }

        await ProtectedAsync(alice, "/api/people/me/availability",
            new { state = "Away", durationMinutes = 45 }, HttpStatusCode.OK, HttpMethod.Put);

        // 1. OrgA member bob sees OrgA directory fields via both scoped and context-aware routes
        foreach (var path in new[]
        {
            $"/api/organizations/{orgAId}/people/{aliceId}/profile-card",
            $"/api/people/{aliceId}/profile-card?organizationId={orgAId}"
        })
        {
            var card = await GetAsync(bob, path);
            Assert.Equal("organization", card.GetProperty("scope").GetString());
            Assert.Equal(orgAId, card.GetProperty("organizationId").GetGuid());
            Assert.Equal(aliceId, card.GetProperty("userId").GetGuid());
            Assert.Equal("Engineering", card.GetProperty("departmentName").GetString());
            Assert.Equal("Hanoi HQ", card.GetProperty("branchName").GetString());
            Assert.Equal("On site at HQ", card.GetProperty("customStatus").GetString());
            Assert.Equal("Verified", card.GetProperty("trustLevel").GetString());
            Assert.False(card.GetProperty("isUnverified").GetBoolean());
            Assert.Equal($"/api/organizations/{orgAId}/people/{aliceId}/avatar", card.GetProperty("avatarUrl").GetString());
            Assert.Equal("Away", card.GetProperty("availability").GetProperty("state").GetString());
            Assert.True(card.GetProperty("canStartDirectChat").GetBoolean());
            Assert.False(card.GetProperty("isBlockedByMe").GetBoolean());
        }

        // 2. Self card cannot start DM with self
        var selfCard = await GetAsync(alice, $"/api/organizations/{orgAId}/people/{aliceId}/profile-card");
        Assert.False(selfCard.GetProperty("canStartDirectChat").GetBoolean());

        // 3. Carol (OrgB, NOT in OrgA) is denied OrgA profile card on BOTH routes
        using (var orgCardAsOutsider = await carol.GetAsync($"/api/organizations/{orgAId}/people/{aliceId}/profile-card"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, orgCardAsOutsider.StatusCode);
        }
        using (var contextCardAsOutsider = await carol.GetAsync($"/api/people/{aliceId}/profile-card?organizationId={orgAId}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, contextCardAsOutsider.StatusCode);
        }

        // 4. Bob (OrgA) cannot look up OrgB-only dave via OrgA route
        using (var crossOrgTarget = await bob.GetAsync($"/api/organizations/{orgAId}/people/{daveId}/profile-card"))
        {
            Assert.Equal(HttpStatusCode.NotFound, crossOrgTarget.StatusCode);
        }

        // 5. Carol (Personal contact, outside OrgA) gets ONLY global fields on Personal & default routes — ZERO OrgA leak!
        foreach (var path in new[]
        {
            $"/api/personal/people/{aliceId}/profile-card",
            $"/api/people/{aliceId}/profile-card"
        })
        {
            var card = await GetAsync(carol, path);
            Assert.Equal("personal", card.GetProperty("scope").GetString());
            Assert.Equal(JsonValueKind.Null, card.GetProperty("organizationId").ValueKind);
            Assert.Equal(aliceId, card.GetProperty("userId").GetGuid());
            Assert.Equal("pc_alice_" + suffix, card.GetProperty("username").GetString());
            Assert.Equal("Away", card.GetProperty("availability").GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, card.GetProperty("avatarUrl").ValueKind);
            Assert.Equal(JsonValueKind.Null, card.GetProperty("customStatus").ValueKind);
            Assert.True(card.GetProperty("isContact").GetBoolean());
            Assert.Empty(card.GetProperty("sharedWorkspaceNames").EnumerateArray());
            Assert.True(card.GetProperty("canStartDirectChat").GetBoolean());

            // Assert Organization-only fields are completely absent
            Assert.False(card.TryGetProperty("departmentId", out _));
            Assert.False(card.TryGetProperty("departmentName", out _));
            Assert.False(card.TryGetProperty("branchId", out _));
            Assert.False(card.TryGetProperty("branchName", out _));
            Assert.False(card.TryGetProperty("orgRole", out _));
            Assert.False(card.TryGetProperty("trustLevel", out _));
            Assert.False(card.TryGetProperty("isUnverified", out _));
            Assert.False(card.TryGetProperty("isOwner", out _));
            Assert.False(card.TryGetProperty("employeeCode", out _));
        }

        // 6. Dave (outsider with no contact, no shared org, no shared conversation) gets 404 on Personal profile card
        using (var strangerCard = await dave.GetAsync($"/api/personal/people/{aliceId}/profile-card"))
        {
            Assert.Equal(HttpStatusCode.NotFound, strangerCard.StatusCode);
        }

        // 7. Disabled target returns 404 on both Org and Personal routes
        using (var disabledOrgCard = await bob.GetAsync($"/api/organizations/{orgAId}/people/{disabledId}/profile-card"))
        {
            Assert.Equal(HttpStatusCode.NotFound, disabledOrgCard.StatusCode);
        }
        using (var disabledPersonalCard = await bob.GetAsync($"/api/personal/people/{disabledId}/profile-card"))
        {
            Assert.Equal(HttpStatusCode.NotFound, disabledPersonalCard.StatusCode);
        }

        // 8. Blocking disables canStartDirectChat and sets isBlockedByMe = true
        using (var blockResp = await bob.PutAsync($"/api/organizations/{orgAId}/people/blocks/{aliceId}", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, blockResp.StatusCode);
        }
        var blockedCard = await GetAsync(bob, $"/api/organizations/{orgAId}/people/{aliceId}/profile-card");
        Assert.True(blockedCard.GetProperty("isBlockedByMe").GetBoolean());
        Assert.False(blockedCard.GetProperty("canStartDirectChat").GetBoolean());

        // 9. SEC-20b: Unverified user at daily new DM quota sees canStartDirectChat = false for a new peer
        await PostAsync(unverifiedQuota, $"/api/organizations/{orgAId}/conversations/dm", new { targetUserId = aliceId }, HttpStatusCode.OK);
        var quotaCard = await GetAsync(unverifiedQuota, $"/api/organizations/{orgAId}/people/{bobId}/profile-card");
        Assert.False(quotaCard.GetProperty("canStartDirectChat").GetBoolean());
        // Existing DM peer still allowed
        var existingPeerCard = await GetAsync(unverifiedQuota, $"/api/organizations/{orgAId}/people/{aliceId}/profile-card");
        Assert.True(existingPeerCard.GetProperty("canStartDirectChat").GetBoolean());
    }

    [Fact]
    public async Task SignalRAvailabilityChangedSyncsMultiDeviceScopedPeersRevocationAndPresenceCap()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var aliceDev1 = Client(baseUri, out var aliceCookies1);
        using var aliceDev2 = Client(baseUri, out var aliceCookies2);
        using var bob = Client(baseUri, out var bobCookies);
        using var carol = Client(baseUri, out var carolCookies);
        using var dave = Client(baseUri, out var daveCookies);

        var aliceName = "sig_alice_" + suffix;
        var aliceId = await RegisterAsync(aliceDev1, aliceName);
        await PostAsync(aliceDev2, "/api/auth/login", new { username = aliceName, password = "Chat-scope-password-2026!" }, HttpStatusCode.OK);
        var bobId = await RegisterAsync(bob, "sig_bob_" + suffix);
        var carolName = "sig_carol_" + suffix;
        _ = await RegisterAsync(carol, carolName);
        var daveId = await RegisterAsync(dave, "sig_dave_" + suffix);

        // OrgA: alice + bob; OrgB: dave; Personal contact: alice <-> carol
        var orgAId = await CreateOrganizationAsync(aliceDev1, "Signal OrgA " + suffix);
        await InviteAndJoinAsync(aliceDev1, bob, orgAId);
        await CreateOrganizationAsync(dave, "Signal OrgB " + suffix);
        await ConnectContactsAsync(aliceDev1, carol, carolName);

        var dev2Events = Channel.CreateUnbounded<JsonElement>();
        var bobEvents = Channel.CreateUnbounded<JsonElement>();
        var carolEvents = Channel.CreateUnbounded<JsonElement>();
        var daveEvents = Channel.CreateUnbounded<JsonElement>();

        await using var hubDev1 = await ConnectPersonalHubAsync(baseUri, aliceCookies1);
        await using var hubDev2 = await ConnectPersonalHubAsync(baseUri, aliceCookies2, dev2Events);
        await using var hubBob = await ConnectPersonalHubAsync(baseUri, bobCookies, bobEvents);
        await using var hubCarol = await ConnectPersonalHubAsync(baseUri, carolCookies, carolEvents);
        await using var hubDave = await ConnectPersonalHubAsync(baseUri, daveCookies, daveEvents);

        // Verify single-window /hubs/personal/messaging connection reports isOnline = true in BOTH Personal and OrgA routes
        var aliceOrgCard = await GetAsync(bob, $"/api/organizations/{orgAId}/people/{aliceId}/profile-card");
        Assert.True(aliceOrgCard.GetProperty("isOnline").GetBoolean());
        var alicePersonalCard = await GetAsync(carol, $"/api/personal/people/{aliceId}/profile-card");
        Assert.True(alicePersonalCard.GetProperty("isOnline").GetBoolean());

        // Drain any initial connect events
        DrainChannel(dev2Events);
        DrainChannel(bobEvents);
        DrainChannel(carolEvents);
        DrainChannel(daveEvents);

        // 1. Alice Device 1 sets DoNotDisturb -> Device 2, OrgA bob, and Personal contact carol receive delta; OrgB dave gets nothing
        await ProtectedAsync(aliceDev1, "/api/people/me/availability",
            new { state = "DoNotDisturb", durationMinutes = 30 }, HttpStatusCode.OK, HttpMethod.Put);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dev2Delta = await ReadForUserAsync(dev2Events, aliceId, "DoNotDisturb", timeout.Token);
        var bobDelta = await ReadForUserAsync(bobEvents, aliceId, "DoNotDisturb", timeout.Token);
        var carolDelta = await ReadForUserAsync(carolEvents, aliceId, "DoNotDisturb", timeout.Token);

        Assert.True(dev2Delta.GetProperty("isOnline").GetBoolean());
        Assert.True(bobDelta.GetProperty("isOnline").GetBoolean());
        Assert.True(carolDelta.GetProperty("isOnline").GetBoolean());
        Assert.Equal(JsonValueKind.String, bobDelta.GetProperty("availability").GetProperty("expiresAt").ValueKind);

        await Task.Delay(150);
        Assert.False(daveEvents.Reader.TryRead(out _));

        // 2. Revoke bob from OrgA -> bob must NOT receive subsequent availability changes from alice
        await ProtectedAsync(aliceDev1, $"/api/organizations/{orgAId}/members/{bobId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        DrainChannel(bobEvents);
        DrainChannel(dev2Events);
        DrainChannel(carolEvents);

        await ProtectedAsync(aliceDev1, "/api/people/me/availability",
            new { state = "Away", durationMinutes = 20 }, HttpStatusCode.OK, HttpMethod.Put);

        await ReadForUserAsync(dev2Events, aliceId, "Away", timeout.Token);
        await ReadForUserAsync(carolEvents, aliceId, "Away", timeout.Token);
        await Task.Delay(150);
        Assert.False(bobEvents.Reader.TryRead(out _));

        // 3. Disconnect aliceDev1 (aliceDev2 still online -> stays online), then disconnect aliceDev2 -> carol gets isOnline = false
        DrainChannel(carolEvents);
        await hubDev1.StopAsync();
        await Task.Delay(150);
        Assert.True((await GetAsync(carol, $"/api/personal/people/{aliceId}/profile-card")).GetProperty("isOnline").GetBoolean());

        await hubDev2.StopAsync();
        var offlineDelta = await ReadOnlineStateAsync(carolEvents, aliceId, expectedOnline: false, timeout.Token);
        Assert.False(offlineDelta.GetProperty("isOnline").GetBoolean());

        // 4. Presence snapshot endpoint enforces <= 50 cap and filters out unauthorized outsiders
        var fiftyOneIds = string.Join(",", Enumerable.Range(0, 51).Select(_ => Guid.NewGuid().ToString("D")));
        using (var tooManyPersonal = await carol.GetAsync($"/api/personal/people/presence?userIds={fiftyOneIds}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooManyPersonal.StatusCode);
            Assert.Equal("invalid_page_size", (await tooManyPersonal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var tooManyOrg = await aliceDev1.GetAsync($"/api/organizations/{orgAId}/people/presence?userIds={fiftyOneIds}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooManyOrg.StatusCode);
        }

        // Carol requests presence for alice (authorized contact) + dave (unauthorized outsider) -> only alice returned
        var snapshot = await GetAsync(carol, $"/api/personal/people/presence?userIds={aliceId},{daveId}");
        var snapItems = snapshot.GetProperty("items").EnumerateArray().ToList();
        var onlyAlice = Assert.Single(snapItems);
        Assert.Equal(aliceId, onlyAlice.GetProperty("userId").GetGuid());
        Assert.False(onlyAlice.GetProperty("isOnline").GetBoolean());
        Assert.Equal("Away", onlyAlice.GetProperty("availability").GetProperty("state").GetString());
    }

    [Fact]
    public async Task DoNotDisturbSuppressesWebPushPreservesActivityAndResumesOnExpiryOrClear()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fakePush = new RecordingPushHandler();
        await using var app = BuildHost(configureBuilder: builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NexusPush:VapidPrivateKeyPem"] = key.ExportECPrivateKeyPem(),
                ["NexusPush:VapidSubject"] = "mailto:dnd-fixture@example.test"
            });
            builder.Services.AddHttpClient(WebPushTransport.ClientName).ConfigurePrimaryHttpMessageHandler(() => fakePush);
        });
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        await RegisterAsync(alice, "dnd_alice_" + suffix);
        var bobName = "dnd_bob_" + suffix;
        var bobId = await RegisterAsync(bob, bobName);

        const string endpoint = "https://fcm.googleapis.com/fcm/send/synthetic-dnd-endpoint";
        await ProtectedAsync(bob, "/api/notifications/push", new { endpoint }, HttpStatusCode.NoContent, HttpMethod.Put);
        await ConnectContactsAsync(alice, bob, bobName);

        var dm = await PostAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK);
        var convId = dm.GetProperty("id").GetGuid();
        var msgPath = $"/api/personal/conversations/{convId}/messages";

        // 1. Bob activates timed DoNotDisturb -> message creates in-app Activity notification, but Web Push is suppressed
        await ProtectedAsync(bob, "/api/people/me/availability",
            new { state = "DoNotDisturb", durationMinutes = 60 }, HttpStatusCode.OK, HttpMethod.Put);

        var msg1 = await PostAsync(alice, msgPath, new { body = "Message while DND", clientMsgId = "dnd-1-" + suffix }, HttpStatusCode.Created);
        var msg1Id = msg1.GetProperty("id").GetGuid();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await WaitForPushOutboxCompletedAsync(app.Services, msg1Id, timeout.Token);

        // Activity notification preserved and unread; zero Web Push sent
        var notifications = await GetAsync(bob, "/api/notifications");
        Assert.Contains(notifications.GetProperty("items").EnumerateArray(),
            n => n.GetProperty("resourceId").GetGuid() == msg1Id && n.GetProperty("isUnread").GetBoolean());
        Assert.False(fakePush.Requests.Reader.TryRead(out _));

        // 2. DND expires in PostgreSQL -> next message immediately delivers Web Push without polling
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Users.Where(u => u.Id == bobId)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.AvailabilityExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-5)));
        }

        await PostAsync(alice, msgPath, new { body = "Message after DND expired", clientMsgId = "dnd-2-" + suffix }, HttpStatusCode.Created);
        var pushAfterExpiry = await fakePush.Requests.Reader.ReadAsync(timeout.Token);
        Assert.Equal(endpoint, pushAfterExpiry.Endpoint);

        // 3. Indefinite DND suppresses push again; clearing to Available resumes push
        await ProtectedAsync(bob, "/api/people/me/availability",
            new { state = "DoNotDisturb" }, HttpStatusCode.OK, HttpMethod.Put);

        var msg3 = await PostAsync(alice, msgPath, new { body = "Message while indefinite DND", clientMsgId = "dnd-3-" + suffix }, HttpStatusCode.Created);
        await WaitForPushOutboxCompletedAsync(app.Services, msg3.GetProperty("id").GetGuid(), timeout.Token);
        Assert.False(fakePush.Requests.Reader.TryRead(out _));

        await ProtectedAsync(bob, "/api/people/me/availability",
            new { state = "Available" }, HttpStatusCode.OK, HttpMethod.Put);

        await PostAsync(alice, msgPath, new { body = "Message after DND cleared", clientMsgId = "dnd-4-" + suffix }, HttpStatusCode.Created);
        var pushAfterClear = await fakePush.Requests.Reader.ReadAsync(timeout.Token);
        Assert.Equal(endpoint, pushAfterClear.Endpoint);
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildHost(Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configureBuilder = null) =>
        NexusHost.Build([
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], configureBuilder: configureBuilder, additionalLogSink: new ErrorSink(output));

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
            password = "Chat-scope-password-2026!",
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

    private static async Task<HubConnection> ConnectPersonalHubAsync(
        Uri baseUri,
        CookieContainer cookies,
        Channel<JsonElement>? availabilitySink = null)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, OrganizationScope.PersonalHub), opts =>
            {
                opts.Cookies = cookies;
                opts.Headers["Origin"] = baseUri.GetLeftPart(UriPartial.Authority);
            })
            .Build();

        if (availabilitySink is not null)
        {
            hub.On<JsonElement>("AvailabilityChanged", payload => availabilitySink.Writer.TryWrite(payload));
        }

        await hub.StartAsync();
        return hub;
    }

    private static void DrainChannel(Channel<JsonElement> ch)
    {
        while (ch.Reader.TryRead(out _))
        {
        }
    }

    private static async Task<JsonElement> ReadForUserAsync(Channel<JsonElement> ch, Guid userId, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            var item = await ch.Reader.ReadAsync(ct);
            if (item.GetProperty("userId").GetGuid() == userId &&
                item.GetProperty("availability").GetProperty("state").GetString() == expectedState)
            {
                return item;
            }
        }
    }

    private static async Task<JsonElement> ReadOnlineStateAsync(Channel<JsonElement> ch, Guid userId, bool expectedOnline, CancellationToken ct)
    {
        while (true)
        {
            var item = await ch.Reader.ReadAsync(ct);
            if (item.GetProperty("userId").GetGuid() == userId &&
                item.GetProperty("isOnline").GetBoolean() == expectedOnline)
            {
                return item;
            }
        }
    }

    private static async Task WaitForPushOutboxCompletedAsync(IServiceProvider services, Guid messageId, CancellationToken ct)
    {
        while (true)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var completed = await (
                from delivery in db.OutboxMessages.AsNoTracking()
                join notice in db.Notifications.AsNoTracking() on delivery.ResourceId equals notice.Id
                where notice.ResourceId == messageId && delivery.Kind == "notification.push" && delivery.CompletedAt != null
                select delivery.Id).AnyAsync(ct);
            if (completed)
            {
                return;
            }

            await Task.Delay(50, ct);
        }
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
        return expected is HttpStatusCode.NoContent or HttpStatusCode.Accepted
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

    private sealed class RecordingPushHandler : HttpMessageHandler
    {
        public Channel<RecordedPush> Requests { get; } = Channel.CreateUnbounded<RecordedPush>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Requests.Writer.WriteAsync(new RecordedPush(
                request.RequestUri!.AbsoluteUri,
                Assert.Single(request.Headers.GetValues("Authorization")),
                Assert.Single(request.Headers.GetValues("Topic"))), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private sealed record RecordedPush(string Endpoint, string Authorization, string Topic);
}
