import { Temporal } from 'temporal-polyfill';

export function localCalendarZone() {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

export function calendarZones(localZone: string) {
  return [...new Set(['UTC', localZone, ...Intl.supportedValuesOf('timeZone')])];
}

export function toEventLocalTime(instant: string, timeZone: string) {
  return Temporal.Instant.from(instant).toZonedDateTimeISO(timeZone).toPlainDateTime().toString({ smallestUnit: 'second' });
}

export function toEventInstant(localTime: string, timeZone: string, original?: { instant: string; timeZone: string }) {
  if (original?.timeZone === timeZone && toEventLocalTime(original.instant, timeZone) ===
      Temporal.PlainDateTime.from(localTime).toString({ smallestUnit: 'second' })) {
    return original.instant;
  }
  return Temporal.PlainDateTime.from(localTime)
    .toZonedDateTime(timeZone, { disambiguation: 'reject' })
    .toInstant().toString();
}

export function defaultEventTimes(timeZone: string, now = Date.now()) {
  const halfHour = 30 * 60_000;
  const start = (Math.floor(now / halfHour) + 1) * halfHour;
  return {
    startLocal: toEventLocalTime(new Date(start).toISOString(), timeZone),
    endLocal: toEventLocalTime(new Date(start + halfHour).toISOString(), timeZone)
  };
}

export function formatCalendarTimes(startUtc: string, endUtc: string, timeZone: string) {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium', timeStyle: 'short', timeZone
  }).formatRange(new Date(startUtc), new Date(endUtc));
}
