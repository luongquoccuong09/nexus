using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;

namespace Nexus.IntegrationTests;

public sealed class MeetingsM612Tests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private const string PersonalApi = "/api/personal";
    private const string Password = "Meeting-fixture-password-2026!";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CalendarRoomChecksForwardedLinksSessionBoundGrantsAndHostControls()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(upstream.BaseAddress);
        using var organizer = new BrowserClient(app);
        using var attendee = new BrowserClient(app);
        using var attendeeOtherDevice = new BrowserClient(app);
        using var observer = new BrowserClient(app);
        using var outsider = new BrowserClient(app);
        using var anonymous = new BrowserClient(app);
        await organizer.RegisterAsync(Username("owner"));
        var attendeeName = Username("attendee");
        var attendeeId = await attendee.RegisterAsync(attendeeName);
        await attendeeOtherDevice.LoginAsync(attendeeName);
        await observer.RegisterAsync(Username("observer"));
        await outsider.RegisterAsync(Username("other"));

        var calendarOrg = await CreateOrgAsync(organizer, "Meeting Organization A");
        var otherOrg = await CreateOrgAsync(outsider, "Meeting Organization B");
        await JoinOrgAsync(organizer, attendee, calendarOrg);
        await JoinOrgAsync(organizer, observer, calendarOrg);
        var api = OrgApi(calendarOrg);
        var date = DateTimeOffset.UtcNow.AddDays(2);
        var eventId = await CreateOnlineEventAsync(organizer, api, date, [attendeeId]);
        var scheduledJoin = $"{api}/calendar/events/{eventId}/meeting/join";
        var hostGrant = await JoinAsync(organizer, scheduledJoin);
        var attendeeGrant = await JoinAsync(attendee, scheduledJoin);
        var repeat = await JoinAsync(attendee, scheduledJoin);
        var secondGrant = await JoinAsync(attendeeOtherDevice, scheduledJoin);
        Assert.Equal(calendarOrg, hostGrant.OrganizationId);
        Assert.True(hostGrant.CanManage);
        Assert.False(attendeeGrant.CanManage);
        Assert.Equal(hostGrant.MeetingId, attendeeGrant.MeetingId);
        Assert.Equal(hostGrant.MeetingId, secondGrant.MeetingId);
        Assert.Equal(attendeeGrant.GrantId, repeat.GrantId);
        Assert.NotEqual(attendeeGrant.GrantId, secondGrant.GrantId);
        Assert.NotEqual(hostGrant.GrantId, attendeeGrant.GrantId);

        var hostClaims = ReadClaims(hostGrant);
        Assert.Equal(hostGrant.GrantId, hostClaims.Subject);
        Assert.Equal(hostClaims.Room, ReadClaims(attendeeGrant).Room);
        Assert.Equal(secondGrant.GrantId, ReadClaims(secondGrant).Subject);
        Assert.InRange(hostClaims.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(1), DateTimeOffset.UtcNow.AddMinutes(5));

        var meeting = $"{api}/meetings/{hostGrant.MeetingId}";
        await observer.SendAsync(HttpMethod.Post, scheduledJoin, expected: HttpStatusCode.NotFound);
        await observer.SendAsync(HttpMethod.Post, $"{meeting}/join", expected: HttpStatusCode.NotFound);
        await outsider.SendAsync(HttpMethod.Post, $"{meeting}/join", expected: HttpStatusCode.Forbidden);
        await outsider.SendAsync(HttpMethod.Post, $"{OrgApi(otherOrg)}/meetings/{hostGrant.MeetingId}/join", expected: HttpStatusCode.NotFound);

        Assert.Equal(0, upstream.SignalRequests);
        await ValidateSignalAsync(anonymous, attendeeGrant, HttpStatusCode.Unauthorized);
        await ValidateSignalAsync(attendeeOtherDevice, attendeeGrant, HttpStatusCode.Unauthorized);
        await ValidateSignalAsync(observer, attendeeGrant, HttpStatusCode.Unauthorized);
        await ValidateSignalAsync(attendee, attendeeGrant, HttpStatusCode.Forbidden, "https://other.example");
        await ValidateSignalAsync(attendee, attendeeGrant, HttpStatusCode.OK);
        await ValidateSignalAsync(attendeeOtherDevice, secondGrant, HttpStatusCode.OK);
        Assert.Equal(0, upstream.SignalRequests);
        using var liveAttendee = await OpenSignalAsync(attendee, attendeeGrant, "/rtc");
        using var liveSecondDevice = await OpenSignalAsync(attendeeOtherDevice, secondGrant, "/rtc/v1");
        Assert.Equal(2, upstream.SignalRequests);

        await attendee.SendAsync(HttpMethod.Post, $"{meeting}/end", expected: HttpStatusCode.Forbidden);
        await attendee.SendAsync(HttpMethod.Delete, $"{meeting}/participants/{hostGrant.GrantId}", expected: HttpStatusCode.Forbidden);
        Assert.Empty(upstream.Removed);
        Assert.Empty(upstream.Ended);

        await organizer.SendAsync(HttpMethod.Delete, $"{meeting}/participants/{attendeeGrant.GrantId}", expected: HttpStatusCode.NoContent);
        await upstream.WaitForRemovedAsync(hostClaims.Room, attendeeGrant.GrantId);
        await upstream.WaitForRemovedAsync(hostClaims.Room, secondGrant.GrantId);
        await AssertSocketClosedAsync(liveAttendee);
        await AssertSocketClosedAsync(liveSecondDevice);
        var signalRequestsAfterKick = upstream.SignalRequests;
        await ValidateSignalAsync(attendee, attendeeGrant, HttpStatusCode.Unauthorized);
        await ValidateSignalAsync(attendeeOtherDevice, secondGrant, HttpStatusCode.Unauthorized);
        await RejectSocketAsync(attendee, attendeeGrant);
        Assert.Equal(signalRequestsAfterKick, upstream.SignalRequests);
        await attendee.SendAsync(HttpMethod.Post, $"{meeting}/join", expected: HttpStatusCode.NotFound);
        await attendeeOtherDevice.SendAsync(HttpMethod.Post, scheduledJoin, expected: HttpStatusCode.NotFound);

        await organizer.SendAsync(HttpMethod.Post, $"{meeting}/end", expected: HttpStatusCode.NoContent);
        await upstream.WaitForEndAsync(hostClaims.Room);
        await ValidateSignalAsync(organizer, hostGrant, HttpStatusCode.Unauthorized);
        await organizer.SendAsync(HttpMethod.Post, scheduledJoin, expected: HttpStatusCode.NotFound);

        var canceledId = await CreateOnlineEventAsync(organizer, api, date.AddDays(1), []);
        var canceledEvent = $"{api}/calendar/events/{canceledId}";
        var canceledGrant = await JoinAsync(organizer, $"{canceledEvent}/meeting/join");
        var version = (await organizer.GetAsync(canceledEvent)).GetProperty("version").GetInt32();
        await organizer.SendAsync(HttpMethod.Post, $"{canceledEvent}/cancel", new { version });
        await upstream.WaitForEndAsync(ReadClaims(canceledGrant).Room);
        await ValidateSignalAsync(organizer, canceledGrant, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PersonalAndCommunityMeetNowFollowLiveConversationMembership()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(upstream.BaseAddress);
        using var organizer = new BrowserClient(app);
        using var attendee = new BrowserClient(app);
        using var orgObserver = new BrowserClient(app);
        await organizer.RegisterAsync(Username("owner"));
        var attendeeName = Username("attendee");
        var attendeeId = await attendee.RegisterAsync(attendeeName);
        await orgObserver.RegisterAsync(Username("observer"));
        var org = await CreateOrgAsync(organizer, "Community meetings");
        await JoinOrgAsync(organizer, attendee, org);
        await JoinOrgAsync(organizer, orgObserver, org);
        await AcceptContactAsync(organizer, attendee, attendeeName);

        var dm = Id(await organizer.SendAsync(HttpMethod.Post, $"{PersonalApi}/conversations/dm", new { targetUserId = attendeeId }));
        var personalStart = $"{PersonalApi}/conversations/{dm}/meetings";
        await attendee.GetAsync($"{personalStart}/current", HttpStatusCode.NoContent);
        var personal = MeetingId(await organizer.SendAsync(HttpMethod.Post, personalStart));
        Assert.Equal(personal, MeetingId(await organizer.SendAsync(HttpMethod.Post, personalStart)));
        Assert.Equal(personal, MeetingId(await attendee.GetAsync($"{personalStart}/current")));
        await orgObserver.GetAsync($"{personalStart}/current", HttpStatusCode.NotFound);
        var personalRoom = $"{PersonalApi}/meetings/{personal}";
        var personalGrant = await JoinAsync(attendee, $"{personalRoom}/join");
        Assert.Null(personalGrant.OrganizationId);
        await ValidateSignalAsync(attendee, personalGrant, HttpStatusCode.OK);
        await attendee.SendAsync(HttpMethod.Post, $"{personalRoom}/leave", new { grantId = personalGrant.GrantId }, HttpStatusCode.NoContent);
        await upstream.WaitForRemovedAsync(ReadClaims(personalGrant).Room, personalGrant.GrantId);
        await ValidateSignalAsync(attendee, personalGrant, HttpStatusCode.Unauthorized);
        var rejoined = await JoinAsync(attendee, $"{personalRoom}/join");
        Assert.NotEqual(personalGrant.GrantId, rejoined.GrantId);
        await ValidateSignalAsync(attendee, rejoined, HttpStatusCode.OK);

        var api = OrgApi(org);
        var community = await organizer.SendAsync(HttpMethod.Post, $"{api}/communities",
            new { name = "Meeting Community", description = "Voice and video", visibility = "Public" }, HttpStatusCode.Created, NewKey());
        var communityId = Id(community);
        var conversationId = community.GetProperty("mainFeedConversationId").GetGuid();
        var communityStart = $"{api}/conversations/{conversationId}/meetings";
        await attendee.SendAsync(HttpMethod.Post, $"{api}/communities/{communityId}/join", new { });
        await orgObserver.SendAsync(HttpMethod.Post, communityStart, expected: HttpStatusCode.NotFound);
        var communityMeeting = MeetingId(await organizer.SendAsync(HttpMethod.Post, communityStart));
        await orgObserver.SendAsync(HttpMethod.Post, $"{api}/meetings/{communityMeeting}/join", expected: HttpStatusCode.NotFound);
        Assert.Equal(communityMeeting, MeetingId(await attendee.GetAsync($"{communityStart}/current")));
        var communityGrant = await JoinAsync(attendee, $"{api}/meetings/{communityMeeting}/join");
        Assert.Equal(org, communityGrant.OrganizationId);
        Assert.NotEqual(ReadClaims(rejoined).Room, ReadClaims(communityGrant).Room);
        await ValidateSignalAsync(attendee, communityGrant, HttpStatusCode.OK);

        await organizer.SendAsync(HttpMethod.Delete, $"{api}/communities/{communityId}/members/{attendeeId}", expected: HttpStatusCode.NoContent);
        await ValidateSignalAsync(attendee, communityGrant, HttpStatusCode.Unauthorized);
        await upstream.WaitForRemovedAsync(ReadClaims(communityGrant).Room, communityGrant.GrantId);
        await attendee.SendAsync(HttpMethod.Post, $"{api}/meetings/{communityMeeting}/join", expected: HttpStatusCode.NotFound);
        await ValidateSignalAsync(attendee, rejoined, HttpStatusCode.OK);

        await organizer.SendAsync(HttpMethod.Put, $"{PersonalApi}/people/blocks/{attendeeId}", expected: HttpStatusCode.NoContent);
        await ValidateSignalAsync(attendee, rejoined, HttpStatusCode.Unauthorized);
        await upstream.WaitForRemovedAsync(ReadClaims(rejoined).Room, rejoined.GrantId);
        await attendee.SendAsync(HttpMethod.Post, $"{personalRoom}/join", expected: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OrganizationRevocationAndDisabledAccountsLoseExistingCalendarGrants()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(upstream.BaseAddress);
        using var organizer = new BrowserClient(app);
        using var former = new BrowserClient(app);
        using var disabled = new BrowserClient(app);
        await organizer.RegisterAsync(Username("owner"));
        var formerId = await former.RegisterAsync(Username("former"));
        var disabledId = await disabled.RegisterAsync(Username("disabled"));
        var org = await CreateOrgAsync(organizer, "Revoked calendar access");
        await JoinOrgAsync(organizer, former, org);
        await JoinOrgAsync(organizer, disabled, org);
        var api = OrgApi(org);
        var eventId = await CreateOnlineEventAsync(organizer, api, DateTimeOffset.UtcNow.AddDays(3), [formerId, disabledId]);
        var join = $"{api}/calendar/events/{eventId}/meeting/join";
        var hostGrant = await JoinAsync(organizer, join);
        var formerGrant = await JoinAsync(former, join);
        var disabledGrant = await JoinAsync(disabled, join);
        await ValidateSignalAsync(former, formerGrant, HttpStatusCode.OK);
        await ValidateSignalAsync(disabled, disabledGrant, HttpStatusCode.OK);
        var room = ReadClaims(hostGrant).Room;

        await organizer.SendAsync(HttpMethod.Delete, $"{api}/members/{formerId}", expected: HttpStatusCode.NoContent);
        await ValidateSignalAsync(former, formerGrant, HttpStatusCode.Unauthorized);
        await upstream.WaitForRemovedAsync(room, formerGrant.GrantId);
        await former.SendAsync(HttpMethod.Post, join, expected: HttpStatusCode.Forbidden);

        // Change the global identity state while the BFF session and media grant still exist.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.Equal(1, await db.Users.Where(user => user.Id == disabledId)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.Status, AccountStatus.Disabled)));
        }

        await ValidateSignalAsync(disabled, disabledGrant, HttpStatusCode.Forbidden);
        await upstream.WaitForRemovedAsync(room, disabledGrant.GrantId);
        await ValidateSignalAsync(organizer, hostGrant, HttpStatusCode.OK);
    }

    private async Task<WebApplication> StartHostAsync(Uri providerAddress)
    {
        var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--environment", "Development", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=300",
            $"--ConnectionStrings:Runtime={postgres.RuntimeConnectionString}",
            "--NexusMeetings:LiveKit:Enabled=true", $"--NexusMeetings:LiveKit:ServerUrl={providerAddress}",
            "--NexusMeetings:LiveKit:ApiKey=nexus_meeting_integration", $"--NexusMeetings:LiveKit:ApiSecret={NewKey()}{NewKey()}"
        ]);
        await app.StartAsync();
        return app;
    }

    private static Uri Address(WebApplication app) => new(Assert.Single(
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));

    private static string OrgApi(Guid organizationId) => $"/api/organizations/{organizationId}";
    private static string NewKey() => Guid.NewGuid().ToString("N");
    private static string Username(string role) => $"meet_{role}_{NewKey()[..8]}";
    private static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();
    private static Guid MeetingId(JsonElement element) => element.GetProperty("meetingId").GetGuid();

    private static async Task<Guid> CreateOrgAsync(BrowserClient client, string name) =>
        Id(await client.SendAsync(HttpMethod.Post, "/api/organizations", new { name }, HttpStatusCode.Created));

    private static async Task JoinOrgAsync(BrowserClient owner, BrowserClient joiner, Guid org)
    {
        var invitation = await owner.SendAsync(HttpMethod.Post, $"{OrgApi(org)}/invitations", new { });
        await joiner.SendAsync(HttpMethod.Post, "/api/organizations/invitations/redeem", new { token = invitation.GetProperty("token").GetString() });
    }

    private static async Task AcceptContactAsync(BrowserClient sender, BrowserClient recipient, string username)
    {
        const string requests = $"{PersonalApi}/contacts/requests";
        await sender.SendAsync(HttpMethod.Post, requests, new { username }, HttpStatusCode.Accepted);
        var pending = Assert.Single((await recipient.GetAsync(requests)).GetProperty("items").EnumerateArray());
        await recipient.SendAsync(HttpMethod.Put, $"{requests}/{Id(pending)}", new { accept = true }, HttpStatusCode.NoContent);
    }

    private static async Task<Guid> CreateOnlineEventAsync(BrowserClient organizer, string api, DateTimeOffset date, Guid[] invitees) =>
        Id(await organizer.SendAsync(HttpMethod.Post, $"{api}/calendar/events", new
        {
            title = "Review meeting", startUtc = date, endUtc = date.AddHours(1), timeZone = "UTC", attendeeIds = invitees, isOnlineMeeting = true
        }, HttpStatusCode.Created, NewKey()));

    private static async Task<Grant> JoinAsync(BrowserClient client, string path) =>
        (await client.SendAsync(HttpMethod.Post, path, requireNoStore: true)).Deserialize<Grant>(JsonOptions)!;

    private static TokenClaims ReadClaims(Grant grant)
    {
        var encoded = grant.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
        var root = claims.RootElement;
        var video = root.GetProperty("video");
        Assert.True(video.GetProperty("roomJoin").GetBoolean());
        Assert.True(video.GetProperty("canPublish").GetBoolean());
        Assert.True(video.GetProperty("canSubscribe").GetBoolean());
        Assert.False(video.TryGetProperty("roomAdmin", out _));
        return new(Guid.Parse(root.GetProperty("sub").GetString()!), video.GetProperty("room").GetString()!,
            DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64()));
    }

    private static async Task ValidateSignalAsync(BrowserClient client, Grant grant, HttpStatusCode expected, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SignalUri(client.BaseAddress, grant, "/rtc/validate"));
        request.Headers.Add("Origin", origin ?? client.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var response = await client.Http.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<ClientWebSocket> OpenSignalAsync(BrowserClient client, Grant grant, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var socket = Socket(client);
        try
        {
            await socket.ConnectAsync(SocketUri(client, grant, path), timeout.Token);
            var buffer = new byte[32];
            var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            Assert.Equal("ready", Encoding.UTF8.GetString(buffer.AsSpan(0, received.Count)));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task RejectSocketAsync(BrowserClient client, Grant grant)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var socket = Socket(client);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(SocketUri(client, grant, "/rtc"), timeout.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
    }

    private static async Task AssertSocketClosedAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var buffer = new byte[32];
            var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }
        catch (WebSocketException) when (socket.State == WebSocketState.Aborted)
        {
        }
    }

    private static ClientWebSocket Socket(BrowserClient client)
    {
        var socket = new ClientWebSocket();
        socket.Options.Cookies = client.Cookies;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", client.BaseAddress.GetLeftPart(UriPartial.Authority));
        return socket;
    }

    private static Uri SocketUri(BrowserClient client, Grant grant, string path) =>
        new UriBuilder(SignalUri(client.BaseAddress, grant, path)) { Scheme = "ws" }.Uri;

    private static Uri SignalUri(Uri address, Grant grant, string suffix) =>
        new(address, $"{grant.SignalPath.TrimEnd('/')}{suffix}?access_token={Uri.EscapeDataString(grant.Token)}");

    private sealed record Grant(Guid MeetingId, Guid? OrganizationId, Guid GrantId, string SignalPath, string Token, bool CanManage);
    private sealed record TokenClaims(Guid Subject, string Room, DateTimeOffset ExpiresAt);

    private sealed class BrowserClient(WebApplication app) : IDisposable
    {
        public Uri BaseAddress { get; } = Address(app);
        public CookieContainer Cookies { get; } = new();
        private HttpClient? _http;
        public HttpClient Http => _http ??= new HttpClient(new HttpClientHandler
        {
            CookieContainer = Cookies, AllowAutoRedirect = false, CheckCertificateRevocationList = true
        }) { BaseAddress = BaseAddress };

        public async Task<Guid> RegisterAsync(string username)
        {
            using var response = await Http.PostAsJsonAsync("/api/auth/register", new
            {
                username, password = Password, displayName = username, termsAccepted = true
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return Id(await response.Content.ReadFromJsonAsync<JsonElement>());
        }

        public async Task LoginAsync(string username)
        {
            using var response = await Http.PostAsJsonAsync("/api/auth/login", new { username, password = Password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<JsonElement> GetAsync(string path, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await Http.GetAsync(path);
            Assert.Equal(expected, response.StatusCode);
            return await ReadJsonAsync(response);
        }

        public async Task<JsonElement> SendAsync(HttpMethod verb, string path, object? body = null,
            HttpStatusCode expected = HttpStatusCode.OK, string? key = null, bool requireNoStore = false)
        {
            var csrf = (await GetAsync("/api/auth/csrf")).GetProperty("token").GetString();
            using var request = new HttpRequestMessage(verb, path) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            using var response = await Http.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            if (requireNoStore) Assert.True(response.Headers.CacheControl?.NoStore);
            return await ReadJsonAsync(response);
        }

        private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
            response.Content.Headers.ContentType?.MediaType == "application/json"
                ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;

        public void Dispose() => _http?.Dispose();
    }

    private sealed class ProviderFixture(WebApplication app) : IAsyncDisposable
    {
        public Uri BaseAddress => Address(app);
        public ConcurrentQueue<(string Room, Guid Subject)> Removed { get; } = new();
        public ConcurrentQueue<string> Ended { get; } = new();
        private int _signalRequests;
        public int SignalRequests => Volatile.Read(ref _signalRequests);

        public static async Task<ProviderFixture> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fixture = new ProviderFixture(app);
            app.UseWebSockets();
            app.MapGet("/rtc", fixture.ConnectAsync);
            app.MapGet("/rtc/v1", fixture.ConnectAsync);
            app.MapPost("/twirp/livekit.RoomService/RemoveParticipant", (JsonElement request) =>
            {
                fixture.Removed.Enqueue((request.GetProperty("room").GetString()!,
                    Guid.Parse(request.GetProperty("identity").GetString()!)));
                return Results.Ok(new { });
            });
            app.MapPost("/twirp/livekit.RoomService/DeleteRoom", (JsonElement request) =>
            {
                fixture.Ended.Enqueue(request.GetProperty("room").GetString()!);
                return Results.Ok(new { });
            });
            await app.StartAsync();
            return fixture;
        }

        private async Task ConnectAsync(HttpContext context)
        {
            if (!context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ||
                context.Request.Query.ContainsKey("access_token"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            Interlocked.Increment(ref _signalRequests);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync("ready"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, context.RequestAborted);
            var buffer = new byte[32];
            try
            {
                await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (WebSocketException) when (socket.State == WebSocketState.Aborted)
            {
            }
        }

        public Task WaitForRemovedAsync(string room, Guid subject) => WaitAsync(() => Removed.Contains((room, subject)));
        public Task WaitForEndAsync(string room) => WaitAsync(() => Ended.Contains(room));

        private static async Task WaitAsync(Func<bool> observed)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!observed()) await Task.Delay(50, timeout.Token);
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
