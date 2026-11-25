import assert from 'node:assert/strict';
import test from 'node:test';
import { activeRingingCall, callResultMessage, mergeCallRecord, shouldJoinCallMedia } from '../src/calls/callState.ts';

function call(overrides = {}) {
  return {
    id: 'call', organizationId: null, conversationId: 'conversation', version: 1, state: 'ringing',
    callerId: 'one', callerName: 'One', calleeId: 'two', calleeName: 'Two', kind: 'Voice',
    meetingId: null, createdAt: '2026-11-25T04:00:00Z', expiresAt: '2026-11-25T04:00:30Z',
    acceptedAt: null, endedAt: null, canJoinMedia: false, ...overrides
  };
}

test('M6.13 a versioned terminal event clears ringing immediately, despite delayed reads', () => {
  const ringing = call();
  const visible = mergeCallRecord(undefined, ringing, ringing);
  const now = Date.parse('2026-11-25T04:00:15Z');
  assert.equal(activeRingingCall(visible, now), ringing);

  const canceled = mergeCallRecord(visible, { ...ringing, state: 'canceled', version: 2 });
  assert.equal(activeRingingCall(canceled, now), null);
  assert.equal(mergeCallRecord(canceled, ringing, ringing), canceled);
  assert.equal(callResultMessage('canceled'), 'Call canceled.');
});

test('M6.13 an accepted event clears ringing on other devices without opening media', () => {
  const ringing = call();
  const first = mergeCallRecord(undefined, ringing, ringing);
  const accepted = call({ state: 'accepted', meetingId: 'meeting', version: 2 });
  const update = mergeCallRecord(first, accepted);
  assert.equal(activeRingingCall(update, Date.parse('2026-11-25T04:00:16Z')), null);
  assert.equal(shouldJoinCallMedia(update, true), false);
  const otherDevice = mergeCallRecord(update, accepted, accepted);
  assert.equal(shouldJoinCallMedia(otherDevice, true), false);

  const selected = { ...accepted, canJoinMedia: true };
  const authorized = mergeCallRecord(update, selected, selected);
  assert.equal(shouldJoinCallMedia(authorized, false), false);
  assert.equal(shouldJoinCallMedia(authorized, true), true);
  assert.equal(mergeCallRecord(authorized, accepted), authorized);
});

test('M6.13 unavailable or expired calls cannot ring using a delayed duplicate', () => {
  const ringing = call();
  const open = mergeCallRecord(undefined, ringing, ringing);
  assert.equal(activeRingingCall(open, Date.parse(ringing.expiresAt)), null);
  const unavailable = { change: open.change, detail: null, unavailable: true };
  assert.equal(mergeCallRecord(unavailable, ringing, ringing), unavailable);
  assert.equal(activeRingingCall(unavailable, Date.parse(ringing.createdAt)), null);
});
