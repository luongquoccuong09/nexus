import assert from 'node:assert/strict';
import test from 'node:test';
import { isQuickSwitcherShortcut, stepQuickSwitcherSelection } from '../src/quickSwitcherKeys.ts';

const ctrlK = { key: 'k', ctrlKey: true, metaKey: false, altKey: false, shiftKey: false,
  repeat: false, defaultPrevented: false, isComposing: false, keyCode: 75 };

test('M6.7 opens with Ctrl or Cmd and respects composer shortcuts and IME', () => {
  assert.equal(isQuickSwitcherShortcut(ctrlK, false), true);
  assert.equal(isQuickSwitcherShortcut({ ...ctrlK, ctrlKey: false, metaKey: true, key: 'K' }, false), true);
  assert.equal(isQuickSwitcherShortcut({ ...ctrlK, defaultPrevented: true }, false), false);
  assert.equal(isQuickSwitcherShortcut({ ...ctrlK, isComposing: true }, false), false);
  assert.equal(isQuickSwitcherShortcut({ ...ctrlK, keyCode: 229 }, false), false);
  assert.equal(isQuickSwitcherShortcut(ctrlK, true), false);
  assert.equal(isQuickSwitcherShortcut({ ...ctrlK, repeat: true }, false), false);
  assert.equal(isQuickSwitcherShortcut({ ...ctrlK, altKey: true }, false), false);
});

test('M6.7 Up and Down wrap between selectable results', () => {
  assert.equal(stepQuickSwitcherSelection(-1, 0, 1), -1);
  assert.equal(stepQuickSwitcherSelection(-1, 3, 1), 0);
  assert.equal(stepQuickSwitcherSelection(-1, 3, -1), 2);
  assert.equal(stepQuickSwitcherSelection(2, 3, 1), 0);
  assert.equal(stepQuickSwitcherSelection(0, 3, -1), 2);
});
