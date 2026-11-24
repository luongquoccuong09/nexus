import { useCallback, useEffect, useRef, useState } from 'react';
import { chatFetch } from './scope';
import { Button, Card, ToastBar } from './ui';

interface BlockedUser {
  blockedUserId: string;
  createdAt: string;
  displayName: string | null;
  username: string | null;
}

interface BlockCursor {
  createdAt: string;
  id: string;
}

export function BlockedUsersSettings({ onChanged }: { onChanged: () => void }) {
  const [items, setItems] = useState<BlockedUser[]>([]);
  const [next, setNext] = useState<BlockCursor | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<'initial' | 'older' | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [notice, setNotice] = useState('');
  const [removeError, setRemoveError] = useState('');
  const loadingRequest = useRef<AbortController | null>(null);
  const removeRequest = useRef<AbortController | null>(null);
  const listHeading = useRef<HTMLParagraphElement>(null);

  const load = useCallback(async (cursor: BlockCursor | null = null) => {
    loadingRequest.current?.abort();
    const controller = new AbortController();
    loadingRequest.current = controller;
    setLoading(true);
    setLoadError(null);
    try {
      const query = new URLSearchParams({ limit: '50' });
      if (cursor) {
        query.set('beforeAt', cursor.createdAt);
        query.set('beforeId', cursor.id);
      }
      const response = await chatFetch(`/api/people/blocks?${query}`, { signal: controller.signal });
      if (!response.ok) throw new Error('Unable to load blocked users.');
      const page: { items: BlockedUser[]; nextBefore: BlockCursor | null } = await response.json();
      if (controller.signal.aborted) return;
      setItems(previous => cursor
        ? [...new Map([...previous, ...page.items].map(item => [item.blockedUserId, item])).values()]
        : page.items);
      setNext(page.nextBefore);
    } catch {
      if (!controller.signal.aborted) setLoadError(cursor ? 'older' : 'initial');
    } finally {
      if (!controller.signal.aborted) setLoading(false);
      if (loadingRequest.current === controller) loadingRequest.current = null;
    }
  }, []);

  useEffect(() => {
    void load();
    return () => {
      loadingRequest.current?.abort();
      removeRequest.current?.abort();
    };
  }, [load]);

  async function unblock(item: BlockedUser) {
    if (removeRequest.current) return;
    const controller = new AbortController();
    removeRequest.current = controller;
    setRemoving(item.blockedUserId);
    setNotice('');
    setRemoveError('');
    try {
      const response = await chatFetch(`/api/people/blocks/${item.blockedUserId}`, { method: 'DELETE', signal: controller.signal });
      if (!response.ok) throw new Error('Unable to unblock user.');
      if (controller.signal.aborted) return;
      setItems(previous => previous.filter(row => row.blockedUserId !== item.blockedUserId));
      setNotice(item.displayName ? `Unblocked ${item.displayName}.` : 'Unblocked account.');
      listHeading.current?.focus({ preventScroll: true });
      onChanged();
    } catch {
      if (!controller.signal.aborted) setRemoveError('Unable to unblock this user. Try again.');
    } finally {
      if (removeRequest.current === controller) removeRequest.current = null;
      if (!controller.signal.aborted) setRemoving(null);
    }
  }

  return (
    <Card title="Blocked users">
      <p className="nx-muted" ref={listHeading} tabIndex={-1}>Blocked users cannot exchange direct messages with you.</p>
      {notice && <ToastBar variant="ok" message={notice} />}
      {removeError && <ToastBar variant="error" message={removeError} />}
      {loadError && <ToastBar variant="error" message={loadError === 'older' ? 'Unable to load more blocked users.' : 'Unable to load blocked users.'} />}
      {loading && <p className="nx-muted" role="status">Loading blocked users…</p>}
      {!loading && !loadError && items.length === 0 && !next && <p className="nx-muted">You have no blocked users.</p>}
      {items.length > 0 && (
        <ul className="nx-blocked-users" aria-label="Users you blocked">
          {items.map(item => {
            const name = item.displayName || 'Unavailable account';
            return (
              <li className="nx-row" key={item.blockedUserId}>
                <div className="nx-user-name-wrap">
                  <strong>{name}</strong>
                  <div className="nx-muted">{item.username ? `@${item.username}` : `Account ${item.blockedUserId.slice(-8)}`}</div>
                </div>
                <Button size="sm" disabled={loading || removing !== null} aria-label={`Unblock ${item.username ? `@${item.username}` : name}`}
                  onClick={() => void unblock(item)}>
                  {removing === item.blockedUserId ? 'Unblocking…' : 'Unblock'}
                </Button>
              </li>
            );
          })}
        </ul>
      )}
      {loadError ? (
        <Button size="sm" disabled={removing !== null} onClick={() => void load(loadError === 'initial' ? null : next)}>Retry</Button>
      ) : next && (
        <Button size="sm" disabled={loading || removing !== null} onClick={() => void load(next)}>Load more</Button>
      )}
    </Card>
  );
}
