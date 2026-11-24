import assert from 'node:assert/strict';
import test from 'node:test';
import { mergeDmReadReceipt } from '../src/chat/dmReadReceipts.ts';

test('M6.8 a privacy change hides an older receipt from HTTP or SignalR', () => {
  const seen = { version: 3, peerLastReadSeq: 12 };
  const hidden = { version: 4 };
  const reopened = { version: 5, peerLastReadSeq: 14 };
  assert.equal(mergeDmReadReceipt(seen, hidden), hidden);
  assert.equal(mergeDmReadReceipt(hidden, seen), hidden);
  assert.equal(mergeDmReadReceipt(hidden, reopened), reopened);
  assert.equal(mergeDmReadReceipt(reopened, hidden), reopened);
});

test('M6.8 delayed receipts cannot rewind the peer cursor', () => {
  const seen = { version: 5, peerLastReadSeq: 14 };
  const older = { version: 5, peerLastReadSeq: 10 };
  assert.equal(mergeDmReadReceipt(seen, older), seen);
  assert.equal(mergeDmReadReceipt(older, seen), seen);
});
