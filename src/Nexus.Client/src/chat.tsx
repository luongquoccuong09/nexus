import { scopedFetch as fetch, organizationId, messagingHub } from "./scope";
import { ChangeEvent, ClipboardEvent, DragEvent, FormEvent, KeyboardEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { ArrowLeft, Ban, Download, FileText, Flag, FolderOpen, MessageSquare, MoreVertical, Paperclip, Plus, Search, Send, Users, X } from 'lucide-react';
import { renderSafeMarkdown } from './markdown';
import {
  dequeueOfflineMessage,
  enqueueOfflineMessage,
  generateClientMsgId,
  getConversationCursor,
  listQueuedMessages,
  reconnectDelayWithJitter,
  setConversationCursor
} from './offlineQueue';
import { Avatar, Badge, Button, EmptyState, Field, Modal, ToastBar } from './ui';

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
    isOnline: boolean;
    avatarUrl?: string | null;
  } | null;
}

function applyReadState(conversation: ConversationListItem, lastReadSeq: number): ConversationListItem {
  if (lastReadSeq <= conversation.lastReadSeq) return conversation;

  return {
    ...conversation,
    lastReadSeq,
    unreadCount: Math.max(0, Math.min(conversation.unreadCount, conversation.nextSeq - lastReadSeq))
  };
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
  previewUrl?: string | null;
  createdAt: string;
}

export interface StorageQuotaSummary {
  conversationUsedBytes: number;
  conversationQuotaBytes: number;
}

export interface ChatMessageItem {
  id: string;
  conversationId: string;
  seq: number;
  senderId: string;
  senderDisplayName: string;
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
  departmentName?: string | null;
  avatarUrl?: string | null;
}

interface GroupMemberItem {
  userId: string;
  username: string;
  displayName: string;
  role: string;
}

export interface CommunityFeedDelta {
  type: 'reaction' | 'comment' | 'pin';
  postId: string;
  emoji?: string;
  userId?: string;
  reacted?: boolean;
  isPinned?: boolean;
  comment?: {
    id: string;
    postId: string;
    authorUserId: string;
    authorDisplayName: string;
    authorIsUnverified: boolean;
    body: string;
    createdAt: string;
  };
}

interface ChatWorkspaceProps {
  currentUserId: string;
  currentDisplayName: string;
  initialVersion: string | null;
  selectedConversationId: string | null;
  directoryUsers: DirectoryUserLite[];
  onSelectConversation: (id: string | null) => void;
  onNewChat: () => void;
  onConnectionStateChange: (state: 'connected' | 'reconnecting' | 'offline') => void;
  onUnreadTotalChange: (total: number) => void;
  onCommunityFeedChanged: (conversationId?: string, delta?: CommunityFeedDelta | null) => void;
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
  initialVersion,
  selectedConversationId,
  directoryUsers,
  onSelectConversation,
  onNewChat,
  onConnectionStateChange,
  onUnreadTotalChange,
  onCommunityFeedChanged,
  onVersionStale
}: ChatWorkspaceProps) {
  const offlineScope = `${currentUserId}:${organizationId ?? "personal"}`;
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
  const [loadingMembers, setLoadingMembers] = useState(false);
  const [addMemberId, setAddMemberId] = useState('');
  const [conversationFiles, setConversationFiles] = useState<AttachmentItem[]>([]);
  const [loadingFiles, setLoadingFiles] = useState(false);
  const [quota, setQuota] = useState<StorageQuotaSummary | null>(null);
  const [pendingAttachments, setPendingAttachments] = useState<AttachmentItem[]>([]);
  const [uploadingFiles, setUploadingFiles] = useState(false);
  const [isDragOver, setIsDragOver] = useState(false);

  // Report modal (DM), More actions menu & Image lightbox (Teams-style inline viewer)
  const [moreMenuOpen, setMoreMenuOpen] = useState(false);
  const [showReportModal, setShowReportModal] = useState(false);
  const [reportReason, setReportReason] = useState('spam');
  const [reportDetails, setReportDetails] = useState('');
  const [lightboxImage, setLightboxImage] = useState<AttachmentItem | null>(null);

  const moreMenuRef = useRef<HTMLDivElement | null>(null);
  const moreBtnRef = useRef<HTMLButtonElement | null>(null);
  const reportMenuItemRef = useRef<HTMLButtonElement | null>(null);
  const blockMenuItemRef = useRef<HTMLButtonElement | null>(null);

  const hubRef = useRef<signalR.HubConnection | null>(null);
  const activeConvRef = useRef<string | null>(selectedConversationId);
  const feedEndRef = useRef<HTMLDivElement | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const typingTimerRef = useRef<number | null>(null);
  activeConvRef.current = selectedConversationId;

  const loadConversationsRef = useRef<() => Promise<void>>(async () => {});
  const syncDeltaRef = useRef<(convId: string) => Promise<void>>(async () => {});
  const reconcileRef = useRef<() => Promise<void>>(async () => {});
  const connStateCbRef = useRef(onConnectionStateChange);
  const communityFeedCbRef = useRef(onCommunityFeedChanged);
  const conversationRefreshRef = useRef({ timer: null as number | null, queued: false });
  const conversationListRequest = useRef(0);
  const deltaRequests = useRef(new Map<string, { queued: boolean; full: boolean; promise: Promise<void> }>());
  const messagesCacheRef = useRef(new Map<string, { items: ChatMessageItem[]; hasMore: boolean; nextBeforeSeq: number | null; lastRead: number }>());
  const cursorCacheRef = useRef(new Map<string, number>());
  const flushOfflineQueueRef = useRef<() => Promise<void>>(async () => {});
  const markReadRef = useRef<(convId: string, seq: number) => Promise<void>>(async () => {});
  const currentUserIdRef = useRef(currentUserId);
  currentUserIdRef.current = currentUserId;
  connStateCbRef.current = onConnectionStateChange;
  communityFeedCbRef.current = onCommunityFeedChanged;

  const scheduleConversationRefresh = useCallback(function scheduleConversationRefresh() {
    const refresh = conversationRefreshRef.current;
    if (refresh.timer !== null) {
      refresh.queued = true;
      return;
    }

    const timer = window.setTimeout(() => {
      refresh.queued = false;
      void loadConversationsRef.current().catch(() => {
        // The reconnect or visibility check will retry a failed background refresh.
      }).finally(() => {
        if (refresh.timer !== timer) return;
        refresh.timer = null;
        if (refresh.queued) scheduleConversationRefresh();
      });
    }, 120);
    refresh.timer = timer;
  }, []);

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
    const requestId = ++conversationListRequest.current;
    const res = await fetch('/api/conversations?limit=50', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      if (requestId !== conversationListRequest.current) return;

      const items: ConversationListItem[] = data.items ?? [];
      setConversations(previous => {
        const readByConversation = new Map(previous.map(item => [item.id, item.lastReadSeq]));
        return items.map(item => applyReadState(item, readByConversation.get(item.id) ?? 0));
      });
      if (!activeConvRef.current && items.length > 0 && window.innerWidth > 860) {
        onSelectConversation(items[0].id);
      }
    }
  }, [onSelectConversation]);

  useEffect(() => {
    onUnreadTotalChange(conversations.reduce((sum, item) => sum + item.unreadCount, 0));
  }, [conversations, onUnreadTotalChange]);

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
    const queued = (await listQueuedMessages(offlineScope)).filter(q => q.conversationId === convId);
    if (activeConvRef.current !== convId) return;

    const seen = new Set(serverItems.map(m => m.clientMsgId));
    const optimistic: ChatMessageItem[] = queued
      .filter(q => !seen.has(q.clientMsgId))
      .map(q => ({
        id: q.clientMsgId,
        conversationId: q.conversationId,
        seq: Number.MAX_SAFE_INTEGER,
        senderId: currentUserId,
        senderDisplayName: currentDisplayName,
        clientMsgId: q.clientMsgId,
        body: q.body,
        createdAt: q.createdAt,
        pending: true
      }));

    const nextLastRead = data.lastReadSeq ?? 0;
    const nextHasMore = Boolean(data.hasMore);
    const nextBefore = typeof data.nextBeforeSeq === 'number' ? data.nextBeforeSeq : null;
    const combined = [...serverItems, ...optimistic];
    messagesCacheRef.current.set(convId, { items: combined, hasMore: nextHasMore, nextBeforeSeq: nextBefore, lastRead: nextLastRead });
    setLastReadSnapshot(nextLastRead);
    setHasMoreOlder(nextHasMore);
    setNextBeforeSeq(nextBefore);
    setMessages(combined);
    if (typeof data.changeSeq === 'number') {
      cursorCacheRef.current.set(convId, data.changeSeq);
      void setConversationCursor(offlineScope, convId, data.changeSeq);
    }
    const maxSeq = serverItems.length > 0 ? serverItems[serverItems.length - 1].seq : 0;
    if (maxSeq > nextLastRead) {
      void markRead(convId, maxSeq);
    }
    requestAnimationFrame(() => feedEndRef.current?.scrollIntoView({ behavior: 'auto' }));
  }, [currentDisplayName, currentUserId, markRead, offlineScope]);

  const loadOlderMessages = useCallback(async () => {
    if (!selectedConversationId || !nextBeforeSeq) return;
    const res = await fetch(
      `/api/conversations/${selectedConversationId}/messages?beforeSeq=${nextBeforeSeq}&limit=50`,
      { credentials: 'same-origin' }
    );
    if (!res.ok || activeConvRef.current !== selectedConversationId) return;
    const data = await res.json();
    if (activeConvRef.current !== selectedConversationId) return;
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
    setLoadingMembers(true);
    try {
      const res = await fetch(`/api/conversations/${convId}/members?limit=50`, { credentials: 'same-origin' });
      if (res.ok && activeConvRef.current === convId) {
        const data = await res.json();
        setGroupMembers(data.items ?? []);
      }
    } finally {
      if (activeConvRef.current === convId) {
        setLoadingMembers(false);
      }
    }
  }, []);

  const loadConversationFiles = useCallback(async (convId: string) => {
    setLoadingFiles(true);
    try {
      const res = await fetch(`/api/conversations/${convId}/files?limit=50`, { credentials: 'same-origin' });
      if (res.ok && activeConvRef.current === convId) {
        const data = await res.json();
        setConversationFiles(data.items ?? []);
        setQuota(data.quota ?? null);
      }
    } finally {
      if (activeConvRef.current === convId) {
        setLoadingFiles(false);
      }
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
    let cursor = cursorCacheRef.current.get(convId) ?? await getConversationCursor(offlineScope, convId);
    if (cursor > 0) cursorCacheRef.current.set(convId, cursor);
    if (activeConvRef.current !== convId) return;
    if (cursor <= 0) {
      await loadMessagesFull(convId);
      return;
    }

    let readThrough = 0;
    try {
      while (activeConvRef.current === convId) {
        const res = await fetch(`/api/conversations/${convId}/changes?after=${cursor}&limit=50`, { credentials: 'same-origin' });
        if (activeConvRef.current !== convId) return;
        if (res.status === 410) {
          await loadMessagesFull(convId);
          return;
        }
        if (!res.ok) return;

        const data = await res.json();
        if (activeConvRef.current !== convId) return;
        const events: Array<{ changeSeq: number; eventType: string; payload: ChatMessageItem }> = data.items ?? [];
        if (events[0] && events[0].changeSeq > cursor + 1) {
          await loadMessagesFull(convId);
          return;
        }

        const added = events
          .filter(event => event.changeSeq > cursor && event.eventType === 'message.created' && event.payload)
          .map(event => event.payload);
        if (added.length > 0) {
          setMessages(prev => {
            const byClientId = new Map(prev.map(message => [message.clientMsgId, message]));
            for (const message of added) byClientId.set(message.clientMsgId, message);
            const next = [...byClientId.values()].sort((a, b) => a.seq - b.seq);
            const cached = messagesCacheRef.current.get(convId);
            if (cached) messagesCacheRef.current.set(convId, { ...cached, items: next });
            return next;
          });
          readThrough = Math.max(readThrough, ...added.map(message => message.seq));
          requestAnimationFrame(() => feedEndRef.current?.scrollIntoView({ behavior: 'smooth' }));
        }

        const lastEventSeq = events.at(-1)?.changeSeq ?? cursor;
        const nextCursor = Math.max(lastEventSeq, typeof data.nextAfter === 'number' ? data.nextAfter : cursor);
        if (nextCursor <= cursor) return;
        cursorCacheRef.current.set(convId, nextCursor);
        void setConversationCursor(offlineScope, convId, nextCursor);
        cursor = nextCursor;
        if (!data.hasMore) return;
      }
    } finally {
      if (readThrough > 0 && activeConvRef.current === convId) {
        void markRead(convId, readThrough).catch(() => {
          // Reading the conversation again will reconcile the read cursor.
        });
      }
    }
  }, [loadMessagesFull, markRead, offlineScope]);

  const requestConversationDelta = useCallback((convId: string, full = false) => {
    const existing = deltaRequests.current.get(convId);
    if (existing) {
      existing.queued = true;
      existing.full ||= full;
      return existing.promise;
    }

    const request = { queued: false, full, promise: Promise.resolve() };
    deltaRequests.current.set(convId, request);
    request.promise = (async () => {
      do {
        const load = request.full ? loadMessagesFull : syncConversationDelta;
        request.queued = false;
        request.full = false;
        await load(convId);
      } while (request.queued && activeConvRef.current === convId);
    })().finally(() => deltaRequests.current.delete(convId));
    return request.promise;
  }, [loadMessagesFull, syncConversationDelta]);

  const flushOfflineQueue = useCallback(async () => {
    const queued = await listQueuedMessages(offlineScope);
    for (const item of queued) {
      try {
        const res = await fetch(`/api/conversations/${item.conversationId}/messages`, {
          method: 'POST',
          credentials: 'same-origin',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ clientMsgId: item.clientMsgId, body: item.body })
        });
        if (res.ok || res.status === 403 || res.status === 409) {
          await dequeueOfflineMessage(offlineScope, item.clientMsgId);
        }
      } catch {
        break;
      }
    }
  }, [offlineScope]);

  const reconcileAll = useCallback(async () => {
    await checkApiVersion();
    await flushOfflineQueue();
    await loadConversations();
    if (activeConvRef.current) {
      await requestConversationDelta(activeConvRef.current);
    }
  }, [checkApiVersion, flushOfflineQueue, loadConversations, requestConversationDelta]);

  loadConversationsRef.current = loadConversations;
  syncDeltaRef.current = requestConversationDelta;
  reconcileRef.current = reconcileAll;
  flushOfflineQueueRef.current = flushOfflineQueue;
  markReadRef.current = markRead;

  useEffect(() => {
    void loadConversations();
  }, [loadConversations]);

  useEffect(() => {
    setRightDrawer('none');
    setMoreMenuOpen(false);
    setPendingAttachments([]);
    setConversationFiles([]);
    setLoadingFiles(false);
    setGroupMembers([]);
    setLoadingMembers(false);
    setQuota(null);
    setTypingText('');
    setError('');
    const cached = selectedConversationId ? messagesCacheRef.current.get(selectedConversationId) : undefined;
    if (cached) {
      setMessages(cached.items);
      setHasMoreOlder(cached.hasMore);
      setNextBeforeSeq(cached.nextBeforeSeq);
      setLastReadSnapshot(cached.lastRead);
    } else {
      setMessages([]);
      setHasMoreOlder(false);
      setNextBeforeSeq(null);
      setLastReadSnapshot(0);
    }
    if (selectedConversationId) {
      void requestConversationDelta(selectedConversationId, !cached).catch(() => {
        if (activeConvRef.current === selectedConversationId) setError('Unable to load messages.');
      });
    }
  }, [selectedConversationId, requestConversationDelta]);

  useEffect(() => {
    let disposed = false;
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(messagingHub)
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: ctx => reconnectDelayWithJitter(ctx.previousRetryCount)
      })
      .build();

    connection.on('ConversationChanged', (evt: { conversationId: string; changeSeq: number; message?: ChatMessageItem | null }) => {
      const incoming = evt.message;
      const isActive = evt.conversationId === activeConvRef.current;

      if (incoming) {
        const isOwn = incoming.senderId === currentUserIdRef.current;
        const mergeMessage = (prev: ChatMessageItem[]) => {
          const byClientId = new Map(prev.map(m => [m.clientMsgId, m]));
          byClientId.set(incoming.clientMsgId, incoming);
          return [...byClientId.values()].sort((a, b) => a.seq - b.seq);
        };

        const cached = messagesCacheRef.current.get(evt.conversationId);
        if (cached) {
          messagesCacheRef.current.set(evt.conversationId, { ...cached, items: mergeMessage(cached.items) });
        }
        if (isActive) {
          setMessages(prev => mergeMessage(prev));
          requestAnimationFrame(() => feedEndRef.current?.scrollIntoView({ behavior: 'smooth' }));
          if (!isOwn) void markReadRef.current(evt.conversationId, incoming.seq).catch(() => {});
        }

        let foundInList = false;
        setConversations(prev => {
          const next = prev.map(c => {
            if (c.id !== evt.conversationId) return c;
            foundInList = true;
            const nextSeq = Math.max(c.nextSeq, incoming.seq);
            const lastReadSeq = isActive || isOwn ? Math.max(c.lastReadSeq, incoming.seq) : c.lastReadSeq;
            return {
              ...c,
              nextSeq,
              changeSeq: Math.max(c.changeSeq, evt.changeSeq),
              lastReadSeq,
              unreadCount: Math.max(0, nextSeq - lastReadSeq),
              updatedAt: incoming.createdAt
            };
          });
          return foundInList
            ? next.sort((a, b) => Date.parse(b.updatedAt ?? '') - Date.parse(a.updatedAt ?? ''))
            : prev;
        });

        const knownSeq = cursorCacheRef.current.get(evt.conversationId) ?? 0;
        if (knownSeq > 0 && evt.changeSeq === knownSeq + 1) {
          cursorCacheRef.current.set(evt.conversationId, evt.changeSeq);
          void setConversationCursor(offlineScope, evt.conversationId, evt.changeSeq);
          if (!foundInList) scheduleConversationRefresh();
          return;
        }
        if (knownSeq > 0 && evt.changeSeq <= knownSeq) {
          if (!foundInList) scheduleConversationRefresh();
          return;
        }
        if (isActive) {
          void syncDeltaRef.current(evt.conversationId).catch(() => {});
        }
        if (!foundInList) scheduleConversationRefresh();
        return;
      }

      if (isActive) {
        const knownSeq = cursorCacheRef.current.get(evt.conversationId) ?? 0;
        if (evt.changeSeq > knownSeq) {
          void syncDeltaRef.current(evt.conversationId)
            .catch(() => {})
            .finally(() => { if (!disposed) scheduleConversationRefresh(); });
        } else {
          scheduleConversationRefresh();
        }
      } else {
        scheduleConversationRefresh();
      }
    });

    connection.on('ReadStateUpdated', (evt: { conversationId: string; lastReadSeq: number }) => {
      setConversations(previous => previous.map(item =>
        item.id === evt.conversationId ? applyReadState(item, evt.lastReadSeq) : item
      ));
    });

    connection.on('CommunityFeedChanged', (evt: { conversationId: string; delta?: CommunityFeedDelta | null }) => {
      communityFeedCbRef.current(evt.conversationId, evt.delta ?? null);
    });

    connection.on('TypingIndicator', (evt: { conversationId: string; displayName: string }) => {
      if (evt.conversationId === activeConvRef.current) {
        setTypingText(`${evt.displayName} is typing…`);
        if (typingTimerRef.current) window.clearTimeout(typingTimerRef.current);
        typingTimerRef.current = window.setTimeout(() => setTypingText(''), 2600);
      }
    });

    connection.onreconnecting(() => {
      if (!disposed) connStateCbRef.current('reconnecting');
    });
    const onReconnected = () => {
      if (!disposed) {
        connStateCbRef.current('connected');
        communityFeedCbRef.current();
        void reconcileRef.current();
      }
    };

    connection.onreconnected(onReconnected);
    connection.onclose(() => {
      if (!disposed) connStateCbRef.current('offline');
    });

    hubRef.current = connection;
    connStateCbRef.current('reconnecting');
    connection.start()
      .then(() => {
        if (!disposed) {
          connStateCbRef.current('connected');
          void flushOfflineQueueRef.current();
        }
      })
      .catch(() => {
        if (!disposed) connStateCbRef.current('offline');
      });

    const onVisible = () => {
      if (document.visibilityState === 'visible') {
        communityFeedCbRef.current();
        void reconcileRef.current();
      }
    };
    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('online', onVisible);

    return () => {
      disposed = true;
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('online', onVisible);
      if (typingTimerRef.current) window.clearTimeout(typingTimerRef.current);
      const refresh = conversationRefreshRef.current;
      if (refresh.timer !== null) window.clearTimeout(refresh.timer);
      refresh.timer = null;
      refresh.queued = false;
      void connection.stop();
    };
  }, [scheduleConversationRefresh]);

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
    const targetConvId = selectedConversationId;
    const optimisticMsg: ChatMessageItem = {
      id: clientMsgId,
      conversationId: targetConvId,
      seq: Number.MAX_SAFE_INTEGER,
      senderId: currentUserId,
      senderDisplayName: currentDisplayName,
      clientMsgId,
      body,
      createdAt,
      pending: true,
      attachments: attached
    };

    // Paint optimistically in 0ms before IndexedDB / network I/O.
    setMessages(prev => {
      const next = [...prev, optimisticMsg];
      const cached = messagesCacheRef.current.get(targetConvId);
      if (cached) messagesCacheRef.current.set(targetConvId, { ...cached, items: next });
      return next;
    });
    setConversations(prev => prev
      .map(c => c.id === targetConvId ? { ...c, updatedAt: createdAt } : c)
      .sort((a, b) => Date.parse(b.updatedAt ?? '') - Date.parse(a.updatedAt ?? ''))
    );
    requestAnimationFrame(() => feedEndRef.current?.scrollIntoView({ behavior: 'smooth' }));

    const queueWrite = attached.length === 0
      ? enqueueOfflineMessage(offlineScope, { clientMsgId, conversationId: targetConvId, body, createdAt })
      : Promise.resolve();

    try {
      await queueWrite;
      const res = await fetch(`/api/conversations/${targetConvId}/messages`, {
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
        void dequeueOfflineMessage(offlineScope, clientMsgId);
        setMessages(prev => {
          const next = prev.map(m => (m.clientMsgId === clientMsgId ? saved : m));
          const cached = messagesCacheRef.current.get(targetConvId);
          if (cached) messagesCacheRef.current.set(targetConvId, { ...cached, items: next });
          return next;
        });
        scheduleConversationRefresh();
        if (rightDrawer === 'files' && attached.length > 0) {
          void loadConversationFiles(targetConvId);
        }
      } else {
        const err = await res.json().catch(() => ({}));
        setError(err.message || 'Unable to send message.');
        if (res.status === 400 || res.status === 403 || res.status === 409) {
          void dequeueOfflineMessage(offlineScope, clientMsgId);
          setMessages(prev => {
            const next = prev.filter(m => m.clientMsgId !== clientMsgId);
            const cached = messagesCacheRef.current.get(targetConvId);
            if (cached) messagesCacheRef.current.set(targetConvId, { ...cached, items: next });
            return next;
          });
          if (attached.length > 0) setPendingAttachments(attached);
        }
      }
    } catch {
      if (attached.length > 0) {
        setPendingAttachments(attached);
        setMessages(prev => {
          const next = prev.filter(m => m.clientMsgId !== clientMsgId);
          const cached = messagesCacheRef.current.get(targetConvId);
          if (cached) messagesCacheRef.current.set(targetConvId, { ...cached, items: next });
          return next;
        });
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

  useEffect(() => {
    if (!moreMenuOpen) return;
    requestAnimationFrame(() => reportMenuItemRef.current?.focus());
    const handleOutsideClick = (ev: MouseEvent) => {
      if (moreMenuRef.current && !moreMenuRef.current.contains(ev.target as Node)) {
        setMoreMenuOpen(false);
      }
    };
    const handleEsc = (ev: globalThis.KeyboardEvent) => {
      if (ev.key === 'Escape') {
        setMoreMenuOpen(false);
        moreBtnRef.current?.focus();
      }
    };
    document.addEventListener('mousedown', handleOutsideClick);
    document.addEventListener('keydown', handleEsc);
    return () => {
      document.removeEventListener('mousedown', handleOutsideClick);
      document.removeEventListener('keydown', handleEsc);
    };
  }, [moreMenuOpen]);

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
            <h2>Chat</h2>
          </div>
          <div className="nx-side-actions">
            <Button variant="primary" size="sm" onClick={onNewChat}>
              <MessageSquare size={14} aria-hidden="true" /> New chat
            </Button>
            <Button size="sm" onClick={() => setShowNewGroupModal(true)}>
              <Plus size={14} aria-hidden="true" /> Create group chat
            </Button>
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
            </div>
          ) : (
            <>
              {dmConvs.length > 0 && (
                <>
                  <div className="nx-section-label">Direct messages ({dmConvs.length})</div>
                  {dmConvs.map(renderConvItem)}
                </>
              )}
              {groupConvs.length > 0 && (
                <>
                  <div className="nx-section-label">Group chats ({groupConvs.length})</div>
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
            description="Select a conversation on the left, start a new chat, or create a group chat."
            actions={
              <>
                <Button variant="primary" onClick={onNewChat}>
                  <MessageSquare size={15} aria-hidden="true" /> New chat
                </Button>
                <Button onClick={() => setShowNewGroupModal(true)}>
                  <Plus size={15} aria-hidden="true" /> Create group chat
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
                <div className="nx-stage-identity">
                  <h3>
                    <span className="nx-stage-name">{activeConv.title}</span>
                  </h3>
                  <div className="nx-stage-subtitle">
                    {activeConv.kind === 'dm' ? (
                      <>
                        <span className={`nx-dot ${activeConv.peer?.isOnline ? 'online' : 'offline'}`} />
                        <span>{activeConv.peer?.isOnline ? 'Online' : 'Offline'}</span>
                        {activeConv.peer?.username && <span className="nx-stage-handle">· @{activeConv.peer.username}</span>}
                      </>
                    ) : (
                      <span>{activeConv.memberCount} {activeConv.memberCount === 1 ? 'member' : 'members'} · Role: {activeConv.role}</span>
                    )}
                  </div>
                </div>
              </div>

              <div className="nx-inline-actions nx-stage-actions">
                <Button
                  variant={rightDrawer === 'files' ? 'primary' : 'default'}
                  size="sm"
                  onClick={() => {
                    const next = rightDrawer === 'files' ? 'none' : 'files';
                    if (next === 'files') {
                      setLoadingFiles(true);
                      void loadConversationFiles(activeConv.id);
                    }
                    setRightDrawer(next);
                  }}
                >
                  <FolderOpen size={15} aria-hidden="true" /> Files
                </Button>
                {activeConv.kind === 'dm' && activeConv.peer ? (
                  <div className="nx-menu-wrap" ref={moreMenuRef}>
                    <button
                      ref={moreBtnRef}
                      type="button"
                      className="nx-btn sm icon-only"
                      aria-label="More actions"
                      title="More actions"
                      aria-haspopup="menu"
                      aria-expanded={moreMenuOpen}
                      onClick={() => setMoreMenuOpen(v => !v)}
                    >
                      <MoreVertical size={15} aria-hidden="true" />
                    </button>
                    {moreMenuOpen && (
                      <div
                        className="nx-dropdown-menu"
                        role="menu"
                        aria-label="Conversation actions"
                        onKeyDown={ev => {
                          if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
                            ev.preventDefault();
                            if (document.activeElement === reportMenuItemRef.current) {
                              blockMenuItemRef.current?.focus();
                            } else {
                              reportMenuItemRef.current?.focus();
                            }
                          }
                        }}
                      >
                        <button
                          ref={reportMenuItemRef}
                          type="button"
                          role="menuitem"
                          className="nx-dropdown-item"
                          onClick={() => {
                            setMoreMenuOpen(false);
                            setShowReportModal(true);
                          }}
                        >
                          <Flag size={14} aria-hidden="true" /> Report user
                        </button>
                        <button
                          ref={blockMenuItemRef}
                          type="button"
                          role="menuitem"
                          className="nx-dropdown-item danger"
                          onClick={() => {
                            setMoreMenuOpen(false);
                            void handleBlockPeer(activeConv.peer!.id, activeConv.peer!.displayName);
                          }}
                        >
                          <Ban size={14} aria-hidden="true" /> Block user
                        </button>
                      </div>
                    )}
                  </div>
                ) : (
                  <Button
                    variant={rightDrawer === 'members' ? 'primary' : 'default'}
                    size="sm"
                    onClick={() => {
                      const next = rightDrawer === 'members' ? 'none' : 'members';
                      if (next === 'members') {
                        setLoadingMembers(true);
                        void loadGroupMembers(activeConv.id);
                      }
                      setRightDrawer(next);
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
                                <div key={att.id} className="nx-att-image-wrap">
                                  <button
                                    type="button"
                                    className="nx-att-thumb-btn"
                                    aria-label={`Preview ${att.fileName}`}
                                    onClick={() => setLightboxImage(att)}
                                  >
                                    <img className="nx-att-thumb-img" src={att.thumbnailUrl} alt={att.fileName} />
                                  </button>
                                  <a
                                    href={att.downloadUrl}
                                    download={att.fileName}
                                    className="nx-att-img-dl"
                                    aria-label={`Download ${att.fileName}`}
                                    title={`Download ${att.fileName} (${formatBytes(att.sizeBytes)})`}
                                  >
                                    <Download size={14} aria-hidden="true" />
                                  </a>
                                </div>
                              ) : (
                                <a key={att.id} href={att.downloadUrl} download={att.fileName} className="nx-att-file-card">
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
            <strong>{loadingMembers ? 'Members' : `Members (${groupMembers.length})`}</strong>
            <Button variant="ghost" size="sm" onClick={() => setRightDrawer('none')}>Close</Button>
          </div>
          <div className="nx-drawer-body">
            {(activeConv.role === 'Owner' || activeConv.role === 'Admin') && (
              <Field label="Add person" htmlFor="add-group-member" compact>
                <div className="nx-inline-actions">
                  <select
                    id="add-group-member"
                    value={addMemberId}
                    onChange={e => setAddMemberId(e.target.value)}
                  >
                    <option value="">Select a person</option>
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
            <strong>{loadingFiles ? 'Files' : `Files (${conversationFiles.length})`}</strong>
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
                    className="nx-quota-fill"
                    style={{ width: `${Math.min(100, Math.round((quota.conversationUsedBytes / Math.max(1, quota.conversationQuotaBytes)) * 100))}%` }}
                  />
                </div>
              </div>
            )}

            {loadingFiles ? (
              <p className="nx-muted" role="status" style={{ textAlign: 'center', marginTop: 18 }}>
                Loading files…
              </p>
            ) : conversationFiles.length === 0 ? (
              <p className="nx-muted" style={{ textAlign: 'center', marginTop: 18 }}>
                No files shared in this conversation yet.
              </p>
            ) : (
              <div>
                {conversationFiles.map(f => (
                  <div className="nx-row" key={f.id}>
                    {f.thumbnailUrl ? (
                      <button
                        type="button"
                        className="nx-file-preview-trigger"
                        onClick={() => setLightboxImage(f)}
                        title={`Preview ${f.fileName}`}
                      >
                        <img src={f.thumbnailUrl} alt="" style={{ width: 32, height: 32, borderRadius: 6, objectFit: 'cover', flexShrink: 0 }} />
                        <span style={{ minWidth: 0 }}>
                          <div className="nx-att-file-title">{f.fileName}</div>
                          <div className="nx-muted">{formatBytes(f.sizeBytes)}</div>
                        </span>
                      </button>
                    ) : (
                      <div className="nx-att-file-meta">
                        <FileText size={18} aria-hidden="true" />
                        <div style={{ minWidth: 0 }}>
                          <div className="nx-att-file-title">{f.fileName}</div>
                          <div className="nx-muted">{formatBytes(f.sizeBytes)}</div>
                        </div>
                      </div>
                    )}
                    <a href={f.downloadUrl} download={f.fileName} className="nx-btn sm" aria-label={`Download ${f.fileName}`} title={`Download ${f.fileName}`}>
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

          <Field label="Add people (optional)" compact>
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
      {/* Image Lightbox Modal (Teams-style full-resolution viewer) */}
      <Modal
        open={Boolean(lightboxImage)}
        onOpenChange={open => { if (!open) setLightboxImage(null); }}
        title={lightboxImage?.fileName ?? 'Image preview'}
        size="xl"
        footer={
          lightboxImage ? (
            <>
              <span className="nx-muted" style={{ marginRight: 'auto', alignSelf: 'center' }}>
                {lightboxImage.imageWidth && lightboxImage.imageHeight
                  ? `${lightboxImage.imageWidth}×${lightboxImage.imageHeight} · `
                  : ''}
                {formatBytes(lightboxImage.sizeBytes)}
              </span>
              <a href={lightboxImage.downloadUrl} download={lightboxImage.fileName} className="nx-btn primary sm">
                <Download size={14} aria-hidden="true" /> Download
              </a>
              <Button size="sm" onClick={() => setLightboxImage(null)}>Close</Button>
            </>
          ) : undefined
        }
      >
        {lightboxImage && (
          <div className="nx-lightbox-body">
            <img
              className="nx-lightbox-img"
              src={lightboxImage.previewUrl ?? lightboxImage.thumbnailUrl ?? lightboxImage.downloadUrl}
              alt={lightboxImage.fileName}
            />
          </div>
        )}
      </Modal>
    </div>
  );
}
