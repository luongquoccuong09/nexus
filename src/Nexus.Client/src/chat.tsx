import { chatFetch as fetch, messagingHub, personalApiPath } from "./scope";
import { ChangeEvent, ClipboardEvent, DragEvent, FormEvent, KeyboardEvent, MouseEvent as ReactMouseEvent, MutableRefObject, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { ArrowLeft, Ban, ChevronDown, ChevronRight, Download, Eye, FileText, Flag, FolderOpen, FolderPlus, Link2, MessageSquare, MoreVertical, Paperclip, Plus, Search, Send, Star, Users, X } from 'lucide-react';
import { renderSafeMarkdown } from './markdown';
import { ChatJumpTarget, copyContentLink, extractContentLinkToken } from './contentLinks';
import { moveIdByOffset, reorderIdList, SidebarSectionDto, useSidebarOrganization } from './sidebarPrefs';
import { SidebarItemActions, SidebarSectionHeader, SidebarSectionModal } from './sidebarControls';
import { ConversationNotificationButton } from './notificationPreferences';
import { useChatDrafts } from './chatDrafts';
import { useChatReading } from './chatReading';
import { shouldSendOnEnter } from './composerKeys';
import { OwnershipTransfer, TransferOwnershipDialog } from './transferOwnership';
import { ListPaging, readCursorPage, useCursorList } from './cursorList';
import { personKey, searchChatCandidates } from './peopleDirectory';
import {
  ChatDraft,
  QueuedMessage,
  clearConversationWork,
  dequeueOfflineMessage,
  generateClientMsgId,
  getConversationCursor,
  listDraftConversationIds,
  listQueuedMessages,
  recordSendFailure,
  reconnectDelayWithJitter,
  restoreQueuedDraft,
  setConversationCursor,
  stageMessage
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
  lastMessagePreview?: {
    bodyPreview: string;
    senderDisplayName: string;
    createdAt: string;
    seq: number;
  } | null;
  peer?: {
    id: string;
    username: string;
    displayName: string;
    isOnline: boolean;
    isBlockedByMe?: boolean;
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
  editedAt?: string | null;
  deletedAt?: string | null;
  pending?: boolean;
  attachments?: AttachmentItem[];
}

export interface DirectoryUserLite {
  id: string;
  username: string;
  displayName: string;
  departmentName?: string | null;
  avatarUrl?: string | null;
  isContact?: boolean;
  workspaceNames?: string[];
}

interface GroupMemberItem {
  userId: string;
  username: string;
  displayName: string;
  role: string;
  isActive?: boolean;
}
const groupMemberKey = (member: GroupMemberItem) => member.userId;

interface ConversationCursor {
  updatedAt: string;
  id: string;
}

interface FilesCursor {
  createdAt: string;
  id: string;
}

async function readPages<Item, Cursor>(
  initial: Cursor | null,
  pageCount: number,
  fetchPage: (cursor: Cursor | null) => Promise<{ items: Item[]; next: Cursor | null }>
) {
  const items: Item[] = [];
  let next = initial;
  let pages = 0;
  do {
    const response = await fetchPage(next);
    items.push(...response.items);
    next = response.next;
    pages++;
  } while (next && pages < pageCount);
  return { items, next, pages };
}

export interface CommunityFeedDelta {
  type: 'reaction' | 'comment' | 'pin' | 'ownership';
  postId?: string;
  communityId?: string;
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
  initialVersion: string | null;
  selectedConversationId: string | null;
  jumpTarget?: ChatJumpTarget | null;
  visible: boolean;
  candidateRevision: number;
  refreshOrgSubscriptionsRef?: MutableRefObject<(() => Promise<void>) | null>;
  onReloadConversationsRef?: MutableRefObject<(() => Promise<void>) | null>;
  onOrganizationAccessRevoked?: (organizationId: string) => void;
  onBlockListChanged: () => void;
  onManageBlockedUsers: () => void;
  onSelectConversation: (id: string | null) => void;
  onNewChat: () => void;
  onOpenContentLink?: (token: string) => void;
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
  initialVersion,
  selectedConversationId,
  jumpTarget,
  visible,
  candidateRevision,
  refreshOrgSubscriptionsRef,
  onReloadConversationsRef,
  onOrganizationAccessRevoked,
  onBlockListChanged,
  onManageBlockedUsers,
  onSelectConversation,
  onNewChat,
  onOpenContentLink,
  onConnectionStateChange,
  onUnreadTotalChange,
  onCommunityFeedChanged,
  onVersionStale
}: ChatWorkspaceProps) {
  const offlineScope = `${currentUserId}:personal`;
  const [conversations, setConversations] = useState<ConversationListItem[]>([]);
  const [moreConversations, setMoreConversations] = useState(false);
  const [loadingConversations, setLoadingConversations] = useState(true);
  const [conversationLoadError, setConversationLoadError] = useState<'refresh' | 'older' | null>(null);
  const [convSearch, setConvSearch] = useState('');
  const [chatFilter, setChatFilter] = useState<'all' | 'dm' | 'group'>('all');
  const [unreadOnly, setUnreadOnly] = useState(false);
  const unreadOnlyRef = useRef(false);
  unreadOnlyRef.current = unreadOnly;
  const [showHiddenConvs, setShowHiddenConvs] = useState(false);
  const [sectionModal, setSectionModal] = useState<{ mode: 'create' } | { mode: 'rename'; section: SidebarSectionDto } | null>(null);
  const [draggedConvId, setDraggedConvId] = useState<string | null>(null);
  const sidebarOrg = useSidebarOrganization('chat', true, currentUserId);
  const [messages, setMessages] = useState<ChatMessageItem[]>([]);
  const [reconnectCount, setReconnectCount] = useState(0);
  const [hasMoreOlder, setHasMoreOlder] = useState(false);
  const [nextBeforeSeq, setNextBeforeSeq] = useState<number | null>(null);
  const [hasNewerMessages, setHasNewerMessages] = useState(false);
  const hasNewerRef = useRef(false);
  hasNewerRef.current = hasNewerMessages;
  const [nextAfterSeq, setNextAfterSeq] = useState<number | null>(null);
  const [highlightedMessageId, setHighlightedMessageId] = useState<string | null>(null);
  const handledJumpNonceRef = useRef(0);
  const pendingJumpRef = useRef<ChatJumpTarget | null>(null);
  const [lastReadSnapshot, setLastReadSnapshot] = useState<number>(0);
  const [typingText, setTypingText] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [storageUnavailable, setStorageUnavailable] = useState(false);
  const onStorageError = useCallback(() => setStorageUnavailable(true), []);
  const { draft, loadingDraft, changeDraft, setLocalDraft, readLocalDraft } = useChatDrafts(
    offlineScope, selectedConversationId, onStorageError);
  const composer = draft?.body ?? '';
  const pendingAttachments = draft?.attachments ?? [];
  const [checkedAttachments, setCheckedAttachments] = useState<{ key: string; missing: string[] } | null>(null);
  const attachmentKey = pendingAttachments.map(item => item.id).join(',');
  const missingAttachments = checkedAttachments?.key === attachmentKey ? checkedAttachments.missing : [];
  const [outgoing, setOutgoing] = useState<QueuedMessage[]>([]);
  const outgoingRef = useRef<QueuedMessage[]>([]);
  const [sending, setSending] = useState(new Set<string>());
  const sendingRef = useRef(new Set<string>());
  const staging = useRef(new Set<string>());
  const discarded = useRef(new Set<string>());

  // New group modal
  const [showNewGroupModal, setShowNewGroupModal] = useState(false);
  const [newGroupTitle, setNewGroupTitle] = useState('');
  const [newGroupSearch, setNewGroupSearch] = useState('');
  const [selectedGroupMemberIds, setSelectedGroupMemberIds] = useState<string[]>([]);

  // Group members & Files drawer (M2.8 / M3.5)
  const [rightDrawer, setRightDrawer] = useState<'none' | 'members' | 'files'>('none');
  const rightDrawerRef = useRef(rightDrawer);
  rightDrawerRef.current = rightDrawer;
  const [groupMemberSearch, setGroupMemberSearch] = useState('');
  const openGroupId = rightDrawer === 'members' ? selectedConversationId : null;
  const openGroupRole = conversations.find(item => item.id === openGroupId)?.role;
  const groupMemberPage = useCursorList(openGroupId,
    async (cursor, signal) => {
      const params = new URLSearchParams({ limit: '50' });
      if (cursor) params.set('cursor', cursor);
      return readCursorPage<GroupMemberItem>(await fetch(`/api/conversations/${openGroupId}/members?${params}`, { signal }),
        'Unable to load group members.');
    }, groupMemberKey);
  const { items: groupMembers, loading: loadingMembers } = groupMemberPage;
  const groupCandidates = useCursorList(showNewGroupModal ? JSON.stringify([currentUserId, candidateRevision, newGroupSearch]) : null,
    (cursor, signal) => searchChatCandidates(newGroupSearch, cursor, signal), personKey, newGroupSearch.trim() ? 180 : 0);
  const addMemberCandidates = useCursorList(openGroupId && (openGroupRole === 'Owner' || openGroupRole === 'Admin')
    ? JSON.stringify([openGroupId, candidateRevision, groupMemberSearch]) : null,
    (cursor, signal) => searchChatCandidates(groupMemberSearch, cursor, signal, openGroupId ?? undefined),
    personKey, groupMemberSearch.trim() ? 180 : 0);
  const refreshGroupMembers = useRef(groupMemberPage.refresh);
  refreshGroupMembers.current = groupMemberPage.refresh;
  const [ownershipTransfer, setOwnershipTransfer] = useState<OwnershipTransfer | null>(null);
  const groupMembersHeading = useRef<HTMLElement | null>(null);
  const [addMemberId, setAddMemberId] = useState('');
  const [conversationFiles, setConversationFiles] = useState<AttachmentItem[]>([]);
  const [loadingFiles, setLoadingFiles] = useState(false);
  const [moreFiles, setMoreFiles] = useState(false);
  const [filesLoadError, setFilesLoadError] = useState<'refresh' | 'older' | null>(null);
  const [quota, setQuota] = useState<StorageQuotaSummary | null>(null);
  const [uploads, setUploads] = useState(new Set<string>());
  const uploadRequests = useRef(new Map<string, AbortController>());
  const uploadingFiles = Boolean(selectedConversationId && uploads.has(selectedConversationId));
  const [isDragOver, setIsDragOver] = useState(false);

  // Report modal (DM), More actions menu & Image lightbox (Teams-style inline viewer)
  const [moreMenuOpen, setMoreMenuOpen] = useState(false);
  const [blockTarget, setBlockTarget] = useState<{ id: string; name: string } | null>(null);
  const [blockingPeer, setBlockingPeer] = useState(false);
  const [blockError, setBlockError] = useState('');
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
  const composerRef = useRef<HTMLTextAreaElement | null>(null);
  const compositionActive = useRef(false);

  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const typingTimerRef = useRef<number | null>(null);
  activeConvRef.current = selectedConversationId;

  const loadConversationsRef = useRef<() => Promise<void>>(async () => {});
  const syncDeltaRef = useRef<(convId: string) => Promise<void>>(async () => {});
  const reconcileRef = useRef<() => Promise<void>>(async () => {});
  const connStateCbRef = useRef(onConnectionStateChange);
  const communityFeedCbRef = useRef(onCommunityFeedChanged);
  const orgRevokedCbRef = useRef(onOrganizationAccessRevoked);
  const discardConversationRef = useRef<(id: string) => void>(() => {});
  const conversationRefreshRef = useRef({ timer: null as number | null, queued: false });
  const conversationListRequest = useRef<AbortController | null>(null);
  const conversationPages = useRef<{ count: number; next: ConversationCursor | null }>({ count: 1, next: null });
  const filesListRequest = useRef<AbortController | null>(null);
  const filePages = useRef<{ count: number; next: FilesCursor | null }>({ count: 1, next: null });
  const deltaRequests = useRef(new Map<string, { queued: boolean; full: boolean; promise: Promise<void> }>());
  const messagesCacheRef = useRef(new Map<string, { items: ChatMessageItem[]; hasMore: boolean; nextBeforeSeq: number | null; lastRead: number }>());
  const cursorCacheRef = useRef(new Map<string, number>());
  const flushOfflineQueueRef = useRef<() => Promise<void>>(async () => {});
  connStateCbRef.current = onConnectionStateChange;
  communityFeedCbRef.current = onCommunityFeedChanged;
  orgRevokedCbRef.current = onOrganizationAccessRevoked;

  const updateOutgoing = useCallback((update: (current: QueuedMessage[]) => QueuedMessage[]) => {
    outgoingRef.current = update(outgoingRef.current);
    setOutgoing(outgoingRef.current);
  }, []);

  const discardConversation = useCallback((id: string) => {
    discarded.current.add(id);
    uploadRequests.current.get(id)?.abort();
    messagesCacheRef.current.delete(id);
    cursorCacheRef.current.delete(id);
    setLocalDraft(id, null);
    updateOutgoing(items => items.filter(item => item.conversationId !== id));
    setConversations(items => items.filter(item => item.id !== id));
    if (activeConvRef.current === id) {
      setMessages([]);
      onSelectConversation(null);
    }
    void clearConversationWork(offlineScope, id).catch(onStorageError);
  }, [offlineScope, onSelectConversation, onStorageError, setLocalDraft, updateOutgoing]);
  discardConversationRef.current = discardConversation;

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

  const loadConversations = useCallback(async (older = false) => {
    const start = older ? conversationPages.current.next : null;
    if (older && !start) return;

    conversationListRequest.current?.abort();
    const controller = new AbortController();
    conversationListRequest.current = controller;
    setLoadingConversations(true);
    setConversationLoadError(null);

    try {
      const { items, next, pages } = await readPages<ConversationListItem, ConversationCursor>(
        start, older ? 1 : conversationPages.current.count, async cursor => {
          const params = new URLSearchParams({ limit: '50' });
          if (unreadOnlyRef.current) params.set('unreadOnly', 'true');
          if (cursor) {
            params.set('beforeAt', cursor.updatedAt);
            params.set('beforeId', cursor.id);
          }
          const response = await fetch(`/api/conversations?${params}`, { signal: controller.signal });
          if (response.status === 401 || response.status === 403) {
            if (!controller.signal.aborted) {
              setConversations([]);
              setMoreConversations(false);
              conversationPages.current = { count: 1, next: null };
            }
            throw new Error('Conversation access is unavailable.');
          }
          if (!response.ok) throw new Error('Unable to load conversations.');
          const page = await response.json();
          return { items: page.items ?? [], next: page.nextBefore ?? null };
        }
      );
      if (controller.signal.aborted) return;

      const selected = activeConvRef.current;
      // Opening an existing chat can select a conversation beyond the loaded sidebar pages.
      if (!older && selected && !items.some(item => item.id === selected) && !discarded.current.has(selected)) {
        const selectedPage = await fetch(`/api/conversations?limit=1&conversationId=${encodeURIComponent(selected)}`, { signal: controller.signal })
          .then(response => response.ok ? response.json() as Promise<{ items: ConversationListItem[] }> : null)
          .catch(() => null);
        if (selectedPage && activeConvRef.current === selected) {
          const { items: selectedItems } = selectedPage;
          if (activeConvRef.current === selected && selectedItems[0]) items.push(selectedItems[0]);
        }
      }
      if (controller.signal.aborted) return;
      conversationPages.current = { count: older ? conversationPages.current.count + pages : pages, next };
      setMoreConversations(next !== null);
      setConversations(previous => {
        const readByConversation = new Map(previous.map(item => [item.id, item.lastReadSeq]));
        const merged = new Map(older ? previous.map(item => [item.id, item]) : []);
        for (const item of items) merged.set(item.id, applyReadState(item, readByConversation.get(item.id) ?? 0));
        return [...merged.values()].sort((a, b) => {
          const diff = Date.parse(b.updatedAt ?? '') - Date.parse(a.updatedAt ?? '');
          return diff || (b.updatedAt ?? '').localeCompare(a.updatedAt ?? '') || b.id.localeCompare(a.id);
        });
      });
      if (!activeConvRef.current && items.length > 0 && window.innerWidth > 860) {
        onSelectConversation(items[0].id);
      }
      if (!older) {
        const authorized = new Set(items.map(item => item.id));
        const ids = new Set(await listDraftConversationIds(offlineScope).catch(() => []));
        for (const message of outgoingRef.current) ids.add(message.conversationId);
        for (const id of ids) {
          if (!authorized.has(id) && !discarded.current.has(id) && !controller.signal.aborted) {
            void revalidateConversationRef.current(id, controller.signal);
          }
        }
      }
    } catch {
      if (!controller.signal.aborted) setConversationLoadError(older ? 'older' : 'refresh');
    } finally {
      if (conversationListRequest.current === controller) {
        conversationListRequest.current = null;
        if (!controller.signal.aborted) setLoadingConversations(false);
      }
    }
  }, [offlineScope, onSelectConversation]);

  useEffect(() => {
    onUnreadTotalChange(conversations.reduce((sum, item) => sum + item.unreadCount, 0));
  }, [conversations, onUnreadTotalChange]);

  const markRead = useCallback(async (convId: string, seq: number) => {
    const response = await fetch(`/api/conversations/${convId}/read`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ lastReadSeq: seq })
    });
    if ([401, 403, 404].includes(response.status)) discardConversation(convId);
    if (!response.ok) throw new Error('Unable to update the read cursor.');
    const { lastReadSeq } = await response.json();
    const cached = messagesCacheRef.current.get(convId);
    if (cached && lastReadSeq > cached.lastRead) messagesCacheRef.current.set(convId, { ...cached, lastRead: lastReadSeq });
    setConversations(items => items.map(item => item.id === convId ? applyReadState(item, lastReadSeq) : item));
    return lastReadSeq as number;
  }, [discardConversation]);

  const loadMessagesFull = useCallback(async (convId: string) => {
    const res = await fetch(`/api/conversations/${convId}/messages?limit=50`, { credentials: 'same-origin' });
    if ([401, 403, 404].includes(res.status)) {
      discardConversation(convId);
      return;
    }
    if (!res.ok) return;
    const data = await res.json();
    const serverItems: ChatMessageItem[] = data.items ?? [];
    if (activeConvRef.current !== convId) return;
    discarded.current.delete(convId);
    const nextLastRead = data.lastReadSeq ?? 0;
    const nextHasMore = Boolean(data.hasMore);
    const nextBefore = typeof data.nextBeforeSeq === 'number' ? data.nextBeforeSeq : null;
    messagesCacheRef.current.set(convId, { items: serverItems, hasMore: nextHasMore, nextBeforeSeq: nextBefore, lastRead: nextLastRead });
    setLastReadSnapshot(nextLastRead);
    setConversations(items => items.map(item => item.id === convId ? applyReadState(item, nextLastRead) : item));
    setHasMoreOlder(nextHasMore);
    setNextBeforeSeq(nextBefore);
    setHasNewerMessages(false);
    setNextAfterSeq(null);
    setMessages(serverItems);
    if (typeof data.changeSeq === 'number') {
      cursorCacheRef.current.set(convId, data.changeSeq);
      void setConversationCursor(offlineScope, convId, data.changeSeq);
    }
  }, [discardConversation, offlineScope]);

  const loadMessagesAround = useCallback(async (convId: string, seq: number): Promise<boolean> => {
    const res = await fetch(`/api/conversations/${convId}/messages?aroundSeq=${seq}&limit=50`, { credentials: 'same-origin' });
    if ([401, 403, 404].includes(res.status)) {
      discardConversation(convId);
      return false;
    }
    if (!res.ok) return false;
    const data = await res.json();
    if (activeConvRef.current !== convId) return false;
    const serverItems: ChatMessageItem[] = data.items ?? [];
    discarded.current.delete(convId);
    const nextLastRead = data.lastReadSeq ?? 0;
    const nextHasMore = Boolean(data.hasMore);
    const nextBefore = typeof data.nextBeforeSeq === 'number' ? data.nextBeforeSeq : null;
    const nextHasNewer = Boolean(data.hasNewer);
    const nextAfter = typeof data.nextAfterSeq === 'number' ? data.nextAfterSeq : null;
    if (!nextHasNewer) {
      messagesCacheRef.current.set(convId, { items: serverItems, hasMore: nextHasMore, nextBeforeSeq: nextBefore, lastRead: nextLastRead });
    }
    setLastReadSnapshot(nextLastRead);
    setHasMoreOlder(nextHasMore);
    setNextBeforeSeq(nextBefore);
    setHasNewerMessages(nextHasNewer);
    setNextAfterSeq(nextAfter);
    setMessages(serverItems);
    return true;
  }, [discardConversation]);

  const loadNewerMessages = useCallback(async () => {
    if (!selectedConversationId || !nextAfterSeq) return;
    const res = await fetch(
      `/api/conversations/${selectedConversationId}/messages?afterSeq=${nextAfterSeq}&limit=50`,
      { credentials: 'same-origin' }
    );
    if ([401, 403, 404].includes(res.status)) discardConversation(selectedConversationId);
    if (!res.ok || activeConvRef.current !== selectedConversationId) return;
    const data = await res.json();
    if (activeConvRef.current !== selectedConversationId) return;
    const newer: ChatMessageItem[] = data.items ?? [];
    const nextHasNewer = Boolean(data.hasNewer);
    const nextAfter = typeof data.nextAfterSeq === 'number' ? data.nextAfterSeq : null;
    setHasNewerMessages(nextHasNewer);
    setNextAfterSeq(nextAfter);
    if (newer.length > 0) {
      setMessages(prev => {
        const byId = new Map<string, ChatMessageItem>();
        for (const m of prev) byId.set(m.id, m);
        for (const m of newer) byId.set(m.id, m);
        const merged = [...byId.values()].sort((a, b) => a.seq - b.seq);
        if (!nextHasNewer) {
          messagesCacheRef.current.set(selectedConversationId, {
            items: merged,
            hasMore: hasMoreOlder,
            nextBeforeSeq,
            lastRead: data.lastReadSeq ?? lastReadSnapshot
          });
        }
        return merged;
      });
    }
  }, [discardConversation, hasMoreOlder, lastReadSnapshot, nextAfterSeq, nextBeforeSeq, selectedConversationId]);

  const loadOlderMessages = useCallback(async () => {
    if (!selectedConversationId || !nextBeforeSeq) return;
    const res = await fetch(
      `/api/conversations/${selectedConversationId}/messages?beforeSeq=${nextBeforeSeq}&limit=50`,
      { credentials: 'same-origin' }
    );
    if ([401, 403, 404].includes(res.status)) discardConversation(selectedConversationId);
    if (!res.ok || activeConvRef.current !== selectedConversationId) return;
    const data = await res.json();
    if (activeConvRef.current !== selectedConversationId) return;
    const older: ChatMessageItem[] = data.items ?? [];
    setHasMoreOlder(Boolean(data.hasMore));
    setNextBeforeSeq(typeof data.nextBeforeSeq === 'number' ? data.nextBeforeSeq : null);
    if (older.length > 0) {
      setMessages(prev => {
        const byId = new Map<string, ChatMessageItem>();
        for (const m of older) byId.set(m.id, m);
        for (const m of prev) byId.set(m.id, m);
        const next = [...byId.values()].sort((a, b) => a.seq - b.seq);
        const cached = messagesCacheRef.current.get(selectedConversationId);
        if (cached) messagesCacheRef.current.set(selectedConversationId, {
          ...cached, items: next, hasMore: Boolean(data.hasMore), nextBeforeSeq: data.nextBeforeSeq ?? null
        });
        return next;
      });
    }
  }, [discardConversation, nextBeforeSeq, selectedConversationId]);

  const loadGroupMembers = useCallback(async (convId: string) => {
    if (activeConvRef.current === convId) await refreshGroupMembers.current();
  }, []);

  const loadConversationFiles = useCallback(async (convId: string, older = false) => {
    const start = older ? filePages.current.next : null;
    if (older && !start) return;

    filesListRequest.current?.abort();
    const controller = new AbortController();
    filesListRequest.current = controller;
    setLoadingFiles(true);
    setFilesLoadError(null);
    try {
      let fileQuota: StorageQuotaSummary | null = null;
      const { items, next, pages } = await readPages<AttachmentItem, FilesCursor>(
        start, older ? 1 : filePages.current.count, async cursor => {
          const params = new URLSearchParams({ limit: '50' });
          if (cursor) {
            params.set('before', cursor.createdAt);
            params.set('beforeId', cursor.id);
          }
          const response = await fetch(`/api/conversations/${convId}/files?${params}`, { signal: controller.signal });
          if ([401, 403, 404].includes(response.status)) {
            if (!controller.signal.aborted && activeConvRef.current === convId) {
              setConversationFiles([]);
              setQuota(null);
              setMoreFiles(false);
              filePages.current = { count: 1, next: null };
            }
            throw new Error('File access is unavailable.');
          }
          if (!response.ok) throw new Error('Unable to load files.');
          const page = await response.json();
          fileQuota = page.quota ?? null;
          return {
            items: page.items ?? [],
            next: page.hasMore && page.nextBefore && page.nextBeforeId
              ? { createdAt: page.nextBefore, id: page.nextBeforeId } : null
          };
        }
      );

      if (!controller.signal.aborted && activeConvRef.current === convId) {
        filePages.current = { count: older ? filePages.current.count + pages : pages, next };
        setMoreFiles(next !== null);
        setConversationFiles(previous => {
          const merged = new Map(older ? previous.map(item => [item.id, item]) : []);
          for (const item of items) merged.set(item.id, item);
          return [...merged.values()];
        });
        setQuota(fileQuota);
      }
    } catch {
      if (!controller.signal.aborted && activeConvRef.current === convId) {
        setFilesLoadError(older ? 'older' : 'refresh');
      }
    } finally {
      if (filesListRequest.current === controller) {
        filesListRequest.current = null;
        if (!controller.signal.aborted && activeConvRef.current === convId) setLoadingFiles(false);
      }
    }
  }, []);

  const uploadFilesToConversation = useCallback(async (fileList: File[]) => {
    const target = selectedConversationId;
    if (!target || loadingDraft || fileList.length === 0 || staging.current.has(target) || uploadRequests.current.has(target)) return;
    setError('');
    const controller = new AbortController();
    uploadRequests.current.set(target, controller);
    setUploads(new Set(uploadRequests.current.keys()));
    try {
      for (const file of fileList) {
        const form = new FormData();
        form.append('clientUploadId', generateClientMsgId());
        form.append('file', file, file.name || 'pasted-image.png');

        const res = await fetch(`/api/conversations/${target}/attachments`, {
          method: 'POST',
          credentials: 'same-origin',
          body: form,
          signal: controller.signal
        });

        const data = await res.json().catch(() => ({}));
        if (controller.signal.aborted) return;
        if (!res.ok) {
          if (activeConvRef.current === target) setError(data.message || `Failed to upload ${file.name}.`);
          continue;
        }
        if (data.attachment) {
          const { id, fileName, sizeBytes, thumbnailUrl } = data.attachment as AttachmentItem;
          changeDraft(target, current => ({ attachments: [...current.attachments, { id, fileName, sizeBytes, thumbnailUrl }] }));
        }
        if (data.quota && activeConvRef.current === target) {
          setQuota(data.quota as StorageQuotaSummary);
        }
      }
    } catch {
      if (!controller.signal.aborted && activeConvRef.current === target) setError('Upload interrupted. Try attaching the file again.');
    } finally {
      uploadRequests.current.delete(target);
      setUploads(new Set(uploadRequests.current.keys()));
    }
  }, [changeDraft, loadingDraft, selectedConversationId]);

  const syncConversationDelta = useCallback(async (convId: string) => {
    let cursor = cursorCacheRef.current.get(convId) ?? await getConversationCursor(offlineScope, convId);
    if (cursor > 0) cursorCacheRef.current.set(convId, cursor);
    if (activeConvRef.current !== convId) return;
    if (cursor <= 0) {
      await loadMessagesFull(convId);
      return;
    }

    while (activeConvRef.current === convId) {
      const res = await fetch(`/api/conversations/${convId}/changes?after=${cursor}&limit=50`, { credentials: 'same-origin' });
      if (activeConvRef.current !== convId) return;
      if (res.status === 410) {
        await loadMessagesFull(convId);
        return;
      }
      if ([401, 403, 404].includes(res.status)) {
        discardConversation(convId);
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
          const byId = new Map(prev.map(message => [message.id, message]));
          for (const message of added) byId.set(message.id, message);
          const next = [...byId.values()].sort((a, b) => a.seq - b.seq);
          const cached = messagesCacheRef.current.get(convId);
          if (cached) messagesCacheRef.current.set(convId, { ...cached, items: next });
          return next;
        });
      }

      const lastEventSeq = events.at(-1)?.changeSeq ?? cursor;
      const nextCursor = Math.max(lastEventSeq, typeof data.nextAfter === 'number' ? data.nextAfter : cursor);
      if (nextCursor <= cursor) return;
      cursorCacheRef.current.set(convId, nextCursor);
      void setConversationCursor(offlineScope, convId, nextCursor);
      cursor = nextCursor;
      if (!data.hasMore) return;
    }
  }, [discardConversation, loadMessagesFull, offlineScope]);

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

  const checkPendingAttachments = useCallback(async (conversationId: string, ids: string[], signal?: AbortSignal) => {
    const found = new Set<string>();
    for (let start = 0; start < Math.max(1, ids.length); start += 50) {
      const params = new URLSearchParams(ids.slice(start, start + 50).map(id => ['id', id]));
      const response = await fetch(`/api/conversations/${conversationId}/attachments/pending?${params}`, { signal });
      if ([401, 403, 404].includes(response.status)) {
        if (!signal?.aborted) discardConversation(conversationId);
        return null;
      }
      if (!response.ok) throw new Error('Unable to verify attachments.');
      const data: { ids: string[] } = await response.json();
      for (const id of data.ids) found.add(id);
    }
    return ids.filter(id => !found.has(id));
  }, [discardConversation]);
  const revalidateConversationRef = useRef<(id: string, signal: AbortSignal) => Promise<unknown>>(async () => {});
  revalidateConversationRef.current = async (id, signal) => checkPendingAttachments(id, [], signal).catch(() => {});

  useEffect(() => {
    if (!selectedConversationId || loadingDraft) return;
    const controller = new AbortController();
    const ids = attachmentKey ? attachmentKey.split(',') : [];
    if (ids.length > 0) {
      void checkPendingAttachments(selectedConversationId, ids, controller.signal)
        .then(missing => {
          if (missing && !controller.signal.aborted) setCheckedAttachments({ key: attachmentKey, missing });
        })
        .catch(() => {});
    }
    return () => controller.abort();
  }, [attachmentKey, checkPendingAttachments, loadingDraft, selectedConversationId]);

  const deliverMessage = useCallback(async (item: QueuedMessage) => {
    const { clientMsgId, conversationId, body, attachments } = item;
    if (discarded.current.has(conversationId) || sendingRef.current.has(clientMsgId) ||
      !outgoingRef.current.some(current => current.clientMsgId === clientMsgId)) return;
    sendingRef.current.add(clientMsgId);
    setSending(new Set(sendingRef.current));
    try {
      const res = await fetch(`/api/conversations/${conversationId}/messages`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ clientMsgId, body, attachmentIds: attachments?.map(file => file.id), preserveReadState: true })
      });
      if (discarded.current.has(conversationId)) return;
      if (!res.ok) {
        if ([401, 403, 404].includes(res.status)) {
          // Read access can remain after a DM is blocked for sending.
          await checkPendingAttachments(conversationId, []).catch(() => {});
        }
        if (discarded.current.has(conversationId)) return;
        const data = await res.json().catch(() => ({}));
        const failure = data.code === 'invalid_attachments'
          ? 'An attachment is no longer available. Edit the draft to attach it again.'
          : data.message || 'Unable to send message. Try again.';
        updateOutgoing(items => items.map(current => current.clientMsgId === clientMsgId ? { ...current, failure } : current));
        await recordSendFailure(offlineScope, clientMsgId, failure).catch(onStorageError);
        return;
      }
      const saved: ChatMessageItem = await res.json();
      if (discarded.current.has(conversationId)) return;
      const merge = (items: ChatMessageItem[]) => items.some(message => message.id === saved.id)
        ? items : [...items, saved].sort((a, b) => a.seq - b.seq);
      const cached = messagesCacheRef.current.get(conversationId);
      if (cached) messagesCacheRef.current.set(conversationId, { ...cached, items: merge(cached.items) });
      if (activeConvRef.current === conversationId) {
        setMessages(merge);
        if (attachments?.length && rightDrawerRef.current === 'files') void loadConversationFiles(conversationId);
      }
      updateOutgoing(items => items.filter(current => current.clientMsgId !== clientMsgId));
      await dequeueOfflineMessage(offlineScope, clientMsgId).catch(onStorageError);
      scheduleConversationRefresh();
    } catch {
      // An interrupted request may have committed. Replay the same payload and key on reconnect.
    } finally {
      sendingRef.current.delete(clientMsgId);
      setSending(new Set(sendingRef.current));
    }
  }, [checkPendingAttachments, loadConversationFiles, offlineScope, onStorageError, scheduleConversationRefresh, updateOutgoing]);

  const flushOfflineQueue = useCallback(async () => {
    const beforeRead = outgoingRef.current;
    const stored = await listQueuedMessages(offlineScope).catch(() => {
      onStorageError();
      return [];
    });
    if (outgoingRef.current === beforeRead) {
      updateOutgoing(items => {
        const byId = new Map(stored.filter(item => !discarded.current.has(item.conversationId))
          .map(item => [item.clientMsgId, item]));
        for (const item of items) byId.set(item.clientMsgId, item);
        return [...byId.values()];
      });
    }
    for (const item of outgoingRef.current) {
      if (!item.failure) await deliverMessage(item);
    }
  }, [deliverMessage, offlineScope, onStorageError, updateOutgoing]);

  const reconcileAll = useCallback(async () => {
    await checkApiVersion();
    await flushOfflineQueue();
    await loadConversations();
    if (activeConvRef.current) {
      await requestConversationDelta(activeConvRef.current);
      if (rightDrawerRef.current === 'members') await loadGroupMembers(activeConvRef.current);
    }
    setReconnectCount(count => count + 1);
  }, [checkApiVersion, flushOfflineQueue, loadConversations, loadGroupMembers, requestConversationDelta]);

  loadConversationsRef.current = loadConversations;
  syncDeltaRef.current = requestConversationDelta;
  reconcileRef.current = reconcileAll;
  flushOfflineQueueRef.current = flushOfflineQueue;

  useEffect(() => {
    conversationPages.current = { count: 1, next: null };
    void loadConversations();
    return () => conversationListRequest.current?.abort();
  }, [loadConversations, unreadOnly]);

  useEffect(() => {
    void flushOfflineQueue();
    const requests = uploadRequests.current;
    return () => requests.forEach(request => request.abort());
  }, [flushOfflineQueue]);

  useEffect(() => {
    if (!onReloadConversationsRef) return;
    onReloadConversationsRef.current = loadConversations;
    return () => {
      if (onReloadConversationsRef.current === loadConversations) {
        onReloadConversationsRef.current = null;
      }
    };
  }, [onReloadConversationsRef, loadConversations]);

  useEffect(() => {
    if (!refreshOrgSubscriptionsRef) return;
    const refresh = async () => {
      if (hubRef.current?.state === signalR.HubConnectionState.Connected) {
        await hubRef.current.invoke('RefreshOrganizationSubscriptions').catch(() => {});
      }
    };
    refreshOrgSubscriptionsRef.current = refresh;
    return () => {
      if (refreshOrgSubscriptionsRef.current === refresh) {
        refreshOrgSubscriptionsRef.current = null;
      }
    };
  }, [refreshOrgSubscriptionsRef]);

  useEffect(() => {
    setRightDrawer('none');
    setOwnershipTransfer(null);
    compositionActive.current = false;
    setMoreMenuOpen(false);
    setConversationFiles([]);
    filesListRequest.current?.abort();
    filePages.current = { count: 1, next: null };
    setMoreFiles(false);
    setFilesLoadError(null);
    setLoadingFiles(false);
    setGroupMemberSearch('');
    setAddMemberId('');
    setQuota(null);
    setTypingText('');
    setError('');
    setHasNewerMessages(false);
    setNextAfterSeq(null);
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
    if (selectedConversationId && pendingJumpRef.current?.conversationId !== selectedConversationId) {
      void requestConversationDelta(selectedConversationId, !cached).catch(() => {
        if (activeConvRef.current === selectedConversationId) setError('Unable to load messages.');
      });
    }
    return () => {
      filesListRequest.current?.abort();
    };
  }, [selectedConversationId, requestConversationDelta]);

  useEffect(() => {
    if (!jumpTarget || jumpTarget.nonce === handledJumpNonceRef.current) return;
    handledJumpNonceRef.current = jumpTarget.nonce;
    pendingJumpRef.current = jumpTarget;
    let cancelled = false;
    let highlightTimer: number | null = null;

    async function runJump(target: ChatJumpTarget) {
      if (activeConvRef.current !== target.conversationId) {
        onSelectConversation(target.conversationId);
        activeConvRef.current = target.conversationId;
      }
      await loadConversations().catch(() => {});
      if (cancelled) return;
      const existing = messagesCacheRef.current.get(target.conversationId)?.items.some(m => m.id === target.messageId);
      if (!existing) {
        await loadMessagesAround(target.conversationId, target.seq);
      }
      if (pendingJumpRef.current?.nonce === target.nonce) pendingJumpRef.current = null;
      if (cancelled) return;
      setHighlightedMessageId(target.messageId);
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          const el = reading.feedRef.current?.querySelector<HTMLElement>(`[data-message-id="${target.messageId}"]`);
          if (el) {
            el.scrollIntoView({ block: 'center', behavior: 'smooth' });
            el.focus({ preventScroll: true });
          }
        });
      });
      highlightTimer = window.setTimeout(() => {
        setHighlightedMessageId(current => current === target.messageId ? null : current);
      }, 4000);
    }

    void runJump(jumpTarget);
    return () => {
      cancelled = true;
      if (highlightTimer !== null) window.clearTimeout(highlightTimer);
    };
  }, [jumpTarget, loadConversations, loadMessagesAround, onSelectConversation]);

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
        const mergeMessage = (prev: ChatMessageItem[]) => {
          const byId = new Map(prev.map(m => [m.id, m]));
          byId.set(incoming.id, incoming);
          return [...byId.values()].sort((a, b) => a.seq - b.seq);
        };

        const cached = messagesCacheRef.current.get(evt.conversationId);
        if (cached) {
          messagesCacheRef.current.set(evt.conversationId, { ...cached, items: mergeMessage(cached.items) });
        }
        if (isActive && !hasNewerRef.current) {
          setMessages(prev => mergeMessage(prev));
        }

        let foundInList = false;
        setConversations(prev => {
          const next = prev.map(c => {
            if (c.id !== evt.conversationId) return c;
            foundInList = true;
            const nextSeq = Math.max(c.nextSeq, incoming.seq);
            return {
              ...c,
              nextSeq,
              changeSeq: Math.max(c.changeSeq, evt.changeSeq),
              unreadCount: c.unreadCount + Math.max(0, nextSeq - Math.max(c.nextSeq, c.lastReadSeq)),
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
        if (rightDrawerRef.current === 'members') void loadGroupMembers(evt.conversationId);
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
      const cached = messagesCacheRef.current.get(evt.conversationId);
      if (cached && evt.lastReadSeq > cached.lastRead) {
        messagesCacheRef.current.set(evt.conversationId, { ...cached, lastRead: evt.lastReadSeq });
      }
      setConversations(previous => previous.map(item =>
        item.id === evt.conversationId ? applyReadState(item, evt.lastReadSeq) : item
      ));
    });

    connection.on('CommunityFeedChanged', (evt: { conversationId: string; delta?: CommunityFeedDelta | null }) => {
      communityFeedCbRef.current(evt.conversationId, evt.delta ?? null);
    });

    connection.on('OrganizationAccessRevoked', (evt: { organizationId: string }) => {
      if (evt?.organizationId) {
        orgRevokedCbRef.current?.(evt.organizationId);
      }
    });

    connection.on('ConversationAccessRevoked', (evt: { conversationId: string; organizationId: string | null }) => {
      if (evt.conversationId && !evt.organizationId) discardConversationRef.current(evt.conversationId);
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
  }, [loadGroupMembers, scheduleConversationRefresh]);

  async function sendCurrentMessage() {
    const conversationId = selectedConversationId;
    if (!conversationId || activeConv?.peer?.isBlockedByMe || uploadRequests.current.has(conversationId) || staging.current.has(conversationId) || missingAttachments.length) return;
    const current = readLocalDraft(conversationId);
    if (!current || (!current.body.trim() && current.attachments.length === 0)) return;
    setError('');
    const body = current.body.trim();
    const previous = current.retry;
    const previousIds = previous?.attachments?.map(file => file.id) ?? [];
    const samePayload = previous?.body === body && previousIds.length === current.attachments.length &&
      current.attachments.every(file => previousIds.includes(file.id));
    const item: QueuedMessage = {
      conversationId,
      clientMsgId: samePayload ? previous.clientMsgId : generateClientMsgId(),
      createdAt: samePayload ? previous.createdAt : new Date().toISOString(),
      body,
      attachments: current.attachments
    };
    staging.current.add(conversationId);
    try {
      await stageMessage(offlineScope, item, current.revision).catch(onStorageError);
      if (discarded.current.has(conversationId)) return;
      if (readLocalDraft(conversationId)?.revision === current.revision) {
        setLocalDraft(conversationId, null);
      }
      updateOutgoing(items => [...items.filter(entry => entry.clientMsgId !== item.clientMsgId), item]);
      void deliverMessage(item);
    } finally {
      staging.current.delete(conversationId);
    }
  }

  async function recoverMessage(item: QueuedMessage, edit: boolean) {
    const { clientMsgId, conversationId } = item;
    if (sendingRef.current.has(clientMsgId) || discarded.current.has(conversationId)) return;
    if (edit && (readLocalDraft(conversationId) !== null || uploadRequests.current.has(conversationId))) return;
    sendingRef.current.add(clientMsgId);
    setSending(new Set(sendingRef.current));
    try {
      if (edit) {
        const restored: ChatDraft = {
          conversationId,
          revision: generateClientMsgId(),
          body: item.body,
          attachments: item.attachments ?? [],
          retry: item
        };
        const saved = await restoreQueuedDraft(offlineScope, restored, clientMsgId).catch(() => {
          onStorageError();
          return true;
        });
        if (discarded.current.has(conversationId)) return;
        if (!saved) {
          if (activeConvRef.current === conversationId) setError('Another tab has a draft in this chat. Finish that draft before editing this message.');
          return;
        }
        setLocalDraft(conversationId, restored);
        if (activeConvRef.current === conversationId) composerRef.current?.focus();
      } else {
        await dequeueOfflineMessage(offlineScope, clientMsgId).catch(onStorageError);
      }
      updateOutgoing(items => items.filter(entry => entry.clientMsgId !== clientMsgId));
    } finally {
      sendingRef.current.delete(clientMsgId);
      setSending(new Set(sendingRef.current));
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
    if (!activeConv?.peer?.isBlockedByMe) setIsDragOver(true);
  }

  function handleStageDrop(e: DragEvent<HTMLElement>) {
    e.preventDefault();
    setIsDragOver(false);
    if (!selectedConversationId || activeConv?.peer?.isBlockedByMe) return;
    const dropped = e.dataTransfer?.files ? Array.from(e.dataTransfer.files) : [];
    if (dropped.length > 0) {
      void uploadFilesToConversation(dropped);
    }
  }

  function handleInlineContentLinkClick(e: ReactMouseEvent<HTMLElement>) {
    if (!onOpenContentLink) return;
    const anchor = (e.target as HTMLElement | null)?.closest('a');
    if (!anchor?.href) return;
    const token = extractContentLinkToken(anchor.href);
    if (!token) return;
    e.preventDefault();
    onOpenContentLink(token);
  }

  async function handleCopyMessageLink(message: ChatMessageItem) {
    const result = await copyContentLink({
      kind: 'message',
      organizationId: null,
      conversationId: message.conversationId,
      messageId: message.id
    });
    setNotice(result.ok ? 'Message link copied.' : 'Unable to copy link automatically.');
  }

  function handleComposerKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (shouldSendOnEnter(e.nativeEvent, compositionActive.current)) {
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
      setNewGroupSearch('');
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
      await Promise.all([loadGroupMembers(selectedConversationId), addMemberCandidates.refresh(), loadConversations()]);
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
      await Promise.all([loadGroupMembers(selectedConversationId), addMemberCandidates.refresh(), loadConversations()]);
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
      discardConversation(selectedConversationId);
      await loadConversations();
    } else {
      const err = await res.json().catch(() => ({}));
      setError(err.message || 'Unable to leave group.');
    }
  }

  async function handleBlockPeer() {
    if (!blockTarget || blockingPeer) return;
    const { id: peerId, name: peerName } = blockTarget;
    setBlockingPeer(true);
    setBlockError('');
    try {
      const res = await fetch(`/api/people/blocks/${peerId}`, { method: 'PUT' });
      if (!res.ok) throw new Error('Unable to block user.');
      setConversations(items => items.map(item => item.peer?.id === peerId
        ? { ...item, peer: { ...item.peer, isBlockedByMe: true } } : item));
      setBlockTarget(null);
      setNotice(`${peerName} has been blocked.`);
      onBlockListChanged();
    } catch {
      setBlockError('Unable to block this user. Try again.');
    } finally {
      setBlockingPeer(false);
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
  const activeId = activeConv?.id;
  const blockedPeer = activeConv?.peer?.isBlockedByMe ?? false;

  useLayoutEffect(() => {
    const input = composerRef.current;
    if (!visible || !input || CSS.supports('field-sizing', 'content')) return;

    const resize = () => {
      input.style.height = 'auto';
      input.style.height = `${input.scrollHeight}px`;
    };
    resize();

    let width = input.offsetWidth;
    const observer = new ResizeObserver(() => {
      if (input.offsetWidth === width) return;
      width = input.offsetWidth;
      resize();
    });
    observer.observe(input);
    return () => observer.disconnect();
  }, [activeId, composer, visible]);

  const activeMessages = useMemo(() => messages.filter(message => message.conversationId === selectedConversationId), [messages, selectedConversationId]);
  const confirmedOwnMessages = new Set(activeMessages.filter(message => message.senderId === currentUserId).map(message => message.clientMsgId));
  const visibleOutgoing = outgoing.filter(item => item.conversationId === selectedConversationId && !confirmedOwnMessages.has(item.clientMsgId));
  const reading = useChatReading(activeConv?.id ?? null, visible, activeMessages, visibleOutgoing.length,
    activeConv?.lastReadSeq ?? 0, markRead, reconnectCount);

  const filteredConvs = useMemo(() => {
    const q = convSearch.trim().toLowerCase();
    return conversations.filter(c => {
      if (unreadOnly && c.unreadCount <= 0) return false;
      if (chatFilter === 'dm' && c.kind !== 'dm') return false;
      if (chatFilter === 'group' && c.kind === 'dm') return false;
      if (!q) return true;
      return c.title.toLowerCase().includes(q)
        || (c.peer?.username ?? '').toLowerCase().includes(q)
        || (c.lastMessagePreview?.bodyPreview ?? '').toLowerCase().includes(q);
    });
  }, [chatFilter, convSearch, conversations, unreadOnly]);

  const sortBySidebarOrder = useCallback((list: ConversationListItem[]) => {
    return [...list].sort((a, b) => {
      const pa = sidebarOrg.itemByTargetId.get(a.id);
      const pb = sidebarOrg.itemByTargetId.get(b.id);
      if (pa && pb && pa.sortOrder !== pb.sortOrder) return pa.sortOrder - pb.sortOrder;
      const diff = Date.parse(b.updatedAt ?? '') - Date.parse(a.updatedAt ?? '');
      return diff || b.id.localeCompare(a.id);
    });
  }, [sidebarOrg.itemByTargetId]);

  const hiddenConvs = useMemo(
    () => sortBySidebarOrder(filteredConvs.filter(c => sidebarOrg.itemByTargetId.get(c.id)?.isHidden)),
    [filteredConvs, sidebarOrg.itemByTargetId, sortBySidebarOrder]
  );
  const nonHiddenConvs = useMemo(
    () => filteredConvs.filter(c => !sidebarOrg.itemByTargetId.get(c.id)?.isHidden),
    [filteredConvs, sidebarOrg.itemByTargetId]
  );
  const favoriteConvs = useMemo(
    () => sortBySidebarOrder(nonHiddenConvs.filter(c => sidebarOrg.itemByTargetId.get(c.id)?.isFavorite)),
    [nonHiddenConvs, sidebarOrg.itemByTargetId, sortBySidebarOrder]
  );
  const customSectionGroups = useMemo(() => {
    return sidebarOrg.sections.map(section => ({
      section,
      items: sortBySidebarOrder(nonHiddenConvs.filter(c => {
        const pref = sidebarOrg.itemByTargetId.get(c.id);
        return !pref?.isFavorite && pref?.sectionId === section.id;
      }))
    }));
  }, [nonHiddenConvs, sidebarOrg.itemByTargetId, sidebarOrg.sections, sortBySidebarOrder]);
  const ungroupedConvs = useMemo(() => {
    const validSectionIds = new Set(sidebarOrg.sections.map(s => s.id));
    return nonHiddenConvs.filter(c => {
      const pref = sidebarOrg.itemByTargetId.get(c.id);
      if (pref?.isFavorite) return false;
      return !pref?.sectionId || !validSectionIds.has(pref.sectionId);
    });
  }, [nonHiddenConvs, sidebarOrg.itemByTargetId, sidebarOrg.sections]);

  const visibleDmConvs = useMemo(() => sortBySidebarOrder(ungroupedConvs.filter(c => c.kind === 'dm')), [sortBySidebarOrder, ungroupedConvs]);
  const visibleGroupConvs = useMemo(() => sortBySidebarOrder(ungroupedConvs.filter(c => c.kind !== 'dm')), [sortBySidebarOrder, ungroupedConvs]);
  const visibleConvCount = favoriteConvs.length
    + customSectionGroups.reduce((sum, g) => sum + g.items.length, 0)
    + visibleDmConvs.length
    + visibleGroupConvs.length
    + hiddenConvs.length;

  const renderConvItem = (c: ConversationListItem, groupList: ConversationListItem[]) => {
    const isActive = c.id === selectedConversationId;
    const isDm = c.kind === 'dm';
    const pref = sidebarOrg.itemByTargetId.get(c.id);
    const groupIds = groupList.map(item => item.id);
    const idx = groupIds.indexOf(c.id);
    const preview = c.lastMessagePreview;
    const previewText = preview
      ? `${preview.senderDisplayName}: ${preview.bodyPreview}`
      : isDm
        ? (c.peer?.isOnline ? 'Online' : `@${c.peer?.username ?? 'direct'}`)
        : `${c.memberCount} ${c.memberCount === 1 ? 'member' : 'members'}`;

    return (
      <div
        key={c.id}
        className={`nx-conv-row-wrap ${draggedConvId === c.id ? 'dragging' : ''}`}
        draggable
        onDragStart={e => {
          setDraggedConvId(c.id);
          e.dataTransfer.effectAllowed = 'move';
          e.dataTransfer.setData('text/plain', c.id);
        }}
        onDragEnd={() => setDraggedConvId(null)}
        onDragOver={e => {
          if (draggedConvId && draggedConvId !== c.id && groupIds.includes(draggedConvId)) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
          }
        }}
        onDrop={e => {
          e.preventDefault();
          if (!draggedConvId || draggedConvId === c.id || !groupIds.includes(draggedConvId)) return;
          const nextOrder = reorderIdList(groupIds, draggedConvId, c.id);
          setDraggedConvId(null);
          void sidebarOrg.persistReorder(nextOrder);
        }}
      >
        <button
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
                {pref?.isFavorite && <Star size={12} aria-label="Favorite" />}
                <span>{c.title}</span>
              </span>
              {c.unreadCount > 0 && <span className="nx-unread-pill">{c.unreadCount > 99 ? '99+' : c.unreadCount}</span>}
            </div>
            <div className="nx-conv-sub nx-conv-preview" title={previewText}>
              {previewText}
            </div>
          </div>
        </button>
        <SidebarItemActions
          itemLabel={c.title}
          pref={pref}
          sections={sidebarOrg.sections}
          canMoveUp={idx > 0}
          canMoveDown={idx >= 0 && idx < groupIds.length - 1}
          onToggleFavorite={() => void sidebarOrg.updateItem(c.id, { isFavorite: !pref?.isFavorite })}
          onToggleHidden={() => void sidebarOrg.updateItem(c.id, { isHidden: !pref?.isHidden })}
          onAssignSection={secId => void sidebarOrg.updateItem(c.id, secId ? { sectionId: secId, isHidden: false } : { clearSection: true })}
          onMoveUp={() => void sidebarOrg.persistReorder(moveIdByOffset(groupIds, c.id, -1))}
          onMoveDown={() => void sidebarOrg.persistReorder(moveIdByOffset(groupIds, c.id, 1))}
        />
      </div>
    );
  };

  return (
    <div className={`nx-chat-layout ${activeConv ? 'has-selection' : ''}`}>
      {/* Conversation Sidebar */}
      <aside className="nx-chat-sidebar" aria-label="Conversations">
        <div className="nx-side-header">
          <div className="nx-side-actions">
            <Button variant="primary" size="sm" onClick={onNewChat}>
              <MessageSquare size={14} aria-hidden="true" /> New chat
            </Button>
            <Button size="sm" onClick={() => setShowNewGroupModal(true)}>
              <Plus size={14} aria-hidden="true" /> Create group chat
            </Button>
            <Button
              variant="ghost"
              size="sm"
              iconOnly
              aria-label="New sidebar section"
              title="New section"
              onClick={() => setSectionModal({ mode: 'create' })}
            >
              <FolderPlus size={14} aria-hidden="true" />
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
          <div className="nx-chat-filters" role="group" aria-label="Conversation type filter">
            <button
              type="button"
              className={`nx-filter-pill ${chatFilter === 'all' ? 'active' : ''}`}
              aria-pressed={chatFilter === 'all'}
              onClick={() => setChatFilter('all')}
            >
              All
            </button>
            <button
              type="button"
              className={`nx-filter-pill ${chatFilter === 'dm' ? 'active' : ''}`}
              aria-pressed={chatFilter === 'dm'}
              onClick={() => setChatFilter('dm')}
            >
              Direct
            </button>
            <button
              type="button"
              className={`nx-filter-pill ${chatFilter === 'group' ? 'active' : ''}`}
              aria-pressed={chatFilter === 'group'}
              onClick={() => setChatFilter('group')}
            >
              Groups
            </button>
            <button
              type="button"
              className={`nx-filter-pill ${unreadOnly ? 'active' : ''}`}
              aria-pressed={unreadOnly}
              onClick={() => setUnreadOnly(v => !v)}
            >
              Unread
            </button>
          </div>
        </div>

        <div className="nx-conv-scroll">
          {sidebarOrg.error && (
            <div className="nx-empty-side">
              <p className="nx-error" role="alert">{sidebarOrg.error}</p>
            </div>
          )}
          {visibleConvCount === 0 && !loadingConversations && !conversationLoadError ? (
            <div className="nx-empty-side">
              <p style={{ margin: '0 0 12px' }}>
                {unreadOnly ? 'No unread conversations.' :
                  conversations.length === 0 ? 'No conversations yet.' :
                    moreConversations ? 'No matches in the loaded conversations.' : 'No matching conversations.'}
              </p>
            </div>
          ) : (
            <>
              {favoriteConvs.length > 0 && (
                <>
                  <div className="nx-section-label">Favorites ({favoriteConvs.length})</div>
                  {favoriteConvs.map(c => renderConvItem(c, favoriteConvs))}
                </>
              )}
              {customSectionGroups.map(({ section, items }) => (
                <div key={section.id}>
                  <SidebarSectionHeader
                    section={section}
                    count={items.length}
                    onRename={sec => setSectionModal({ mode: 'rename', section: sec })}
                    onDelete={sec => void sidebarOrg.deleteSection(sec.id)}
                  />
                  {items.length === 0 ? (
                    <div className="nx-ws-empty">No chats in this section yet.</div>
                  ) : (
                    items.map(c => renderConvItem(c, items))
                  )}
                </div>
              ))}
              {visibleDmConvs.length > 0 && (
                <>
                  <div className="nx-section-label">Direct messages ({visibleDmConvs.length})</div>
                  {visibleDmConvs.map(c => renderConvItem(c, visibleDmConvs))}
                </>
              )}
              {visibleGroupConvs.length > 0 && (
                <>
                  <div className="nx-section-label">Group chats ({visibleGroupConvs.length})</div>
                  {visibleGroupConvs.map(c => renderConvItem(c, visibleGroupConvs))}
                </>
              )}
              {hiddenConvs.length > 0 && (
                <div className="nx-hidden-group">
                  <button
                    type="button"
                    className="nx-hidden-toggle"
                    aria-expanded={showHiddenConvs}
                    onClick={() => setShowHiddenConvs(v => !v)}
                  >
                    {showHiddenConvs ? <ChevronDown size={13} aria-hidden="true" /> : <ChevronRight size={13} aria-hidden="true" />}
                    <Eye size={13} aria-hidden="true" />
                    <span>Hidden ({hiddenConvs.length})</span>
                  </button>
                  {showHiddenConvs && hiddenConvs.map(c => renderConvItem(c, hiddenConvs))}
                </div>
              )}
            </>
          )}
          {(moreConversations || loadingConversations || conversationLoadError) && (
            <div className="nx-empty-side">
              {conversationLoadError && <p className="nx-error" role="alert">Unable to load conversations.</p>}
              {loadingConversations ? <span role="status">Loading conversations…</span> : (
                <Button size="sm" onClick={() => void loadConversations(conversationLoadError !== 'refresh')}>
                  {conversationLoadError ? 'Retry' : 'Load more conversations'}
                </Button>
              )}
            </div>
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
                <ConversationNotificationButton key={activeConv.id} conversationId={activeConv.id} />
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
                          className={`nx-dropdown-item ${blockedPeer ? '' : 'danger'}`}
                          onClick={() => {
                            moreBtnRef.current?.focus();
                            setMoreMenuOpen(false);
                            if (blockedPeer) onManageBlockedUsers();
                            else if (activeConv.peer) {
                              setBlockError('');
                              setBlockTarget({ id: activeConv.peer.id, name: activeConv.peer.displayName });
                            }
                          }}
                        >
                          <Ban size={14} aria-hidden="true" /> {blockedPeer ? 'Manage blocked users' : 'Block user'}
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
                      setRightDrawer(next);
                    }}
                  >
                    <Users size={15} aria-hidden="true" /> Members ({activeConv.memberCount})
                  </Button>
                )}
              </div>
            </header>

            <div className="nx-chat-read-area">
            <div className="nx-msg-feed" ref={reading.feedRef} onScroll={reading.onScroll} role="region" aria-label="Conversation messages" tabIndex={0}>
              {hasMoreOlder && (
                <Button size="sm" className="nx-load-older" onClick={() => void loadOlderMessages()}>
                  Load older messages
                </Button>
              )}

              {activeMessages.length === 0 && visibleOutgoing.length === 0 && (
                <div style={{ margin: 'auto', textAlign: 'center', color: 'var(--text-muted)', padding: '32px 16px' }}>
                  <MessageSquare size={28} aria-hidden="true" />
                  <p style={{ margin: '8px 0 0' }}>No messages yet. Say hello!</p>
                </div>
              )}

              {activeMessages.map((m, idx) => {
                const prev = idx > 0 ? activeMessages[idx - 1] : null;
                const prevSeq = prev ? prev.seq : 0;
                const showDivider = lastReadSnapshot > 0 && prevSeq <= lastReadSnapshot && m.seq > lastReadSnapshot && !m.pending;
                const prevDate = prev ? formatDateLabel(prev.createdAt) : '';
                const curDate = formatDateLabel(m.createdAt);
                const showDate = curDate && curDate !== prevDate;
                const isOwn = m.senderId === currentUserId;
                const compact = Boolean(prev && !showDate && !showDivider && prev.senderId === m.senderId);

                return (
                  <div key={m.id} className="nx-msg-group-wrap">
                    {showDate && <div className="nx-date-sep">{curDate}</div>}
                    {showDivider && <div className="nx-new-divider" role="separator">New messages</div>}
                    <div className={`nx-msg-row ${isOwn ? 'own' : ''} ${compact ? 'compact' : ''} ${m.pending ? 'pending' : ''}`}>
                      {!isOwn && (
                        compact
                          ? <div className="nx-msg-avatar-spacer" />
                          : <Avatar displayName={m.senderDisplayName} size="sm" />
                      )}
                      <div
                        className={`nx-msg-bubble ${highlightedMessageId === m.id ? 'nx-highlight-target' : ''}`}
                        data-message-seq={m.seq}
                        data-message-id={m.id}
                        tabIndex={-1}
                      >
                        <div className="nx-msg-head">
                          {!compact && (
                            <>
                              <strong>{isOwn ? 'You' : m.senderDisplayName}</strong>
                              <span className="nx-msg-time">
                                {m.pending ? 'Sending…' : formatTime(m.createdAt)}
                              </span>
                            </>
                          )}
                          {!m.pending && !m.deletedAt && (
                            <span className="nx-msg-actions">
                              <Button
                                variant="ghost"
                                size="sm"
                                iconOnly
                                aria-label="Copy link to message"
                                title="Copy link"
                                onClick={() => void handleCopyMessageLink(m)}
                              >
                                <Link2 size={13} aria-hidden="true" />
                              </Button>
                            </span>
                          )}
                        </div>
                        {m.body && (
                          <div
                            className="nx-msg-body"
                            onClick={handleInlineContentLinkClick}
                            dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(m.body) }}
                          />
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
              {hasNewerMessages && selectedConversationId && (
                <div className="nx-jump-banner" role="region" aria-label="Viewing older messages">
                  {nextAfterSeq && (
                    <Button size="sm" onClick={() => void loadNewerMessages()}>Load newer messages</Button>
                  )}
                  <Button size="sm" variant="primary" onClick={() => void loadMessagesFull(selectedConversationId)}>
                    Jump to latest
                  </Button>
                </div>
              )}
              {visibleOutgoing.map(item => {
                const sendingNow = sending.has(item.clientMsgId);
                return (
                  <div className="nx-msg-row own" key={item.clientMsgId}>
                    <div className="nx-msg-bubble">
                      <div className="nx-msg-head">
                        <strong>You</strong>
                        <span className="nx-msg-time" role="status">
                          {sendingNow ? 'Sending…' : item.failure ? 'Send failed' : 'Queued for reconnect'}
                        </span>
                      </div>
                      {item.body && <div className="nx-msg-body" dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(item.body) }} />}
                      {item.attachments?.map(file => <div className="nx-outgoing-file" key={file.id}><FileText size={14} aria-hidden="true" />{file.fileName}</div>)}
                      {!sendingNow && (
                        <>
                          {item.failure && <div className="nx-draft-notice">{item.failure}</div>}
                          <div className="nx-send-recovery">
                            <Button size="sm" onClick={() => void deliverMessage(item)}>Retry</Button>
                            <Button size="sm" disabled={loadingDraft || Boolean(draft) || uploadingFiles} onClick={() => void recoverMessage(item, true)}>Edit draft</Button>
                            <Button size="sm" variant="ghost" onClick={() => void recoverMessage(item, false)}>Dismiss</Button>
                          </div>
                        </>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>
            {reading.showNewMessages && (
              <Button className="nx-jump-new" size="sm" variant="primary" onClick={reading.showNewest}>New messages</Button>
            )}
            </div>

            <div className="nx-typing-bar" aria-live="polite">{typingText}</div>

            <form
              className="nx-composer-wrap"
              onSubmit={e => {
                e.preventDefault();
                void sendCurrentMessage();
              }}
            >
              {blockedPeer && (
                <div className="nx-blocked-chat">
                  <span>You blocked this user. Direct messaging is unavailable.</span>
                  <Button size="sm" onClick={onManageBlockedUsers}>Manage blocked users</Button>
                </div>
              )}
              {storageUnavailable && <p className="nx-draft-notice" role="status">This browser could not save chat drafts or queued messages. Keep this tab open to recover them.</p>}
              {missingAttachments.length > 0 && <p className="nx-draft-notice" role="status">Some attachments are unavailable. Remove them and attach the files again.</p>}
              {pendingAttachments.length > 0 && (
                <div className="nx-pending-attachments" aria-label="Pending attachments">
                  {pendingAttachments.map(att => (
                    <div className="nx-att-chip" key={att.id}>
                      {att.thumbnailUrl && !missingAttachments.includes(att.id) ? (
                        <img src={att.thumbnailUrl} alt={att.fileName} />
                      ) : (
                        <FileText size={15} aria-hidden="true" />
                      )}
                      <span className="nx-att-chip-name" title={att.fileName}>{att.fileName}</span>
                      <span className="nx-muted">{missingAttachments.includes(att.id) ? 'Unavailable' : formatBytes(att.sizeBytes)}</span>
                      <Button
                        variant="ghost"
                        size="sm"
                        iconOnly
                        aria-label={`Remove ${att.fileName}`}
                        onClick={() => changeDraft(activeConv.id, current => ({ attachments: current.attachments.filter(file => file.id !== att.id) }))}
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
                  title="Attach files"
                  disabled={blockedPeer || loadingDraft || uploadingFiles}
                  onClick={() => fileInputRef.current?.click()}
                >
                  <Paperclip size={16} aria-hidden="true" />
                </Button>
                <textarea
                  ref={composerRef}
                  rows={1}
                  aria-label="Message"
                  placeholder={blockedPeer ? undefined : loadingDraft ? 'Loading draft…' : uploadingFiles ? 'Uploading…' : 'Write a message…'}
                  disabled={blockedPeer || loadingDraft}
                  value={composer}
                  onCompositionStart={() => { compositionActive.current = true; }}
                  onCompositionEnd={() => { compositionActive.current = false; }}
                  onBlur={() => { compositionActive.current = false; }}
                  onKeyDown={handleComposerKeyDown}
                  onPaste={handleComposerPaste}
                  onChange={e => {
                    const body = e.target.value;
                    changeDraft(activeConv.id, () => ({ body }));
                    if (hubRef.current?.state === signalR.HubConnectionState.Connected && selectedConversationId) {
                      void hubRef.current.invoke('SendTyping', selectedConversationId).catch(() => {});
                    }
                  }}
                />
                <Button
                  type="submit"
                  variant="primary"
                  size="sm"
                  disabled={blockedPeer || loadingDraft || uploadingFiles || missingAttachments.length > 0 || (!composer.trim() && pendingAttachments.length === 0)}
                >
                  <Send size={14} aria-hidden="true" /> Send
                </Button>
              </div>
            </form>
          </>
        )}
      </section>

      {/* Group Members Drawer */}
      {activeConv && activeConv.kind !== 'dm' && rightDrawer === 'members' && (
        <aside className="nx-drawer" aria-label="Group members">
          <div className="nx-drawer-head">
            <strong ref={groupMembersHeading} tabIndex={-1}>{loadingMembers ? 'Members' : `Members (${groupMembers.length})`}</strong>
            <Button variant="ghost" size="sm" onClick={() => setRightDrawer('none')}>Close</Button>
          </div>
          <div className="nx-drawer-body">
            {(activeConv.role === 'Owner' || activeConv.role === 'Admin') && (
              <div>
              <Field label="Find people to add" htmlFor="group-member-search" compact>
                <input id="group-member-search" type="search" value={groupMemberSearch} onChange={event => {
                  setGroupMemberSearch(event.target.value);
                  setAddMemberId('');
                }} />
              </Field>
              <Field label="Add person" htmlFor="add-group-member" compact>
                <div className="nx-inline-actions">
                  <select
                    id="add-group-member"
                    value={addMemberId}
                    onChange={e => setAddMemberId(e.target.value)}
                  >
                    <option value="">{addMemberCandidates.loading ? 'Loading people…'
                      : addMemberCandidates.items.length ? 'Select a person' : 'No matching people'}</option>
                    {addMemberCandidates.items
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
              <ListPaging loading={addMemberCandidates.loading} error={addMemberCandidates.error}
                hasMore={addMemberCandidates.next !== null} onMore={addMemberCandidates.loadMore}
                onRetry={addMemberCandidates.retry} moreLabel="Load more people to add" />
              </div>
            )}

            <div>
              {groupMembers.map(gm => (
                <div className="nx-row nx-managed-member" key={gm.userId}>
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
                      <div className="nx-member-actions">
                        {activeConv.role === 'Owner' && gm.isActive !== false && (
                          <Button variant="ghost" size="sm" disabled={loadingMembers}
                            aria-label={`Transfer ownership to ${gm.displayName} (@${gm.username})`}
                            onClick={() => setOwnershipTransfer({
                              path: personalApiPath(`/api/conversations/${activeConv.id}/transfer-ownership`),
                              resourceLabel: 'Group chat',
                              resourceName: activeConv.title,
                              userId: gm.userId,
                              displayName: gm.displayName,
                              username: gm.username
                            })}
                          >Transfer ownership</Button>
                        )}
                        <Button variant="danger" size="sm" onClick={() => void handleRemoveMember(gm.userId)}>
                          Remove
                        </Button>
                      </div>
                    )}
                </div>
              ))}
            </div>
            <ListPaging loading={loadingMembers} error={groupMemberPage.error} hasMore={groupMemberPage.next !== null}
              onMore={groupMemberPage.loadMore} onRetry={groupMemberPage.retry} moreLabel="Load more members" />

            <div className="nx-drawer-footer">
              <Button variant="danger" fullWidth onClick={() => void handleLeaveGroup()}>
                Leave group
              </Button>
            </div>
          </div>
        </aside>
      )}

      {ownershipTransfer && activeConv && (
        <TransferOwnershipDialog target={ownershipTransfer} onClose={() => setOwnershipTransfer(null)}
          onRefresh={() => { void loadConversations(); void loadGroupMembers(activeConv.id); }}
          onTransferred={() => {
            setOwnershipTransfer(null);
            void loadConversations();
            void loadGroupMembers(activeConv.id);
            setNotice(`${ownershipTransfer.displayName} is now Owner of ${ownershipTransfer.resourceName}.`);
            requestAnimationFrame(() => groupMembersHeading.current?.focus());
          }} />
      )}

      {/* Conversation Files Drawer (M3.5) */}
      {activeConv && rightDrawer === 'files' && (
        <aside className="nx-drawer" aria-label="Conversation files">
          <div className="nx-drawer-head">
            <strong>{loadingFiles && conversationFiles.length === 0 ? 'Files' : `Files (${conversationFiles.length})`}</strong>
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

            {loadingFiles && conversationFiles.length === 0 ? (
              <p className="nx-muted" role="status" style={{ textAlign: 'center', marginTop: 18 }}>
                Loading files…
              </p>
            ) : conversationFiles.length === 0 && !filesLoadError ? (
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
            {(moreFiles || filesLoadError || (loadingFiles && conversationFiles.length > 0)) && (
              <div className="nx-empty-side">
                {filesLoadError && <p className="nx-error" role="alert">Unable to load files.</p>}
                {loadingFiles && conversationFiles.length > 0 && <span role="status">Loading files…</span>}
                {!loadingFiles && (
                  <Button size="sm" onClick={() => void loadConversationFiles(activeConv.id, filesLoadError !== 'refresh')}>
                    {filesLoadError ? 'Retry' : 'Load more files'}
                  </Button>
                )}
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

          <Field label="Find people to add (optional)" htmlFor="new-group-search" compact>
            <input id="new-group-search" type="search" value={newGroupSearch} onChange={event => setNewGroupSearch(event.target.value)} />
          </Field>
          {selectedGroupMemberIds.length > 0 && <p className="nx-muted">{selectedGroupMemberIds.length} selected</p>}
          {groupCandidates.items.length === 0 && !groupCandidates.error &&
            <p className="nx-muted">{groupCandidates.loading ? 'Searching…' : 'No matching people.'}</p>}
          {groupCandidates.items.length > 0 && (
              <div className="nx-member-picker">
                {groupCandidates.items.map(u => {
                  const checked = selectedGroupMemberIds.includes(u.id);
                  const contextTag = [
                    ...(u.workspaceNames ?? []),
                    ...(u.isContact ? ['Contact'] : []),
                    ...(u.departmentName ? [u.departmentName] : [])
                  ].filter(Boolean).join(', ');
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
                        <div className="nx-muted">@{u.username}{contextTag ? ` · ${contextTag}` : ''}</div>
                      </div>
                    </label>
                  );
                })}
              </div>
          )}
          <ListPaging loading={groupCandidates.loading} error={groupCandidates.error} hasMore={groupCandidates.next !== null}
            onMore={groupCandidates.loadMore} onRetry={groupCandidates.retry} moreLabel="Load more people" />
        </form>
      </Modal>

      <Modal
        open={Boolean(blockTarget)}
        onOpenChange={open => { if (!open && !blockingPeer) setBlockTarget(null); }}
        title={blockTarget ? `Block ${blockTarget.name}?` : 'Block user'}
        footer={(
          <>
            <Button disabled={blockingPeer} onClick={() => setBlockTarget(null)}>Cancel</Button>
            <Button variant="danger" disabled={blockingPeer} onClick={() => void handleBlockPeer()}>
              {blockingPeer ? 'Blocking…' : 'Block user'}
            </Button>
          </>
        )}
      >
        <p className="nx-muted">Neither of you will be able to send direct messages until you unblock this user.</p>
        {blockError && <ToastBar variant="error" message={blockError} />}
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
      <SidebarSectionModal
        open={sectionModal !== null}
        mode={sectionModal?.mode === 'rename' ? 'rename' : 'create'}
        initialName={sectionModal?.mode === 'rename' ? sectionModal.section.name : ''}
        busy={sidebarOrg.busy}
        onClose={() => setSectionModal(null)}
        onSubmit={async name => {
          if (sectionModal?.mode === 'rename') {
            const ok = await sidebarOrg.renameSection(sectionModal.section.id, name);
            if (ok) setSectionModal(null);
          } else {
            const created = await sidebarOrg.createSection(name);
            if (created) setSectionModal(null);
          }
        }}
      />
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
