using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Identity;

namespace Nexus.IntegrationTests;

public sealed class CalendarM611Tests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private const string PersonalEvents = "/api/personal/calendar/events";
    private static readonly DateTimeOffset CalendarStart = new(2027, 3, 14, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task PersonalOrganizationAndCommunityEventsFollowLiveAccess()
    {
        await using var app = await StartHostAsync();
        using var owner = Client(app);
        using var invitee = Client(app);
        using var observer = Client(app);
        using var otherOwner = Client(app);
        var ownerId = await RegisterAsync(owner, Username("owner"));
        var inviteeId = await RegisterAsync(invitee, Username("invitee"));
        var observerId = await RegisterAsync(observer, Username("observer"));
        var otherName = Username("other");
        var otherId = await RegisterAsync(otherOwner, otherName);
        var orgA = await CreateOrganizationAsync(owner, "Calendar Organization A");
        var orgB = await CreateOrganizationAsync(otherOwner, "Calendar Organization B");
        await JoinOrganizationAsync(owner, invitee, orgA);
        await JoinOrganizationAsync(owner, observer, orgA);
        var orgAEvents = OrgEvents(orgA);
        var orgBEvents = OrgEvents(orgB);
        var communityId = await CreateCommunityAsync(owner, orgA);
        await JoinCommunityAsync(invitee, orgA, communityId);

        var personalRequest = Event("Personal planning", attendees: [otherId]);
        Assert.Equal("calendar_ineligible_attendees", (await SendAsync(owner, HttpMethod.Post, PersonalEvents,
            personalRequest, HttpStatusCode.Forbidden, NewKey())).GetProperty("code").GetString());
        await AcceptContactAsync(owner, otherOwner, otherName);
        await SendAsync(owner, HttpMethod.Post, "/api/personal/conversations/dm", new { targetUserId = otherId });
        var personal = await CreateEventAsync(owner, PersonalEvents, personalRequest);
        var personalId = Id(personal);
        Assert.Equal(ownerId, personal.GetProperty("organizerId").GetGuid());
        Assert.Equal(JsonValueKind.Null, personal.GetProperty("organizationId").ValueKind);
        Assert.Equal(personalId, Id(await GetAsync(otherOwner, $"{PersonalEvents}/{personalId}")));
        await GetAsync(invitee, $"{PersonalEvents}/{personalId}", HttpStatusCode.NotFound);

        await SendAsync(owner, HttpMethod.Post, orgAEvents, Event("Ineligible Organization invitation", attendees: [otherId]),
            HttpStatusCode.Forbidden, NewKey());
        var organization = await CreateEventAsync(owner, orgAEvents, Event("Organization planning", attendees: [inviteeId]));
        var organizationId = Id(organization);
        var otherEvent = await CreateEventAsync(otherOwner, orgBEvents, Event("Other Organization planning"));

        await SendAsync(owner, HttpMethod.Post, orgAEvents,
            Event("Ineligible Community invitation", communityId, [observerId]), HttpStatusCode.Forbidden, NewKey());
        var community = await CreateEventAsync(owner, orgAEvents, Event("Community planning", communityId, [inviteeId]));
        var communityEventId = Id(community);
        var communityPath = $"{orgAEvents}/{communityEventId}";
        var rosterPath = $"{communityPath}/attendees";

        Assert.Equal(orgA, community.GetProperty("organizationId").GetGuid());
        Assert.Equal(communityId, community.GetProperty("communityId").GetGuid());
        Assert.Equal(2, Items(await GetAsync(invitee, Range(orgAEvents))).Length);
        Assert.Equal(communityEventId, Id(Assert.Single(Items(await GetAsync(invitee,
            Range(orgAEvents) + $"&communityId={communityId}")))));
        Assert.Empty(Items(await GetAsync(observer, Range(orgAEvents))));
        Assert.Equal(Id(otherEvent), Id(Assert.Single(Items(await GetAsync(otherOwner, Range(orgBEvents))))));
        await GetAsync(otherOwner, $"{orgAEvents}/{organizationId}", HttpStatusCode.Forbidden);
        await GetAsync(otherOwner, $"{orgBEvents}/{organizationId}", HttpStatusCode.NotFound);
        await GetAsync(owner, $"{orgAEvents}/{personalId}", HttpStatusCode.NotFound);
        await GetAsync(observer, rosterPath, HttpStatusCode.NotFound);

        await JoinCommunityAsync(observer, orgA, communityId);
        var observerEvent = await GetAsync(observer, communityPath);
        Assert.False(observerEvent.GetProperty("canRsvp").GetBoolean());
        await SendAsync(observer, HttpMethod.Put, $"{communityPath}/rsvp", new { status = "Going" }, HttpStatusCode.Forbidden);

        foreach (var status in new[] { "Going", "Tentative", "Declined" })
        {
            var response = await SendAsync(invitee, HttpMethod.Put, $"{communityPath}/rsvp", new { status });
            Assert.Equal(status, response.GetProperty("myRsvp").GetString());
            Assert.False(response.GetProperty("canEdit").GetBoolean());
        }

        var attendee = Assert.Single(Items(await GetAsync(observer, rosterPath)));
        Assert.Equal(inviteeId, attendee.GetProperty("userId").GetGuid());
        Assert.Equal("Declined", attendee.GetProperty("status").GetString());
        Assert.False(attendee.TryGetProperty("email", out _));
        await GetAsync(owner, $"{rosterPath}?limit=51", HttpStatusCode.BadRequest);

        await RemoveCommunityMemberAsync(owner, orgA, communityId, inviteeId);
        await GetAsync(invitee, communityPath, HttpStatusCode.NotFound);
        await GetAsync(invitee, rosterPath, HttpStatusCode.NotFound);
        await SendAsync(invitee, HttpMethod.Put, $"{communityPath}/rsvp", new { status = "Going" }, HttpStatusCode.NotFound);
        Assert.Empty(Items(await GetAsync(owner, rosterPath)));
        Assert.Equal(organizationId, Id(Assert.Single(Items(await GetAsync(invitee, Range(orgAEvents))))));

        await SendAsync(owner, HttpMethod.Delete, $"/api/organizations/{orgA}/members/{inviteeId}", null, HttpStatusCode.NoContent);
        await GetAsync(invitee, Range(orgAEvents), HttpStatusCode.Forbidden);
        await GetAsync(invitee, $"{orgAEvents}/{organizationId}", HttpStatusCode.Forbidden);

        await SendAsync(otherOwner, HttpMethod.Put, $"/api/personal/people/blocks/{ownerId}", null, HttpStatusCode.NoContent);
        await GetAsync(otherOwner, $"{PersonalEvents}/{personalId}", HttpStatusCode.NotFound);
        Assert.Empty(Items(await GetAsync(otherOwner, Range(PersonalEvents))));
    }

    [Fact]
    public async Task CalendarRoundTripsDstOffsetsAndProtectsEditsAndCancellation()
    {
        await using var app = await StartHostAsync();
        using var owner = Client(app);
        using var invitee = Client(app);
        var ownerId = await RegisterAsync(owner, Username("owner"));
        var inviteeId = await RegisterAsync(invitee, Username("invitee"));
        var org = await CreateOrganizationAsync(owner, "Calendar versioning");
        await JoinOrganizationAsync(owner, invitee, org);
        var route = OrgEvents(org);
        var draft = Event("DST boundary", attendees: [inviteeId]) with
        {
            StartUtc = new(2027, 3, 14, 1, 30, 0, TimeSpan.FromHours(-5)),
            EndUtc = new(2027, 3, 14, 3, 30, 0, TimeSpan.FromHours(-4)),
            TimeZone = "Eastern Standard Time"
        };

        using (var withoutCsrf = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(draft) })
        {
            withoutCsrf.Headers.Add("Idempotency-Key", NewKey());
            using var response = await owner.SendAsync(withoutCsrf);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var replayKey = NewKey();
        var created = await CreateEventAsync(owner, route, draft, replayKey);
        var id = Id(created);
        var entry = $"{route}/{id}";
        Assert.Equal(id, Id(await CreateEventAsync(owner, route, draft, replayKey)));
        await SendAsync(owner, HttpMethod.Post, route, draft with { Title = "Different request" }, HttpStatusCode.Conflict, replayKey);
        await SendAsync(owner, HttpMethod.Post, route, draft with { TimeZone = "Not/A/TimeZone" }, HttpStatusCode.BadRequest, NewKey());
        Assert.Equal(ownerId, created.GetProperty("organizerId").GetGuid());
        var before = await GetAsync(invitee, entry);
        var starts = before.GetProperty("startUtc").GetDateTimeOffset();
        var ends = before.GetProperty("endUtc").GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, starts.Offset);
        Assert.Equal(TimeSpan.Zero, ends.Offset);
        Assert.Equal(CalendarStart, starts);
        Assert.Equal(TimeSpan.FromHours(1), ends - starts);
        var zoneId = before.GetProperty("timeZone").GetString();
        Assert.Equal("America/New_York", zoneId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId!);
        Assert.Equal(draft.StartUtc.TimeOfDay, TimeZoneInfo.ConvertTime(starts, zone).TimeOfDay);
        Assert.Equal(draft.EndUtc.TimeOfDay, TimeZoneInfo.ConvertTime(ends, zone).TimeOfDay);
        Assert.True(created.GetProperty("canEdit").GetBoolean());

        var version = created.GetProperty("version").GetInt32();
        await SendAsync(invitee, HttpMethod.Put, entry, draft with { AttendeeIds = [], Version = version }, HttpStatusCode.Forbidden);
        await SendAsync(invitee, HttpMethod.Put, $"{entry}/rsvp", new { status = "Maybe" }, HttpStatusCode.BadRequest);
        await SendAsync(invitee, HttpMethod.Put, $"{entry}/rsvp", new { status = "Going" });

        using var first = await RequestAsync(owner, HttpMethod.Put, entry, draft with { Title = "First edit", Version = version });
        using var second = await RequestAsync(owner, HttpMethod.Put, entry, draft with { Title = "Second edit", Version = version });
        var contenders = await Task.WhenAll(owner.SendAsync(first), owner.SendAsync(second));
        string winningTitle;
        try
        {
            Assert.Single(contenders, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(contenders, response => response.StatusCode == HttpStatusCode.Conflict);
            winningTitle = contenders[0].StatusCode == HttpStatusCode.OK ? "First edit" : "Second edit";
        }
        finally
        {
            foreach (var contender in contenders) contender.Dispose();
        }

        var edited = await GetAsync(invitee, entry);
        var currentVersion = edited.GetProperty("version").GetInt32();
        Assert.Equal(version + 1, currentVersion);
        Assert.Equal("Going", edited.GetProperty("myRsvp").GetString());
        Assert.Equal(winningTitle, edited.GetProperty("title").GetString());
        await SendAsync(owner, HttpMethod.Post, $"{entry}/cancel", new { version }, HttpStatusCode.Conflict);
        var canceled = await SendAsync(owner, HttpMethod.Post, $"{entry}/cancel", new { version = currentVersion });
        Assert.Equal(JsonValueKind.String, canceled.GetProperty("canceledAt").ValueKind);
        Assert.False(canceled.GetProperty("canEdit").GetBoolean());
        Assert.Equal(currentVersion + 1, canceled.GetProperty("version").GetInt32());
        await SendAsync(invitee, HttpMethod.Put, $"{entry}/rsvp", new { status = "Declined" }, HttpStatusCode.Conflict);
        Assert.Equal(id, Id(Assert.Single(Items(await GetAsync(invitee, Range(route))))));
    }

    [Fact]
    public async Task CalendarPagesBeyondFiftyEventsAndAttendeesWithoutSkippingItems()
    {
        await using var app = await StartHostAsync();
        using var owner = Client(app);
        await RegisterAsync(owner, Username("owner"));
        var eventIds = new HashSet<Guid>();
        for (var index = 0; index < 51; index++)
        {
            var entry = await CreateEventAsync(owner, PersonalEvents, Event($"Planning session {index}"));
            Assert.True(eventIds.Add(Id(entry)));
        }

        var range = Range(PersonalEvents);
        await GetAsync(owner, range + "&cursor=not-a-cursor", HttpStatusCode.BadRequest);
        await GetAsync(owner, Range(PersonalEvents, days: 63), HttpStatusCode.BadRequest);
        Assert.True(eventIds.SetEquals(await ReadTwoPagesAsync(owner, range + "&", Id)));

        var org = await CreateOrganizationAsync(owner, "Calendar attendee paging");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var roster = Enumerable.Range(1, 51).Select(index =>
        {
            var username = Username("attendee");
            return new NexusUser { Id = Guid.NewGuid(), UserName = username, DisplayName = $"Attendee {index}" };
        }).ToArray();
        db.Users.AddRange(roster);
        foreach (var account in roster) OrganizationFixtureHandler.Add(db, org, account);
        await db.SaveChangesAsync();

        var route = OrgEvents(org);
        var attendeeIds = roster.Select(account => account.Id).ToArray();
        var group = await CreateEventAsync(owner, route, Event("Large event", attendees: attendeeIds));
        var fetchedAttendees = await ReadTwoPagesAsync(owner, $"{route}/{Id(group)}/attendees?",
            item => item.GetProperty("userId").GetGuid());
        Assert.True(attendeeIds.ToHashSet().SetEquals(fetchedAttendees));
    }

    [Fact]
    public async Task CalendarOutboxRechecksAccessAndDeduplicatesReminders()
    {
        await using var app = await StartHostAsync();
        using var owner = Client(app);
        using var going = Client(app);
        using var revoked = Client(app);
        using var declined = Client(app);
        var ownerId = await RegisterAsync(owner, Username("owner"));
        var goingId = await RegisterAsync(going, Username("going"));
        var revokedId = await RegisterAsync(revoked, Username("revoked"));
        var declinedId = await RegisterAsync(declined, Username("declined"));
        var org = await CreateOrganizationAsync(owner, "Calendar reminders");
        var community = await CreateCommunityAsync(owner, org);
        foreach (var member in new[] { going, revoked, declined })
        {
            await JoinOrganizationAsync(owner, member, org);
            await JoinCommunityAsync(member, org, community);
        }

        var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(3).AddHours(14);
        var draft = Event("Project review", community, [goingId, revokedId, declinedId]) with
        {
            StartUtc = start, EndUtc = start.AddHours(1), ReminderMinutes = 30
        };
        var route = OrgEvents(org);
        var created = await CreateEventAsync(owner, route, draft);
        var eventId = Id(created);
        var entry = $"{route}/{eventId}";

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var messages = db.OutboxMessages.AsNoTracking().Where(item => item.ResourceId == eventId);
        var notifications = db.Notifications.AsNoTracking().Where(item => item.ResourceId == eventId);
        var invitation = await messages.SingleAsync(item => item.Kind == "calendar.invitation");
        var oldReminder = await messages.SingleAsync(item => item.Kind == "calendar.reminder");
        Assert.Equal(start.AddMinutes(-30), oldReminder.AvailableAt);
        var calendarEvent = db.CalendarEvents.AsNoTracking().Where(item => item.Id == eventId);
        Assert.Equal(oldReminder.Id, await calendarEvent.Select(item => item.ReminderOutboxId).SingleAsync());
        await WaitForDeliveryAsync(db, invitation.Id);
        var inviteNotices = await notifications.Where(item => item.Kind == "calendar.invitation").ToArrayAsync();
        Assert.Equal(3, inviteNotices.Length);
        var revokedNotice = Assert.Single(inviteNotices, item => item.RecipientId == revokedId);
        var revokedSource = $"/api/notifications/{revokedNotice.Id}/source";
        var source = await GetAsync(revoked, revokedSource);
        Assert.Equal(eventId, source.GetProperty("calendarEventId").GetGuid());
        await GetAsync(going, revokedSource, HttpStatusCode.NotFound);

        await SendAsync(going, HttpMethod.Put, $"{entry}/rsvp", new { status = "Going" });
        await SendAsync(declined, HttpMethod.Put, $"{entry}/rsvp", new { status = "Declined" });
        var changed = await SendAsync(owner, HttpMethod.Put, entry, draft with
        {
            StartUtc = start.AddHours(1), EndUtc = start.AddHours(2), Version = created.GetProperty("version").GetInt32()
        });
        var update = await messages.SingleAsync(item => item.Kind == "calendar.updated");
        await WaitForDeliveryAsync(db, update.Id);
        Assert.Equal(3, await notifications.CountAsync(item => item.Kind == "calendar.updated"));

        var currentReminder = await messages.SingleAsync(item => item.Kind == "calendar.reminder" && item.Id != oldReminder.Id);
        Assert.Equal(start.AddMinutes(30), currentReminder.AvailableAt);
        Assert.Equal(currentReminder.Id, await calendarEvent.Select(item => item.ReminderOutboxId).SingleAsync());
        await RemoveCommunityMemberAsync(owner, org, community, revokedId);
        await GetAsync(revoked, revokedSource, HttpStatusCode.NotFound);
        Assert.DoesNotContain(Items(await GetAsync(revoked, "/api/notifications")), item => Id(item) == revokedNotice.Id);

        Assert.Equal(1, await MakeReadyAsync(db, oldReminder.Id));
        await WaitForDeliveryAsync(db, oldReminder.Id);
        Assert.Empty(await notifications.Where(item => item.Kind == "calendar.reminder")
            .Select(item => new { item.OutboxMessageId, item.RecipientId }).ToListAsync());
        Assert.Null(await messages.Where(item => item.Id == currentReminder.Id).Select(item => item.CompletedAt).SingleAsync());
        Assert.Equal(1, await MakeReadyAsync(db, currentReminder.Id));
        await WaitForDeliveryAsync(db, currentReminder.Id);
        var reminded = await notifications.Where(item => item.Kind == "calendar.reminder")
            .Select(item => item.RecipientId).ToArrayAsync();
        Assert.Equal(2, reminded.Length);
        Assert.Contains(ownerId, reminded);
        Assert.Contains(goingId, reminded);
        Assert.DoesNotContain(revokedId, reminded);
        Assert.DoesNotContain(declinedId, reminded);

        await db.OutboxMessages.Where(item => item.Id == currentReminder.Id)
            .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.CompletedAt, (DateTimeOffset?)null));
        await WaitForDeliveryAsync(db, currentReminder.Id, minimumAttempts: 2);
        Assert.Equal(2, await notifications.CountAsync(item => item.Kind == "calendar.reminder"));

        await SendAsync(owner, HttpMethod.Post, $"{entry}/cancel", new { version = changed.GetProperty("version").GetInt32() });
        var cancellation = await messages.SingleAsync(item => item.Kind == "calendar.canceled");
        await WaitForDeliveryAsync(db, cancellation.Id);
        var cancelledDelivery = await notifications.Where(item => item.Kind == "calendar.canceled")
            .Select(item => item.RecipientId).ToArrayAsync();
        Assert.Contains(goingId, cancelledDelivery);
        Assert.DoesNotContain(revokedId, cancelledDelivery);
    }

    [Fact]
    public async Task CalendarAtStartReminderDeliversWhileEventIsInProgress()
    {
        await using var app = await StartHostAsync();
        using var owner = Client(app);
        var ownerId = await RegisterAsync(owner, Username("owner"));
        var later = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(2).AddHours(12);
        var created = await CreateEventAsync(owner, PersonalEvents, Event("At-start reminder") with
        {
            StartUtc = later, EndUtc = later.AddHours(1), ReminderMinutes = 0
        });
        var eventId = Id(created);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var reminder = await db.OutboxMessages.AsNoTracking().SingleAsync(item =>
            item.ResourceId == eventId && item.Kind == "calendar.reminder");
        Assert.Equal(later, reminder.AvailableAt);

        // Advance the stored event and its job together to exercise delivery just after the start.
        var justStarted = DateTimeOffset.UtcNow.AddSeconds(-5);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.CalendarEvents.Where(item => item.Id == eventId)
                .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.StartUtc, justStarted)
                    .SetProperty(item => item.EndUtc, justStarted.AddHours(1)));
            await db.OutboxMessages.Where(item => item.Id == reminder.Id)
                .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.AvailableAt, justStarted));
            await transaction.CommitAsync();
        }

        await WaitForDeliveryAsync(db, reminder.Id);
        var notification = await db.Notifications.AsNoTracking().SingleAsync(item => item.OutboxMessageId == reminder.Id);
        Assert.Equal(ownerId, notification.RecipientId);
        Assert.Equal("calendar.reminder", notification.Kind);
        var source = await GetAsync(owner, $"/api/notifications/{notification.Id}/source");
        Assert.Equal(eventId, source.GetProperty("calendarEventId").GetGuid());
    }

    private async Task<WebApplication> StartHostAsync()
    {
        var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={postgres.RuntimeConnectionString}"
        ]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new HttpClient(new HttpClientHandler { CheckCertificateRevocationList = true })
        {
            BaseAddress = new Uri(Assert.Single(addresses))
        };
    }

    private static string Username(string role) => $"cal_{role}_{NewKey()[..8]}";
    private static string NewKey() => Guid.NewGuid().ToString("N");
    private static string OrgEvents(Guid org) => $"/api/organizations/{org}/calendar/events";
    private static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();
    private static JsonElement[] Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToArray();

    private static string Range(string route, int days = 7)
    {
        var from = CalendarStart.AddDays(-1);
        return $"{route}?fromUtc={Uri.EscapeDataString(from.ToString("O"))}&toUtc={Uri.EscapeDataString(from.AddDays(days).ToString("O"))}";
    }

    private static CalendarDraft Event(string title, Guid? community = null, Guid[]? attendees = null) =>
        new(title, "Discuss the calendar plan.", CalendarStart, CalendarStart.AddHours(1), "UTC", community, attendees ?? []);

    private static async Task<Guid> RegisterAsync(HttpClient client, string username)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username, displayName = username, password = "Calendar-fixture-password-2026!", termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Id(await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static async Task<Guid> CreateOrganizationAsync(HttpClient owner, string name) =>
        Id(await SendAsync(owner, HttpMethod.Post, "/api/organizations", new { name }, HttpStatusCode.Created));

    private static async Task<Guid> CreateCommunityAsync(HttpClient owner, Guid org) =>
        Id(await SendAsync(owner, HttpMethod.Post, $"/api/organizations/{org}/communities",
            new { name = "Calendar Community", description = "Project calendar access", visibility = "Public" }, HttpStatusCode.Created, NewKey()));

    private static async Task JoinOrganizationAsync(HttpClient owner, HttpClient joiner, Guid org)
    {
        var invitation = await SendAsync(owner, HttpMethod.Post, $"/api/organizations/{org}/invitations", new { });
        await SendAsync(joiner, HttpMethod.Post, "/api/organizations/invitations/redeem", new { token = invitation.GetProperty("token").GetString() });
    }

    private static Task<JsonElement> JoinCommunityAsync(HttpClient joiner, Guid org, Guid community) =>
        SendAsync(joiner, HttpMethod.Post, $"/api/organizations/{org}/communities/{community}/join", new { });

    private static Task<JsonElement> RemoveCommunityMemberAsync(HttpClient owner, Guid org, Guid community, Guid member) =>
        SendAsync(owner, HttpMethod.Delete, $"/api/organizations/{org}/communities/{community}/members/{member}", null, HttpStatusCode.NoContent);

    private static async Task AcceptContactAsync(HttpClient sender, HttpClient recipient, string username)
    {
        const string endpoint = "/api/personal/contacts/requests";
        await SendAsync(sender, HttpMethod.Post, endpoint, new { username }, HttpStatusCode.Accepted);
        var request = Assert.Single(Items(await GetAsync(recipient, endpoint)));
        await SendAsync(recipient, HttpMethod.Put, $"{endpoint}/{Id(request)}", new { accept = true }, HttpStatusCode.NoContent);
    }

    private static Task<JsonElement> CreateEventAsync(HttpClient client, string route, CalendarDraft draft, string? key = null) =>
        SendAsync(client, HttpMethod.Post, route, draft, HttpStatusCode.Created, key ?? NewKey());

    private static async Task<Guid[]> ReadTwoPagesAsync(HttpClient client, string query, Func<JsonElement, Guid> id)
    {
        await GetAsync(client, query + "limit=51", HttpStatusCode.BadRequest);
        var first = await GetAsync(client, query + "limit=50");
        Assert.Equal(50, Items(first).Length);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        var second = await GetAsync(client, query + "limit=50&cursor=" + Uri.EscapeDataString(cursor!));
        var last = Assert.Single(Items(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        var result = Items(first).Append(last).Select(id).ToArray();
        Assert.Equal(51, result.Distinct().Count());
        return result;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        return expected == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
    }

    private static async Task<HttpRequestMessage> RequestAsync(HttpClient client, HttpMethod method, string path, object? body, string? key = null)
    {
        var csrf = await GetAsync(client, "/api/auth/csrf");
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string path, object? body,
        HttpStatusCode expected = HttpStatusCode.OK, string? key = null)
    {
        using var request = await RequestAsync(client, method, path, body, key);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
    }

    private static Task<int> MakeReadyAsync(NexusDbContext db, Guid outboxId) =>
        db.OutboxMessages.Where(item => item.Id == outboxId)
            .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1)));

    private static async Task WaitForDeliveryAsync(NexusDbContext db, Guid outboxId, int minimumAttempts = 1)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var state = await db.OutboxMessages.AsNoTracking().Where(item => item.Id == outboxId)
                .Select(item => new { item.CompletedAt, item.Attempts, item.LastFailure }).SingleAsync(timeout.Token);
            Assert.Null(state.LastFailure);
            if (state.CompletedAt is not null && state.Attempts >= minimumAttempts) return;
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed record CalendarDraft(string Title, string Description, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
        string TimeZone, Guid? CommunityId, Guid[] AttendeeIds, int? ReminderMinutes = null, int? Version = null);
}
