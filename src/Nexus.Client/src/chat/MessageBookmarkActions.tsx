import { Bookmark, Pin } from 'lucide-react';
import { Button } from '../ui';
import type { ChatMessageItem } from './types';

export type BookmarkKind = 'pin' | 'save';
export type PendingBookmark = { kind: BookmarkKind; value: boolean };
export type ToggleBookmark = (message: ChatMessageItem, kind: BookmarkKind) => void;

export function MessageBookmarkActions({ message, canPin, pending, onToggle }: {
  message: ChatMessageItem;
  canPin: boolean;
  pending?: PendingBookmark;
  onToggle: ToggleBookmark;
}) {
  const pinned = pending?.kind === 'pin' ? pending.value : !!message.isPinned;
  const saved = pending?.kind === 'save' ? pending.value : !!message.isSaved;
  const pinLabel = pinned ? 'Unpin message' : 'Pin message';
  const saveLabel = saved ? 'Remove saved message' : 'Save message';

  return (
    <>
      {canPin && <Button variant="ghost" size="sm" iconOnly className="nx-msg-bookmark-action"
        aria-label={pinLabel} title={pinLabel} aria-pressed={pinned} disabled={!!pending}
        onClick={() => onToggle(message, 'pin')}>
        <Pin size={13} aria-hidden="true" />
      </Button>}
      <Button variant="ghost" size="sm" iconOnly className="nx-msg-bookmark-action"
        aria-label={saveLabel} title={saveLabel} aria-pressed={saved} disabled={!!pending}
        onClick={() => onToggle(message, 'save')}>
        <Bookmark size={13} aria-hidden="true" />
      </Button>
    </>
  );
}
