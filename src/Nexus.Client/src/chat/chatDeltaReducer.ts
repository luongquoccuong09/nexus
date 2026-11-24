import type { ChatDelta, ChatMessageItem, ConversationListItem, MessageMutationDelta, ThreadState } from './types';

export const QUICK_CHAT_REACTIONS = ['👍', '❤️', '😂', '🎉', '👀'] as const;
export const RECALLED_MESSAGE_LABEL = 'This message was recalled';

export function isMessageMutation(delta: ChatDelta): delta is MessageMutationDelta {
  return delta.type === 'message_edited' || delta.type === 'message_recalled';
}

export function applyMessageMutation(message: ChatMessageItem, delta: MessageMutationDelta): ChatMessageItem {
  if (message.id !== delta.messageId || delta.version <= (message.version ?? 1) || message.deletedAt) return message;
  const updated = { ...message, body: delta.body, version: delta.version, editedAt: delta.editedAt, deletedAt: delta.deletedAt };
  return delta.type === 'message_recalled' || delta.deletedAt
    ? { ...updated, body: '', attachments: [], reactions: [], isPinned: false, isSaved: false } : updated;
}

export function formatMessagePreview(body: string) {
  const text = body.trim().replace(/\s+/g, ' ');
  if (text.length > 120) return text.slice(0, 117) + '…';
  return text || '[Attachment]';
}

export function applyPreviewMutation(
  preview: NonNullable<ConversationListItem['lastMessagePreview']>,
  delta: MessageMutationDelta
) {
  if (delta.parentMessageId || preview.seq !== delta.seq || delta.version <= (preview.version ?? 1)) return preview;
  return {
    ...preview,
    messageId: delta.messageId,
    bodyPreview: delta.type === 'message_recalled' || delta.deletedAt ? RECALLED_MESSAGE_LABEL : formatMessagePreview(delta.body),
    version: delta.version
  };
}

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
  const idx = messages.findIndex(m => m.id === delta.messageId);
  if (idx < 0) return messages;
  const target = messages[idx];
  let updated: ChatMessageItem;
  if (delta.type === 'reaction') {
    if (changeSeq <= (target.reactionSnapshotSeq ?? 0)) return messages;
    updated = applyReactionToMessage(target, {
      emoji: delta.emoji,
      reacted: delta.reacted,
      isMine: delta.userId === currentUserId
    });
    if (updated !== target) updated = { ...updated, reactionSnapshotSeq: changeSeq };
  } else if (delta.type === 'thread_reply_created') {
    if (delta.lastReplySeq <= (target.lastReplySeq ?? 0)) return messages;
    updated = {
      ...target,
      replyCount: delta.replyCount,
      lastReplyAt: delta.lastReplyAt,
      lastReplySeq: delta.lastReplySeq
    };
  } else if (delta.type === 'message_pin') {
    if (target.deletedAt || changeSeq <= (target.pinSnapshotSeq ?? target.reactionSnapshotSeq ?? 0)) return messages;
    updated = { ...target, isPinned: delta.pinned, pinSnapshotSeq: changeSeq };
  } else if (delta.type === 'message_save') {
    if (target.deletedAt || delta.version <= (target.savedVersion ?? 0)) return messages;
    updated = { ...target, isSaved: delta.saved, savedVersion: delta.version };
  } else {
    updated = applyMessageMutation(target, delta);
  }
  if (updated === target) return messages;
  const next = messages.slice();
  next[idx] = updated;
  return next;
}

export function applyThreadStateToMessage(message: ChatMessageItem, state: ThreadState): ChatMessageItem {
  if (message.id !== state.parentMessageId || (message.threadStateVersion ?? 0) > state.version) return message;
  return {
    ...message,
    isFollowing: state.isFollowing,
    lastReadReplySeq: state.lastReadReplySeq,
    threadStateVersion: state.version
  };
}

/**
 * Converts a persisted `/changes` event into a typed `ChatDelta` so replay and SignalR
 * share the exact same reducer path.
 */
export function deltaFromConversationEvent(
  eventType: string,
  payload: unknown
): ChatDelta | null {
  if (!payload || typeof payload !== 'object') return null;

  const record = payload as Record<string, unknown>;
  const messageId = typeof record.messageId === 'string' ? record.messageId.trim() : '';
  if (eventType === 'message.pinned' || eventType === 'message.unpinned') {
    if (!messageId) return null;
    return { type: 'message_pin', messageId, pinned: eventType === 'message.pinned',
      parentMessageId: typeof record.parentMessageId === 'string' ? record.parentMessageId : null };
  }
  if (eventType === 'message.thread_reply.created') {
    if (!messageId || typeof record.seq !== 'number' || typeof record.replyCount !== 'number'
      || typeof record.lastReplyAt !== 'string' || typeof record.lastReplySeq !== 'number'
      || !record.message || typeof record.message !== 'object') return null;
    const message = record.message as ChatMessageItem;
    if (!message.id || message.parentMessageId !== messageId) return null;
    return {
      type: 'thread_reply_created', messageId, seq: record.seq, message,
      replyCount: record.replyCount, lastReplyAt: record.lastReplyAt, lastReplySeq: record.lastReplySeq
    };
  }
  if (eventType === 'message.edited' || eventType === 'message.recalled') {
    if (!messageId || typeof record.seq !== 'number' || typeof record.version !== 'number' || typeof record.body !== 'string') return null;
    const type = eventType === 'message.recalled' || record.deletedAt ? 'message_recalled' : 'message_edited';
    return {
      type, messageId, seq: record.seq, version: record.version, body: record.body,
      parentMessageId: typeof record.parentMessageId === 'string' ? record.parentMessageId : null,
      editedAt: typeof record.editedAt === 'string' ? record.editedAt : null,
      deletedAt: typeof record.deletedAt === 'string' ? record.deletedAt : null
    };
  }
  if (eventType !== 'message.reaction.added' && eventType !== 'message.reaction.removed') return null;
  const emoji = typeof record.emoji === 'string' ? record.emoji.trim() : '';
  const userId = typeof record.userId === 'string' ? record.userId.trim() : '';
  if (!messageId || !emoji || !userId) return null;

  const reacted = eventType === 'message.reaction.added';
  const seq = typeof record.seq === 'number' && Number.isFinite(record.seq) ? record.seq : undefined;
  const parentMessageId = typeof record.parentMessageId === 'string' ? record.parentMessageId : undefined;

  return {
    type: 'reaction', messageId, emoji, userId, reacted,
    ...(seq !== undefined ? { seq } : {}),
    ...(parentMessageId ? { parentMessageId } : {})
  };
}
