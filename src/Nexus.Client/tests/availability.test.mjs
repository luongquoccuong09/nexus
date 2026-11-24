import test from 'node:test';
import assert from 'node:assert/strict';
import {
  availabilityToDotTone,
  formatAvailabilityExpiry,
  formatAvailabilityLabel,
  getEarliestExpiryDelayMs,
  getExpiryDelayMs,
  isChimeSuppressed,
  resolveEffectiveAvailability,
  resolveUserAvailability
} from '../src/availability.ts';

test('M6.28: resolveEffectiveAvailability auto-reverts expired states without polling', () => {
  const now = Date.parse('2026-11-24T12:00:00.000Z');

  // Unexpired timed Busy remains Busy
  const activeBusy = resolveEffectiveAvailability(
    { state: 'Busy', expiresAt: '2026-11-24T13:00:00.000Z' },
    now
  );
  assert.deepEqual(activeBusy, { state: 'Busy', expiresAt: '2026-11-24T13:00:00.000Z' });

  // At or past expiresAt auto-reverts to Available
  assert.deepEqual(
    resolveEffectiveAvailability({ state: 'Busy', expiresAt: '2026-11-24T12:00:00.000Z' }, now),
    { state: 'Available', expiresAt: null }
  );
  assert.deepEqual(
    resolveEffectiveAvailability({ state: 'DoNotDisturb', expiresAt: '2026-11-24T11:59:59.000Z' }, now),
    { state: 'Available', expiresAt: null }
  );

  // Indefinite non-Available stays active
  assert.deepEqual(
    resolveEffectiveAvailability({ state: 'Away', expiresAt: null }, now),
    { state: 'Away', expiresAt: null }
  );

  // Available always strips any stray expiry
  assert.deepEqual(
    resolveEffectiveAvailability({ state: 'Available', expiresAt: '2026-11-24T14:00:00.000Z' }, now),
    { state: 'Available', expiresAt: null }
  );
});

test('M6.28: formatAvailabilityLabel and availabilityToDotTone produce accessible text and tones', () => {
  assert.equal(formatAvailabilityLabel('Available', true), 'Available');
  assert.equal(formatAvailabilityLabel('Available', false), 'Offline');
  assert.equal(formatAvailabilityLabel('Busy', true), 'Busy');
  assert.equal(formatAvailabilityLabel('Away', true), 'Away');
  assert.equal(formatAvailabilityLabel('DoNotDisturb', true), 'Do not disturb');
  assert.equal(formatAvailabilityLabel('DoNotDisturb', false), 'Do not disturb');

  assert.equal(availabilityToDotTone('Available', true), 'available');
  assert.equal(availabilityToDotTone('Available', false), 'offline');
  assert.equal(availabilityToDotTone('Busy', true), 'busy');
  assert.equal(availabilityToDotTone('Away', false), 'away');
  assert.equal(availabilityToDotTone('DoNotDisturb', true), 'dnd');
});

test('M6.28: formatAvailabilityExpiry formats same-day and multi-day timestamps and omits expired values', () => {
  const sameDayStart = new Date(2026, 10, 24, 10, 0, 0).getTime();
  const sameDayTarget = new Date(2026, 10, 24, 14, 30, 0).toISOString();
  const sameDayLabel = formatAvailabilityExpiry(sameDayTarget, sameDayStart, 'en-US');
  assert.equal(sameDayLabel, 'Until 14:30');

  const nextDayTarget = new Date(2026, 10, 25, 9, 15, 0).toISOString();
  const nextDayLabel = formatAvailabilityExpiry(nextDayTarget, sameDayStart, 'en-US');
  assert.match(nextDayLabel ?? '', /^Until Nov 25, 09:15$/);

  // Expired or null returns null
  assert.equal(formatAvailabilityExpiry(null, sameDayStart), null);
  assert.equal(formatAvailabilityExpiry(new Date(2026, 10, 24, 9, 0, 0).toISOString(), sameDayStart), null);
});

test('M6.28: isChimeSuppressed is true only during active unexpired DoNotDisturb', () => {
  const now = Date.parse('2026-11-24T12:00:00.000Z');
  assert.equal(isChimeSuppressed({ state: 'DoNotDisturb', expiresAt: null }, now), true);
  assert.equal(isChimeSuppressed({ state: 'DoNotDisturb', expiresAt: '2026-11-24T12:30:00.000Z' }, now), true);
  assert.equal(isChimeSuppressed({ state: 'DoNotDisturb', expiresAt: '2026-11-24T12:00:00.000Z' }, now), false);
  assert.equal(isChimeSuppressed({ state: 'Busy', expiresAt: null }, now), false);
  assert.equal(isChimeSuppressed({ state: 'Available', expiresAt: null }, now), false);
});

test('M6.28: expiry delay helpers and live map resolution support zero-polling timers', () => {
  const now = Date.parse('2026-11-24T12:00:00.000Z');
  assert.equal(getExpiryDelayMs({ state: 'Available', expiresAt: null }, now), null);
  assert.equal(getExpiryDelayMs({ state: 'Busy', expiresAt: '2026-11-24T12:01:00.000Z' }, now), 60_050);

  const earliest = getEarliestExpiryDelayMs(
    [
      { state: 'Busy', expiresAt: '2026-11-24T12:10:00.000Z' },
      { state: 'DoNotDisturb', expiresAt: '2026-11-24T12:02:00.000Z' },
      { state: 'Away', expiresAt: null }
    ],
    now
  );
  assert.equal(earliest, 120_050);

  const resolved = resolveUserAvailability(
    'u-1',
    { isOnline: false, availability: { state: 'Available', expiresAt: null } },
    {
      'u-1': {
        isOnline: true,
        availability: { state: 'DoNotDisturb', expiresAt: '2026-11-24T13:00:00.000Z' }
      }
    },
    now
  );
  assert.equal(resolved.isOnline, true);
  assert.equal(resolved.availability.state, 'DoNotDisturb');
  assert.equal(resolved.dotTone, 'dnd');
  assert.equal(resolved.label, 'Do not disturb');
});
