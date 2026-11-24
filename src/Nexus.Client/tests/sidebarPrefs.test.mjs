import test from 'node:test';
import assert from 'node:assert/strict';
import { moveIdByOffset, reorderIdList } from '../src/sidebarOrder.ts';

test('M6.26: reorders sidebar item IDs for pointer drag-and-drop and keyboard moves', () => {
  const ids = ['a', 'b', 'c', 'd'];
  assert.deepEqual(reorderIdList(ids, 'c', 'a'), ['c', 'a', 'b', 'd']);
  assert.deepEqual(reorderIdList(ids, 'a', 'd'), ['b', 'c', 'd', 'a']);
  assert.deepEqual(moveIdByOffset(ids, 'b', -1), ['b', 'a', 'c', 'd']);
  assert.deepEqual(moveIdByOffset(ids, 'b', 1), ['a', 'c', 'b', 'd']);
  assert.deepEqual(moveIdByOffset(ids, 'a', -1), ids);
  assert.deepEqual(moveIdByOffset(ids, 'd', 1), ids);
});
