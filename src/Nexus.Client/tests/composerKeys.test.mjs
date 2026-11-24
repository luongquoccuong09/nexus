import assert from 'node:assert/strict';
import test from 'node:test';
import { shouldSendOnEnter } from '../src/composerKeys.ts';

const enter = { key: 'Enter', shiftKey: false, isComposing: false, keyCode: 13 };

test('plain Enter sends and Shift+Enter stays in the composer', () => {
  assert.equal(shouldSendOnEnter(enter, false), true);
  assert.equal(shouldSendOnEnter({ ...enter, shiftKey: true }, false), false);
  assert.equal(shouldSendOnEnter({ ...enter, key: 'a' }, false), false);
});

test('Enter leaves native, tracked, and legacy IME events to the composer', () => {
  assert.equal(shouldSendOnEnter({ ...enter, isComposing: true }, false), false);
  assert.equal(shouldSendOnEnter(enter, true), false);
  assert.equal(shouldSendOnEnter({ ...enter, keyCode: 229 }, false), false);
  assert.equal(shouldSendOnEnter(enter, false), true);
});
