import assert from 'node:assert/strict';
import test from 'node:test';
import { defaultEventTimes, formatCalendarTimes, toEventInstant, toEventLocalTime } from '../src/calendar/calendarTime.ts';

const newYork = 'America/New_York';

test('Calendar preserves the local appointment time across summer and winter offsets', () => {
  for (const [local, utc] of [
    ['2026-03-01T09:00:00', '2026-03-01T14:00:00Z'],
    ['2026-03-08T09:00:00', '2026-03-08T13:00:00Z'],
    ['2026-10-25T09:00:00', '2026-10-25T13:00:00Z'],
    ['2026-11-01T09:00:00', '2026-11-01T14:00:00Z'],
  ]) {
    assert.equal(toEventInstant(local, newYork), utc);
    assert.equal(toEventLocalTime(utc, newYork), local);
  }

  assert.notEqual(
    formatCalendarTimes('2026-03-08T13:00:00Z', '2026-03-08T14:00:00Z', newYork),
    formatCalendarTimes('2026-03-08T13:00:00Z', '2026-03-08T14:00:00Z', 'Europe/London'),
  );
});

test('Calendar rejects ambiguous new times and preserves an existing ambiguous instant on edit', () => {
  assert.throws(() => toEventInstant('2026-03-08T02:30', newYork), RangeError);
  assert.throws(() => toEventInstant('2026-11-01T01:30', newYork), RangeError);
  const original = { instant: '2026-11-01T06:30:00Z', timeZone: newYork };
  assert.equal(toEventInstant('2026-11-01T01:30', newYork, original), original.instant);
});

test('Default event times remain valid when the next interval crosses the spring clock change', () => {
  assert.deepEqual(defaultEventTimes(newYork, Date.parse('2026-03-08T06:45:00Z')), {
    startLocal: '2026-03-08T03:00:00',
    endLocal: '2026-03-08T03:30:00',
  });
});
