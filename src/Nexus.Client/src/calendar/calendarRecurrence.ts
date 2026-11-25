import { Temporal } from 'temporal-polyfill';

export type RecurrenceFrequency = 'Daily' | 'Weekly' | 'Monthly';
export const recurrenceFrequencies: readonly RecurrenceFrequency[] = ['Daily', 'Weekly', 'Monthly'];
export const maxRecurrenceInterval = 365;
export const recurrenceUnits: Record<RecurrenceFrequency, string> = { Daily: 'day', Weekly: 'week', Monthly: 'month' };

export interface CalendarRecurrence {
  frequency: RecurrenceFrequency;
  interval: number;
  untilLocalDate: string | null;
}

export type CalendarRecurrenceInput = CalendarRecurrence | { frequency: 'None'; interval: null; untilLocalDate: null };

export interface CalendarEventTarget {
  organizationId: string | null;
  eventId: string;
  occurrenceStartUtc?: string | null;
}

export function calendarEventId(target: CalendarEventTarget) {
  const series = `${target.organizationId ?? 'personal'}:${target.eventId}`;
  if (!target.occurrenceStartUtc) return series;
  return `${series}:${Temporal.Instant.from(target.occurrenceStartUtc).toString()}`;
}

export function recurrenceInput(frequency: RecurrenceFrequency | 'None', intervalText: string,
  untilLocalDate: string, startLocal: string): CalendarRecurrenceInput {
  if (frequency === 'None') return { frequency, interval: null, untilLocalDate: null };
  const interval = Number(intervalText);
  if (!Number.isInteger(interval) || interval < 1 || interval > maxRecurrenceInterval) {
    throw new Error(`Repeat every must be between 1 and ${maxRecurrenceInterval}.`);
  }
  if (untilLocalDate) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(untilLocalDate)) throw new Error('Enter a valid repeat end date.');
    const until = Temporal.PlainDate.from(untilLocalDate);
    const start = Temporal.PlainDateTime.from(startLocal).toPlainDate();
    if (Temporal.PlainDate.compare(until, start) < 0) {
      throw new Error('Repeat until must be on or after the start date.');
    }
  }
  return { frequency, interval, untilLocalDate: untilLocalDate || null };
}

export function describeRecurrence(recurrence: CalendarRecurrence) {
  const unit = recurrenceUnits[recurrence.frequency];
  const interval = recurrence.interval === 1 ? unit : `${recurrence.interval} ${unit}s`;
  const until = recurrence.untilLocalDate
    ? ` through ${new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeZone: 'UTC' })
        .format(new Date(`${recurrence.untilLocalDate}T00:00:00Z`))}`
    : '';
  return `Every ${interval}${until}`;
}
