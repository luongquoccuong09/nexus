import test from 'node:test';
import assert from 'node:assert/strict';
import {
  QUICK_CHAT_REACTIONS,
  applyChatDelta,
  applyReactionToMessage,
  deltaFromConversationEvent
} from '../src/chat/chatDeltaReducer.ts';

function makeMessage(overrides = {}) {
  return {
    id: 'msg-1',
    conversationId: 'conv-1',
    seq: 10,
    senderId: 'user-2',
    senderDisplayName: 'Alex Rivera',
    clientMsgId: 'c-10',
    body: 'Hello team',
    createdAt: '2026-11-24T12:00:00.000Z',
    ...overrides
  };
}

test('M6.1: QUICK_CHAT_REACTIONS exposes the five canonical quick emojis in order', () => {
  assert.deepEqual([...QUICK_CHAT_REACTIONS], ['👍', '❤️', '😂', '🎉', '👀']);
});

test('M6.1: applyReactionToMessage adds and removes local reactions cleanly', () => {
  const initial = makeMessage();

  // Add first reaction when reactions array is undefined
  const added = applyReactionToMessage(initial, { emoji: '👍', reacted: true, isMine: true });
  assert.deepEqual(added.reactions, [{ emoji: '👍', count: 1, reactedByMe: true }]);
  assert.equal(initial.reactions, undefined);

  // Add second distinct reaction while preserving pill order
  const addedSecond = applyReactionToMessage(added, { emoji: '👩‍💻', reacted: true, isMine: true });
  assert.deepEqual(addedSecond.reactions, [
    { emoji: '👍', count: 1, reactedByMe: true },
    { emoji: '👩‍💻', count: 1, reactedByMe: true }
  ]);

  // Remove count-to-zero deletes pill cleanly
  const removedFirst = applyReactionToMessage(addedSecond, { emoji: '👍', reacted: false, isMine: true });
  assert.deepEqual(removedFirst.reactions, [{ emoji: '👩‍💻', count: 1, reactedByMe: true }]);

  const removedAll = applyReactionToMessage(removedFirst, { emoji: '👩‍💻', reacted: false, isMine: true });
  assert.deepEqual(removedAll.reactions, []);
});

test('M6.1: applyReactionToMessage deduplicates own SignalR echo without double counting or underflow', () => {
  const msgWithMine = makeMessage({
    reactions: [
      { emoji: '❤️', count: 2, reactedByMe: true },
      { emoji: '🎉', count: 1, reactedByMe: false }
    ]
  });

  // Own echo for an already-applied reaction is a strict no-op
  const afterAddEcho = applyReactionToMessage(msgWithMine, { emoji: '❤️', reacted: true, isMine: true });
  assert.equal(afterAddEcho, msgWithMine);
  assert.deepEqual(afterAddEcho.reactions, [
    { emoji: '❤️', count: 2, reactedByMe: true },
    { emoji: '🎉', count: 1, reactedByMe: false }
  ]);

  // Own remove for an un-reacted pill (whether missing or reacted by others only) does not decrement
  const afterMissingRemoveEcho = applyReactionToMessage(msgWithMine, { emoji: '👀', reacted: false, isMine: true });
  assert.equal(afterMissingRemoveEcho, msgWithMine);

  const afterOtherOnlyRemoveEcho = applyReactionToMessage(msgWithMine, { emoji: '🎉', reacted: false, isMine: true });
  assert.equal(afterOtherOnlyRemoveEcho, msgWithMine);
  assert.deepEqual(afterOtherOnlyRemoveEcho.reactions, [
    { emoji: '❤️', count: 2, reactedByMe: true },
    { emoji: '🎉', count: 1, reactedByMe: false }
  ]);
});

test('M6.1: applyReactionToMessage applies remote user reactions and removes pills at zero', () => {
  const base = makeMessage({
    reactions: [{ emoji: '👍', count: 1, reactedByMe: true }]
  });

  // Remote user adds same emoji -> count increments while keeping reactedByMe true
  const withRemoteAdd = applyReactionToMessage(base, { emoji: '👍', reacted: true, isMine: false });
  assert.deepEqual(withRemoteAdd.reactions, [{ emoji: '👍', count: 2, reactedByMe: true }]);

  // Remote user adds a new emoji -> reactedByMe stays false
  const withRemoteNew = applyReactionToMessage(withRemoteAdd, { emoji: '😂', reacted: true, isMine: false });
  assert.deepEqual(withRemoteNew.reactions, [
    { emoji: '👍', count: 2, reactedByMe: true },
    { emoji: '😂', count: 1, reactedByMe: false }
  ]);

  // Remote user removes existing emoji -> count drops cleanly while reactedByMe preserved
  const afterRemoteRemoveOne = applyReactionToMessage(withRemoteNew, { emoji: '👍', reacted: false, isMine: false });
  assert.deepEqual(afterRemoteRemoveOne.reactions, [
    { emoji: '👍', count: 1, reactedByMe: true },
    { emoji: '😂', count: 1, reactedByMe: false }
  ]);

  // Remote user removes sole reaction -> pill removed cleanly
  const afterRemoteRemoveZero = applyReactionToMessage(afterRemoteRemoveOne, { emoji: '😂', reacted: false, isMine: false });
  assert.deepEqual(afterRemoteRemoveZero.reactions, [{ emoji: '👍', count: 1, reactedByMe: true }]);

  // Remote remove on missing emoji does not underflow
  const afterMissingRemove = applyReactionToMessage(afterRemoteRemoveZero, { emoji: '👀', reacted: false, isMine: false });
  assert.equal(afterMissingRemove, afterRemoteRemoveZero);
});

test('M6.1: applyChatDelta targets only matching message, handles own echo, and ignores unloaded messages', () => {
  const msg1 = makeMessage({ id: 'msg-1', seq: 1, reactions: [] });
  const msg2 = makeMessage({ id: 'msg-2', seq: 2, reactions: [{ emoji: '👍', count: 1, reactedByMe: true }] });
  const list = [msg1, msg2];

  // Remote add on msg-1
  const step1 = applyChatDelta(
    list,
    { type: 'reaction', messageId: 'msg-1', seq: 1, emoji: '🎉', userId: 'user-other', reacted: true },
    'user-me', 1
  );
  assert.notEqual(step1, list);
  assert.equal(step1[1], msg2);
  assert.deepEqual(step1[0].reactions, [{ emoji: '🎉', count: 1, reactedByMe: false }]);

  // Own echo on msg-2 returns identical array reference
  const step2 = applyChatDelta(
    step1,
    { type: 'reaction', messageId: 'msg-2', seq: 2, emoji: '👍', userId: 'user-me', reacted: true },
    'user-me', 2
  );
  assert.equal(step2, step1);

  // Delta for an older/unloaded message returns identical array reference
  const step3 = applyChatDelta(
    step2,
    { type: 'reaction', messageId: 'msg-unloaded', seq: 0, emoji: '❤️', userId: 'user-other', reacted: true },
    'user-me', 3
  );
  assert.equal(step3, step2);
});

test('M6.1: replay skips a peer reaction already present in a loaded history page', () => {
  const list = [makeMessage({
    reactionSnapshotSeq: 12,
    reactions: [{ emoji: '👍', count: 1, reactedByMe: false }]
  })];
  const delta = { type: 'reaction', messageId: 'msg-1', seq: 10, emoji: '👍', userId: 'peer', reacted: true };
  assert.equal(applyChatDelta(list, delta, 'me', 12), list);
  assert.deepEqual(applyChatDelta(list, delta, 'me', 13)[0].reactions, [
    { emoji: '👍', count: 2, reactedByMe: false }
  ]);
});

test('M6.1: deltaFromConversationEvent converts reaction /changes events and ignores non-reaction events', () => {
  const addedDelta = deltaFromConversationEvent('message.reaction.added', {
    messageId: 'msg-1',
    seq: 5,
    emoji: '👍',
    userId: 'user-2',
    reacted: true
  });
  assert.deepEqual(addedDelta, {
    type: 'reaction',
    messageId: 'msg-1',
    seq: 5,
    emoji: '👍',
    userId: 'user-2',
    reacted: true
  });

  const removedDelta = deltaFromConversationEvent('message.reaction.removed', {
    messageId: 'msg-1',
    seq: 5,
    emoji: '👍',
    userId: 'user-2',
    reacted: false
  });
  assert.deepEqual(removedDelta, {
    type: 'reaction',
    messageId: 'msg-1',
    seq: 5,
    emoji: '👍',
    userId: 'user-2',
    reacted: false
  });

  // Missing optional seq still succeeds
  const withoutSeq = deltaFromConversationEvent('message.reaction.added', {
    messageId: 'msg-2',
    emoji: '👀',
    userId: 'user-3'
  });
  assert.deepEqual(withoutSeq, {
    type: 'reaction',
    messageId: 'msg-2',
    emoji: '👀',
    userId: 'user-3',
    reacted: true
  });

  // Non-reaction events and malformed payloads return null
  assert.equal(deltaFromConversationEvent('message.created', { id: 'msg-1' }), null);
  assert.equal(deltaFromConversationEvent('message.reaction.added', null), null);
  assert.equal(deltaFromConversationEvent('message.reaction.added', { messageId: '', emoji: '👍', userId: 'u1' }), null);
  assert.equal(deltaFromConversationEvent('message.reaction.removed', { messageId: 'm1', emoji: '   ', userId: 'u1' }), null);

  // Replay through applyChatDelta produces identical result to live delta
  const replayed = [addedDelta, removedDelta].reduce(
    (acc, d, idx) => (d ? applyChatDelta(acc, d, 'user-1', idx + 1) : acc),
    [makeMessage({ id: 'msg-1', seq: 5, reactions: [] })]
  );
  assert.deepEqual(replayed[0].reactions, []);
});
