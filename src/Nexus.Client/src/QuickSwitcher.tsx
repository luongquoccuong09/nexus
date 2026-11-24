import { KeyboardEvent, useEffect, useRef, useState } from 'react';
import { ListPaging, readCursorPage, useCursorList } from './cursorList';
import type { CommunityItemDto } from './communities/types';
import type { OrganizationItem } from './organizationControls';
import { searchChatCandidates } from './peopleDirectory';
import { isComposingKey, stepQuickSwitcherSelection } from './quickSwitcherKeys';
import { chatFetch, orgFetch } from './scope';
import { Field, Modal } from './ui';

export type QuickSwitchAction = 'search' | 'new-chat' | 'chat' | 'communities' | 'people' | 'activity' | 'security';
export type QuickSwitchTarget =
  | { kind: 'action'; action: QuickSwitchAction }
  | { kind: 'conversation' | 'person'; id: string }
  | { kind: 'channel'; id: string; organizationId: string };

interface Option {
  key: string;
  title: string;
  detail: string;
  target: QuickSwitchTarget;
}

interface Conversation {
  id: string;
  title: string;
}

const actions: { action: QuickSwitchAction; title: string }[] = [
  { action: 'search', title: 'Search messages' },
  { action: 'new-chat', title: 'New chat' },
  { action: 'chat', title: 'Chat' },
  { action: 'communities', title: 'Communities' },
  { action: 'people', title: 'People' },
  { action: 'activity', title: 'Activity' },
  { action: 'security', title: 'Profile & security' }
];

const keyById = (item: { id: string }) => item.id;
const optionKey = (item: Option) => item.key;
const optionId = (key: string) => `nx-switch-${key}`;

export default function QuickSwitcher({ organizations, currentUserId, candidateRevision, onClose, onChoose }: {
  organizations: OrganizationItem[];
  currentUserId: string;
  candidateRevision: number;
  onClose: () => void;
  onChoose: (target: QuickSwitchTarget) => Promise<void>;
}) {
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [opening, setOpening] = useState(false);
  const openingRef = useRef(false);
  const input = useRef<HTMLInputElement>(null);
  const composing = useRef(false);
  const search = query.trim();
  const matches = (value: string) => value.toLowerCase().includes(search.toLowerCase());
  const conversations = useCursorList<Conversation, { updatedAt: string; id: string }>(search, async (cursor, signal) => {
    const params = new URLSearchParams({ limit: '50', q: search });
    if (cursor) {
      params.set('beforeAt', cursor.updatedAt);
      params.set('beforeId', cursor.id);
    }
    const response = await chatFetch(`/api/conversations?${params}`, { signal });
    if (!response.ok) throw new Error('Unable to load conversations.');
    const page = await response.json() as { items: Conversation[]; nextBefore: { updatedAt: string; id: string } | null };
    return { items: page.items, next: page.nextBefore };
  }, keyById, search ? 120 : 0);
  const people = useCursorList(search ? JSON.stringify([candidateRevision, search]) : null,
    (cursor, signal) => searchChatCandidates(search, cursor, signal), keyById, 120);
  const channels = useCursorList<Option, Record<string, string>>(
    organizations.length ? JSON.stringify([organizations.map(org => org.id), search]) : null,
    async (cursor, signal) => {
      const pages = await Promise.all(organizations.filter(org => cursor === null || cursor[org.id]).map(async org => {
        const params = new URLSearchParams({ limit: '50', q: search });
        if (cursor?.[org.id]) params.set('cursor', cursor[org.id]);
        const page = await readCursorPage<CommunityItemDto>(await orgFetch(org.id, `/api/communities?${params}`, { signal }),
          'Unable to load workspace channels.');
        const items: Option[] = page.items.flatMap(community => {
          const matchesCommunity = matches(community.name) || matches(org.name);
          const candidates = community.mainFeedConversationId
            ? [{ id: community.mainFeedConversationId, name: community.name }, ...community.channels]
            : community.channels;
          return candidates.filter(channel => matchesCommunity || matches(channel.name)).map(channel => ({
            key: `channel-${channel.id}`, title: channel.name, detail: `${community.name} · ${org.name}`,
            target: { kind: 'channel', id: channel.id, organizationId: org.id }
          }));
        });
        return { id: org.id, items, next: page.next };
      }));
      const next = Object.fromEntries(pages.flatMap(page => page.next ? [[page.id, page.next]] : []));
      return { items: pages.flatMap(page => page.items), next: Object.keys(next).length ? next : null };
    }, optionKey, search ? 120 : 0);

  const actionOptions: Option[] = actions.filter(action => matches(action.title)).map(({ action, title }) => ({
    key: `action-${action}`, title, detail: 'Action', target: { kind: 'action', action }
  }));
  const options: Option[] = [
    ...conversations.items.map(({ id, title }): Option => ({
      key: `conversation-${id}`, title, detail: 'Conversation', target: { kind: 'conversation', id }
    })),
    ...channels.items,
    ...people.items.filter(person => person.id !== currentUserId).map((person): Option => ({
      key: `person-${person.id}`, title: person.displayName, detail: `@${person.username}`,
      target: { kind: 'person', id: person.id }
    })),
    ...actionOptions
  ];
  const selectedIndex = options.findIndex(option => option.key === selected);
  const active = options[selectedIndex];
  const loading = conversations.loading || people.loading || channels.loading;

  useEffect(() => {
    if (selected) document.getElementById(optionId(selected))?.scrollIntoView({ block: 'nearest' });
  }, [selected]);

  async function choose(option: Option) {
    if (openingRef.current) return;
    openingRef.current = true;
    setOpening(true);
    setError('');
    try {
      await onChoose(option.target);
      onClose();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Unable to open this result.');
      input.current?.focus();
    } finally {
      openingRef.current = false;
      setOpening(false);
    }
  }

  function navigate(event: KeyboardEvent<HTMLInputElement>) {
    if (isComposingKey(event.nativeEvent, composing.current) || event.altKey || event.ctrlKey || event.metaKey) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const next = stepQuickSwitcherSelection(selectedIndex, options.length, event.key === 'ArrowDown' ? 1 : -1);
      setSelected(options[next]?.key ?? null);
    } else if (event.key === 'Enter' && (active || options[0])) {
      event.preventDefault();
      void choose(active ?? options[0]);
    }
  }

  return (
    <Modal open title="Quick switcher" initialFocusRef={input} onOpenChange={open => { if (!open) onClose(); }}
      onEscapeKeyDown={event => { if (isComposingKey(event, composing.current)) event.preventDefault(); }}>
      <Field label="Find a conversation, channel, person, or action" htmlFor="nx-switch-query" compact>
        <input id="nx-switch-query" type="text" role="combobox" aria-autocomplete="list" aria-expanded="true"
          aria-controls="nx-switch-results" aria-activedescendant={active && optionId(active.key)}
          maxLength={120} value={query} ref={input} onKeyDown={navigate}
          onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
          onBlur={() => { composing.current = false; }}
          onChange={event => { setQuery(event.target.value); setSelected(null); setError(''); }} />
      </Field>
      {error && <p className="nx-error" role="alert">{error}</p>}
      <div className="nx-quick-switcher-results" id="nx-switch-results" role="listbox" aria-label="Quick switcher results"
        aria-busy={loading || opening}>
        {options.map(option => (
          <div key={option.key} id={optionId(option.key)} role="option" aria-selected={option === active}
            className="nx-quick-switcher-option" onMouseDown={event => event.preventDefault()}
            onClick={() => void choose(option)}>
            <strong>{option.title}</strong><span className="nx-muted">{option.detail}</span>
          </div>
        ))}
      </div>
      {loading && <p role="status">Finding results…</p>}
      {!loading && options.length === 0 && <p role="status">No matching destinations.</p>}
      <ListPaging loading={conversations.loading} error={conversations.error} hasMore={conversations.next !== null}
        onRetry={() => void conversations.retry()} onMore={() => void conversations.loadMore()} moreLabel="More conversations" />
      <ListPaging loading={channels.loading} error={channels.error} hasMore={channels.next !== null}
        onRetry={() => void channels.retry()} onMore={() => void channels.loadMore()} moreLabel="More channels" />
      <ListPaging loading={people.loading} error={people.error} hasMore={people.next !== null}
        onRetry={() => void people.retry()} onMore={() => void people.loadMore()} moreLabel="More people" />
    </Modal>
  );
}
