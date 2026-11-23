import { FormEvent, KeyboardEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { IconArrowLeft, IconChat, IconPlus, IconSearch, IconSend, IconUsers } from './icons';
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
  updatedAt?: string;
  peer?: {
    id: string;
    username: string;
    displayName: string;
    isUnverified: boolean;
    isOnline: boolean;
    avatarUrl?: string | null;
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

export interface DirectoryUserLite {
  id: string;
  username: string;
  displayName: string;
  isUnverified: boolean;
  departmentName?: string | null;
  avatarUrl?: string | null;
}

interface GroupMemberItem {
  userId: string;
  username: string;
  displayName: string;
  isUnverified: boolean;
  role: string;
}

interface ChatWorkspaceProps {
  currentUserId: string;
  currentDisplayName: string;
  currentIsUnverified: boolean;
  initialVersion: string | null;
  selectedConversationId: string | null;
  directoryUsers: DirectoryUserLite[];
  onSelectConversation: (id: string | null) => void;
  onOpenDirectory: () => void;
  onConnectionStateChange: (state: 'connected' | 'reconnecting' | 'offline') => void;
  onUnreadTotalChange: (total: number) => void;
  onVersionStale: () => void;
}

function getInitials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function formatDateLabel(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const today = new Date();
  if (d.toDateString() === today.toDateString()) return 'Today';
  const yesterday = new Date(today);
  yesterday.setDate(today.getDate() - 1);
  if (d.toDateString() === yesterday.toDateString()) return 'Yesterday';
  return d.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
}

export function ChatWorkspace({
  currentUserId,
  currentDisplayName,
  currentIsUnverified,
  initialVersion,
  selectedConversationId,
  directoryUsers,
  onSelectConversation,
  onOpenDirectory,
  onConnectionStateChange,
  onUnreadTotalChange,
  onVersionStale
}: ChatWorkspaceProps) {
  const [conversations, setConversations] = useState<ConversationListItem[]>([]);
  const [convSearch, setConvSearch] = useState('');
  const [messages, setMessages] = useState<ChatMessageItem[]>([]);
  const [hasMoreOlder, setHasMoreOlder] = useState(false);
  const [nextBeforeSeq, setNextBeforeSeq] = useState<number | null>(null);
  const [lastReadSnapshot, setLastReadSnapshot] = useState<number>(0);
  const [composer, setComposer] = useState('');
  const [typingText, setTypingText] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  // New group modal
  const [showNewGroupModal, setShowNewGroupModal] = useState(false);
  const [newGroupTitle, setNewGroupTitle] = useState('');
  const [selectedGroupMemberIds, setSelectedGroupMemberIds] = useState<string[]>([]);

  // Group members drawer
  const [showMembersDrawer, setShowMembersDrawer] = useState(false);
  const [groupMembers, setGroupMembers] = useState<GroupMemberItem[]>([]);
  const [addMemberId, setAddMemberId] = useState('');

  // Report modal (DM)
  const [showReportModal, setShowReportModal] = useState(false);
  const [reportReason, setReportReason] = useState('spam');
  const [reportDetails, setReportDetails] = useState('');

  const hubRef = useRef<signalR.HubConnection | null>(null);
  const activeConvRef = useRef<string | null>(selectedConversationId);
  const feedEndRef = useRef<HTMLDivElement | null>(null);
  const typingTimerRef = useRef<number | null>(null);
  activeConvRef.current = selectedConversationId;

  const checkApiVersion = useCallback(async () => {
    if (!initialVersion) return;
    try {
      const res = await fetch('/api/version');
      if (res.ok) {
        const data = await res.json();
        if (typeof data.version === 'string' && data.version !== initialVersion) {
          onVersionStale();
        }
      }
    } catch {
      // Ignore transient offline error.
    }
  }, [initialVersion, onVersionStale]);

  const loadConversations = useCallback(async () => {
    const res = await fetch('/api/conversations?limit=50', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      const items: ConversationListItem[] = data.items ?? [];
      setConversations(items);
      onUnreadTotalChange(items.reduce((sum, c) => sum + (c.unreadCount || 0), 0));
      if (!activeConvRef.current && items.length > 0 && window.innerWidth > 860) {
        onSelectConversation(items[0].id);
      }
    }
  }, [onSelectConversation, onUnreadTotalChange]);

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
    setHasMoreOlder(Boolean(data.hasMore));
    setNextBeforeSeq(typeof data.nextBeforeSeq === 'number' ? data.nextBeforeSeq : null);
    setMessages([...serverItems, ...optimistic]);
    if (typeof data.changeSeq === 'number') {
      await setConversationCursor(convId, data.changeSeq);
    }
    const maxSeq = serverItems.length > 0 ? serverItems[serverItems.length - 1].seq : 0;
    if (maxSeq > (data.lastReadSeq ?? 0)) {
      await markRead(convId, maxSeq);
    }
    requestAnimationFrame(() => feedEndRef.current?.scrollIntoView({ behavior: 'auto' }));
  }, [currentDisplayName, currentIsUnverified, currentUserId, markRead]);

  const loadOlderMessages = useCallback(async () => {
    if (!selectedConversationId || !nextBeforeSeq) return;
    const res = await fetch(
      `/api/conversations/${selectedConversationId}/messages?beforeSeq=${nextBeforeSeq}&limit=50`,
      { credentials: 'same-origin' }
    );
    if (!res.ok) return;
    const data = await res.json();
    const older: ChatMessageItem[] = data.items ?? [];
    setHasMoreOlder(Boolean(data.hasMore));
    setNextBeforeSeq(typeof data.nextBeforeSeq === 'number' ? data.nextBeforeSeq : null);
    if (older.length > 0) {
      setMessages(prev => {
        const byId = new Map<string, ChatMessageItem>();
        for (const m of older) byId.set(m.clientMsgId, m);
        for (const m of prev) byId.set(m.clientMsgId, m);
        return [...byId.values()].sort((a, b) => a.seq - b.seq);
      });
    }
  }, [nextBeforeSeq, selectedConversationId]);

  const loadGroupMembers = useCallback(async (convId: string) => {
    const res = await fetch(`/api/conversations/${convId}/members?limit=50`, { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setGroupMembers(data.items ?? []);
    }
  }, []);

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
      requestAnimationFrame(() => feedEndRef.current?.scrollIntoView({ behavior: 'smooth' }));
    }

    const nextCursor = typeof data.nextAfter === 'number' ? Math.max(maxChangeSeq, data.nextAfter) : maxChangeSeq;
    await setConversationCursor(convId, nextCursor);
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
    setShowMembersDrawer(false);
    setTypingText('');
    setError('');
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
        if (typingTimerRef.current) window.clearTimeout(typingTimerRef.current);
        typingTimerRef.current = window.setTimeout(() => setTypingText(''), 2600);
      }
    });

    connection.onreconnecting(() => onConnectionStateChange('reconnecting'));
    connection.onreconnected(() => {
      onConnectionStateChange('connected');
      void reconcileAll();
    });
    connection.onclose(() => onConnectionStateChange('offline'));

    hubRef.current = connection;
    onConnectionStateChange('reconnecting');
    connection.start()
      .then(() => onConnectionStateChange('connected'))
      .catch(() => onConnectionStateChange('offline'));

    const onVisible = () => {
      if (document.visibilityState === 'visible') {
        void reconcileAll();
      }
    };
    document.addEventListener('visibilitychange', onVisible);

    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      if (typingTimerRef.current) window.clearTimeout(typingTimerRef.current);
      void connection.stop();
    };
  }, [loadConversations, onConnectionStateChange, reconcileAll, syncConversationDelta]);

  async function sendCurrentMessage() {
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
    requestAnimationFrame(() => feedEndRef.current?.scrollIntoView({ behavior: 'smooth' }));

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
          setMessages(prev => prev.filter(m => m.clientMsgId !== item.clientMsgId));
        }
      }
    } catch {
      setError('Offline — message queued locally and will send automatically on reconnect.');
    }
  }

  function handleComposerKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void sendCurrentMessage();
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
      body: JSON.stringify({ title, memberUserIds: selectedGroupMemberIds })
    });
    if (res.ok) {
      const created = await res.json();
      setNewGroupTitle('');
      setSelectedGroupMemberIds([]);
      setShowNewGroupModal(false);
      await loadConversations();
      onSelectConversation(created.id);
    } else {
      const err = await res.json().catch(() => ({}));
      setError(err.message || 'Unable to create group.');
    }
  }

  async function handleAddMember() {
    if (!selectedConversationId || !addMemberId) return;
    const res = await fetch(`/api/conversations/${selectedConversationId}/members`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userId: addMemberId })
    });
    if (res.ok) {
      setAddMemberId('');
      await Promise.all([loadGroupMembers(selectedConversationId), loadConversations()]);
    } else {
      const err = await res.json().catch(() => ({}));
      setError(err.message || 'Unable to add member.');
    }
  }

  async function handleRemoveMember(targetUserId: string) {
    if (!selectedConversationId) return;
    const res = await fetch(`/api/conversations/${selectedConversationId}/members/${targetUserId}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (res.ok) {
      await Promise.all([loadGroupMembers(selectedConversationId), loadConversations()]);
    } else {
      setError('Unable to remove member.');
    }
  }

  async function handleLeaveGroup() {
    if (!selectedConversationId) return;
    const res = await fetch(`/api/conversations/${selectedConversationId}/leave`, {
      method: 'POST',
      credentials: 'same-origin'
    });
    if (res.ok) {
      setShowMembersDrawer(false);
      onSelectConversation(null);
      await loadConversations();
    } else {
      const err = await res.json().catch(() => ({}));
      setError(err.message || 'Unable to leave group.');
    }
  }

  async function handleBlockPeer(peerId: string, peerName: string) {
    if (!window.confirm(`Block ${peerName}? Neither of you will be able to send direct messages until unblocked.`)) {
      return;
    }
    const res = await fetch(`/api/people/blocks/${peerId}`, {
      method: 'PUT',
      credentials: 'same-origin'
    });
    if (res.ok) {
      setNotice(`${peerName} has been blocked.`);
    } else {
      setError('Unable to block user.');
    }
  }

  async function handleSubmitReport(e: FormEvent) {
    e.preventDefault();
    const peer = activeConv?.peer;
    if (!peer) return;
    const res = await fetch('/api/people/reports', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        reportedUserId: peer.id,
        conversationId: activeConv.id,
        reason: reportReason,
        details: reportDetails
      })
    });
    if (res.ok) {
      setShowReportModal(false);
      setReportDetails('');
      setNotice('Report submitted to administrators.');
    } else {
      setError('Unable to submit report.');
    }
  }

  const activeConv = conversations.find(c => c.id === selectedConversationId) ?? null;

  const filteredConvs = useMemo(() => {
    const q = convSearch.trim().toLowerCase();
    if (!q) return conversations;
    return conversations.filter(c => c.title.toLowerCase().includes(q) || (c.peer?.username ?? '').toLowerCase().includes(q));
  }, [convSearch, conversations]);

  const dmConvs = useMemo(() => filteredConvs.filter(c => c.kind === 'dm'), [filteredConvs]);
  const groupConvs = useMemo(() => filteredConvs.filter(c => c.kind !== 'dm'), [filteredConvs]);

  const otherDirectoryUsers = useMemo(
    () => directoryUsers.filter(u => u.id !== currentUserId),
    [currentUserId, directoryUsers]
  );

  const renderConvItem = (c: ConversationListItem) => {
    const isActive = c.id === selectedConversationId;
    const isDm = c.kind === 'dm';
    return (
      <button
        key={c.id}
        type="button"
        className={`nx-conv-item ${isActive ? 'active' : ''}`}
        onClick={() => onSelectConversation(c.id)}
      >
        <span className="nx-avatar-wrap">
          {isDm && c.peer?.avatarUrl ? (
            <img className="nx-avatar" src={c.peer.avatarUrl} alt="" />
          ) : (
            <span className={`nx-avatar ${isDm ? '' : 'group'}`}>{getInitials(c.title)}</span>
          )}
          {isDm && <span className={`nx-avatar-dot nx-dot ${c.peer?.isOnline ? 'online' : 'offline'}`} />}
        </span>
        <div className="nx-conv-meta">
          <div className="nx-conv-top">
            <span className="nx-conv-name">
              <span>{c.title}</span>
              {c.peer?.isUnverified && <span className="nx-badge unverified">Unverified</span>}
            </span>
            {c.unreadCount > 0 && <span className="nx-unread-pill">{c.unreadCount > 99 ? '99+' : c.unreadCount}</span>}
          </div>
          <div className="nx-conv-sub">
            {isDm
              ? (c.peer?.isOnline ? 'Online' : `@${c.peer?.username ?? 'direct'}`)
              : `${c.memberCount} ${c.memberCount === 1 ? 'member' : 'members'}`}
          </div>
        </div>
      </button>
    );
  };

  return (
    <div className={`nx-chat-layout ${activeConv ? 'has-selection' : ''}`}>
      {/* Conversation Sidebar */}
      <aside className="nx-chat-sidebar" aria-label="Conversations">
        <div className="nx-side-header">
          <div className="nx-side-header-row">
            <h2>Chats</h2>
            <div style={{ display: 'flex', gap: 6 }}>
              <button
                type="button"
                className="nx-btn sm"
                title="Start a direct message"
                onClick={onOpenDirectory}
              >
                New DM
              </button>
              <button
                type="button"
                className="nx-btn primary sm"
                title="Create a new group chat"
                onClick={() => setShowNewGroupModal(true)}
              >
                <IconPlus size={14} /> Group
              </button>
            </div>
          </div>
          <div className="nx-search-input-wrap">
            <IconSearch />
            <input
              type="text"
              aria-label="Filter conversations"
              placeholder="Filter conversations…"
              value={convSearch}
              onChange={e => setConvSearch(e.target.value)}
            />
          </div>
        </div>

        <div className="nx-conv-scroll">
          {filteredConvs.length === 0 ? (
            <div className="nx-empty-side">
              <p style={{ margin: '0 0 12px' }}>No conversations yet.</p>
              <button type="button" className="nx-btn primary sm" onClick={onOpenDirectory}>
                Find colleagues in Directory
              </button>
            </div>
          ) : (
            <>
              {dmConvs.length > 0 && (
                <>
                  <div className="nx-section-label">Direct Messages ({dmConvs.length})</div>
                  {dmConvs.map(renderConvItem)}
                </>
              )}
              {groupConvs.length > 0 && (
                <>
                  <div className="nx-section-label">Group Chats ({groupConvs.length})</div>
                  {groupConvs.map(renderConvItem)}
                </>
              )}
            </>
          )}
        </div>
      </aside>

      {/* Active Conversation Stage */}
      <section className="nx-chat-stage" aria-label="Message thread">
        {error && (
          <div className="nx-toast-bar error" role="alert">
            <span>{error}</span>
            <button type="button" onClick={() => setError('')} aria-label="Dismiss error">×</button>
          </div>
        )}
        {notice && (
          <div className="nx-toast-bar ok" role="status">
            <span>{notice}</span>
            <button type="button" onClick={() => setNotice('')} aria-label="Dismiss notice">×</button>
          </div>
        )}

        {!activeConv ? (
          <div className="nx-empty-stage">
            <div className="nx-empty-stage-card">
              <div className="nx-brand-mark" style={{ margin: '0 auto' }}>N</div>
              <h3>Welcome to Nexus</h3>
              <p>Select a conversation on the left, message a colleague from the Directory, or create a group chat.</p>
              <div style={{ display: 'flex', justifyContent: 'center', gap: 10 }}>
                <button type="button" className="nx-btn primary" onClick={onOpenDirectory}>
                  <IconUsers size={16} /> Open Directory
                </button>
                <button type="button" className="nx-btn" onClick={() => setShowNewGroupModal(true)}>
                  <IconPlus size={15} /> New Group
                </button>
              </div>
            </div>
          </div>
        ) : (
          <>
            <header className="nx-stage-header">
              <div className="nx-stage-title">
                <button
                  type="button"
                  className="nx-btn ghost icon-only nx-mobile-back"
                  onClick={() => onSelectConversation(null)}
                  aria-label="Back to conversations"
                >
                  <IconArrowLeft />
                </button>
                <span className="nx-avatar-wrap">
                  {activeConv.kind === 'dm' && activeConv.peer?.avatarUrl ? (
                    <img className="nx-avatar" src={activeConv.peer.avatarUrl} alt="" />
                  ) : (
                    <span className={`nx-avatar ${activeConv.kind === 'dm' ? '' : 'group'}`}>
                      {getInitials(activeConv.title)}
                    </span>
                  )}
                  {activeConv.kind === 'dm' && (
                    <span className={`nx-avatar-dot nx-dot ${activeConv.peer?.isOnline ? 'online' : 'offline'}`} />
                  )}
                </span>
                <div>
                  <h3>
                    <span>{activeConv.title}</span>
                    {activeConv.peer?.isUnverified && <span className="nx-badge unverified">Unverified</span>}
                  </h3>
                  <div className="nx-stage-subtitle">
                    {activeConv.kind === 'dm' ? (
                      <>
                        <span className={`nx-dot ${activeConv.peer?.isOnline ? 'online' : 'offline'}`} />
                        <span>{activeConv.peer?.isOnline ? 'Online' : 'Offline'}</span>
                        {activeConv.peer?.username && <span>· @{activeConv.peer.username}</span>}
                      </>
                    ) : (
                      <span>{activeConv.memberCount} members · Role: {activeConv.role}</span>
                    )}
                  </div>
                </div>
              </div>

              <div className="nx-stage-actions">
                {activeConv.kind === 'dm' && activeConv.peer ? (
                  <>
                    <button
                      type="button"
                      className="nx-btn sm"
                      onClick={() => setShowReportModal(true)}
                    >
                      Report
                    </button>
                    <button
                      type="button"
                      className="nx-btn danger sm"
                      onClick={() => void handleBlockPeer(activeConv.peer!.id, activeConv.peer!.displayName)}
                    >
                      Block
                    </button>
                  </>
                ) : (
                  <button
                    type="button"
                    className={`nx-btn sm ${showMembersDrawer ? 'primary' : ''}`}
                    onClick={() => {
                      const next = !showMembersDrawer;
                      setShowMembersDrawer(next);
                      if (next) void loadGroupMembers(activeConv.id);
                    }}
                  >
                    <IconUsers size={15} /> Members ({activeConv.memberCount})
                  </button>
                )}
              </div>
            </header>

            <div className="nx-msg-feed">
              {hasMoreOlder && (
                <button type="button" className="nx-btn sm nx-load-older" onClick={() => void loadOlderMessages()}>
                  Load older messages
                </button>
              )}

              {messages.length === 0 && (
                <div style={{ margin: 'auto', textAlign: 'center', color: 'var(--text-muted)', padding: '32px 16px' }}>
                  <IconChat size={30} />
                  <p style={{ margin: '8px 0 0' }}>No messages yet. Say hello!</p>
                </div>
              )}

              {messages.map((m, idx) => {
                const prev = idx > 0 ? messages[idx - 1] : null;
                const prevSeq = prev ? prev.seq : 0;
                const showDivider = lastReadSnapshot > 0 && prevSeq <= lastReadSnapshot && m.seq > lastReadSnapshot && !m.pending;
                const prevDate = prev ? formatDateLabel(prev.createdAt) : '';
                const curDate = formatDateLabel(m.createdAt);
                const showDate = curDate && curDate !== prevDate;
                const isOwn = m.senderId === currentUserId;
                const compact = Boolean(prev && !showDate && !showDivider && prev.senderId === m.senderId);

                return (
                  <div key={m.clientMsgId} style={{ display: 'flex', flexDirection: 'column' }}>
                    {showDate && <div className="nx-date-sep">{curDate}</div>}
                    {showDivider && <div className="nx-new-divider" role="separator">New messages</div>}
                    <div className={`nx-msg-row ${isOwn ? 'own' : ''} ${compact ? 'compact' : ''} ${m.pending ? 'pending' : ''}`}>
                      {!isOwn && (
                        compact
                          ? <div className="nx-msg-avatar-spacer" />
                          : <span className="nx-avatar sm">{getInitials(m.senderDisplayName)}</span>
                      )}
                      <div className="nx-msg-bubble">
                        {!compact && (
                          <div className="nx-msg-head">
                            <strong>{isOwn ? 'You' : m.senderDisplayName}</strong>
                            {m.senderIsUnverified && !isOwn && <span className="nx-badge unverified">Unverified</span>}
                            <span className="nx-msg-time">
                              {m.pending ? 'Sending…' : formatTime(m.createdAt)}
                            </span>
                          </div>
                        )}
                        <div className="nx-msg-body" dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(m.body) }} />
                      </div>
                    </div>
                  </div>
                );
              })}
              <div ref={feedEndRef} />
            </div>

            <div className="nx-typing-bar" aria-live="polite">{typingText}</div>

            <form
              className="nx-composer-wrap"
              onSubmit={e => {
                e.preventDefault();
                void sendCurrentMessage();
              }}
            >
              <div className="nx-composer-box">
                <textarea
                  rows={1}
                  aria-label="Message"
                  placeholder={`Message ${activeConv.title}…`}
                  value={composer}
                  onKeyDown={handleComposerKeyDown}
                  onChange={e => {
                    setComposer(e.target.value);
                    if (hubRef.current?.state === signalR.HubConnectionState.Connected && selectedConversationId) {
                      void hubRef.current.invoke('SendTyping', selectedConversationId).catch(() => {});
                    }
                  }}
                />
                <button type="submit" className="nx-btn primary sm" disabled={!composer.trim()}>
                  <IconSend size={14} /> Send
                </button>
              </div>
              <div className="nx-composer-hint">
                <span>Supports **bold**, *italic*, `code`, and https:// links</span>
                <span>Enter to send · Shift+Enter for new line</span>
              </div>
            </form>
          </>
        )}
      </section>

      {/* Group Members Drawer */}
      {activeConv && activeConv.kind !== 'dm' && showMembersDrawer && (
        <aside className="nx-drawer" aria-label="Group members">
          <div className="nx-drawer-head">
            <strong>Members ({groupMembers.length})</strong>
            <button type="button" className="nx-btn ghost sm" onClick={() => setShowMembersDrawer(false)}>Close</button>
          </div>
          <div className="nx-drawer-body">
            {(activeConv.role === 'Owner' || activeConv.role === 'Admin') && (
              <div className="nx-field" style={{ marginBottom: 4 }}>
                <label htmlFor="add-group-member">Add colleague</label>
                <div style={{ display: 'flex', gap: 6 }}>
                  <select
                    id="add-group-member"
                    value={addMemberId}
                    onChange={e => setAddMemberId(e.target.value)}
                  >
                    <option value="">Select person…</option>
                    {otherDirectoryUsers
                      .filter(u => !groupMembers.some(gm => gm.userId === u.id))
                      .map(u => (
                        <option key={u.id} value={u.id}>
                          {u.displayName} (@{u.username})
                        </option>
                      ))}
                  </select>
                  <button type="button" className="nx-btn primary sm" disabled={!addMemberId} onClick={() => void handleAddMember()}>
                    Add
                  </button>
                </div>
              </div>
            )}

            <div>
              {groupMembers.map(gm => (
                <div className="nx-row" key={gm.userId}>
                  <div style={{ minWidth: 0 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 5, flexWrap: 'wrap' }}>
                      <strong style={{ fontSize: 13 }}>{gm.displayName}</strong>
                      <span className="nx-badge role">{gm.role}</span>
                      {gm.isUnverified && <span className="nx-badge unverified">Unverified</span>}
                    </div>
                    <div className="nx-muted">@{gm.username}</div>
                  </div>
                  {(activeConv.role === 'Owner' || activeConv.role === 'Admin') &&
                    gm.userId !== currentUserId &&
                    gm.role !== 'Owner' && (
                      <button
                        type="button"
                        className="nx-btn danger sm"
                        onClick={() => void handleRemoveMember(gm.userId)}
                      >
                        Remove
                      </button>
                    )}
                </div>
              ))}
            </div>

            <div style={{ marginTop: 'auto', paddingTop: 12, borderTop: '1px solid var(--border-soft)' }}>
              <button type="button" className="nx-btn danger" style={{ width: '100%' }} onClick={() => void handleLeaveGroup()}>
                Leave group
              </button>
            </div>
          </div>
        </aside>
      )}

      {/* New Group Modal */}
      {showNewGroupModal && (
        <div className="nx-modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="new-group-title">
          <form className="nx-modal" onSubmit={handleCreateGroup}>
            <div className="nx-modal-head">
              <h3 id="new-group-title">Create group chat</h3>
              <button type="button" className="nx-btn ghost sm" onClick={() => setShowNewGroupModal(false)}>×</button>
            </div>
            <div className="nx-modal-body">
              <div className="nx-field">
                <label htmlFor="modal-group-name">Group name</label>
                <input
                  id="modal-group-name"
                  type="text"
                  required
                  maxLength={120}
                  placeholder="e.g. Project Alpha, Engineering…"
                  value={newGroupTitle}
                  onChange={e => setNewGroupTitle(e.target.value)}
                />
              </div>

              <div className="nx-field" style={{ marginBottom: 0 }}>
                <label>Add colleagues (optional)</label>
                {otherDirectoryUsers.length === 0 ? (
                  <p className="nx-muted">No other active accounts in Directory yet.</p>
                ) : (
                  <div className="nx-member-picker">
                    {otherDirectoryUsers.map(u => {
                      const checked = selectedGroupMemberIds.includes(u.id);
                      return (
                        <label className="nx-member-option" key={u.id}>
                          <input
                            type="checkbox"
                            checked={checked}
                            onChange={e => {
                              setSelectedGroupMemberIds(prev =>
                                e.target.checked ? [...prev, u.id] : prev.filter(id => id !== u.id)
                              );
                            }}
                          />
                          <span className="nx-avatar sm">{getInitials(u.displayName)}</span>
                          <div>
                            <div style={{ fontWeight: 600, fontSize: 13 }}>
                              {u.displayName}{' '}
                              {u.isUnverified && <span className="nx-badge unverified">Unverified</span>}
                            </div>
                            <div className="nx-muted">@{u.username}{u.departmentName ? ` · ${u.departmentName}` : ''}</div>
                          </div>
                        </label>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
            <div className="nx-modal-foot">
              <button type="button" className="nx-btn" onClick={() => setShowNewGroupModal(false)}>Cancel</button>
              <button type="submit" className="nx-btn primary">Create group</button>
            </div>
          </form>
        </div>
      )}

      {/* Report User Modal */}
      {showReportModal && activeConv?.peer && (
        <div className="nx-modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="report-user-title">
          <form className="nx-modal" onSubmit={handleSubmitReport}>
            <div className="nx-modal-head">
              <h3 id="report-user-title">Report {activeConv.peer.displayName}</h3>
              <button type="button" className="nx-btn ghost sm" onClick={() => setShowReportModal(false)}>×</button>
            </div>
            <div className="nx-modal-body">
              <div className="nx-field">
                <label htmlFor="report-reason">Reason</label>
                <select id="report-reason" value={reportReason} onChange={e => setReportReason(e.target.value)}>
                  <option value="spam">Spam or unsolicited messages</option>
                  <option value="impersonation">Impersonation</option>
                  <option value="harassment">Harassment or abuse</option>
                  <option value="other">Other</option>
                </select>
              </div>
              <div className="nx-field" style={{ marginBottom: 0 }}>
                <label htmlFor="report-details">Additional details (optional)</label>
                <textarea
                  id="report-details"
                  rows={3}
                  maxLength={1000}
                  value={reportDetails}
                  onChange={e => setReportDetails(e.target.value)}
                />
              </div>
            </div>
            <div className="nx-modal-foot">
              <button type="button" className="nx-btn" onClick={() => setShowReportModal(false)}>Cancel</button>
              <button type="submit" className="nx-btn danger">Submit report</button>
            </div>
          </form>
        </div>
      )}
    </div>
  );
}
