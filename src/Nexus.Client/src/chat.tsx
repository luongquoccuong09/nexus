import { FormEvent, useCallback, useEffect, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { renderSafeMarkdown } from './markdown';
import {
  dequeueOfflineMessage,
  enqueueOfflineMessage,
  generateClientMsgId,
  getConversationCursor,
  listQueuedMessages,
  QueuedMessage,
  reconnectDelayWithJitter,
  setConversationCursor
} from './offlineQueue';

export interface ConversationListItem {
  id: string;
  kind: string;
  title: string;
  role: string;
  nextSeq: number;
  changeSeq: number;
  lastReadSeq: number;
  unreadCount: number;
  memberCount: number;
  peer?: {
    id: string;
    username: string;
    displayName: string;
    isUnverified: boolean;
    isOnline: boolean;
  } | null;
}

export interface ChatMessageItem {
  id: string;
  conversationId: string;
  seq: number;
  senderId: string;
  senderDisplayName: string;
  senderIsUnverified: boolean;
  clientMsgId: string;
  body: string;
  createdAt: string;
  pending?: boolean;
}

interface ChatWorkspaceProps {
  currentUserId: string;
  currentDisplayName: string;
  currentIsUnverified: boolean;
  initialVersion: string | null;
  selectedConversationId: string | null;
  onSelectConversation: (id: string | null) => void;
}

export function ChatWorkspace({
  currentUserId,
  currentDisplayName,
  currentIsUnverified,
  initialVersion,
  selectedConversationId,
  onSelectConversation
}: ChatWorkspaceProps) {
  const [conversations, setConversations] = useState<ConversationListItem[]>([]);
  const [messages, setMessages] = useState<ChatMessageItem[]>([]);
  const [lastReadSnapshot, setLastReadSnapshot] = useState<number>(0);
  const [composer, setComposer] = useState('');
  const [newGroupTitle, setNewGroupTitle] = useState('');
  const [connState, setConnState] = useState<'connected' | 'reconnecting' | 'offline'>('reconnecting');
  const [versionStale, setVersionStale] = useState(false);
  const [typingText, setTypingText] = useState('');
  const [error, setError] = useState('');

  const hubRef = useRef<signalR.HubConnection | null>(null);
  const activeConvRef = useRef<string | null>(selectedConversationId);
  activeConvRef.current = selectedConversationId;

  const checkApiVersion = useCallback(async () => {
    if (!initialVersion) return;
    try {
      const res = await fetch('/api/version');
      if (res.ok) {
        const data = await res.json();
        if (typeof data.version === 'string' && data.version !== initialVersion) {
          setVersionStale(true);
        }
      }
    } catch {
      // Ignore transient offline error.
    }
  }, [initialVersion]);

  const loadConversations = useCallback(async () => {
    const res = await fetch('/api/conversations?limit=50', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      const items: ConversationListItem[] = data.items ?? [];
      setConversations(items);
      if (!activeConvRef.current && items.length > 0) {
        onSelectConversation(items[0].id);
      }
    }
  }, [onSelectConversation]);

  const markRead = useCallback(async (convId: string, seq: number) => {
    if (seq <= 0) return;
    await fetch(`/api/conversations/${convId}/read`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ lastReadSeq: seq })
    });
  }, []);

  const loadMessagesFull = useCallback(async (convId: string) => {
    const res = await fetch(`/api/conversations/${convId}/messages?limit=50`, { credentials: 'same-origin' });
    if (!res.ok) return;
    const data = await res.json();
    const serverItems: ChatMessageItem[] = data.items ?? [];
    const queued = (await listQueuedMessages()).filter(q => q.conversationId === convId);
    const seen = new Set(serverItems.map(m => m.clientMsgId));
    const optimistic: ChatMessageItem[] = queued
      .filter(q => !seen.has(q.clientMsgId))
      .map(q => ({
        id: q.clientMsgId,
        conversationId: q.conversationId,
        seq: Number.MAX_SAFE_INTEGER,
        senderId: currentUserId,
        senderDisplayName: currentDisplayName,
        senderIsUnverified: currentIsUnverified,
        clientMsgId: q.clientMsgId,
        body: q.body,
        createdAt: q.createdAt,
        pending: true
      }));

    setLastReadSnapshot(data.lastReadSeq ?? 0);
    setMessages([...serverItems, ...optimistic]);
    if (typeof data.changeSeq === 'number') {
      await setConversationCursor(convId, data.changeSeq);
    }
    const maxSeq = serverItems.length > 0 ? serverItems[serverItems.length - 1].seq : 0;
    if (maxSeq > (data.lastReadSeq ?? 0)) {
      await markRead(convId, maxSeq);
    }
  }, [currentDisplayName, currentIsUnverified, currentUserId, markRead]);

  const syncConversationDelta = useCallback(async (convId: string) => {
    const cursor = await getConversationCursor(convId);
    if (cursor <= 0) {
      await loadMessagesFull(convId);
      return;
    }

    const res = await fetch(`/api/conversations/${convId}/changes?after=${cursor}&limit=50`, { credentials: 'same-origin' });
    if (res.status === 410) {
      await loadMessagesFull(convId);
      return;
    }
    if (!res.ok) return;

    const data = await res.json();
    const events: Array<{ changeSeq: number; eventType: string; payload: ChatMessageItem }> = data.items ?? [];
    if (events.length === 0) return;

    // Gap check
    if (events[0].changeSeq > cursor + 1) {
      await loadMessagesFull(convId);
      return;
    }

    const added: ChatMessageItem[] = [];
    let maxChangeSeq = cursor;
    for (const ev of events) {
      if (ev.changeSeq <= maxChangeSeq) continue;
      maxChangeSeq = ev.changeSeq;
      if (ev.eventType === 'message.created' && ev.payload) {
        added.push(ev.payload);
      }
    }

    if (added.length > 0) {
      setMessages(prev => {
        const byClientId = new Map<string, ChatMessageItem>();
        for (const m of prev) byClientId.set(m.clientMsgId, m);
        for (const m of added) byClientId.set(m.clientMsgId, m);
        return [...byClientId.values()].sort((a, b) => a.seq - b.seq);
      });
      const maxSeq = added[added.length - 1].seq;
      await markRead(convId, maxSeq);
    }

    await setConversationCursor(convId, maxChangeSeq);
  }, [loadMessagesFull, markRead]);

  const flushOfflineQueue = useCallback(async () => {
    const queued = await listQueuedMessages();
    for (const item of queued) {
      try {
        const res = await fetch(`/api/conversations/${item.conversationId}/messages`, {
          method: 'POST',
          credentials: 'same-origin',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ clientMsgId: item.clientMsgId, body: item.body })
        });
        if (res.ok || res.status === 403 || res.status === 409) {
          await dequeueOfflineMessage(item.clientMsgId);
        }
      } catch {
        break;
      }
    }
  }, []);

  const reconcileAll = useCallback(async () => {
    await checkApiVersion();
    await flushOfflineQueue();
    await loadConversations();
    if (activeConvRef.current) {
      await syncConversationDelta(activeConvRef.current);
    }
  }, [checkApiVersion, flushOfflineQueue, loadConversations, syncConversationDelta]);

  useEffect(() => {
    void loadConversations();
  }, [loadConversations]);

  useEffect(() => {
    if (selectedConversationId) {
      void loadMessagesFull(selectedConversationId);
    } else {
      setMessages([]);
    }
  }, [selectedConversationId, loadMessagesFull]);

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/messaging')
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: ctx => reconnectDelayWithJitter(ctx.previousRetryCount)
      })
      .build();

    connection.on('ConversationChanged', (evt: { conversationId: string; changeSeq: number }) => {
      void loadConversations();
      if (evt.conversationId === activeConvRef.current) {
        void syncConversationDelta(evt.conversationId);
      }
    });

    connection.on('ReadStateUpdated', () => {
      void loadConversations();
    });

    connection.on('TypingIndicator', (evt: { conversationId: string; displayName: string }) => {
      if (evt.conversationId === activeConvRef.current) {
        setTypingText(`${evt.displayName} is typing…`);
        setTimeout(() => setTypingText(''), 2500);
      }
    });

    connection.onreconnecting(() => setConnState('reconnecting'));
    connection.onreconnected(() => {
      setConnState('connected');
      void reconcileAll();
    });
    connection.onclose(() => setConnState('offline'));

    hubRef.current = connection;
    connection.start()
      .then(() => setConnState('connected'))
      .catch(() => setConnState('offline'));

    const onVisible = () => {
      if (document.visibilityState === 'visible') {
        void reconcileAll();
      }
    };
    document.addEventListener('visibilitychange', onVisible);

    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      void connection.stop();
    };
  }, [loadConversations, reconcileAll, syncConversationDelta]);

  async function handleSend(e: FormEvent) {
    e.preventDefault();
    if (!selectedConversationId) return;
    const body = composer.trim();
    if (!body) return;
    setError('');
    setComposer('');

    const item: QueuedMessage = {
      clientMsgId: generateClientMsgId(),
      conversationId: selectedConversationId,
      body,
      createdAt: new Date().toISOString()
    };

    await enqueueOfflineMessage(item);
    setMessages(prev => [
      ...prev,
      {
        id: item.clientMsgId,
        conversationId: item.conversationId,
        seq: Number.MAX_SAFE_INTEGER,
        senderId: currentUserId,
        senderDisplayName: currentDisplayName,
        senderIsUnverified: currentIsUnverified,
        clientMsgId: item.clientMsgId,
        body: item.body,
        createdAt: item.createdAt,
        pending: true
      }
    ]);

    try {
      const res = await fetch(`/api/conversations/${selectedConversationId}/messages`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ clientMsgId: item.clientMsgId, body: item.body })
      });
      if (res.ok) {
        const saved: ChatMessageItem = await res.json();
        await dequeueOfflineMessage(item.clientMsgId);
        setMessages(prev => prev.map(m => (m.clientMsgId === item.clientMsgId ? saved : m)));
        await loadConversations();
      } else {
        const err = await res.json().catch(() => ({}));
        setError(err.message || 'Unable to send message.');
        if (res.status === 403 || res.status === 409) {
          await dequeueOfflineMessage(item.clientMsgId);
        }
      }
    } catch {
      setError('Offline — message queued in IndexedDB and will retry on reconnect.');
    }
  }

  async function handleCreateGroup(e: FormEvent) {
    e.preventDefault();
    const title = newGroupTitle.trim();
    if (!title) return;
    setError('');
    const res = await fetch('/api/conversations/groups', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': generateClientMsgId()
      },
      body: JSON.stringify({ title, memberUserIds: [] })
    });
    if (res.ok) {
      const created = await res.json();
      setNewGroupTitle('');
      await loadConversations();
      onSelectConversation(created.id);
    } else {
      const err = await res.json().catch(() => ({}));
      setError(err.message || 'Unable to create group.');
    }
  }

  const activeConv = conversations.find(c => c.id === selectedConversationId) ?? null;

  return (
    <section className="nx-card">
      <div className="nx-header" style={{ marginBottom: 12 }}>
        <h2 style={{ margin: 0 }}>Chat</h2>
        <span className="nx-muted">
          Realtime: <strong>{connState}</strong>
        </span>
      </div>

      {versionStale && (
        <div className="nx-banner">
          A new version of Project Nexus is available.{' '}
          <button type="button" className="nx-btn" onClick={() => window.location.reload()}>Reload</button>
        </div>
      )}
      {error && <p className="nx-error" role="alert">{error}</p>}

      <div className="nx-chat-grid">
        <aside className="nx-chat-list">
          <form onSubmit={handleCreateGroup} className="nx-flex" style={{ marginBottom: 10 }}>
            <input
              type="text"
              placeholder="New group name…"
              value={newGroupTitle}
              onChange={e => setNewGroupTitle(e.target.value)}
              style={{ flex: 1, padding: '6px 8px', borderRadius: 6, border: '1px solid var(--border)' }}
            />
            <button type="submit" className="nx-btn">+ Group</button>
          </form>

          {conversations.length === 0 ? (
            <p className="nx-muted">No conversations yet. Start a DM from Directory or create a group.</p>
          ) : (
            conversations.map(c => (
              <button
                key={c.id}
                type="button"
                className={`nx-conv-btn ${c.id === selectedConversationId ? 'active' : ''}`}
                onClick={() => onSelectConversation(c.id)}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', width: '100%' }}>
                  <span>
                    <strong>{c.title}</strong>
                    {c.peer?.isUnverified && <span className="nx-badge unverified">Unverified</span>}
                  </span>
                  {c.unreadCount > 0 && <span className="nx-badge role">{c.unreadCount}</span>}
                </div>
                <div className="nx-muted" style={{ fontSize: 12 }}>
                  {c.kind === 'dm' ? (c.peer?.isOnline ? 'Online' : 'Offline') : `${c.memberCount} members`}
                </div>
              </button>
            ))
          )}
        </aside>

        <div className="nx-chat-pane">
          {!activeConv ? (
            <p className="nx-muted">Select a conversation to view messages.</p>
          ) : (
            <>
              <div style={{ borderBottom: '1px solid var(--border)', paddingBottom: 8, marginBottom: 10 }}>
                <strong>{activeConv.title}</strong>
                {activeConv.peer?.isUnverified && <span className="nx-badge unverified">Unverified</span>}
              </div>

              <div className="nx-msg-list">
                {messages.map((m, idx) => {
                  const prevSeq = idx > 0 ? messages[idx - 1].seq : 0;
                  const showDivider = lastReadSnapshot > 0 && prevSeq <= lastReadSnapshot && m.seq > lastReadSnapshot && !m.pending;
                  return (
                    <div key={m.clientMsgId}>
                      {showDivider && <div className="nx-new-divider" role="separator">New messages</div>}
                      <div className={`nx-msg ${m.senderId === currentUserId ? 'own' : ''}`}>
                        <div className="nx-muted" style={{ fontSize: 12 }}>
                          <strong>{m.senderDisplayName}</strong>
                          {m.senderIsUnverified && <span className="nx-badge unverified">Unverified</span>}
                          {m.pending ? ' · Queued…' : ` · #${m.seq}`}
                        </div>
                        <div dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(m.body) }} />
                      </div>
                    </div>
                  );
                })}
              </div>

              {typingText && <div className="nx-muted" style={{ fontSize: 12, margin: '4px 0' }}>{typingText}</div>}

              <form onSubmit={handleSend} className="nx-flex" style={{ marginTop: 10 }}>
                <input
                  type="text"
                  aria-label="Message"
                  placeholder="Write a message (**bold**, *italic*, `code`, https://…)"
                  value={composer}
                  onChange={e => {
                    setComposer(e.target.value);
                    if (hubRef.current?.state === signalR.HubConnectionState.Connected && selectedConversationId) {
                      void hubRef.current.invoke('SendTyping', selectedConversationId).catch(() => {});
                    }
                  }}
                  style={{ flex: 1, padding: '8px 10px', borderRadius: 7, border: '1px solid var(--border)' }}
                />
                <button type="submit" className="nx-btn primary">Send</button>
              </form>
            </>
          )}
        </div>
      </div>
    </section>
  );
}
