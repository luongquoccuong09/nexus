import assert from 'node:assert/strict';
import test from 'node:test';
import { callBackDetails, callHistoryPath, describeCallDuration } from '../src/calls/callHistory.ts';

test('call history keeps each filter and opaque continuation within a fifty-row request', () => {
  const first = new URL(callHistoryPath('incoming', null), 'https://nexus.example');
  assert.equal(first.pathname, '/api/calls/history');
  assert.equal(first.searchParams.get('filter'), 'incoming');
  assert.equal(first.searchParams.get('limit'), '50');
  assert.equal(first.searchParams.has('cursor'), false);

  const later = new URL(callHistoryPath('missed', 'cursor+/='), 'https://nexus.example');
  assert.equal(later.searchParams.get('filter'), 'missed');
  assert.equal(later.searchParams.get('limit'), '50');
  assert.equal(later.searchParams.get('cursor'), 'cursor+/=');
});

test('call back preserves authorized scope and call kind and respects current busy state', () => {
  const eligible = {
    organizationId: 'organization-id', conversationId: 'conversation-id',
    counterpartName: 'Call participant', kind: 'video', canCallBack: true
  };
  assert.deepEqual(callBackDetails(eligible, true), {
    target: { organizationId: 'organization-id', conversationId: 'conversation-id' },
    peerName: 'Call participant', kind: 'Video'
  });
  assert.deepEqual(callBackDetails({ ...eligible, kind: 'voice', organizationId: null }, true), {
    target: { organizationId: null, conversationId: 'conversation-id' },
    peerName: 'Call participant', kind: 'Voice'
  });
  assert.equal(callBackDetails({ ...eligible, canCallBack: false }, true), null);
  assert.equal(callBackDetails(eligible, false), null);
});

test('call history distinguishes a known zero duration from an unknown duration', () => {
  assert.equal(describeCallDuration(null), null);
  assert.equal(describeCallDuration(0), '0 sec');
  assert.equal(describeCallDuration(12), '12 sec');
  assert.equal(describeCallDuration(120), '2 min');
  assert.equal(describeCallDuration(3725), '1 hr 2 min 5 sec');
});
