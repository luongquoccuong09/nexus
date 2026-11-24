import { FormEvent, useRef, useState } from 'react';
import { ListPaging, readCursorPage, useCursorList } from './cursorList';
import type { OrganizationItem } from './organizationControls';
import { orgApiPath, personalApiPath } from './scope';
import { Button, Field, Modal } from './ui';

interface SearchResult {
  id: string;
  kind: 'message' | 'post';
  conversationTitle: string;
  authorName: string;
  createdAt: string;
  title: string | null;
  preview: string;
  hasAttachment: boolean;
  sourceLink: string;
}

interface SearchConversation { id: string; title: string }
interface SearchCommunity {
  id: string;
  name: string;
  mainFeedConversationId?: string | null;
  channels?: { id: string; name: string }[];
}

const resultKey = (item: SearchResult) => item.kind + ':' + item.id;
const optionKey = (item: { id: string }) => item.id;

function dayBoundary(value: string, nextDay = false) {
  if (!value) return undefined;
  const date = new Date(value + 'T00:00:00');
  if (nextDay) date.setDate(date.getDate() + 1);
  return date.toISOString();
}

export default function MessageSearchDialog({ organizations, recalledIds, onClose, onOpenMessage }: {
  organizations: OrganizationItem[];
  recalledIds: ReadonlySet<string>;
  onClose: () => void;
  onOpenMessage: (token: string) => void;
}) {
  const [workspace, setWorkspace] = useState('');
  const [conversation, setConversation] = useState('');
  const [formError, setFormError] = useState('');
  const [submittedPath, setSubmittedPath] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const buildPath = (path: string) => workspace ? orgApiPath(workspace, path) : personalApiPath(path);

  const conversations = useCursorList<SearchConversation>(buildPath('/api/conversations'), async (cursor, signal) => {
    const query = new URLSearchParams({ limit: '50' });
    if (cursor) {
      const before = JSON.parse(cursor) as { updatedAt: string; id: string };
      query.set('beforeAt', before.updatedAt);
      query.set('beforeId', before.id);
    }
    const response = await fetch(buildPath(`/api/conversations?${query}`), { signal });
    if (!response.ok) throw new Error('Unable to load conversations.');
    const page = await response.json() as { items: SearchConversation[]; nextBefore: object | null };
    return { items: page.items, next: page.nextBefore ? JSON.stringify(page.nextBefore) : null };
  }, optionKey);
  const communities = useCursorList<SearchCommunity>(workspace ? buildPath('/api/communities') : null, async (cursor, signal) => {
    const query = new URLSearchParams({ limit: '50' });
    if (cursor) query.set('cursor', cursor);
    return readCursorPage<SearchCommunity>(await fetch(buildPath(`/api/communities?${query}`), { signal }), 'Unable to load channels.');
  }, optionKey);
  const results = useCursorList<SearchResult>(submittedPath, async (cursor, signal) => {
    if (!submittedPath) return { items: [], next: null };
    const suffix = cursor ? '&cursor=' + encodeURIComponent(cursor) : '';
    return readCursorPage<SearchResult>(await fetch(submittedPath + suffix, { signal }), 'Unable to search messages.');
  }, resultKey);
  const visibleResults = results.items.filter(item => item.kind !== 'message' || !recalledIds.has(item.id));

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError('');
    const fields = new FormData(event.currentTarget);
    const from = dayBoundary(String(fields.get('from') ?? ''));
    const to = dayBoundary(String(fields.get('to') ?? ''), true);
    if (from && to && from >= to) {
      setFormError('Choose an end date on or after the start date.');
      return;
    }
    const query = new URLSearchParams({ limit: '50' });
    for (const field of ['q', 'sender', 'hasAttachment']) {
      const value = String(fields.get(field) ?? '').trim();
      if (value) query.set(field, value);
    }
    if (conversation) query.set('conversationId', conversation);
    if (from) query.set('from', from);
    if (to) query.set('to', to);
    const path = buildPath(`/api/messages/search?${query}`);
    if (path === submittedPath) void results.refresh();
    else setSubmittedPath(path);
  }

  return (
    <Modal open title="Search messages" initialFocusRef={input} onOpenChange={open => { if (!open) onClose(); }}>
      <form className="nx-message-search-form" onSubmit={submit}>
        <Field label="Search text" htmlFor="nx-search-text" compact>
          <input id="nx-search-text" name="q" type="search" maxLength={120} ref={input} />
        </Field>
        {organizations.length > 0 && <Field label="Scope" htmlFor="nx-search-scope" compact>
          <select id="nx-search-scope" value={workspace} onChange={event => {
            setWorkspace(event.target.value);
            setConversation('');
            setSubmittedPath(null);
          }}>
            <option value="">Personal Chat</option>
            {organizations.map(org => <option key={org.id} value={org.id}>{org.name}</option>)}
          </select>
        </Field>}
        <Field label="Conversation or channel" htmlFor="nx-search-conversation" compact>
          <select id="nx-search-conversation" value={conversation} onChange={event => setConversation(event.target.value)}>
            <option value="">All conversations</option>
            {conversations.items.map(item => <option key={item.id} value={item.id}>{item.title}</option>)}
            {communities.items.map(item => <optgroup key={item.id} label={item.name}>
              {item.mainFeedConversationId && <option value={item.mainFeedConversationId}>{item.name}</option>}
              {item.channels?.map(channel => <option key={channel.id} value={channel.id}>{channel.name}</option>)}
            </optgroup>)}
          </select>
        </Field>
        <ListPaging loading={conversations.loading} error={conversations.error} hasMore={conversations.next !== null}
          onRetry={() => void conversations.retry()} onMore={() => void conversations.loadMore()} moreLabel="More conversations" />
        <ListPaging loading={communities.loading} error={communities.error} hasMore={communities.next !== null}
          onRetry={() => void communities.retry()} onMore={() => void communities.loadMore()} moreLabel="More channels" />
        <Field label="Sender" htmlFor="nx-search-sender" compact>
          <input id="nx-search-sender" name="sender" placeholder="Name or username" maxLength={120} />
        </Field>
        <div className="nx-message-search-dates">
          <Field label="From" htmlFor="nx-search-from" compact>
            <input id="nx-search-from" name="from" type="date" />
          </Field>
          <Field label="Through" htmlFor="nx-search-to" compact>
            <input id="nx-search-to" name="to" type="date" />
          </Field>
        </div>
        <Field label="Attachments" htmlFor="nx-search-attachments" compact>
          <select id="nx-search-attachments" name="hasAttachment">
            <option value="">Any</option>
            <option value="true">With attachments</option>
            <option value="false">Without attachments</option>
          </select>
        </Field>
        {formError && <p className="nx-error" role="alert">{formError}</p>}
        <Button type="submit" variant="primary" size="sm">Search</Button>
      </form>
      {results.loading && <p role="status">Searching…</p>}
      {submittedPath && !results.loading && !results.error && !visibleResults.length && <p>No matching messages.</p>}
      <ul className="nx-bookmarked-messages">
        {visibleResults.map(item => (
          <li key={resultKey(item)}>
            <strong>{item.conversationTitle}</strong>
            <p className="nx-muted">{item.authorName} · <time dateTime={item.createdAt}>{new Date(item.createdAt).toLocaleDateString()}</time></p>
            {item.title && <p><strong>{item.title}</strong></p>}
            <p>{item.preview}</p>
            <Button size="sm" onClick={() => { onClose(); onOpenMessage(item.sourceLink); }}>
              {item.kind === 'post' ? 'Go to post' : 'Go to message'}
            </Button>
          </li>
        ))}
      </ul>
      <ListPaging loading={results.loading} error={results.error} hasMore={results.next !== null}
        onRetry={() => void results.retry()} onMore={() => void results.loadMore()} />
    </Modal>
  );
}
