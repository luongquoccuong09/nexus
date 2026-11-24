import type { ChatDelta, ChatMessageItem } from './types';

export const QUICK_CHAT_REACTIONS = ['👍', '❤️', '😂', '🎉', '👀'] as const;

export interface ReactionUpdateInput {
  emoji: string;
  reacted: boolean;
  isMine: boolean;
}

/**
 * Applies a reaction add/remove transition to a single chat message immutably.
 * Idempotent for own reactions so optimistic toggles never double-count on SignalR echo.
 */
export function applyReactionToMessage(
  message: ChatMessageItem,
  input: ReactionUpdateInput
): ChatMessageItem {
  const emoji = input.emoji.trim();
  if (!emoji || message.deletedAt) return message;

  const current = message.reactions ?? [];
  const index = current.findIndex(r => r.emoji === emoji);
  const existing = current[index];
  const wasMine = existing?.reactedByMe ?? false;
  if ((input.isMine && wasMine === input.reacted) || (!existing && !input.reacted)) return message;

  const count = (existing?.count ?? 0) + (input.reacted ? 1 : -1);
  const reactions = current.slice();
  if (count <= 0) {
    reactions.splice(index, 1);
  } else {
    const entry = { emoji, count, reactedByMe: input.isMine ? input.reacted : wasMine };
    if (index < 0) reactions.push(entry);
    else reactions[index] = entry;
  }
  return { ...message, reactions };
}

/**
 * Applies a live SignalR or replayed conversation delta to a loaded message list.
 */
export function applyChatDelta(
  messages: ChatMessageItem[],
  delta: ChatDelta,
  currentUserId: string,
  changeSeq: number
): ChatMessageItem[] {
  if (delta.type === 'reaction') {
    const idx = messages.findIndex(m => m.id === delta.messageId);
    if (idx < 0) return messages;
    const target = messages[idx];
    if (changeSeq <= (target.reactionSnapshotSeq ?? 0)) return messages;
    const updated = applyReactionToMessage(target, {
      emoji: delta.emoji,
      reacted: delta.reacted,
      isMine: delta.userId === currentUserId
    });
    if (updated === target) return messages;
    const next = messages.slice();
    next[idx] = updated;
    return next;
  }
  return messages;
}

/**
 * Converts a persisted `/changes` event into a typed `ChatDelta` so replay and SignalR
 * share the exact same reducer path.
 */
export function deltaFromConversationEvent(
  eventType: string,
  payload: unknown
): ChatDelta | null {
  if (eventType !== 'message.reaction.added' && eventType !== 'message.reaction.removed') {
    return null;
  }
  if (!payload || typeof payload !== 'object') return null;

  const record = payload as Record<string, unknown>;
  const messageId = typeof record.messageId === 'string' ? record.messageId.trim() : '';
  const emoji = typeof record.emoji === 'string' ? record.emoji.trim() : '';
  const userId = typeof record.userId === 'string' ? record.userId.trim() : '';
  if (!messageId || !emoji || !userId) return null;

  const reacted = eventType === 'message.reaction.added';
  const seq = typeof record.seq === 'number' && Number.isFinite(record.seq) ? record.seq : undefined;

  return seq !== undefined
    ? { type: 'reaction', messageId, seq, emoji, userId, reacted }
    : { type: 'reaction', messageId, emoji, userId, reacted };
}
