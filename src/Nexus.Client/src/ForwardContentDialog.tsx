import { FormEvent, useEffect, useRef, useState } from 'react';
import { encodeContentLink, type ForwardableContent } from './contentLinks';
import { ListPaging, readCursorPage, useCursorList } from './cursorList';
import type { CommunityItemDto } from './communities/types';
import { generateClientMsgId } from './offlineQueue';
import type { OrganizationItem } from './organizationControls';
import { orgApiPath, personalApiPath, protectedRequest } from './scope';
import { Button, Field, Modal } from './ui';

interface Destination { id: string; title: string; kind: string; peer?: { isBlockedByMe?: boolean } | null }
interface Cursor { updatedAt: string; id: string }
const byId = (item: { id: string }) => item.id;

export default function ForwardContentDialog({ source, organizations, onClose, onForwarded }: {
  source: ForwardableContent;
  organizations: OrganizationItem[];
  onClose: () => void;
  onForwarded: () => void;
}) {
  const [scope, setScope] = useState(source.organizationId ?? '');
  const [query, setQuery] = useState('');
  const [destination, setDestination] = useState<Destination | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const input = useRef<HTMLInputElement>(null);
  const request = useRef<AbortController | null>(null);
  const sendIds = useRef(new Map<string, string>());
  const buildPath = (path: string) => scope ? orgApiPath(scope, path) : personalApiPath(path);
  const filter = query.trim();
  const listKey = JSON.stringify([scope, filter]);
  const conversations = useCursorList<Destination, Cursor>(listKey, async (cursor, signal) => {
    const params = new URLSearchParams({ q: filter, limit: '50' });
    if (cursor) {
      params.set('beforeAt', cursor.updatedAt);
      params.set('beforeId', cursor.id);
    }
    const response = await fetch(buildPath(`/api/conversations?${params}`), { signal });
    if (!response.ok) throw new Error('Unable to load conversations.');
    const page = await response.json() as { items: Destination[]; nextBefore: Cursor | null };
    return { items: page.items.filter(item => !item.peer?.isBlockedByMe), next: page.nextBefore };
  }, byId, filter ? 120 : 0);
  const communities = useCursorList<CommunityItemDto>(scope ? listKey : null, async (cursor, signal) => {
    const params = new URLSearchParams({ q: filter, limit: '50' });
    if (cursor) params.set('cursor', cursor);
    return readCursorPage<CommunityItemDto>(await fetch(buildPath(`/api/communities?${params}`), { signal }), 'Unable to load channels.');
  }, byId, filter ? 120 : 0);
  const matches = (text: string) => text.toLowerCase().includes(filter.toLowerCase());
  const channels: Destination[] = communities.items.flatMap(community => {
    const manager = community.myRole === 'Owner' || community.myRole === 'Admin';
    const candidates = community.mainFeedConversationId && (manager || community.mainFeedPostingPolicy !== 'AdminsOnly')
      ? [{ id: community.mainFeedConversationId, name: community.name, postingPolicy: community.mainFeedPostingPolicy, isArchived: false }, ...community.channels]
      : community.channels;
    return candidates.filter(channel => !channel.isArchived && (manager || channel.postingPolicy !== 'AdminsOnly') &&
      (matches(community.name) || matches(channel.name))).map(channel => ({ id: channel.id, title: channel.name, kind: 'channel' }));
  });
  const options = [...conversations.items, ...channels];
  const loading = conversations.loading || communities.loading;

  useEffect(() => () => request.current?.abort(), []);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!destination || request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setPending(true);
    setError('');
    const key = JSON.stringify([scope, destination.id]);
    const clientMsgId = sendIds.current.get(key) ?? generateClientMsgId();
    sendIds.current.set(key, clientMsgId);
    try {
      const response = await protectedRequest(buildPath(`/api/conversations/${destination.id}/forward`), 'POST', {
        sourceLink: encodeContentLink(source), clientMsgId
      }, controller.signal);
      if (controller.signal.aborted) return;
      if (!response.ok) {
        const failure = await response.json().catch(() => null);
        throw new Error(failure?.message || 'You no longer have permission to forward to this destination.');
      }
      onForwarded();
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Unable to forward this item. Try again.');
    } finally {
      if (!controller.signal.aborted) {
        request.current = null;
        setPending(false);
      }
    }
  }

  return (
    <Modal open title={source.kind === 'post' ? 'Forward post' : 'Forward message'} initialFocusRef={input}
      onOpenChange={open => { if (!open && !request.current) onClose(); }}>
      <form className="nx-forward-form" onSubmit={submit}>
        {organizations.length > 0 && <Field label="Destination scope" htmlFor="nx-forward-scope" compact>
          <select id="nx-forward-scope" value={scope} disabled={pending} onChange={event => {
            setScope(event.target.value);
            setDestination(null);
            setError('');
          }}>
            <option value="">Personal Chat</option>
            {organizations.map(org => <option key={org.id} value={org.id}>{org.name}</option>)}
          </select>
        </Field>}
        <Field label="Find a destination" htmlFor="nx-forward-query" compact>
          <input id="nx-forward-query" type="search" maxLength={120} ref={input} value={query} disabled={pending}
            onChange={event => { setQuery(event.target.value); setDestination(null); }} />
        </Field>
        {loading && <p role="status">Finding destinations…</p>}
        {!loading && options.length === 0 && <p role="status">No matching destinations.</p>}
        <div className="nx-forward-destinations" role="group" aria-label="Destinations">
          {options.map(item => <Button key={item.id} variant="ghost" size="sm" disabled={pending} aria-pressed={destination?.id === item.id}
            onClick={() => { setDestination(item); setError(''); }}>
            {item.title}<span className="nx-muted">{item.kind === 'channel' ? 'Channel' : 'Conversation'}</span>
          </Button>)}
        </div>
        <ListPaging loading={conversations.loading} error={conversations.error} hasMore={conversations.next !== null}
          onRetry={() => void conversations.retry()} onMore={() => void conversations.loadMore()} moreLabel="More conversations" />
        <ListPaging loading={communities.loading} error={communities.error} hasMore={communities.next !== null}
          onRetry={() => void communities.retry()} onMore={() => void communities.loadMore()} moreLabel="More channels" />
        {error && <p className="nx-error" role="alert">{error}</p>}
        <div className="nx-inline-actions">
          <Button type="submit" variant="primary" disabled={!destination || pending}>{pending ? 'Forwarding…' : 'Forward'}</Button>
          <Button disabled={pending} onClick={onClose}>Cancel</Button>
        </div>
      </form>
    </Modal>
  );
}
