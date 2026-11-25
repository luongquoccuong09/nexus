using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Calls;
using static Nexus.IntegrationTests.MeetingsM612Tests;

namespace Nexus.IntegrationTests;

public sealed class DirectCallsM613Tests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private const string PersonalApi = "/api/personal";
    private const string ActiveCalls = "/api/calls/active";

    [Fact]
    public async Task PersonalCallRingsAllDevicesAndOnlyInitiatingOrAcceptingSessionsCanJoin()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var caller = new BrowserClient(app);
        using var callerOtherDevice = new BrowserClient(app);
        using var callee = new BrowserClient(app);
        using var calleeOtherDevice = new BrowserClient(app);
        using var outsider = new BrowserClient(app);
        var callerName = Username("caller");
        var callerId = await caller.RegisterAsync(callerName);
        await callerOtherDevice.LoginAsync(callerName);
        var calleeName = Username("callee");
        var calleeId = await callee.RegisterAsync(calleeName);
        await calleeOtherDevice.LoginAsync(calleeName);
        await outsider.RegisterAsync(Username("other"));
        await AcceptContactAsync(caller, callee, calleeName);
        var dm = await OpenDmAsync(caller, PersonalApi, calleeId);

        await using var callerEvents = await CallEvents.ConnectAsync(caller);
        await using var callerSecondaryEvents = await CallEvents.ConnectAsync(callerOtherDevice);
        await using var calleeEvents = await CallEvents.ConnectAsync(callee);
        await using var calleeSecondaryEvents = await CallEvents.ConnectAsync(calleeOtherDevice);
        await using var outsiderEvents = await CallEvents.ConnectAsync(outsider);
        var participants = new[] { callerEvents, callerSecondaryEvents, calleeEvents, calleeSecondaryEvents };

        var start = $"{PersonalApi}/conversations/{dm}/calls";
        var operation = Guid.NewGuid();
        var payload = new { kind = "Video", clientCallId = operation };
        using var firstRequest = await caller.CreateRequestAsync(HttpMethod.Post, start, payload);
        using var replayRequest = await caller.CreateRequestAsync(HttpMethod.Post, start, payload);
        var starts = await Task.WhenAll(caller.Http.SendAsync(firstRequest), caller.Http.SendAsync(replayRequest));
        using var firstResponse = starts[0];
        using var secondResponse = starts[1];
        var ringing = await Assert.Single(starts, response => response.StatusCode == HttpStatusCode.Created)
            .Content.ReadFromJsonAsync<JsonElement>();
        var replay = await Assert.Single(starts, response => response.StatusCode == HttpStatusCode.OK)
            .Content.ReadFromJsonAsync<JsonElement>();
        var callId = Id(ringing);
        var route = $"{PersonalApi}/calls/{callId}";
        Assert.Equal("ringing", State(ringing));
        Assert.Equal("Video", ringing.GetProperty("kind").GetString());
        Assert.Equal(callerId, ringing.GetProperty("callerId").GetGuid());
        Assert.Equal(calleeId, ringing.GetProperty("calleeId").GetGuid());
        Assert.Null(OrganizationId(ringing));
        Assert.False(CanJoin(ringing));
        var expires = ringing.GetProperty("expiresAt").GetDateTimeOffset();
        var created = ringing.GetProperty("createdAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromSeconds(30), expires - created);
        await ObserveAsync(participants, ringing);

        Assert.Equal(callId, Id(replay));
        Assert.Equal(Version(ringing), Version(replay));
        await StartAsync(caller, start, "Voice", operation, HttpStatusCode.Conflict);
        await StartAsync(caller, start, "Video", Guid.NewGuid(), HttpStatusCode.Conflict);
        Assert.Equal(callId, Id(Assert.Single(Items(await calleeOtherDevice.GetAsync(ActiveCalls)))));
        await calleeOtherDevice.GetAsync(ActiveCalls + "?limit=51", HttpStatusCode.BadRequest);
        Assert.Empty(Items(await outsider.GetAsync(ActiveCalls)));
        await outsider.GetAsync(route, HttpStatusCode.NotFound);
        await outsider.SendAsync(HttpMethod.Post, $"{route}/accept", expected: HttpStatusCode.NotFound);
        await caller.SendAsync(HttpMethod.Post, $"{route}/accept", expected: HttpStatusCode.Forbidden);
        await callee.SendAsync(HttpMethod.Post, $"{route}/cancel", expected: HttpStatusCode.Forbidden);

        var answers = await Task.WhenAll(callee.SendAsync(HttpMethod.Post, $"{route}/accept"),
            calleeOtherDevice.SendAsync(HttpMethod.Post, $"{route}/accept"));
        Assert.All(answers, answer => Assert.Equal("accepted", State(answer)));
        var winnerIndex = Array.FindIndex(answers, CanJoin);
        var winningAnswer = Assert.Single(answers, CanJoin);
        var winningClient = winnerIndex == 0 ? callee : calleeOtherDevice;
        var losingClient = winnerIndex == 0 ? calleeOtherDevice : callee;
        Assert.Equal(Version(ringing) + 1, Version(winningAnswer));
        Assert.Equal(Version(answers[0]), Version(answers[1]));
        await ObserveAsync(participants, winningAnswer);
        Assert.True(CanJoin(await caller.GetAsync(route)));
        Assert.False(CanJoin(await callerOtherDevice.GetAsync(route)));
        Assert.True(CanJoin(await winningClient.GetAsync(route)));
        Assert.False(CanJoin(await losingClient.GetAsync(route)));

        var meetingId = winningAnswer.GetProperty("meetingId").GetGuid();
        var media = $"{PersonalApi}/meetings/{meetingId}/join";
        var callerGrant = await JoinAsync(caller, media);
        var winnerGrant = await JoinAsync(winningClient, media);
        Assert.Equal(ReadClaims(callerGrant).Room, ReadClaims(winnerGrant).Room);
        Assert.NotEqual(callerGrant.GrantId, winnerGrant.GrantId);
        await callerOtherDevice.SendAsync(HttpMethod.Post, media, expected: HttpStatusCode.NotFound);
        await losingClient.SendAsync(HttpMethod.Post, media, expected: HttpStatusCode.NotFound);
        await ValidateSignalAsync(winningClient, winnerGrant, HttpStatusCode.OK);
        await ValidateSignalAsync(losingClient, winnerGrant, HttpStatusCode.Unauthorized);
        using var activeSocket = await OpenSignalAsync(winningClient, winnerGrant, "/rtc");

        await caller.SendAsync(HttpMethod.Put, $"{PersonalApi}/people/blocks/{calleeId}", expected: HttpStatusCode.NoContent);
        await ObserveAsync(participants, callId, "ended", Version(winningAnswer) + 1);
        await ValidateSignalAsync(winningClient, winnerGrant, HttpStatusCode.Unauthorized);
        await ValidateSignalAsync(caller, callerGrant, HttpStatusCode.Unauthorized);
        await AssertSocketClosedAsync(activeSocket);
        await upstream.WaitForEndAsync(ReadClaims(winnerGrant).Room);
        await winningClient.SendAsync(HttpMethod.Post, media, expected: HttpStatusCode.NotFound);
        await StartAsync(caller, start, "Voice", Guid.NewGuid(), HttpStatusCode.NotFound);
        Assert.Empty(Items(await callee.GetAsync(ActiveCalls)));
        Assert.False(outsiderEvents.ReceivedAny);
    }

    [Fact]
    public async Task OrganizationAcceptCancelAndExpiryAreSerializedAndStartLimitSpansScopes()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var caller = new BrowserClient(app);
        using var callee = new BrowserClient(app);
        using var calleeOtherDevice = new BrowserClient(app);
        using var otherOwner = new BrowserClient(app);
        var callerName = Username("caller");
        var calleeName = Username("callee");
        var callerId = await caller.RegisterAsync(callerName);
        var calleeId = await callee.RegisterAsync(calleeName);
        await calleeOtherDevice.LoginAsync(calleeName);
        await otherOwner.RegisterAsync(Username("other"));
        var orgA = await CreateOrgAsync(caller, "Direct call Organization A");
        var orgB = await CreateOrgAsync(otherOwner, "Direct call Organization B");
        await JoinOrgAsync(caller, callee, orgA);
        var api = OrgApi(orgA);
        var dm = await OpenDmAsync(caller, api, calleeId);
        var personalDm = await OpenDmAsync(caller, PersonalApi, calleeId);
        var start = $"{api}/conversations/{dm}/calls";
        var personalStart = $"{PersonalApi}/conversations/{personalDm}/calls";

        await using var callerEvents = await CallEvents.ConnectAsync(caller);
        await using var calleeEvents = await CallEvents.ConnectAsync(callee, orgA);
        await using var otherDeviceEvents = await CallEvents.ConnectAsync(calleeOtherDevice);
        await using var outsiderEvents = await CallEvents.ConnectAsync(otherOwner);
        var participants = new[] { callerEvents, calleeEvents, otherDeviceEvents };

        var running = await StartAsync(caller, start, "Voice", Guid.NewGuid());
        var callId = Id(running);
        var route = $"{api}/calls/{callId}";
        Assert.Equal(orgA, OrganizationId(running));
        await ObserveAsync(participants, running);
        Assert.Equal(callId, Id(Assert.Single(Items(await calleeOtherDevice.GetAsync(ActiveCalls)))));
        Assert.Empty(Items(await otherOwner.GetAsync(ActiveCalls)));
        await otherOwner.GetAsync(route, HttpStatusCode.Forbidden);
        await otherOwner.GetAsync($"{OrgApi(orgB)}/calls/{callId}", HttpStatusCode.NotFound);
        await callee.GetAsync($"{PersonalApi}/calls/{callId}", HttpStatusCode.NotFound);

        using var acceptRequest = await callee.CreateRequestAsync(HttpMethod.Post, $"{route}/accept");
        using var cancelRequest = await caller.CreateRequestAsync(HttpMethod.Post, $"{route}/cancel");
        var competing = await Task.WhenAll(callee.Http.SendAsync(acceptRequest), caller.Http.SendAsync(cancelRequest));
        string winningState;
        try
        {
            Assert.Single(competing, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(competing, response => response.StatusCode == HttpStatusCode.Conflict);
            winningState = competing[0].StatusCode == HttpStatusCode.OK ? "accepted" : "canceled";
        }
        finally
        {
            foreach (var response in competing) response.Dispose();
        }

        var settled = await callee.GetAsync(route);
        Assert.Equal(winningState, State(settled));
        Assert.Equal(Version(running) + 1, Version(settled));
        await ObserveAsync(participants, settled);
        if (winningState == "accepted")
        {
            var ended = await calleeOtherDevice.SendAsync(HttpMethod.Post, $"{route}/end");
            Assert.Equal("ended", State(ended));
            await ObserveAsync(participants, ended);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, settled.GetProperty("meetingId").ValueKind);
        }

        var declinedStart = await StartAsync(caller, start, "Voice", Guid.NewGuid());
        await ObserveAsync(participants, declinedStart);
        var declined = await callee.SendAsync(HttpMethod.Post, $"{api}/calls/{Id(declinedStart)}/decline");
        Assert.Equal("declined", State(declined));
        await ObserveAsync(participants, declined);

        var canceledStart = await StartAsync(caller, start, "Voice", Guid.NewGuid());
        await ObserveAsync(participants, canceledStart);
        var canceled = await caller.SendAsync(HttpMethod.Post, $"{api}/calls/{Id(canceledStart)}/cancel");
        Assert.Equal("canceled", State(canceled));
        await ObserveAsync(participants, canceled);

        var missedKey = Guid.NewGuid();
        var ringing = await StartAsync(caller, start, "Voice", missedKey);
        await ObserveAsync(participants, ringing);
        var missedId = Id(ringing);
        var missingRoute = $"{api}/calls/{missedId}";
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await db.DirectCalls.Where(call => call.Id == missedId).ExecuteUpdateAsync(change => change
            .SetProperty(call => call.CreatedAt, now.AddSeconds(-31))
            .SetProperty(call => call.ExpiresAt, now.AddSeconds(-1))));
        await Task.WhenAll(callee.SendAsync(HttpMethod.Post, $"{missingRoute}/accept", expected: HttpStatusCode.Conflict),
            calleeOtherDevice.SendAsync(HttpMethod.Post, $"{missingRoute}/decline", expected: HttpStatusCode.Conflict));
        await ObserveAsync(participants, missedId, "missed", Version(ringing) + 1);
        var missed = await callee.GetAsync(missingRoute);
        Assert.Equal("missed", State(missed));
        Assert.Equal(Version(ringing) + 1, Version(missed));
        Assert.Equal(JsonValueKind.Null, missed.GetProperty("meetingId").ValueKind);
        Assert.Equal(missedId, Id(await StartAsync(caller, start, "Voice", missedKey, HttpStatusCode.OK)));
        var persisted = await db.DirectCalls.AsNoTracking().Where(call => call.CallerId == callerId && call.ClientCallId == missedKey)
            .Select(call => new { call.Id, call.State }).ToListAsync();
        var record = Assert.Single(persisted);
        Assert.Equal(missedId, record.Id);
        Assert.Equal(DirectCallState.Missed, record.State);
        Assert.Empty(Items(await callee.GetAsync(ActiveCalls)));

        var fifth = await StartAsync(caller, personalStart, "Video", Guid.NewGuid());
        await caller.SendAsync(HttpMethod.Post, $"{PersonalApi}/calls/{Id(fifth)}/cancel");
        using var request = await caller.CreateRequestAsync(HttpMethod.Post, start, new { kind = "Voice", clientCallId = Guid.NewGuid() });
        using var throttled = await caller.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
        Assert.False(outsiderEvents.ReceivedAny);
    }

    [Fact]
    public async Task DisablingAccountDeniesFreshAndExistingCallMediaCredentials()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var caller = new BrowserClient(app);
        using var callee = new BrowserClient(app);
        await caller.RegisterAsync(Username("caller"));
        var calleeName = Username("callee");
        var calleeId = await callee.RegisterAsync(calleeName);
        await AcceptContactAsync(caller, callee, calleeName);
        var dm = await OpenDmAsync(caller, PersonalApi, calleeId);
        var started = await StartAsync(caller, $"{PersonalApi}/conversations/{dm}/calls", "Voice", Guid.NewGuid());
        var route = $"{PersonalApi}/calls/{Id(started)}";
        var accepted = await callee.SendAsync(HttpMethod.Post, $"{route}/accept");
        var media = $"{PersonalApi}/meetings/{accepted.GetProperty("meetingId").GetGuid()}/join";
        var grant = await JoinAsync(callee, media);
        using var activeSocket = await OpenSignalAsync(callee, grant, "/rtc");
        using var nextGrant = await callee.CreateRequestAsync(HttpMethod.Post, media);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        Assert.Equal(1, await db.Users.Where(user => user.Id == calleeId)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.Status, AccountStatus.Disabled)));

        await ValidateSignalAsync(callee, grant, HttpStatusCode.Forbidden);
        using var replay = await callee.Http.SendAsync(nextGrant);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        await AssertSocketClosedAsync(activeSocket);
    }

    private static string Username(string role) => $"call_{role}_{Guid.NewGuid():N}"[..24];
    private static string? State(JsonElement item) => item.GetProperty("state").GetString();
    private static int Version(JsonElement item) => item.GetProperty("version").GetInt32();
    private static bool CanJoin(JsonElement item) => item.GetProperty("canJoinMedia").GetBoolean();
    private static Guid? OrganizationId(JsonElement item) => item.GetProperty("organizationId").ValueKind == JsonValueKind.Null
        ? null : item.GetProperty("organizationId").GetGuid();
    private static JsonElement[] Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToArray();

    private static async Task<Guid> OpenDmAsync(BrowserClient caller, string scope, Guid callee) =>
        Id(await caller.SendAsync(HttpMethod.Post, $"{scope}/conversations/dm", new { targetUserId = callee }));

    private static Task<JsonElement> StartAsync(BrowserClient caller, string path, string kind, Guid clientCallId,
        HttpStatusCode expected = HttpStatusCode.Created) => caller.SendAsync(HttpMethod.Post, path, new { kind, clientCallId }, expected);

    private static Task ObserveAsync(CallEvents[] devices, JsonElement call) =>
        ObserveAsync(devices, Id(call), State(call)!, Version(call));

    private static Task ObserveAsync(CallEvents[] devices, Guid callId, string state, int version) => Task.WhenAll(
        devices.Select(device => device.WaitAsync(callId, state, version)));

    private sealed class CallEvents(HubConnection hub, Channel<JsonElement> events) : IAsyncDisposable
    {
        public bool ReceivedAny => events.Reader.TryPeek(out _);

        public static async Task<CallEvents> ConnectAsync(BrowserClient client, Guid? org = null)
        {
            var path = org is { } id ? $"/hubs/organizations/{id}/messaging" : "/hubs/personal/messaging";
            var hub = new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress, path), options =>
            {
                options.Cookies = client.Cookies;
                options.Headers["Origin"] = client.BaseAddress.GetLeftPart(UriPartial.Authority);
            }).Build();
            var events = Channel.CreateUnbounded<JsonElement>();
            hub.On<JsonElement>("DirectCallChanged", call => events.Writer.TryWrite(call));
            await hub.StartAsync();
            return new CallEvents(hub, events);
        }

        public async Task WaitAsync(Guid id, string state, int version)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var call = await events.Reader.ReadAsync(timeout.Token);
                if (Id(call) != id || State(call) != state) continue;
                Assert.Equal(version, Version(call));
                Assert.False(call.TryGetProperty("token", out _));
                Assert.False(call.TryGetProperty("canJoinMedia", out _));
                return;
            }
        }

        public ValueTask DisposeAsync() => hub.DisposeAsync();
    }
}
