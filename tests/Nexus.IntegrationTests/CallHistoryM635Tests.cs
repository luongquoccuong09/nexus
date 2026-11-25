using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Calls;
using static Nexus.IntegrationTests.MeetingsM612Tests;

namespace Nexus.IntegrationTests;

public sealed class CallHistoryM635Tests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private const string Personal = "/api/personal";
    private const string History = "/api/calls/history";
    private static readonly DirectCallState[] TerminalStates =
        [DirectCallState.Ended, DirectCallState.Declined, DirectCallState.Canceled, DirectCallState.Missed];

    [Fact]
    public async Task CallHistoryPagesTiedRowsAndFiltersByActorAcrossDevicesAndScopes()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var viewer = new BrowserClient(app);
        using var viewerOtherDevice = new BrowserClient(app);
        using var peer = new BrowserClient(app);
        using var outsider = new BrowserClient(app);
        var viewerName = UserName("viewer");
        var viewerId = await viewer.RegisterAsync(viewerName);
        await viewerOtherDevice.LoginAsync(viewerName);
        var peerName = UserName("peer");
        var peerId = await peer.RegisterAsync(peerName);
        var outsiderId = await outsider.RegisterAsync(UserName("other"));
        var orgA = await CreateOrgAsync(viewer, "History Organization A");
        var orgB = await CreateOrgAsync(outsider, "History Organization B");
        await JoinOrgAsync(viewer, peer, orgA);
        await JoinOrgAsync(outsider, peer, orgB);
        await AcceptContactAsync(viewer, peer, peerName);

        var personalDm = await OpenDmAsync(viewer, Personal, peerId);
        var organizationDm = await OpenDmAsync(viewer, OrgApi(orgA), peerId);
        var separateDm = await OpenDmAsync(outsider, OrgApi(orgB), peerId);
        var created = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-1).AddHours(14);
        var tied = Enumerable.Range(0, 54).Select(index =>
        {
            var personal = index % 2 == 0;
            var outgoing = index % 3 == 0;
            return Row(personal ? null : orgA, personal ? personalDm : organizationDm,
                outgoing ? viewerId : peerId, outgoing ? peerId : viewerId, TerminalStates[index % TerminalStates.Length], created);
        }).ToArray();
        var completedVideo = Row(orgA, organizationDm, viewerId, peerId, DirectCallState.Ended,
            created.AddMinutes(2), DirectCallKind.Video);
        var separate = Row(orgB, separateDm, outsiderId, peerId, DirectCallState.Missed, created);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            db.DirectCalls.AddRange(tied.Append(completedVideo).Append(separate));
            await db.SaveChangesAsync();
        }

        var ongoing = await viewer.SendAsync(HttpMethod.Post, CallStart(Personal, personalDm),
            new { kind = "Voice", clientCallId = Guid.NewGuid() }, HttpStatusCode.Created);
        var first = await viewer.GetAsync(History + "?limit=50");
        var firstItems = Items(first);
        Assert.Equal(50, firstItems.Length);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        var next = await viewer.GetAsync(History + "?filter=all&limit=50&cursor=" + Uri.EscapeDataString(cursor!));
        var secondItems = Items(next);
        Assert.Equal(5, secondItems.Length);
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);
        var fetched = firstItems.Concat(secondItems).ToArray();
        var expected = tied.Append(completedVideo).ToArray();
        Assert.Equal(expected.OrderByDescending(call => call.CreatedAt).ThenByDescending(call => call.Id).Select(call => call.Id),
            fetched.Select(Id));
        Assert.Equal(55, fetched.Select(Id).Distinct().Count());
        Assert.DoesNotContain(fetched, call => Id(call) == Id(ongoing));
        Assert.DoesNotContain(fetched, call => Id(call) == separate.Id);
        Assert.All(fetched, call =>
        {
            Assert.Equal(peerId, call.GetProperty("counterpartId").GetGuid());
            Assert.Equal(peerName, call.GetProperty("counterpartName").GetString());
            Assert.True(call.GetProperty("canCallBack").GetBoolean());
            Assert.False(call.TryGetProperty("token", out _));
            Assert.False(call.TryGetProperty("callerSessionId", out _));
            Assert.False(call.TryGetProperty("calleeSessionId", out _));
        });

        var displayed = fetched[0];
        Assert.Equal(completedVideo.Id, Id(displayed));
        Assert.Equal("video", displayed.GetProperty("kind").GetString());
        Assert.Equal("ended", displayed.GetProperty("state").GetString());
        Assert.Equal("outgoing", displayed.GetProperty("direction").GetString());
        Assert.Equal(orgA, displayed.GetProperty("organizationId").GetGuid());
        Assert.Equal(organizationDm, displayed.GetProperty("conversationId").GetGuid());
        Assert.Equal(completedVideo.CreatedAt, displayed.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(completedVideo.AcceptedAt, displayed.GetProperty("acceptedAt").GetDateTimeOffset());
        Assert.Equal(completedVideo.EndedAt, displayed.GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal(65, displayed.GetProperty("durationSeconds").GetInt32());

        var otherSession = await viewerOtherDevice.GetAsync(History + "?filter=all&limit=50");
        Assert.Equal(firstItems.Select(Id), Items(otherSession).Select(Id));
        var otherPage = await viewerOtherDevice.GetAsync(History + "?limit=50&cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Equal(secondItems.Select(Id), Items(otherPage).Select(Id));
        Assert.Equal(separate.Id, Id(Assert.Single(Items(await outsider.GetAsync(History)))));

        await AssertFilterAsync(viewer, "incoming", expected.Where(call => call.CalleeId == viewerId));
        await AssertFilterAsync(viewer, "outgoing", expected.Where(call => call.CallerId == viewerId));
        var missed = await AssertFilterAsync(viewer, "missed", expected.Where(call => call.CalleeId == viewerId && call.State == DirectCallState.Missed));
        Assert.NotEmpty(missed);
        Assert.All(missed, call =>
        {
            Assert.Equal("incoming", call.GetProperty("direction").GetString());
            Assert.Equal("missed", call.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, call.GetProperty("acceptedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, call.GetProperty("durationSeconds").ValueKind);
        });
        await viewer.GetAsync(History + "?filter=incoming&cursor=" + Uri.EscapeDataString(cursor!), HttpStatusCode.BadRequest);
        await viewer.GetAsync(History + "?filter=unknown", HttpStatusCode.BadRequest);
        await viewer.GetAsync(History + "?limit=51", HttpStatusCode.BadRequest);
        await viewer.GetAsync(History + "?cursor=not-a-cursor", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BlockKeepsReadableHistoryButCallBackAndScopeChangesRecheckAccess()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var viewer = new BrowserClient(app);
        using var peer = new BrowserClient(app);
        var viewerId = await viewer.RegisterAsync(UserName("viewer"));
        var peerName = UserName("peer");
        var peerId = await peer.RegisterAsync(peerName);
        var org = await CreateOrgAsync(peer, "Call back Organization");
        var orgApi = OrgApi(org);
        await JoinOrgAsync(peer, viewer, org);
        await AcceptContactAsync(viewer, peer, peerName);
        var personalDm = await OpenDmAsync(viewer, Personal, peerId);
        var organizationDm = await OpenDmAsync(viewer, orgApi, peerId);
        var created = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-1).AddHours(14);
        var personalCall = Row(null, personalDm, peerId, viewerId, DirectCallState.Missed, created);
        var orgCall = Row(org, organizationDm, peerId, viewerId, DirectCallState.Ended, created);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        db.DirectCalls.AddRange(personalCall, orgCall);
        await db.SaveChangesAsync();

        var before = Items(await viewer.GetAsync(History));
        Assert.Equal(2, before.Length);
        Assert.All(before, item => Assert.True(item.GetProperty("canCallBack").GetBoolean()));
        var callback = CallStart(Personal, personalDm);
        var started = await viewer.SendAsync(HttpMethod.Post, callback,
            new { kind = "Voice", clientCallId = Guid.NewGuid() }, HttpStatusCode.Created);
        Assert.Equal(viewerId, started.GetProperty("callerId").GetGuid());
        Assert.Equal(peerId, started.GetProperty("calleeId").GetGuid());
        await viewer.SendAsync(HttpMethod.Post, $"{Personal}/calls/{Id(started)}/cancel");

        await peer.SendAsync(HttpMethod.Put, $"{Personal}/people/blocks/{viewerId}", expected: HttpStatusCode.NoContent);
        var blocked = Assert.Single(Items(await viewer.GetAsync(History + "?filter=missed")));
        Assert.Equal(personalCall.Id, Id(blocked));
        Assert.Equal(peerId, blocked.GetProperty("counterpartId").GetGuid());
        Assert.False(blocked.GetProperty("canCallBack").GetBoolean());
        await viewer.SendAsync(HttpMethod.Post, callback, new { kind = "Voice", clientCallId = Guid.NewGuid() }, HttpStatusCode.NotFound);
        Assert.True(Assert.Single(Items(await viewer.GetAsync(History)), call => Id(call) == orgCall.Id)
            .GetProperty("canCallBack").GetBoolean());

        await peer.SendAsync(HttpMethod.Delete, $"{orgApi}/members/{viewerId}", expected: HttpStatusCode.NoContent);
        var remaining = Items(await viewer.GetAsync(History));
        Assert.DoesNotContain(remaining, call => Id(call) == orgCall.Id);
        Assert.Contains(remaining, call => Id(call) == personalCall.Id);
        Assert.DoesNotContain(Items(await peer.GetAsync(History)), call => Id(call) == orgCall.Id);
        await viewer.SendAsync(HttpMethod.Post, CallStart(orgApi, organizationDm), new { kind = "Voice", clientCallId = Guid.NewGuid() },
            HttpStatusCode.Forbidden);

        Assert.Equal(1, await db.Users.Where(account => account.Id == viewerId)
            .ExecuteUpdateAsync(change => change.SetProperty(account => account.Status, AccountStatus.Disabled)));
        await viewer.GetAsync(History, HttpStatusCode.Forbidden);
        Assert.Empty(Items(await peer.GetAsync(History)));
    }

    private static string UserName(string role) => $"history_{role}_{Guid.NewGuid():N}"[..24];
    private static string CallStart(string scope, Guid dm) => $"{scope}/conversations/{dm}/calls";
    private static JsonElement[] Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToArray();

    private static async Task<Guid> OpenDmAsync(BrowserClient caller, string scope, Guid peerId) =>
        Id(await caller.SendAsync(HttpMethod.Post, $"{scope}/conversations/dm", new { targetUserId = peerId }));

    private static DirectCallEntity Row(Guid? organization, Guid conversation, Guid caller, Guid callee, DirectCallState state,
        DateTimeOffset created, DirectCallKind kind = DirectCallKind.Voice) => new()
    {
        Id = Guid.NewGuid(), ClientCallId = Guid.NewGuid(), OrganizationId = organization, ConversationId = conversation,
        CallerId = caller, CalleeId = callee, State = state, Kind = kind, CreatedAt = created, ExpiresAt = created.AddSeconds(30),
        CheckedAt = created, AcceptedAt = state == DirectCallState.Ended ? created.AddSeconds(5) : null,
        EndedAt = created.AddSeconds(state == DirectCallState.Ended ? 70 : 30)
    };

    private static async Task<JsonElement[]> AssertFilterAsync(BrowserClient client, string filter, IEnumerable<DirectCallEntity> calls)
    {
        var items = Items(await client.GetAsync(History + "?limit=50&filter=" + filter));
        Assert.Equal(calls.OrderByDescending(call => call.CreatedAt).ThenByDescending(call => call.Id).Select(call => call.Id), items.Select(Id));
        if (filter is "incoming" or "outgoing")
            Assert.All(items, item => Assert.Equal(filter, item.GetProperty("direction").GetString()));
        return items;
    }
}
