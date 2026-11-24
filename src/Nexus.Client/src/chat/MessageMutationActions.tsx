import { useEffect, useState } from 'react';
import { Pencil, Trash2 } from 'lucide-react';
import { Button } from '../ui';
import type { ChatMessageItem } from './types';

export type MessageMutationKind = 'edit' | 'recall';
export type OpenMessageMutation = (kind: MessageMutationKind, message: ChatMessageItem, trigger: HTMLButtonElement) => void;
export const MESSAGE_CHANGE_WINDOW_MS = 15 * 60 * 1000;

export function MessageMutationActions({
  message, currentUserId, blocked, pending, onOpen
}: {
  message: ChatMessageItem;
  currentUserId: string;
  blocked: boolean;
  pending: boolean;
  onOpen: OpenMessageMutation;
}) {
  const [, tick] = useState(0);
  const deadline = Date.parse(message.createdAt) + MESSAGE_CHANGE_WINDOW_MS;
  const eligible = message.senderId === currentUserId && !message.pending && !message.deletedAt && !blocked;
  useEffect(() => {
    const remaining = deadline - Date.now();
    if (!eligible || !(remaining > 0)) return;
    const timer = window.setTimeout(() => tick(value => value + 1), remaining + 10);
    return () => window.clearTimeout(timer);
  }, [deadline, eligible]);
  if (!eligible || !(Date.now() < deadline)) return null;

  return (
    <>
      <Button variant="ghost" size="sm" iconOnly className="nx-msg-mutation-action"
        aria-label="Edit message" title="Edit message" disabled={pending}
        onClick={event => onOpen('edit', message, event.currentTarget)}>
        <Pencil size={13} aria-hidden="true" />
      </Button>
      <Button variant="ghost" size="sm" iconOnly className="nx-msg-mutation-action"
        aria-label="Recall message" title="Recall message" disabled={pending}
        onClick={event => onOpen('recall', message, event.currentTarget)}>
        <Trash2 size={13} aria-hidden="true" />
      </Button>
    </>
  );
}
