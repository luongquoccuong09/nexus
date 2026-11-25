using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using ICalendar = Ical.Net.Calendar;

namespace Nexus.Web.Modules.Calendar;

internal sealed record CalendarOccurrence(
    DateTimeOffset OriginalStartUtc, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    string Title, string? Description, DateTimeOffset? CanceledAt);

internal static class CalendarRecurrences
{
    internal static bool IsRecurring(CalendarEventEntity item) => item.RecurrenceFrequency.HasValue;

    internal static bool ContainsOriginal(CalendarEventEntity item, DateTimeOffset originalUtc) =>
        Expand(item, originalUtc.ToUniversalTime(), Shift(originalUtc, TimeSpan.FromTicks(1))).Any();

    internal static IEnumerable<DateTimeOffset> OriginalStarts(CalendarEventEntity item, DateTimeOffset fromUtc) =>
        Expand(item, fromUtc.ToUniversalTime(), DateTimeOffset.MaxValue).Select(occurrence => occurrence.OriginalStartUtc);

    internal static async Task<CalendarOccurrence?> FindAsync(NexusDbContext db, CalendarEventEntity item,
        DateTimeOffset originalUtc, CancellationToken ct)
    {
        originalUtc = originalUtc.ToUniversalTime();
        if (!ContainsOriginal(item, originalUtc)) return null;
        var change = await db.CalendarOccurrenceOverrides.AsNoTracking().SingleOrDefaultAsync(
            entry => entry.EventId == item.Id && entry.OriginalStartUtc == originalUtc, ct);
        return Resolve(item, originalUtc, change);
    }

    internal static CalendarOccurrence Resolve(CalendarEventEntity item, DateTimeOffset originalUtc, CalendarOccurrenceOverride? change)
    {
        if (change?.StartUtc is { } moved && change.EndUtc is { } movedEnd)
            return new CalendarOccurrence(originalUtc, moved, movedEnd, change.Title!, change.Description,
                item.CanceledAt ?? change.CanceledAt);
        return new CalendarOccurrence(originalUtc, originalUtc, Shift(originalUtc, item.EndUtc - item.StartUtc),
            item.Title, item.Description, item.CanceledAt ?? change?.CanceledAt);
    }

    internal static IEnumerable<CalendarOccurrence> Expand(CalendarEventEntity item, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (!IsRecurring(item) || fromUtc >= toUtc || toUtc <= item.StartUtc) yield break;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(item.TimeZone);
        var anchor = LocalTime(WholeSecond(item.StartUtc), zone);
        var zoneBounds = TimeSpan.FromDays(1);
        if (item.RecurrenceUntilLocalDate is { } until &&
            DateOnly.FromDateTime(LocalTime(Shift(fromUtc, -zoneBounds), zone)) > until) yield break;

        var rule = BuildRule(item);
        if (item.RecurrenceUntilLocalDate is { } ends)
            rule.Until = new CalDateTime(ends.ToDateTime(new TimeOnly(23, 59, 59)));
        var definition = new CalendarEvent { DtStart = new CalDateTime(anchor), RecurrenceRule = rule };

        // Evaluate wall-clock candidates independently, then map to physical instants.
        // DST gaps have no occurrence and the first offset resolves an ambiguous wall time.
        var localStart = LocalTime(Shift(fromUtc, -zoneBounds), zone);
        var localEnd = LocalTime(Shift(toUtc, zoneBounds), zone);
        var fraction = item.StartUtc.UtcTicks % TimeSpan.TicksPerSecond;
        foreach (var candidate in definition.GetOccurrences(new CalDateTime(localStart)).TakeWhileBefore(new CalDateTime(localEnd)))
        {
            var local = candidate.Period.StartTime.Value;
            if (zone.IsInvalidTime(local)) continue;
            var original = local == anchor ? item.StartUtc : Shift(ToUtc(local, zone), TimeSpan.FromTicks(fraction));
            if (original < fromUtc) continue;
            if (original >= toUtc) yield break;
            yield return Resolve(item, original, null);
        }
    }

    internal static DateTimeOffset Shift(DateTimeOffset value, TimeSpan delta) =>
        new(Math.Clamp(value.UtcTicks + delta.Ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), TimeSpan.Zero);

    internal static string Serialize(CalendarEventEntity item, IEnumerable<CalendarOccurrenceOverride> changes)
    {
        var calendar = new ICalendar { ProductId = "-//Project Nexus//Calendar//EN" };
        var zone = TimeZoneInfo.FindSystemTimeZoneById(item.TimeZone);
        var master = ExportEntry(item, zone, item.StartUtc, item.EndUtc, item.Title, item.Description);
        calendar.Events.Add(master);
        if (item.TimeZone != CalDateTime.UtcTzId)
            calendar.AddTimeZone(new VTimeZone(item.TimeZone));

        if (IsRecurring(item))
        {
            var rule = BuildRule(item);
            if (item.RecurrenceUntilLocalDate is { } ends)
                rule.Until = UtcDate(EndOfLocalDate(ends, zone));
            master.RecurrenceRule = rule;

            var hasAnchorException = false;
            foreach (var change in changes)
            {
                hasAnchorException |= change.OriginalStartUtc == item.StartUtc;
                var original = ZonedDate(change.OriginalStartUtc, item.TimeZone, zone);
                if (change.CanceledAt is not null)
                {
                    master.ExceptionDates.Add(original);
                    continue;
                }
                if (change.StartUtc is not { } moved || change.EndUtc is not { } movedEnd) continue;
                var exception = ExportEntry(item, zone, moved, movedEnd, change.Title!, change.Description);
                exception.RecurrenceIdentifier = new RecurrenceIdentifier(original, RecurrenceRange.ThisInstance);
                calendar.Events.Add(exception);
            }

            // RFC 5545 gives an overlapping local DTSTART the earlier offset. Keep the
            // wall-clock series anchor and detach its first instance if Nexus chose the later one.
            if (master.DtStart!.TzId == CalDateTime.UtcTzId && item.TimeZone != CalDateTime.UtcTzId)
            {
                var anchor = ZonedDate(item.StartUtc, item.TimeZone, zone);
                if (!hasAnchorException)
                {
                    var first = ExportEntry(item, zone, item.StartUtc, item.EndUtc, item.Title, item.Description);
                    first.RecurrenceIdentifier = new RecurrenceIdentifier(anchor, RecurrenceRange.ThisInstance);
                    calendar.Events.Add(first);
                }
                master.DtStart = anchor;
                var nominalEnd = Shift(ToUtc(anchor.Value, zone), item.EndUtc - item.StartUtc);
                master.DtEnd = ExactDate(nominalEnd, item.TimeZone, zone);
            }
        }

        return new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Unable to serialize calendar.");
    }

    private static CalendarEvent ExportEntry(CalendarEventEntity item, TimeZoneInfo zone,
        DateTimeOffset start, DateTimeOffset end, string title, string? description) => new()
    {
        Uid = $"{item.Id:N}@nexus",
        Summary = title,
        Description = description,
        DtStart = ExactDate(start, item.TimeZone, zone),
        DtEnd = ExactDate(end, item.TimeZone, zone),
        DtStamp = UtcDate(item.UpdatedAt),
        Sequence = item.Version,
        Status = item.CanceledAt is null ? null : "CANCELLED"
    };

    private static RecurrenceRule BuildRule(CalendarEventEntity item) => new(item.RecurrenceFrequency switch
    {
        CalendarRecurrenceFrequency.Daily => FrequencyType.Daily,
        CalendarRecurrenceFrequency.Weekly => FrequencyType.Weekly,
        CalendarRecurrenceFrequency.Monthly => FrequencyType.Monthly,
        _ => throw new InvalidOperationException("The Calendar event does not recur.")
    }, item.RecurrenceInterval!.Value);

    private static DateTime LocalTime(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).DateTime;

    private static DateTimeOffset WholeSecond(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone) =>
        zone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));

    private static DateTimeOffset EndOfLocalDate(DateOnly date, TimeZoneInfo zone)
    {
        if (date == DateOnly.MaxValue) return DateTimeOffset.MaxValue;
        var next = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(next)) next = next.AddMinutes(1);
        return Shift(ToUtc(next, zone), TimeSpan.FromSeconds(-1));
    }

    private static CalDateTime UtcDate(DateTimeOffset date) => new(WholeSecond(date).UtcDateTime, CalDateTime.UtcTzId);

    private static CalDateTime ZonedDate(DateTimeOffset time, string zoneId, TimeZoneInfo zone) =>
        zoneId == CalDateTime.UtcTzId ? UtcDate(time) : new CalDateTime(LocalTime(WholeSecond(time), zone), zoneId);

    private static CalDateTime ExactDate(DateTimeOffset time, string zoneId, TimeZoneInfo zone)
    {
        var normalized = WholeSecond(time);
        var local = LocalTime(normalized, zone);
        return zoneId == CalDateTime.UtcTzId || ToUtc(local, zone) != normalized
            ? UtcDate(normalized)
            : new CalDateTime(local, zoneId);
    }
}
