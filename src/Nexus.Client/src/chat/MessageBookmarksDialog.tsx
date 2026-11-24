import { useEffect, useRef } from 'react';
import { ListPaging, readCursorPage, useCursorList } from '../cursorList';
import { personalApiPath } from '../scope';
import { Button, Modal } from '../ui';

interface BookmarkedMessage {
  messageId: string;
  conversationId: string;
  conversationTitle: string;
  bodyPreview: string;
  sourceLink: string;
}

export default function MessageBookmarksDialog({ conversationId, revision, hiddenIds, hiddenConversations, onClose, onOpenMessage }: {
  conversationId?: string;
  revision: number;
  hiddenIds: ReadonlySet<string>;
  hiddenConversations: ReadonlySet<string>;
  onClose: () => void;
  onOpenMessage: (token: string) => void;
}) {
  const path = conversationId ? `/api/conversations/${conversationId}/pins` : '/api/saved-messages';
  const page = useCursorList<BookmarkedMessage>(path, async (cursor, signal) => {
    const query = new URLSearchParams({ limit: '50' });
    if (cursor) {
      const [beforeAt, beforeId] = cursor.split('|');
      query.set('beforeAt', beforeAt);
      query.set('beforeId', beforeId);
    }
    return readCursorPage<BookmarkedMessage>(await fetch(personalApiPath(`${path}?${query}`), { signal }), 'Unable to load messages.');
  }, item => item.messageId);
  const previousRevision = useRef(revision);
  useEffect(() => {
    if (previousRevision.current === revision) return;
    previousRevision.current = revision;
    void page.refresh();
  }, [revision, page.refresh]);
  const items = page.items.filter(item => !hiddenIds.has(item.messageId) && !hiddenConversations.has(item.conversationId));

  return (
    <Modal open title={conversationId ? 'Pinned messages' : 'Saved messages'} onOpenChange={open => { if (!open) onClose(); }}>
      {page.loading && items.length === 0 && <p role="status">Loading messages…</p>}
      {!page.loading && !page.error && items.length === 0 && <p className="nx-muted">
        {conversationId ? 'No pinned messages.' : 'No saved messages.'}
      </p>}
      <ul className="nx-bookmarked-messages">
        {items.map(item => (
          <li key={item.messageId}>
            <strong>{item.conversationTitle}</strong>
            <p>{item.bodyPreview}</p>
            <Button size="sm" onClick={() => {
              onClose();
              onOpenMessage(item.sourceLink);
            }}>Go to message</Button>
          </li>
        ))}
      </ul>
      <ListPaging loading={page.loading} error={page.error} hasMore={page.next !== null}
        onRetry={() => void page.retry()} onMore={() => void page.loadMore()} />
    </Modal>
  );
}
