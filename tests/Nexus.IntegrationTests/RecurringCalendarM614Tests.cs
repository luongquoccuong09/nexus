using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web.Infrastructure.Data;
using static Nexus.IntegrationTests.MeetingsM612Tests;

namespace Nexus.IntegrationTests;

public sealed class RecurringCalendarM614Tests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private const string PersonalEvents = "/api/personal/calendar/events";
    private const string NewYork = "America/New_York";
    private static readonly DateTimeOffset SpringStart = new(2027, 3, 7, 9, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset FallStart = new(2027, 10, 31, 9, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public async Task SeriesKeepsLocalTimeAcrossBothDstChangesAndPagesOccurrenceKeys()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var owner = new BrowserClient(app);
        await owner.RegisterAsync(Username("owner"));

        var spring = await CreateAsync(owner, PersonalEvents,
            Series("Spring planning", SpringStart, "Eastern Standard Time", "2027-03-28"));
        var autumn = await CreateAsync(owner, PersonalEvents,
            Series("Autumn planning", FallStart, NewYork, "2027-11-14"));
        Assert.Equal(NewYork, spring.GetProperty("timeZone").GetString());

        var springStarts = new[] { Utc(3, 7, 14), Utc(3, 14, 13), Utc(3, 21, 13), Utc(3, 28, 13) };
        var autumnStarts = new[] { Utc(10, 31, 13), Utc(11, 7, 14), Utc(11, 14, 14) };
        await AssertOccurrencesAsync(owner, Id(spring), springStarts, Utc(3, 7, 0), Utc(4, 5, 0));
        await AssertOccurrencesAsync(owner, Id(autumn), autumnStarts, Utc(10, 31, 0), Utc(11, 22, 0));

        var gapSeries = await CreateAsync(owner, PersonalEvents, Series("Spring clock gap", SpringStart.AddHours(-6.5), NewYork, "2027-03-21"));
        var gapRange = Range(PersonalEvents, Utc(3, 14, 5), Utc(3, 14, 9));
        Assert.Empty(Items(await owner.GetAsync(gapRange)));
        var afterGap = Assert.Single(Items(await owner.GetAsync(Range(PersonalEvents, Utc(3, 21, 5), Utc(3, 21, 9)))));
        Assert.Equal(Id(gapSeries), Id(afterGap));
        Assert.Equal(Utc(3, 21, 6).AddMinutes(30), OccurrenceKey(afterGap));

        var ambiguousSeries = await CreateAsync(owner, PersonalEvents,
            Series("Autumn repeated clock hour", FallStart.AddHours(-7.5), NewYork, "2027-11-07"));
        var ambiguous = Assert.Single(Items(await owner.GetAsync(Range(PersonalEvents, Utc(11, 7, 5), Utc(11, 7, 8)))));
        Assert.Equal(Id(ambiguousSeries), Id(ambiguous));
        Assert.Equal(Utc(11, 7, 5).AddMinutes(30), OccurrenceKey(ambiguous));

        var springIcs = await ExportAsync(owner, $"{PersonalEvents}/{Id(spring)}");
        Assert.Contains("BEGIN:VTIMEZONE", springIcs);
        Assert.Contains("TZID:America/New_York", springIcs);
        Assert.Contains("DTSTART;TZID=America/New_York:20270307T090000", springIcs);
        var rule = Assert.Single(springIcs, line => line.StartsWith("RRULE:FREQ=WEEKLY", StringComparison.Ordinal));
        var until = Assert.Single(rule.Split(';'), part => part.StartsWith("UNTIL=", StringComparison.Ordinal));
        var utcLimit = DateTimeOffset.ParseExact(until[6..], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        Assert.InRange(utcLimit, springStarts[^1], springStarts[^1].AddDays(7).AddSeconds(-1));

        await owner.SendAsync(HttpMethod.Post, PersonalEvents,
            Series("Invalid start", SpringStart, NewYork, "2027-03-06"), HttpStatusCode.BadRequest, Key());
        await owner.SendAsync(HttpMethod.Post, PersonalEvents,
            Series("Invalid interval", SpringStart, NewYork) with { Recurrence = new("Weekly", 366, null) },
            HttpStatusCode.BadRequest, Key());

        var dailyStart = new DateTimeOffset(2027, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var daily = await CreateAsync(owner, PersonalEvents,
            Series("Daily check-in", dailyStart, "UTC") with { Recurrence = new("Daily", 1, null) });
        await owner.SendAsync(HttpMethod.Put, $"{PersonalEvents}/{Id(daily)}/occurrences",
            Replacement(dailyStart.AddDays(49), dailyStart.AddDays(50), Version(daily), "Moved daily check-in"));
        var range = Range(PersonalEvents, dailyStart, dailyStart.AddDays(51));
        await owner.GetAsync(range + "&limit=51", HttpStatusCode.BadRequest);
        await owner.GetAsync(Range(PersonalEvents, dailyStart, dailyStart.AddDays(63)), HttpStatusCode.BadRequest);
        var first = await owner.GetAsync(range + "&limit=50");
        Assert.Equal(50, Items(first).Length);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        var second = await owner.GetAsync(range + "&limit=50&cursor=" + Uri.EscapeDataString(cursor!));
        var last = Assert.Single(Items(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        var fetched = Items(first).Append(last).ToArray();
        Assert.All(fetched, occurrence => Assert.Equal(Id(daily), Id(occurrence)));
        Assert.Equal(Enumerable.Range(0, 51).Select(offset => dailyStart.AddDays(offset)), fetched.Select(OccurrenceKey));
        Assert.Equal(Start(fetched[^2]), Start(fetched[^1]));
        var laterStart = new DateTimeOffset(2050, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var farFuture = Assert.Single(Items(await owner.GetAsync(Range(PersonalEvents, laterStart, laterStart.AddDays(1)))));
        Assert.Equal(laterStart, OccurrenceKey(farFuture));
    }

    [Fact]
    public async Task MovedAndCanceledOccurrencesKeepOriginalIdentityAndExportStableUid()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var owner = new BrowserClient(app);
        using var attendee = new BrowserClient(app);
        await owner.RegisterAsync(Username("owner"));
        var attendeeName = Username("attendee");
        var attendeeId = await attendee.RegisterAsync(attendeeName);
        await AcceptContactAsync(owner, attendee, attendeeName);

        var master = await CreateAsync(owner, PersonalEvents, Series("Weekly planning", SpringStart, NewYork, "2027-03-28") with
        {
            AttendeeIds = [attendeeId]
        });
        var masterId = Id(master);
        var route = $"{PersonalEvents}/{masterId}";
        var beforeIcs = await ExportAsync(attendee, route);
        var uid = Uid(beforeIcs);
        var original = Utc(3, 14, 13);
        var canceledStart = Utc(3, 21, 13);
        var version = Version(master);
        var firstMove = Replacement(original, Utc(3, 20, 16), version, "Moved planning session");
        var secondMove = Replacement(original, Utc(3, 20, 17), version, "Rescheduled planning session");

        await attendee.SendAsync(HttpMethod.Put, route + "/occurrences", firstMove, HttpStatusCode.Forbidden);
        await owner.SendAsync(HttpMethod.Put, route + "/occurrences", firstMove with { OccurrenceStartUtc = Utc(3, 15, 13) },
            HttpStatusCode.NotFound);
        using var a = await owner.CreateRequestAsync(HttpMethod.Put, route + "/occurrences", firstMove);
        using var b = await owner.CreateRequestAsync(HttpMethod.Put, route + "/occurrences", secondMove);
        var contenders = await Task.WhenAll(owner.Http.SendAsync(a), owner.Http.SendAsync(b));
        using var firstResult = contenders[0];
        using var secondResult = contenders[1];
        Assert.Single(contenders, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(contenders, result => result.StatusCode == HttpStatusCode.Conflict);
        var winner = firstResult.StatusCode == HttpStatusCode.OK ? firstMove : secondMove;

        var moved = await attendee.GetAsync(Detail(route, original));
        Assert.Equal(masterId, Id(moved));
        Assert.Equal(original, OccurrenceKey(moved));
        Assert.Equal(winner.StartUtc, Start(moved));
        Assert.Equal(winner.Title, moved.GetProperty("title").GetString());
        Assert.Equal(version + 1, Version(moved));
        await attendee.GetAsync(Detail(route, winner.StartUtc), HttpStatusCode.NotFound);
        Assert.Empty(Items(await attendee.GetAsync(Range(PersonalEvents, Utc(3, 14, 0), Utc(3, 15, 0)))));
        var inMovedRange = Assert.Single(Items(await attendee.GetAsync(Range(PersonalEvents, Utc(3, 20, 0), Utc(3, 21, 0)))));
        Assert.Equal(original, OccurrenceKey(inMovedRange));
        Assert.Equal(winner.StartUtc, Start(inMovedRange));
        Assert.Equal(masterId, Id(inMovedRange));

        var movedIcs = await ExportAsync(attendee, route);
        Assert.Equal(uid, Uid(movedIcs));
        Assert.Contains("RECURRENCE-ID;TZID=America/New_York:20270314T090000", movedIcs);
        Assert.Contains($"SUMMARY:{winner.Title}", movedIcs);

        await owner.SendAsync(HttpMethod.Post, route + "/occurrences/cancel", new { version, occurrenceStartUtc = canceledStart },
            HttpStatusCode.Conflict);
        var canceled = await owner.SendAsync(HttpMethod.Post, route + "/occurrences/cancel",
            new { version = Version(moved), occurrenceStartUtc = canceledStart });
        Assert.Equal(canceledStart, OccurrenceKey(canceled));
        Assert.Equal(JsonValueKind.String, canceled.GetProperty("canceledAt").ValueKind);
        Assert.Equal(Version(moved) + 1, Version(canceled));
        var canceledDetail = await attendee.GetAsync(Detail(route, canceledStart));
        Assert.Equal(JsonValueKind.String, canceledDetail.GetProperty("canceledAt").ValueKind);
        Assert.Empty(Items(await attendee.GetAsync(Range(PersonalEvents, Utc(3, 21, 0), Utc(3, 22, 0)))));
        var canceledIcs = await ExportAsync(attendee, route);
        Assert.Equal(uid, Uid(canceledIcs));
        Assert.Contains("RECURRENCE-ID;TZID=America/New_York:20270314T090000", canceledIcs);
        Assert.Contains("EXDATE;TZID=America/New_York:20270321T090000", canceledIcs);
    }

    [Fact]
    public async Task IcsAndOccurrenceDetailsRecheckPersonalOrganizationAndCommunityAccess()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var owner = new BrowserClient(app);
        using var member = new BrowserClient(app);
        using var outsider = new BrowserClient(app);
        await owner.RegisterAsync(Username("owner"));
        var memberName = Username("member");
        var memberId = await member.RegisterAsync(memberName);
        await outsider.RegisterAsync(Username("other"));
        var orgA = await CreateOrgAsync(owner, "Recurring Organization A");
        var orgB = await CreateOrgAsync(outsider, "Recurring Organization B");
        await JoinOrgAsync(owner, member, orgA);
        await AcceptContactAsync(owner, member, memberName);
        var orgApi = OrgApi(orgA);
        var orgEvents = $"{orgApi}/calendar/events";
        var otherEvents = $"{OrgApi(orgB)}/calendar/events";
        var community = await owner.SendAsync(HttpMethod.Post, $"{orgApi}/communities", new
        {
            name = "Recurring Community", description = "Calendar access", visibility = "Public"
        }, HttpStatusCode.Created, Key());
        var communityId = Id(community);
        await member.SendAsync(HttpMethod.Post, $"{orgApi}/communities/{communityId}/join", new { });

        var personal = await CreateAsync(owner, PersonalEvents, Series("Private series", SpringStart, NewYork) with { AttendeeIds = [memberId] });
        var organization = await CreateAsync(owner, orgEvents, Series("Organization series", SpringStart, NewYork) with { AttendeeIds = [memberId] });
        var communitySeries = await CreateAsync(owner, orgEvents, Series("Community series", SpringStart, NewYork) with { CommunityId = communityId });
        var otherSeries = await CreateAsync(outsider, otherEvents, Series("Other series", SpringStart, NewYork));
        var personalRoute = $"{PersonalEvents}/{Id(personal)}";
        var organizationRoute = $"{orgEvents}/{Id(organization)}";
        var communityRoute = $"{orgEvents}/{Id(communitySeries)}";
        var second = Utc(3, 14, 13);

        Assert.Contains("SUMMARY:Private series", await ExportAsync(member, personalRoute));
        Assert.Contains("SUMMARY:Organization series", await ExportAsync(member, organizationRoute));
        Assert.Contains("SUMMARY:Community series", await ExportAsync(member, communityRoute));
        Assert.Equal(second, OccurrenceKey(await member.GetAsync(Detail(communityRoute, second))));
        Assert.Contains("SUMMARY:Other series", await ExportAsync(outsider, $"{otherEvents}/{Id(otherSeries)}"));
        await ExportAsync(outsider, personalRoute, HttpStatusCode.NotFound);
        await ExportAsync(outsider, organizationRoute, HttpStatusCode.Forbidden);
        await ExportAsync(outsider, $"{otherEvents}/{Id(organization)}", HttpStatusCode.NotFound);

        await owner.SendAsync(HttpMethod.Delete, $"{orgApi}/communities/{communityId}/members/{memberId}", expected: HttpStatusCode.NoContent);
        await ExportAsync(member, communityRoute, HttpStatusCode.NotFound);
        await member.GetAsync(Detail(communityRoute, second), HttpStatusCode.NotFound);
        Assert.Contains("SUMMARY:Organization series", await ExportAsync(member, organizationRoute));

        await owner.SendAsync(HttpMethod.Delete, $"{orgApi}/members/{memberId}", expected: HttpStatusCode.NoContent);
        await ExportAsync(member, organizationRoute, HttpStatusCode.Forbidden);
        await member.GetAsync(Detail(organizationRoute, second), HttpStatusCode.Forbidden);
        Assert.Contains("SUMMARY:Private series", await ExportAsync(member, personalRoute));
        Assert.Equal(second, OccurrenceKey(await member.GetAsync(Detail(personalRoute, second))));
    }

    [Fact]
    public async Task OnlineOccurrenceMoveKeepsRoomAndCancellationRevokesOnlyThatOccurrence()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var owner = new BrowserClient(app);
        using var attendee = new BrowserClient(app);
        await owner.RegisterAsync(Username("owner"));
        var attendeeName = Username("attendee");
        var attendeeId = await attendee.RegisterAsync(attendeeName);
        await AcceptContactAsync(owner, attendee, attendeeName);
        var master = await CreateAsync(owner, PersonalEvents, Series("Online weekly planning", SpringStart, NewYork) with
        {
            AttendeeIds = [attendeeId], IsOnlineMeeting = true
        });
        var route = $"{PersonalEvents}/{Id(master)}";
        var original = Utc(3, 14, 13);
        var sibling = Utc(3, 21, 13);
        var originalJoin = Detail(route + "/meeting/join", original);
        var siblingJoin = Detail(route + "/meeting/join", sibling);
        var invalidJoin = Detail(route + "/meeting/join", Utc(3, 15, 13));
        await attendee.SendAsync(HttpMethod.Post, invalidJoin, expected: HttpStatusCode.NotFound);
        var first = await JoinAsync(attendee, originalJoin);
        var second = await JoinAsync(attendee, siblingJoin);
        Assert.NotEqual(first.MeetingId, second.MeetingId);
        Assert.NotEqual(ReadClaims(first).Room, ReadClaims(second).Room);

        var updated = await owner.SendAsync(HttpMethod.Put, route + "/occurrences",
            Replacement(original, Utc(3, 20, 16), Version(master), "Moved online discussion"));
        var reopened = await JoinAsync(attendee, originalJoin);
        Assert.Equal(first.MeetingId, reopened.MeetingId);
        Assert.Equal(first.GrantId, reopened.GrantId);
        await ValidateSignalAsync(attendee, first, HttpStatusCode.OK);
        await ValidateSignalAsync(attendee, second, HttpStatusCode.OK);

        var canceled = await owner.SendAsync(HttpMethod.Post, route + "/occurrences/cancel",
            new { version = Version(updated), occurrenceStartUtc = original });
        await upstream.WaitForEndAsync(ReadClaims(first).Room);
        await attendee.SendAsync(HttpMethod.Post, originalJoin, expected: HttpStatusCode.NotFound);
        await ValidateSignalAsync(attendee, first, HttpStatusCode.Unauthorized);
        await ValidateSignalAsync(attendee, second, HttpStatusCode.OK);
        Assert.Equal(second.MeetingId, (await JoinAsync(attendee, siblingJoin)).MeetingId);

        await owner.SendAsync(HttpMethod.Post, route + "/cancel", new { version = Version(canceled) });
        await upstream.WaitForEndAsync(ReadClaims(second).Room);
        await ValidateSignalAsync(attendee, second, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RecurringRemindersCarryOriginalOccurrenceKeyAndDoNotDeliverCanceledOccurrence()
    {
        await using var upstream = await ProviderFixture.StartAsync();
        await using var app = await StartHostAsync(postgres, upstream.BaseAddress);
        using var owner = new BrowserClient(app);
        using var attendee = new BrowserClient(app);
        await owner.RegisterAsync(Username("owner"));
        var attendeeName = Username("attendee");
        var attendeeId = await attendee.RegisterAsync(attendeeName);
        await AcceptContactAsync(owner, attendee, attendeeName);
        var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(3).AddHours(14);
        var last = start.AddDays(2);
        var draft = Series("Recurring reminder", start, "UTC") with
        {
            Recurrence = new Rule("Daily", 1, last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            AttendeeIds = [attendeeId], ReminderMinutes = 30
        };
        var series = await CreateAsync(owner, PersonalEvents, draft);
        var seriesId = Id(series);
        var route = $"{PersonalEvents}/{seriesId}";

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var eventQuery = db.CalendarEvents.AsNoTracking().Where(item => item.Id == seriesId);
        var reminders = db.OutboxMessages.AsNoTracking().Where(item => item.ResourceId == seriesId && item.Kind == "calendar.reminder");
        var attendeeNotices = db.Notifications.AsNoTracking().Where(item => item.RecipientId == attendeeId &&
            item.ResourceId == seriesId && item.Kind == "calendar.reminder");
        var first = await reminders.SingleAsync();
        Assert.Equal(start.AddMinutes(-30), first.AvailableAt);
        Assert.Equal(first.Id, await eventQuery.Select(item => item.ReminderOutboxId).SingleAsync());
        await DispatchReminderAsync(db, first.Id);
        var firstNotice = await attendeeNotices.SingleAsync(item => item.OutboxMessageId == first.Id);
        var firstSource = await attendee.GetAsync($"/api/notifications/{firstNotice.Id}/source");
        Assert.Equal(start, OccurrenceKey(firstSource));

        var second = await reminders.SingleAsync(item => item.Id != first.Id && item.CompletedAt == null);
        Assert.Equal(start.AddDays(1).AddMinutes(-30), second.AvailableAt);
        Assert.Equal(second.Id, await eventQuery.Select(item => item.ReminderOutboxId).SingleAsync());
        await DispatchReminderAsync(db, second.Id);
        var secondNotice = await attendeeNotices.SingleAsync(item => item.OutboxMessageId == second.Id);
        var secondSource = await attendee.GetAsync($"/api/notifications/{secondNotice.Id}/source");
        Assert.Equal(start.AddDays(1), OccurrenceKey(secondSource));

        var third = await reminders.SingleAsync(item => item.Id != first.Id && item.Id != second.Id && item.CompletedAt == null);
        Assert.Equal(last.AddMinutes(-30), third.AvailableAt);
        await owner.SendAsync(HttpMethod.Post, route + "/occurrences/cancel", new { version = Version(series), occurrenceStartUtc = last });
        var outstanding = await reminders.AnyAsync(item => item.Id == third.Id && item.CompletedAt == null);
        if (outstanding) await DispatchReminderAsync(db, third.Id);
        Assert.Null(await eventQuery.Select(item => item.ReminderOutboxId).SingleAsync());
        Assert.False(await attendeeNotices.AnyAsync(item => item.OutboxMessageId == third.Id));

        await DispatchReminderAsync(db, first.Id, minimumAttempts: 2);
        Assert.Equal(2, await attendeeNotices.CountAsync());
        var visible = Items(await attendee.GetAsync("/api/notifications")).Where(item => item.GetProperty("kind").GetString() == "calendar.reminder"
            && item.GetProperty("resourceId").GetGuid() == seriesId).Select(Id).ToArray();
        Assert.Equal(2, visible.Length);
        Assert.Contains(firstNotice.Id, visible);
        Assert.Contains(secondNotice.Id, visible);
    }

    private static string Username(string role) => $"series_{role}_{Key()[..8]}";
    private static string Key() => Guid.NewGuid().ToString("N");
    private static int Version(JsonElement item) => item.GetProperty("version").GetInt32();
    private static DateTimeOffset OccurrenceKey(JsonElement item) => item.GetProperty("occurrenceStartUtc").GetDateTimeOffset();
    private static DateTimeOffset Start(JsonElement item) => item.GetProperty("startUtc").GetDateTimeOffset();
    private static JsonElement[] Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToArray();
    private static DateTimeOffset Utc(int month, int day, int hour) => new(2027, month, day, hour, 0, 0, TimeSpan.Zero);
    private static string Encode(DateTimeOffset value) => Uri.EscapeDataString(value.ToUniversalTime().ToString("O"));
    private static string Range(string route, DateTimeOffset from, DateTimeOffset to) => $"{route}?fromUtc={Encode(from)}&toUtc={Encode(to)}";
    private static string Detail(string route, DateTimeOffset original) => $"{route}?occurrenceStartUtc={Encode(original)}";

    private static Draft Series(string title, DateTimeOffset start, string zone, string? until = null) =>
        new(title, "Agenda for the planning session.", start, start.AddHours(1), zone, [], null, new("Weekly", 1, until));

    private static OccurrenceEdit Replacement(DateTimeOffset original, DateTimeOffset replacement, int version, string title) =>
        new(version, original, title, "Updated planning agenda.", replacement, replacement.AddHours(1));

    private static Task<JsonElement> CreateAsync(BrowserClient client, string path, Draft draft) =>
        client.SendAsync(HttpMethod.Post, path, draft, HttpStatusCode.Created, Key());

    private static async Task DispatchReminderAsync(NexusDbContext db, Guid jobId, int minimumAttempts = 1)
    {
        var job = db.OutboxMessages.Where(item => item.Id == jobId);
        Assert.Equal(1, await job.ExecuteUpdateAsync(change => change
            .SetProperty(item => item.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1))
            .SetProperty(item => item.CompletedAt, (DateTimeOffset?)null)));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var state = await job.AsNoTracking().Select(item => new { item.CompletedAt, item.Attempts, item.LastFailure }).SingleAsync(timeout.Token);
            Assert.Null(state.LastFailure);
            if (state.CompletedAt is not null && state.Attempts >= minimumAttempts) return;
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task AssertOccurrencesAsync(BrowserClient client, Guid masterId, DateTimeOffset[] expected,
        DateTimeOffset from, DateTimeOffset to)
    {
        var occurrences = Items(await client.GetAsync(Range(PersonalEvents, from, to)));
        Assert.Equal(expected, occurrences.Select(Start));
        Assert.Equal(expected, occurrences.Select(OccurrenceKey));
        Assert.All(occurrences, occurrence =>
        {
            Assert.Equal(masterId, Id(occurrence));
            Assert.Equal(TimeSpan.Zero, Start(occurrence).Offset);
            Assert.Equal(9, TimeZoneInfo.ConvertTimeBySystemTimeZoneId(Start(occurrence), NewYork).Hour);
            Assert.Equal(TimeSpan.FromHours(1), occurrence.GetProperty("endUtc").GetDateTimeOffset() - Start(occurrence));
        });
    }

    private static async Task<string[]> ExportAsync(BrowserClient client, string route, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.Http.GetAsync(route + "/ics");
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK) return [];
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet, ignoreCase: true);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        var text = await response.Content.ReadAsStringAsync();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n ", "", StringComparison.Ordinal)
            .Replace("\n\t", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("BEGIN:VCALENDAR", lines[0], StringComparison.Ordinal);
        Assert.Equal("END:VCALENDAR", lines[^1]);
        return lines;
    }

    private static string Uid(string[] lines) => Assert.Single(lines.Where(line => line.StartsWith("UID:", StringComparison.Ordinal)).Distinct());

    private sealed record Draft(string Title, string Description, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZone,
        Guid[] AttendeeIds, Guid? CommunityId, Rule Recurrence, bool IsOnlineMeeting = false, int? ReminderMinutes = null);
    private sealed record Rule(string Frequency, int Interval, string? UntilLocalDate);
    private sealed record OccurrenceEdit(int Version, DateTimeOffset OccurrenceStartUtc, string Title, string Description,
        DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}
