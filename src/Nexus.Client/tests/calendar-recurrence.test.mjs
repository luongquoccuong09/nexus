import assert from 'node:assert/strict';
import test from 'node:test';
import { calendarEventId, describeRecurrence, recurrenceInput } from '../src/calendar/calendarRecurrence.ts';

test('recurring calendar keys preserve a moved occurrence and distinguish other instances and scopes', () => {
  const series = { organizationId: 'organization', eventId: 'series' };
  const first = { ...series, occurrenceStartUtc: '2026-03-01T14:00:00+00:00' };
  const moved = { ...first, startUtc: '2026-03-02T17:00:00Z' };
  assert.equal(calendarEventId(first), calendarEventId(moved));
  assert.equal(calendarEventId(first), calendarEventId({ ...first, occurrenceStartUtc: '2026-03-01T14:00:00Z' }));
  assert.notEqual(calendarEventId(first), calendarEventId({ ...first, occurrenceStartUtc: '2026-03-08T13:00:00Z' }));
  assert.notEqual(calendarEventId(first), calendarEventId({ ...first, organizationId: null }));
  assert.notEqual(calendarEventId(first), calendarEventId(series));
});

test('repeat until is inclusive in the event local date and may be omitted', () => {
  assert.deepEqual(recurrenceInput('Weekly', '2', '2026-03-08', '2026-03-08T23:30:00'), {
    frequency: 'Weekly', interval: 2, untilLocalDate: '2026-03-08'
  });
  assert.throws(() => recurrenceInput('Weekly', '2', '2026-03-07', '2026-03-08T00:01:00'), /on or after/);
  assert.deepEqual(recurrenceInput('Monthly', '1', '', '2026-03-08T09:00:00'), {
    frequency: 'Monthly', interval: 1, untilLocalDate: null
  });
  assert.equal(describeRecurrence({ frequency: 'Weekly', interval: 2, untilLocalDate: null }), 'Every 2 weeks');
});

test('turning off repeat clears earlier settings and intervals must be bounded whole numbers', () => {
  assert.deepEqual(recurrenceInput('None', '99', '2026-03-12', '2026-03-08T09:00:00'), {
    frequency: 'None', interval: null, untilLocalDate: null
  });
  for (const interval of ['', '0', '366', '1.5']) {
    assert.throws(() => recurrenceInput('Daily', interval, '', '2026-11-25T10:00:00'), /between 1 and 365/);
  }
  assert.throws(() => recurrenceInput('Daily', '1', '2026-02-30', '2026-01-01T10:00:00'));
});
