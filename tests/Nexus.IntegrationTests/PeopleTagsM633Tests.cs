using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.People;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class PeopleTagsM633Tests(PostgresContainerFixture pg, ITestOutputHelper output)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task OrgAndCommunityTagCrudValidationCsrfAuthzAndPagination()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var owner = Client(baseUri, out _);
        using var member = Client(baseUri, out _);
        using var nonCommMember = Client(baseUri, out _);
        using var orgBUser = Client(baseUri, out _);

        var ownerId = await RegisterAsync(owner, "tag_own_" + suffix);
        var memberId = await RegisterAsync(member, "tag_mem_" + suffix);
        var nonCommId = await RegisterAsync(nonCommMember, "tag_non_" + suffix);
        _ = await RegisterAsync(orgBUser, "tag_orgb_" + suffix);

        var orgAId = await CreateOrganizationAsync(owner, "Tags OrgA " + suffix);
        await InviteAndJoinAsync(owner, member, orgAId);
        await InviteAndJoinAsync(owner, nonCommMember, orgAId);
        var orgBId = await CreateOrganizationAsync(orgBUser, "Tags OrgB " + suffix);

        // 1. CSRF & validation checks
        using (var noCsrf = await owner.PostAsJsonAsync($"/api/organizations/{orgAId}/tags", new { name = "On Call", slug = "oncall" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            Assert.Equal("invalid_csrf", (await noCsrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        foreach (var reserved in new[] { "all", "channel", "everyone", "here", "a", "bad slug!" })
        {
            var bad = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/tags",
                new { name = "Reserved Test", slug = reserved }, HttpStatusCode.BadRequest);
            Assert.Equal("invalid_tag_slug", bad.GetProperty("code").GetString());
        }

        // Ordinary member & OrgB outsider denied creating Org tags (403)
        await ProtectedAsync(member, $"/api/organizations/{orgAId}/tags", new { name = "No Perm", slug = "noperm" }, HttpStatusCode.Forbidden);
        await ProtectedAsync(orgBUser, $"/api/organizations/{orgAId}/tags", new { name = "OrgB", slug = "orgb" }, HttpStatusCode.Forbidden);

        // 2. Org tag CRUD + duplicate slug 409 + member management
        var created = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/tags", new
        {
            name = "On Call",
            slug = "oncall",
            description = "Primary incident responders",
            memberUserIds = new[] { memberId }
        }, HttpStatusCode.Created);
        var orgTagId = created.GetProperty("id").GetGuid();
        Assert.Equal("organization", created.GetProperty("scope").GetString());
        Assert.Equal(1, created.GetProperty("memberCount").GetInt32());

        // Duplicate slug in same Org -> 409 tag_slug_exists
        var dup = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/tags",
            new { name = "On Call 2", slug = "oncall" }, HttpStatusCode.Conflict);
        Assert.Equal("tag_slug_exists", dup.GetProperty("code").GetString());

        // Same slug in OrgB is completely isolated and allowed!
        await ProtectedAsync(orgBUser, $"/api/organizations/{orgBId}/tags",
            new { name = "OrgB On Call", slug = "oncall" }, HttpStatusCode.Created);

        // Update tag + add/list/remove member
        var updated = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/tags/{orgTagId}",
            new { name = "Incident On-Call", slug = "oncall", description = "Updated desc" }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal("Incident On-Call", updated.GetProperty("name").GetString());

        await ProtectedAsync(owner, $"/api/organizations/{orgAId}/tags/{orgTagId}/members", new { userId = nonCommId }, HttpStatusCode.OK);
        var tagMembers = await GetAsync(member, $"/api/organizations/{orgAId}/tags/{orgTagId}/members");
        Assert.Equal(2, tagMembers.GetProperty("items").GetArrayLength());

        await ProtectedAsync(owner, $"/api/organizations/{orgAId}/tags/{orgTagId}/members/{nonCommId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        var afterRemove = await GetAsync(member, $"/api/organizations/{orgAId}/tags/{orgTagId}/members");
        Assert.Single(afterRemove.GetProperty("items").EnumerateArray());

        // 3. Community-scoped tags + includeOrgTags=true + eligible member check + SEC-20b
        var comm = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
            new { name = "Eng Comm " + suffix, visibility = "Public" }, HttpStatusCode.Created);
        var commId = comm.GetProperty("id").GetGuid();
        await PostAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/join", new { }, HttpStatusCode.OK);

        // Non-community-member cannot be added to Community tag
        var ineligible = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/tags", new
        {
            name = "Backend",
            slug = "backend",
            memberUserIds = new[] { nonCommId }
        }, HttpStatusCode.BadRequest);
        Assert.Equal("ineligible_tag_member", ineligible.GetProperty("code").GetString());

        // Community Owner creates @backend with eligible member
        var commTag = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/tags", new
        {
            name = "Backend",
            slug = "backend",
            memberUserIds = new[] { memberId }
        }, HttpStatusCode.Created);
        var commTagId = commTag.GetProperty("id").GetGuid();
        Assert.Equal("community", commTag.GetProperty("scope").GetString());

        // includeOrgTags=true returns BOTH Community @backend and Org @oncall
        var allUsable = await GetAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/tags?includeOrgTags=true");
        Assert.False(allUsable.GetProperty("canManage").GetBoolean());
        Assert.Equal(2, allUsable.GetProperty("items").GetArrayLength());

        // Ordinary community member denied creating/deleting Community tag
        await ProtectedAsync(member, $"/api/organizations/{orgAId}/communities/{commId}/tags",
            new { name = "Member Tag", slug = "memtag" }, HttpStatusCode.Forbidden);

        // SEC-20b: Official community blocks Unverified member from listing community tags
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Communities.Where(c => c.Id == commId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsOfficial, true));
        }
        using (var unvList = await member.GetAsync($"/api/organizations/{orgAId}/communities/{commId}/tags"))
            Assert.Equal(HttpStatusCode.Forbidden, unvList.StatusCode);

        // Delete community tag + verify audit logs
        await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{commId}/tags/{commTagId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);

        // 4. Pagination > 50 Org tags + hard cap 51 rejected
        using (var badLimit = await member.GetAsync($"/api/organizations/{orgAId}/tags?limit=51"))
            Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var now = DateTimeOffset.UtcNow;
            for (var i = 1; i <= 51; i++)
            {
                db.PeopleTags.Add(new PeopleTagEntity
                {
                    Id = Guid.CreateVersion7(),
                    OrganizationId = orgAId,
                    Name = $"Seed Tag {i:D2}",
                    Slug = $"seed-{i:D2}",
                    CreatedByUserId = ownerId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            await db.SaveChangesAsync();

            var audits = await db.AuditLogs.AsNoTracking()
                .Where(a => a.OrganizationId == orgAId && a.ResourceType == "PeopleTag")
                .Select(a => a.Action).ToListAsync();
            Assert.Contains("PEOPLE_TAG_CREATED", audits);
            Assert.Contains("PEOPLE_TAG_UPDATED", audits);
            Assert.Contains("PEOPLE_TAG_DELETED", audits);
        }

        var p1 = await GetAsync(member, $"/api/organizations/{orgAId}/tags?limit=50");
        Assert.Equal(50, p1.GetProperty("items").GetArrayLength());
        var nextCursor = p1.GetProperty("nextCursor").GetString()!;
        Assert.False(string.IsNullOrEmpty(nextCursor));
        var p2 = await GetAsync(member, $"/api/organizations/{orgAId}/tags?limit=50&cursor={Uri.EscapeDataString(nextCursor)}");
        Assert.Equal(2, p2.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, p2.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task TagMentionsDeduplicateOverlappingMembersRespectMuteScopeAndRevocation()
    {
        await using var app = BuildHost();
        await app.StartAsync();
        var baseUri = GetBaseUri(app);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var owner = Client(baseUri, out _);
        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);

        _ = await RegisterAsync(owner, "tm_own_" + suffix);
        var aliceName = "tm_alice_" + suffix;
        var aliceId = await RegisterAsync(alice, aliceName);
        var bobId = await RegisterAsync(bob, "tm_bob_" + suffix);

        var orgAId = await CreateOrganizationAsync(owner, "Mention OrgA " + suffix);
        await InviteAndJoinAsync(owner, alice, orgAId);
        await InviteAndJoinAsync(owner, bob, orgAId);

        var comm1 = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
            new { name = "Comm One " + suffix, visibility = "Public" }, HttpStatusCode.Created);
        var comm1Id = comm1.GetProperty("id").GetGuid();
        var comm1FeedId = comm1.GetProperty("mainFeedConversationId").GetGuid();
        await PostAsync(alice, $"/api/organizations/{orgAId}/communities/{comm1Id}/join", new { }, HttpStatusCode.OK);
        await PostAsync(bob, $"/api/organizations/{orgAId}/communities/{comm1Id}/join", new { }, HttpStatusCode.OK);

        var comm2 = await PostWithIdempotencyAsync(owner, $"/api/organizations/{orgAId}/communities",
            new { name = "Comm Two " + suffix, visibility = "Public" }, HttpStatusCode.Created);
        var comm2Id = comm2.GetProperty("id").GetGuid();
        await PostAsync(bob, $"/api/organizations/{orgAId}/communities/{comm2Id}/join", new { }, HttpStatusCode.OK);

        // Org tag @oncall = [alice, bob]; Comm1 tag @backend = [alice]; Comm2 tag @ops = [bob]
        var oncallTag = (await ProtectedAsync(owner, $"/api/organizations/{orgAId}/tags",
            new { name = "On Call", slug = "oncall", memberUserIds = new[] { aliceId, bobId } }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var backendTag = (await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{comm1Id}/tags",
            new { name = "Backend", slug = "backend", memberUserIds = new[] { aliceId } }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        _ = await ProtectedAsync(owner, $"/api/organizations/{orgAId}/communities/{comm2Id}/tags",
            new { name = "Ops", slug = "ops", memberUserIds = new[] { bobId } }, HttpStatusCode.Created);

        // 1. Overlapping mentions (@oncall + @backend + @alice) in Comm1 deliver EXACTLY ONE notification to alice & ONE to bob
        var post1 = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{comm1FeedId}/posts", new
        {
            body = $"Deploy alert @oncall @backend @{aliceName}"
        }, HttpStatusCode.Created);
        var post1Id = post1.GetProperty("id").GetGuid();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await WaitForOutboxCompletedAsync(app.Services, post1Id, timeout.Token);

        var aliceNotifs = (await GetAsync(alice, "/api/notifications")).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("resourceId").GetGuid() == post1Id).ToList();
        var aliceSingle = Assert.Single(aliceNotifs);
        Assert.Equal("mention.post", aliceSingle.GetProperty("kind").GetString());

        var bobNotifs = (await GetAsync(bob, "/api/notifications")).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("resourceId").GetGuid() == post1Id).ToList();
        Assert.Single(bobNotifs);

        // 2. Other-community tag (@ops from Comm2) does NOT expand in Comm1
        var post2 = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{comm1FeedId}/posts", new
        {
            body = "Ping @ops from wrong community"
        }, HttpStatusCode.Created);
        var post2Id = post2.GetProperty("id").GetGuid();
        await Task.Delay(250);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.ResourceId == post2Id));
        }

        // 3. Personal scope DM NEVER expands Org tag @oncall
        var dmId = (await PostAsync(owner, "/api/personal/conversations/dm", new { targetUserId = aliceId }, HttpStatusCode.OK))
            .GetProperty("id").GetGuid();
        var dmMsg = await PostAsync(owner, $"/api/personal/conversations/{dmId}/messages", new
        {
            body = "Personal DM mentioning @oncall",
            clientMsgId = "dm-tag-" + suffix
        }, HttpStatusCode.Created);
        var dmMsgId = dmMsg.GetProperty("id").GetGuid();
        await Task.Delay(250);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.ResourceId == dmMsgId && n.Kind == "mention.message"));
        }

        // 4. Muted conversation suppresses @backend mention notification for alice
        await ProtectedAsync(alice, $"/api/notifications/conversations/{comm1FeedId}/preference",
            new { mode = "muted" }, HttpStatusCode.NoContent, HttpMethod.Put);
        var post3 = await PostAsync(owner, $"/api/organizations/{orgAId}/channels/{comm1FeedId}/posts", new
        {
            body = "Muted ping @backend"
        }, HttpStatusCode.Created);
        var post3Id = post3.GetProperty("id").GetGuid();
        await WaitForOutboxCompletedAsync(app.Services, post3Id, timeout.Token);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.ResourceId == post3Id && n.RecipientId == aliceId));
        }

        // 5. Revocation cleanup: removing alice from Comm1 deletes her from @backend; revoking bob from OrgA deletes him from @oncall
        using (var remComm = await owner.DeleteAsync($"/api/organizations/{orgAId}/communities/{comm1Id}/members/{aliceId}"))
            Assert.Equal(HttpStatusCode.NoContent, remComm.StatusCode);
        await ProtectedAsync(owner, $"/api/organizations/{orgAId}/members/{bobId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.False(await db.PeopleTagMembers.AnyAsync(tm => tm.TagId == backendTag && tm.UserId == aliceId));
            Assert.False(await db.PeopleTagMembers.AnyAsync(tm => tm.TagId == oncallTag && tm.UserId == bobId));
        }
    }

    private static async Task WaitForOutboxCompletedAsync(IServiceProvider services, Guid resourceId, CancellationToken ct)
    {
        while (true)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var done = await db.OutboxMessages.AsNoTracking()
                .AnyAsync(o => o.ResourceId == resourceId && o.CompletedAt != null, ct);
            if (done) return;
            await Task.Delay(50, ct);
        }
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildHost() =>
        NexusHost.Build([
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));

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
            password = "People-tags-password-2026!",
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
