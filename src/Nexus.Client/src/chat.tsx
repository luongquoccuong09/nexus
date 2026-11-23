import { ChangeEvent, ClipboardEvent, DragEvent, FormEvent, KeyboardEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { ArrowLeft, Download, FileText, FolderOpen, MessageSquare, Paperclip, Plus, Search, Send, Users, X } from 'lucide-react';
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
import { Avatar, Badge, Button, EmptyState, Field, Modal, ToastBar, TrustBadge } from './ui';

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

export interface AttachmentItem {
  id: string;
  conversationId: string;
  messageId?: string | null;
  uploaderUserId: string;
  clientUploadId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  sha256Hex: string;
  imageWidth?: number | null;
  imageHeight?: number | null;
  state: string;
  scanStatus: string;
  downloadUrl: string;
  thumbnailUrl?: string | null;
  createdAt: string;
}

export interface StorageQuotaSummary {
  userUsedBytes: number;
  userQuotaBytes: number;
  userWarning: boolean;
  conversationUsedBytes: number;
  conversationQuotaBytes: number;
  conversationWarning: boolean;
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
  attachments?: AttachmentItem[];
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

function formatTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
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

  // Group members & Files drawer (M2.8 / M3.5)
  const [rightDrawer, setRightDrawer] = useState<'none' | 'members' | 'files'>('none');
  const [groupMembers, setGroupMembers] = useState<GroupMemberItem[]>([]);
  const [addMemberId, setAddMemberId] = useState('');
  const [conversationFiles, setConversationFiles] = useState<AttachmentItem[]>([]);
  const [quota, setQuota] = useState<StorageQuotaSummary | null>(null);
  const [pendingAttachments, setPendingAttachments] = useState<AttachmentItem[]>([]);
  const [uploadingFiles, setUploadingFiles] = useState(false);
  const [isDragOver, setIsDragOver] = useState(false);

  // Report modal (DM)
  const [showReportModal, setShowReportModal] = useState(false);
  const [reportReason, setReportReason] = useState('spam');
  const [reportDetails, setReportDetails] = useState('');

  const hubRef = useRef<signalR.HubConnection | null>(null);
  const activeConvRef = useRef<string | null>(selectedConversationId);
  const feedEndRef = useRef<HTMLDivElement | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
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

  const loadConversationFiles = useCallback(async (convId: string) => {
    const res = await fetch(`/api/conversations/${convId}/files?limit=50`, { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setConversationFiles(data.items ?? []);
      setQuota(data.quota ?? null);
    }
  }, []);

  const uploadFilesToConversation = useCallback(async (fileList: File[]) => {
    if (!selectedConversationId || fileList.length === 0) return;
    setError('');
    setUploadingFiles(true);
    try {
      for (const file of fileList) {
        const form = new FormData();
        form.append('clientUploadId', generateClientMsgId());
        form.append('file', file, file.name || 'pasted-image.png');

        const res = await fetch(`/api/conversations/${selectedConversationId}/attachments`, {
          method: 'POST',
          credentials: 'same-origin',
          body: form
        });

        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
          setError(data.message || `Failed to upload ${file.name}.`);
          continue;
        }
        if (data.attachment) {
          setPendingAttachments(prev => [...prev, data.attachment as AttachmentItem]);
        }
        if (data.quota) {
          setQuota(data.quota as StorageQuotaSummary);
        }
      }
    } catch {
      setError('Upload failed while offline.');
    } finally {
      setUploadingFiles(false);
    }
  }, [selectedConversationId]);

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
    setRightDrawer('none');
    setPendingAttachments([]);
    setConversationFiles([]);
    setQuota(null);
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
    const attached = pendingAttachments;
    if (!body && attached.length === 0) return;
    setError('');
    setComposer('');
    setPendingAttachments([]);

    const clientMsgId = generateClientMsgId();
    const createdAt = new Date().toISOString();

    if (attached.length === 0) {
      const item: QueuedMessage = {
        clientMsgId,
        conversationId: selectedConversationId,
        body,
        createdAt
      };
      await enqueueOfflineMessage(item);
    }

    setMessages(prev => [
      ...prev,
      {
        id: clientMsgId,
        conversationId: selectedConversationId,
        seq: Number.MAX_SAFE_INTEGER,
        senderId: currentUserId,
        senderDisplayName: currentDisplayName,
        senderIsUnverified: currentIsUnverified,
        clientMsgId,
        body,
        createdAt,
        pending: true,
        attachments: attached
      }
    ]);
    requestAnimationFrame(() => feedEndRef.current?.scrollIntoView({ behavior: 'smooth' }));

    try {
      const res = await fetch(`/api/conversations/${selectedConversationId}/messages`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          clientMsgId,
          body,
          attachmentIds: attached.map(a => a.id)
        })
      });
      if (res.ok) {
        const saved: ChatMessageItem = await res.json();
        await dequeueOfflineMessage(clientMsgId);
        setMessages(prev => prev.map(m => (m.clientMsgId === clientMsgId ? saved : m)));
        await loadConversations();
        if (rightDrawer === 'files' && attached.length > 0) {
          void loadConversationFiles(selectedConversationId);
        }
      } else {
        const err = await res.json().catch(() => ({}));
        setError(err.message || 'Unable to send message.');
        if (res.status === 400 || res.status === 403 || res.status === 409) {
          await dequeueOfflineMessage(clientMsgId);
          setMessages(prev => prev.filter(m => m.clientMsgId !== clientMsgId));
          if (attached.length > 0) setPendingAttachments(attached);
        }
      }
    } catch {
      if (attached.length > 0) {
        setPendingAttachments(attached);
        setMessages(prev => prev.filter(m => m.clientMsgId !== clientMsgId));
        setError('Offline — reconnect to send messages with attachments.');
      } else {
        setError('Offline — message queued locally and will send automatically on reconnect.');
      }
    }
  }

  function handleFileInputChange(e: ChangeEvent<HTMLInputElement>) {
    const files = e.target.files ? Array.from(e.target.files) : [];
    e.target.value = '';
    if (files.length > 0) {
      void uploadFilesToConversation(files);
    }
  }

  function handleComposerPaste(e: ClipboardEvent<HTMLTextAreaElement>) {
    const items = e.clipboardData?.items;
    if (!items) return;
    const pastedFiles: File[] = [];
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (item.kind === 'file') {
        const f = item.getAsFile();
        if (f) pastedFiles.push(f);
      }
    }
    if (pastedFiles.length > 0) {
      e.preventDefault();
      void uploadFilesToConversation(pastedFiles);
    }
  }

  function handleStageDragOver(e: DragEvent<HTMLElement>) {
    if (!selectedConversationId) return;
    e.preventDefault();
    setIsDragOver(true);
  }

  function handleStageDrop(e: DragEvent<HTMLElement>) {
    e.preventDefault();
    setIsDragOver(false);
    if (!selectedConversationId) return;
    const dropped = e.dataTransfer?.files ? Array.from(e.dataTransfer.files) : [];
    if (dropped.length > 0) {
      void uploadFilesToConversation(dropped);
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
      setRightDrawer('none');
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
        <Avatar
          displayName={c.title}
          avatarUrl={isDm ? c.peer?.avatarUrl : null}
          variant={isDm ? 'user' : 'group'}
          presence={isDm ? (c.peer?.isOnline ? 'online' : 'offline') : undefined}
        />
        <div className="nx-conv-meta">
          <div className="nx-conv-top">
            <span className="nx-conv-name">
              <span>{c.title}</span>
              {c.peer && <TrustBadge isUnverified={c.peer.isUnverified} showVerified={false} />}
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
            <div className="nx-inline-actions">
              <Button size="sm" title="Start a direct message" onClick={onOpenDirectory}>
                New DM
              </Button>
              <Button variant="primary" size="sm" title="Create a new group chat" onClick={() => setShowNewGroupModal(true)}>
                <Plus size={14} aria-hidden="true" /> Group
              </Button>
            </div>
          </div>
          <div className="nx-search-input-wrap">
            <Search size={15} aria-hidden="true" />
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
              <Button variant="primary" size="sm" onClick={onOpenDirectory}>
                Find colleagues in Directory
              </Button>
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
      <section
        className={`nx-chat-stage ${isDragOver ? 'drag-over' : ''}`}
        aria-label="Message thread"
        onDragOver={handleStageDragOver}
        onDragLeave={() => setIsDragOver(false)}
        onDrop={handleStageDrop}
      >
        {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        {notice && <ToastBar variant="ok" message={notice} onDismiss={() => setNotice('')} />}

        {!activeConv ? (
          <EmptyState
            icon={<div className="nx-brand-mark" style={{ margin: '0 auto' }}>N</div>}
            title="Welcome to Nexus"
            description="Select a conversation on the left, message a colleague from the Directory, or create a group chat."
            actions={
              <>
                <Button variant="primary" onClick={onOpenDirectory}>
                  <Users size={16} aria-hidden="true" /> Open Directory
                </Button>
                <Button onClick={() => setShowNewGroupModal(true)}>
                  <Plus size={15} aria-hidden="true" /> New Group
                </Button>
              </>
            }
          />
        ) : (
          <>
            <header className="nx-stage-header">
              <div className="nx-stage-title">
                <Button
                  variant="ghost"
                  iconOnly
                  className="nx-mobile-back"
                  onClick={() => onSelectConversation(null)}
                  aria-label="Back to conversations"
                >
                  <ArrowLeft size={16} aria-hidden="true" />
                </Button>
                <Avatar
                  displayName={activeConv.title}
                  avatarUrl={activeConv.kind === 'dm' ? activeConv.peer?.avatarUrl : null}
                  variant={activeConv.kind === 'dm' ? 'user' : 'group'}
                  presence={activeConv.kind === 'dm' ? (activeConv.peer?.isOnline ? 'online' : 'offline') : undefined}
                />
                <div>
                  <h3>
                    <span>{activeConv.title}</span>
                    {activeConv.peer && <TrustBadge isUnverified={activeConv.peer.isUnverified} showVerified={false} />}
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

              <div className="nx-inline-actions">
                <Button
                  variant={rightDrawer === 'files' ? 'primary' : 'default'}
                  size="sm"
                  onClick={() => {
                    const next = rightDrawer === 'files' ? 'none' : 'files';
                    setRightDrawer(next);
                    if (next === 'files') void loadConversationFiles(activeConv.id);
                  }}
                >
                  <FolderOpen size={15} aria-hidden="true" /> Files
                </Button>
                {activeConv.kind === 'dm' && activeConv.peer ? (
                  <>
                    <Button size="sm" onClick={() => setShowReportModal(true)}>
                      Report
                    </Button>
                    <Button
                      variant="danger"
                      size="sm"
                      onClick={() => void handleBlockPeer(activeConv.peer!.id, activeConv.peer!.displayName)}
                    >
                      Block
                    </Button>
                  </>
                ) : (
                  <Button
                    variant={rightDrawer === 'members' ? 'primary' : 'default'}
                    size="sm"
                    onClick={() => {
                      const next = rightDrawer === 'members' ? 'none' : 'members';
                      setRightDrawer(next);
                      if (next === 'members') void loadGroupMembers(activeConv.id);
                    }}
                  >
                    <Users size={15} aria-hidden="true" /> Members ({activeConv.memberCount})
                  </Button>
                )}
              </div>
            </header>

            <div className="nx-msg-feed">
              {hasMoreOlder && (
                <Button size="sm" className="nx-load-older" onClick={() => void loadOlderMessages()}>
                  Load older messages
                </Button>
              )}

              {messages.length === 0 && (
                <div style={{ margin: 'auto', textAlign: 'center', color: 'var(--text-muted)', padding: '32px 16px' }}>
                  <MessageSquare size={28} aria-hidden="true" />
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
                  <div key={m.clientMsgId} className="nx-msg-group-wrap">
                    {showDate && <div className="nx-date-sep">{curDate}</div>}
                    {showDivider && <div className="nx-new-divider" role="separator">New messages</div>}
                    <div className={`nx-msg-row ${isOwn ? 'own' : ''} ${compact ? 'compact' : ''} ${m.pending ? 'pending' : ''}`}>
                      {!isOwn && (
                        compact
                          ? <div className="nx-msg-avatar-spacer" />
                          : <Avatar displayName={m.senderDisplayName} size="sm" />
                      )}
                      <div className="nx-msg-bubble">
                        {!compact && (
                          <div className="nx-msg-head">
                            <strong>{isOwn ? 'You' : m.senderDisplayName}</strong>
                            {!isOwn && <TrustBadge isUnverified={m.senderIsUnverified} showVerified={false} />}
                            <span className="nx-msg-time">
                              {m.pending ? 'Sending…' : formatTime(m.createdAt)}
                            </span>
                          </div>
                        )}
                        {m.body && (
                          <div className="nx-msg-body" dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(m.body) }} />
                        )}
                        {m.attachments && m.attachments.length > 0 && (
                          <div className="nx-msg-attachments">
                            {m.attachments.map(att => (
                              att.thumbnailUrl ? (
                                <div key={att.id}>
                                  <a
                                    href={att.downloadUrl}
                                    className="nx-att-thumb-link"
                                    title={`Download ${att.fileName}`}
                                  >
                                    <img className="nx-att-thumb-img" src={att.thumbnailUrl} alt={att.fileName} />
                                  </a>
                                  <div className="nx-muted" style={{ marginTop: 2 }}>
                                    {att.fileName} · {formatBytes(att.sizeBytes)}
                                  </div>
                                </div>
                              ) : (
                                <a key={att.id} href={att.downloadUrl} className="nx-att-file-card">
                                  <span className="nx-att-file-meta">
                                    <FileText size={16} aria-hidden="true" />
                                    <span style={{ minWidth: 0 }}>
                                      <div className="nx-att-file-title">{att.fileName}</div>
                                      <div className="nx-muted">{formatBytes(att.sizeBytes)}</div>
                                    </span>
                                  </span>
                                  <Download size={15} aria-hidden="true" />
                                </a>
                              )
                            ))}
                          </div>
                        )}
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
              {pendingAttachments.length > 0 && (
                <div className="nx-pending-attachments" aria-label="Pending attachments">
                  {pendingAttachments.map(att => (
                    <div className="nx-att-chip" key={att.id}>
                      {att.thumbnailUrl ? (
                        <img src={att.thumbnailUrl} alt={att.fileName} />
                      ) : (
                        <FileText size={15} aria-hidden="true" />
                      )}
                      <span className="nx-att-chip-name" title={att.fileName}>{att.fileName}</span>
                      <span className="nx-muted">{formatBytes(att.sizeBytes)}</span>
                      <Button
                        variant="ghost"
                        size="sm"
                        iconOnly
                        aria-label={`Remove ${att.fileName}`}
                        onClick={() => setPendingAttachments(prev => prev.filter(p => p.id !== att.id))}
                      >
                        <X size={13} aria-hidden="true" />
                      </Button>
                    </div>
                  ))}
                </div>
              )}

              <input
                ref={fileInputRef}
                type="file"
                multiple
                style={{ display: 'none' }}
                onChange={handleFileInputChange}
              />

              <div className="nx-composer-box">
                <Button
                  variant="ghost"
                  size="sm"
                  iconOnly
                  aria-label="Attach files"
                  title="Attach files (or paste image / drag & drop)"
                  disabled={uploadingFiles}
                  onClick={() => fileInputRef.current?.click()}
                >
                  <Paperclip size={16} aria-hidden="true" />
                </Button>
                <textarea
                  rows={1}
                  aria-label="Message"
                  placeholder={uploadingFiles ? 'Uploading attachment…' : `Message ${activeConv.title}…`}
                  value={composer}
                  onKeyDown={handleComposerKeyDown}
                  onPaste={handleComposerPaste}
                  onChange={e => {
                    setComposer(e.target.value);
                    if (hubRef.current?.state === signalR.HubConnectionState.Connected && selectedConversationId) {
                      void hubRef.current.invoke('SendTyping', selectedConversationId).catch(() => {});
                    }
                  }}
                />
                <Button
                  type="submit"
                  variant="primary"
                  size="sm"
                  disabled={uploadingFiles || (!composer.trim() && pendingAttachments.length === 0)}
                >
                  <Send size={14} aria-hidden="true" /> Send
                </Button>
              </div>
              <div className="nx-composer-hint">
                <span>Paste images (Ctrl+V), drag &amp; drop files, or use **bold**, *italic*, `code`</span>
                <span>Enter to send · Shift+Enter for new line</span>
              </div>
            </form>
          </>
        )}
      </section>

      {/* Group Members Drawer */}
      {activeConv && activeConv.kind !== 'dm' && rightDrawer === 'members' && (
        <aside className="nx-drawer" aria-label="Group members">
          <div className="nx-drawer-head">
            <strong>Members ({groupMembers.length})</strong>
            <Button variant="ghost" size="sm" onClick={() => setRightDrawer('none')}>Close</Button>
          </div>
          <div className="nx-drawer-body">
            {(activeConv.role === 'Owner' || activeConv.role === 'Admin') && (
              <Field label="Add colleague" htmlFor="add-group-member" compact>
                <div className="nx-inline-actions">
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
                  <Button variant="primary" size="sm" disabled={!addMemberId} onClick={() => void handleAddMember()}>
                    Add
                  </Button>
                </div>
              </Field>
            )}

            <div>
              {groupMembers.map(gm => (
                <div className="nx-row" key={gm.userId}>
                  <div style={{ minWidth: 0 }}>
                    <div className="nx-user-name-wrap">
                      <strong style={{ fontSize: 13 }}>{gm.displayName}</strong>
                      <Badge variant="role">{gm.role}</Badge>
                      <TrustBadge isUnverified={gm.isUnverified} showVerified={false} />
                    </div>
                    <div className="nx-muted">@{gm.username}</div>
                  </div>
                  {(activeConv.role === 'Owner' || activeConv.role === 'Admin') &&
                    gm.userId !== currentUserId &&
                    gm.role !== 'Owner' && (
                      <Button variant="danger" size="sm" onClick={() => void handleRemoveMember(gm.userId)}>
                        Remove
                      </Button>
                    )}
                </div>
              ))}
            </div>

            <div className="nx-drawer-footer">
              <Button variant="danger" fullWidth onClick={() => void handleLeaveGroup()}>
                Leave group
              </Button>
            </div>
          </div>
        </aside>
      )}

      {/* Conversation Files Drawer (M3.5) */}
      {activeConv && rightDrawer === 'files' && (
        <aside className="nx-drawer" aria-label="Conversation files">
          <div className="nx-drawer-head">
            <strong>Files ({conversationFiles.length})</strong>
            <Button variant="ghost" size="sm" onClick={() => setRightDrawer('none')}>Close</Button>
          </div>
          <div className="nx-drawer-body">
            {quota && (
              <div className="nx-quota-box">
                <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                  <strong>Conversation storage</strong>
                  <span className="nx-muted">
                    {formatBytes(quota.conversationUsedBytes)} / {formatBytes(quota.conversationQuotaBytes)}
                  </span>
                </div>
                <div className="nx-quota-bar">
                  <div
                    className={`nx-quota-fill ${quota.conversationWarning ? 'warn' : ''}`}
                    style={{ width: `${Math.min(100, Math.round((quota.conversationUsedBytes / Math.max(1, quota.conversationQuotaBytes)) * 100))}%` }}
                  />
                </div>
                {(quota.conversationWarning || quota.userWarning) && (
                  <ToastBar
                    variant="error"
                    rounded
                    message="Storage usage has exceeded 80% of quota."
                  />
                )}
              </div>
            )}

            {conversationFiles.length === 0 ? (
              <p className="nx-muted" style={{ textAlign: 'center', marginTop: 18 }}>
                No files shared in this conversation yet.
              </p>
            ) : (
              <div>
                {conversationFiles.map(f => (
                  <div className="nx-row" key={f.id}>
                    <div className="nx-att-file-meta">
                      {f.thumbnailUrl ? (
                        <img src={f.thumbnailUrl} alt="" style={{ width: 32, height: 32, borderRadius: 6, objectFit: 'cover' }} />
                      ) : (
                        <FileText size={18} aria-hidden="true" />
                      )}
                      <div style={{ minWidth: 0 }}>
                        <div className="nx-att-file-title">{f.fileName}</div>
                        <div className="nx-muted">{formatBytes(f.sizeBytes)}</div>
                      </div>
                    </div>
                    <a href={f.downloadUrl} className="nx-btn sm" title={`Download ${f.fileName}`}>
                      <Download size={13} aria-hidden="true" />
                    </a>
                  </div>
                ))}
              </div>
            )}
          </div>
        </aside>
      )}

      {/* New Group Modal (Radix Dialog) */}
      <Modal
        open={showNewGroupModal}
        onOpenChange={setShowNewGroupModal}
        title="Create group chat"
        footer={
          <>
            <Button onClick={() => setShowNewGroupModal(false)}>Cancel</Button>
            <Button type="submit" form="create-group-form" variant="primary">Create group</Button>
          </>
        }
      >
        <form id="create-group-form" onSubmit={handleCreateGroup}>
          <Field label="Group name" htmlFor="modal-group-name">
            <input
              id="modal-group-name"
              type="text"
              required
              maxLength={120}
              placeholder="e.g. Engineering, Ops Team…"
              value={newGroupTitle}
              onChange={e => setNewGroupTitle(e.target.value)}
            />
          </Field>

          <Field label="Add colleagues (optional)" compact>
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
                      <Avatar displayName={u.displayName} avatarUrl={u.avatarUrl} size="sm" />
                      <div>
                        <div className="nx-user-name-wrap">
                          <strong style={{ fontSize: 13 }}>{u.displayName}</strong>
                          <TrustBadge isUnverified={u.isUnverified} showVerified={false} />
                        </div>
                        <div className="nx-muted">@{u.username}{u.departmentName ? ` · ${u.departmentName}` : ''}</div>
                      </div>
                    </label>
                  );
                })}
              </div>
            )}
          </Field>
        </form>
      </Modal>

      {/* Report User Modal (Radix Dialog) */}
      <Modal
        open={showReportModal && Boolean(activeConv?.peer)}
        onOpenChange={setShowReportModal}
        title={activeConv?.peer ? `Report ${activeConv.peer.displayName}` : 'Report user'}
        footer={
          <>
            <Button onClick={() => setShowReportModal(false)}>Cancel</Button>
            <Button type="submit" form="report-user-form" variant="danger">Submit report</Button>
          </>
        }
      >
        <form id="report-user-form" onSubmit={handleSubmitReport}>
          <Field label="Reason" htmlFor="report-reason">
            <select id="report-reason" value={reportReason} onChange={e => setReportReason(e.target.value)}>
              <option value="spam">Spam or unsolicited messages</option>
              <option value="impersonation">Impersonation</option>
              <option value="harassment">Harassment or abuse</option>
              <option value="other">Other</option>
            </select>
          </Field>
          <Field label="Additional details (optional)" htmlFor="report-details" compact>
            <textarea
              id="report-details"
              rows={3}
              maxLength={1000}
              value={reportDetails}
              onChange={e => setReportDetails(e.target.value)}
            />
          </Field>
        </form>
      </Modal>
    </div>
  );
}
