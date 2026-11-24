import { chatFetch as fetch, messagingHub, personalApiPath, protectedRequest } from "./scope";
import { CSSProperties, ChangeEvent, ClipboardEvent, DragEvent, FormEvent, KeyboardEvent, MouseEvent as ReactMouseEvent, Suspense, lazy, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { ArrowLeft, Ban, Bookmark, ChevronDown, ChevronRight, Eye, FileText, Flag, FolderOpen, FolderPlus, Link2, Mail, MessageSquare, Mic, MoreVertical, Paperclip, Pin, Plus, Search, Send, Star, Users, X } from 'lucide-react';
import { AttachmentGallery } from './attachments/AttachmentGallery';
import { AttachmentItem, StorageQuotaSummary, formatBytes } from './attachments/attachmentUtils';
import { ImageLightboxModal } from './attachments/ImageLightboxModal';
import { renderSafeMarkdown } from './markdown';
import { ChatJumpTarget, copyContentLink, extractContentLinkToken } from './contentLinks';
import { moveIdByOffset, reorderIdList, SidebarSectionDto, useSidebarOrganization } from './sidebarPrefs';
import { SidebarItemActions, SidebarSectionHeader } from './sidebarControls';
import { ConversationNotificationButton } from './notificationPreferences';
import { useChatDrafts } from './chatDrafts';
import { useChatReading } from './chatReading';
import { shouldSendOnEnter } from './composerKeys';
import { ComposerToolbar, handleComposerFormattingKeyDown } from './ComposerToolbar';
import {
  AvailabilityChangedEvent,
  normalizeAvailability,
  resolveUserAvailability
} from './availability';
import { OwnershipTransfer } from './transferOwnership';
import { readCursorPage, useCursorList } from './cursorList';
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
import { Avatar, Badge, Button, EmptyState, ToastBar } from './ui';
import { ChatMembersDrawer } from './chat/ChatDrawers';
import { MessageMutationActions } from './chat/MessageMutationActions';
import { BookmarkKind, MessageBookmarkActions, PendingBookmark, ToggleBookmark } from './chat/MessageBookmarkActions';
import type { OpenMessageMutation } from './chat/MessageMutationActions';
import type { MessageMutationResult, MessageMutationTarget } from './chat/MessageMutationDialog';
import type { ThreadDeltaReceiver, ThreadTarget } from './chat/ThreadPane';
import { mergeDmReadReceipt, type DmReadReceipt, type DmReadReceiptPreference } from './chat/dmReadReceipts';
import {
  QUICK_CHAT_REACTIONS,
  RECALLED_MESSAGE_LABEL,
  applyChatDelta,
  applyMessageMutation,
  applyPreviewMutation,
  applyReactionToMessage,
  applyThreadStateToMessage,
  deltaFromConversationEvent,
  formatMessagePreview,
  isMessageMutation
} from './chat/chatDeltaReducer';
import {
  ChatDelta,
  ChatMessageItem,
  ChatReadState,
  ChatWorkspaceProps,
  CommunityFeedDelta,
  ConversationCursor,
  ConversationListItem,
  DirectoryUserLite,
  GroupMemberItem,
  MessageMutationDelta,
  MessagePinDelta,
  MessageSaveDelta,
  ReactionSummary,
  ThreadState
} from './chat/types';

const LazySharedContentDrawer = lazy(() => import('./shared/SharedContentDrawer'));
const LazyVoiceNoteRecorder = lazy(() => import('./voice/VoiceNoteRecorder'));
const LazyEmojiPicker = lazy(() => import('./EmojiPicker'));
const LazyThreadPane = lazy(() => import('./chat/ThreadPane').then(module => ({ default: module.ThreadPane })));
const LazyMessageMutationDialog = lazy(() => import('./chat/MessageMutationDialog'));
const LazyChatModals = lazy(() => import('./chat/ChatModals').then(module => ({ default: module.ChatModals })));
const LazyBookmarksDialog = lazy(() => import('./chat/MessageBookmarksDialog'));

const QUICK_REACTION_LABELS: Record<(typeof QUICK_CHAT_REACTIONS)[number], string> = {
  '👍': 'thumbs up',
  '❤️': 'heart',
  '😂': 'laugh',
  '🎉': 'celebrate',
  '👀': 'eyes'
};

interface ChatMessagePage {
  items: ChatMessageItem[];
  changeSeq: number;
  hasMore: boolean;
  hasNewer: boolean;
  nextBeforeSeq: number | null;
  nextAfterSeq: number | null;
  lastReadSeq: number;
  dmReadReceipt?: DmReadReceipt | null;
}

function computeReactionPopoverStyle(triggerEl: HTMLElement): CSSProperties {
  const rect = triggerEl.getBoundingClientRect();
  const vw = window.innerWidth;
  const vh = window.innerHeight;
  const margin = 8;
  const topSafe = 56;
  const bottomSafe = vw <= 860 ? 68 : 8;
  const popoverWidth = Math.min(336, Math.max(240, vw - margin * 2));
  const left = Math.max(margin, Math.min(rect.left, vw - popoverWidth - margin));
  const spaceBelow = vh - bottomSafe - rect.bottom - 6;
  const spaceAbove = rect.top - topSafe - 6;

  if (spaceBelow >= 280 || spaceBelow >= spaceAbove) {
    const maxHeight = Math.min(340, Math.max(180, spaceBelow));
    const maxTop = Math.max(topSafe, vh - bottomSafe - maxHeight);
    const top = Math.max(topSafe, Math.min(rect.bottom + 6, maxTop));
    return {
      left: `${Math.round(left)}px`,
      top: `${Math.round(top)}px`,
      width: `${Math.round(popoverWidth)}px`,
      maxHeight: `${Math.round(maxHeight)}px`
    };
  }
  const maxHeight = Math.min(340, Math.max(180, spaceAbove));
  const maxBottom = Math.max(bottomSafe, vh - topSafe - maxHeight);
  const bottom = Math.max(bottomSafe, Math.min(vh - rect.top + 6, maxBottom));
  return {
    left: `${Math.round(left)}px`,
    bottom: `${Math.round(bottom)}px`,
    width: `${Math.round(popoverWidth)}px`,
    maxHeight: `${Math.round(maxHeight)}px`
  };
}

export type {
  AttachmentItem,
  ChatDelta,
  ChatMessageItem,
  CommunityFeedDelta,
  ConversationListItem,
  DirectoryUserLite,
  ReactionSummary,
  StorageQuotaSummary
};

function applyReadState(conversation: ConversationListItem, lastReadSeq: number, unreadCount?: number,
  manual?: Pick<ChatReadState, 'manualUnreadFromSeq' | 'manualUnreadVersion'>): ConversationListItem {
  const existingVersion = conversation.manualUnreadVersion ?? 0;
  const staleMarker = !!manual && manual.manualUnreadVersion < existingVersion;
  if (lastReadSeq < conversation.lastReadSeq && (!manual || manual.manualUnreadVersion <= existingVersion)) return conversation;
  if (!manual && lastReadSeq === conversation.lastReadSeq && unreadCount === undefined) return conversation;
  const manualFrom = manual && !staleMarker ? manual.manualUnreadFromSeq : conversation.manualUnreadFromSeq;
  const read = Math.max(lastReadSeq, conversation.lastReadSeq);

  return {
    ...conversation,
    lastReadSeq: read,
    manualUnreadFromSeq: manualFrom,
    manualUnreadVersion: manual && !staleMarker ? manual.manualUnreadVersion : existingVersion,
    unreadCount: staleMarker ? conversation.unreadCount : unreadCount ??
      (!manualFrom && (conversation.latestMainSeq ?? conversation.nextSeq) <= read ? 0 : conversation.unreadCount)
  };
}

const groupMemberKey = (member: GroupMemberItem) => member.userId;

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
  onMessageRecalled,
  showDmReadReceipts,
  onDmReadReceiptPreference,
  onConnectionStateChange,
  onUnreadTotalChange,
  onCommunityFeedChanged,
  onVersionStale,
  availabilityMap,
  nowMs,
  onAvailabilityChanged,
  onOpenProfileCard
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
  const messagesRef = useRef<ChatMessageItem[]>(messages);
  messagesRef.current = messages;
  const currentUserIdRef = useRef(currentUserId);
  currentUserIdRef.current = currentUserId;
  const inFlightReactionKeysRef = useRef(new Set<string>());
  const [pendingReactions, setPendingReactions] = useState<Record<string, Record<string, boolean>>>({});
  const addReactionWrapRefs = useRef(new Map<string, HTMLSpanElement>());
  const [reactionPicker, setReactionPicker] = useState<{
    messageId: string;
    style: CSSProperties;
  } | null>(null);
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
  const knownDmReceipts = useRef(new Map<string, DmReadReceipt>());
  const dmReceiptPreferenceCallback = useRef(onDmReadReceiptPreference);
  dmReceiptPreferenceCallback.current = onDmReadReceiptPreference;

  // New group modal & Private self chat (M6.29)
  const [showNewGroupModal, setShowNewGroupModal] = useState(false);
  const [openingSelfChat, setOpeningSelfChat] = useState(false);
  const openingSelfChatRef = useRef(false);
  const [newGroupTitle, setNewGroupTitle] = useState('');
  const [newGroupSearch, setNewGroupSearch] = useState('');
  const [selectedGroupMemberIds, setSelectedGroupMemberIds] = useState<string[]>([]);

  // Group members & Files drawer (M2.8 / M3.5)
  const [rightDrawer, setRightDrawer] = useState<'none' | 'members' | 'files' | 'thread'>('none');
  const rightDrawerRef = useRef(rightDrawer);
  rightDrawerRef.current = rightDrawer;
  const [threadTarget, setThreadTarget] = useState<ThreadTarget | null>(null);
  const [threadStates, setThreadStates] = useState<Record<string, ThreadState>>({});
  const [pendingThreadReplies, setPendingThreadReplies] = useState(new Map<string, string>());
  const pendingThreadReplyRef = useRef(new Map<string, string>());
  const threadOriginRef = useRef<HTMLElement | null>(null);
  const threadDeltaRef = useRef<ThreadDeltaReceiver | null>(null);
  const updateThreadState = useCallback((state: ThreadState) => {
    setThreadStates(current => (current[state.parentMessageId]?.version ?? -1) > state.version
      ? current
      : { ...current, [state.parentMessageId]: state });
  }, []);

  const updatePendingThreadReply = useCallback((parentMessageId: string, clientMsgId: string, pending: boolean) => {
    if (pending) pendingThreadReplyRef.current.set(clientMsgId, parentMessageId);
    else if (!pendingThreadReplyRef.current.delete(clientMsgId)) return;
    setPendingThreadReplies(new Map(pendingThreadReplyRef.current));
  }, []);

  const dispatchThreadDelta = useCallback((conversationId: string, delta: ChatDelta, changeSeq: number) => {
    if (delta.type === 'thread_reply_created' && delta.message.senderId === currentUserIdRef.current) {
      updatePendingThreadReply(delta.messageId, delta.message.clientMsgId, false);
    }
    threadDeltaRef.current?.(conversationId, delta, changeSeq);
  }, [updatePendingThreadReply]);
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
  const [sharedRefreshNonce, setSharedRefreshNonce] = useState(0);
  const [sharedLinkRevision, setSharedLinkRevision] = useState(0);
  const sharedBtnWrapRef = useRef<HTMLSpanElement | null>(null);
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
  const [mutationTarget, setMutationTarget] = useState<MessageMutationTarget | null>(null);
  const [pendingMutations, setPendingMutations] = useState(new Map<string, MessageMutationDelta>());
  const inFlightMessageIdsRef = useRef(new Map<string, MessageMutationDelta>());
  const confirmedMutations = useRef(new Map<string, { conversationId: string; delta: MessageMutationDelta }>());
  const [recalledMessageIds, setRecalledMessageIds] = useState(new Set<string>());
  const onMessageRecalledRef = useRef(onMessageRecalled);
  onMessageRecalledRef.current = onMessageRecalled;
  const [bookmarkPanel, setBookmarkPanel] = useState<{ conversationId?: string } | null>(null);
  const [bookmarkRevision, setBookmarkRevision] = useState(0);
  const [pendingBookmarks, setPendingBookmarks] = useState(new Map<string, PendingBookmark>());
  const bookmarkRequests = useRef(new Map<string, PendingBookmark>());
  const unreadRequests = useRef(new Map<string, number>());
  const [pendingUnread, setPendingUnread] = useState(new Map<string, number>());
  const skipAutoSelect = useRef(false);
  const [observedUnreadVersion, setObservedUnreadVersion] = useState<number | null>(null);
  const bookmarkStates = useRef(new Map<string, { delta: MessagePinDelta | MessageSaveDelta; changeSeq: number }>());
  const savedSyncNeeded = useRef(new Set<string>());
  const [mutationError, setMutationError] = useState<{ conversationId: string; message: string } | null>(null);
  const mutationTrigger = useRef<HTMLButtonElement | null>(null);

  const moreMenuRef = useRef<HTMLDivElement | null>(null);
  const moreBtnRef = useRef<HTMLButtonElement | null>(null);
  const reportMenuItemRef = useRef<HTMLButtonElement | null>(null);
  const blockMenuItemRef = useRef<HTMLButtonElement | null>(null);

  const hubRef = useRef<signalR.HubConnection | null>(null);
  const activeConvRef = useRef<string | null>(selectedConversationId);
  const composerRef = useRef<HTMLTextAreaElement | null>(null);
  const compositionActive = useRef(false);
  const [linkTriggerSignal, setLinkTriggerSignal] = useState(0);
  const [voiceRecorderOpen, setVoiceRecorderOpen] = useState(false);
  const voiceBtnWrapRef = useRef<HTMLSpanElement | null>(null);

  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const typingTimerRef = useRef<number | null>(null);
  activeConvRef.current = selectedConversationId;

  const loadConversationsRef = useRef<() => Promise<void>>(async () => {});
  const syncDeltaRef = useRef<(convId: string) => Promise<void>>(async () => {});
  const reconcileRef = useRef<() => Promise<void>>(async () => {});
  const connStateCbRef = useRef(onConnectionStateChange);
  const communityFeedCbRef = useRef(onCommunityFeedChanged);
  const orgRevokedCbRef = useRef(onOrganizationAccessRevoked);
  const availabilityChangedCbRef = useRef(onAvailabilityChanged);
  const discardConversationRef = useRef<(id: string) => void>(() => {});
  const conversationRefreshRef = useRef({ timer: null as number | null, queued: false });
  const conversationListRequest = useRef<AbortController | null>(null);
  const conversationPages = useRef<{ count: number; next: ConversationCursor | null }>({ count: 1, next: null });
  const deltaRequests = useRef(new Map<string, { queued: boolean; full: boolean; promise: Promise<void> }>());
  const messagesCacheRef = useRef(new Map<string, { items: ChatMessageItem[]; hasMore: boolean; nextBeforeSeq: number | null; lastRead: number }>());
  const cursorCacheRef = useRef(new Map<string, number>());
  const flushOfflineQueueRef = useRef<() => Promise<void>>(async () => {});
  connStateCbRef.current = onConnectionStateChange;
  communityFeedCbRef.current = onCommunityFeedChanged;
  orgRevokedCbRef.current = onOrganizationAccessRevoked;
  availabilityChangedCbRef.current = onAvailabilityChanged;

  const updateOutgoing = useCallback((update: (current: QueuedMessage[]) => QueuedMessage[]) => {
    outgoingRef.current = update(outgoingRef.current);
    setOutgoing(outgoingRef.current);
  }, []);

  const closeRecalledMedia = useCallback((messageId: string) => {
    setLightboxImage(current => current?.messageId === messageId ? null : current);
    setReactionPicker(current => current?.messageId === messageId ? null : current);
  }, []);

  const confirmBookmark = useCallback((conversationId: string, delta: MessagePinDelta | MessageSaveDelta, changeSeq = 0) => {
    const key = `${delta.type}:${delta.messageId}`;
    const previous = bookmarkStates.current.get(key);
    if (discarded.current.has(conversationId) || (previous && (delta.type === 'message_pin'
      ? changeSeq <= previous.changeSeq
      : previous.delta.type === 'message_save' && delta.version <= previous.delta.version))) return;
    bookmarkStates.current.set(key, { delta, changeSeq });
    const apply = (items: ChatMessageItem[]) => applyChatDelta(items, delta, currentUserIdRef.current, changeSeq);
    const cached = messagesCacheRef.current.get(conversationId);
    if (cached) messagesCacheRef.current.set(conversationId, { ...cached, items: apply(cached.items) });
    if (activeConvRef.current === conversationId) setMessages(apply);
    threadDeltaRef.current?.(conversationId, delta, changeSeq);
    setBookmarkRevision(current => current + 1);
  }, []);

  const refreshSavedState = useCallback(async (conversationId: string) => {
    const cached = messagesCacheRef.current.get(conversationId);
    if (!cached || !savedSyncNeeded.current.has(conversationId)) return;
    for (let start = 0; start < cached.items.length; start += 50) {
      const query = new URLSearchParams(cached.items.slice(start, start + 50).map(message => ['ids', message.id]));
      const response = await fetch(`/api/conversations/${conversationId}/saved-state?${query}`);
      if (!response.ok) return;
      const data: { items: Omit<MessageSaveDelta, 'type'>[] } = await response.json();
      for (const item of data.items) confirmBookmark(conversationId, { ...item, type: 'message_save' });
    }
    savedSyncNeeded.current.delete(conversationId);
  }, [confirmBookmark]);

  const applyConfirmedMutation = useCallback((conversationId: string, delta: MessageMutationDelta, changeSeq = 0) => {
    if (discarded.current.has(conversationId)) return;
    const previous = confirmedMutations.current.get(delta.messageId)?.delta;
    if (previous && (previous.version >= delta.version || previous.deletedAt)) return;
    confirmedMutations.current.set(delta.messageId, { conversationId, delta });
    const apply = (items: ChatMessageItem[]) => applyChatDelta(items, delta, currentUserIdRef.current, changeSeq);
    const cached = messagesCacheRef.current.get(conversationId);
    if (cached) messagesCacheRef.current.set(conversationId, { ...cached, items: apply(cached.items) });
    if (activeConvRef.current === conversationId) setMessages(apply);
    threadDeltaRef.current?.(conversationId, delta, changeSeq);
    if (!delta.parentMessageId) setConversations(current => current.map(item => {
      if (item.id !== conversationId || !item.lastMessagePreview) return item;
      const preview = applyPreviewMutation(item.lastMessagePreview, delta);
      return preview === item.lastMessagePreview ? item : { ...item, lastMessagePreview: preview };
    }));
    if (delta.type === 'message_recalled') {
      setRecalledMessageIds(current => new Set([...current, delta.messageId]));
      closeRecalledMedia(delta.messageId);
      onMessageRecalledRef.current?.(delta.messageId);
    } else if (activeConvRef.current === conversationId) {
      setSharedLinkRevision(current => current + 1);
    }
  }, [closeRecalledMedia]);

  const discardConversation = useCallback((id: string) => {
    discarded.current.add(id);
    knownDmReceipts.current.delete(id);
    uploadRequests.current.get(id)?.abort();
    messagesCacheRef.current.delete(id);
    cursorCacheRef.current.delete(id);
    setBookmarkPanel(current => current?.conversationId === id ? null : current);
    setBookmarkRevision(current => current + 1);
    for (const [messageId, entry] of confirmedMutations.current) {
      if (entry.conversationId === id) confirmedMutations.current.delete(messageId);
    }
    setThreadStates(current => Object.fromEntries(Object.entries(current).filter(([, state]) => state.conversationId !== id)));
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
      for (const item of items) {
        if (item.kind !== 'dm' || item.dmReadReceipt === undefined) continue;
        if (!item.dmReadReceipt) {
          knownDmReceipts.current.delete(item.id);
          continue;
        }
        const receipt = mergeDmReadReceipt(knownDmReceipts.current.get(item.id), item.dmReadReceipt);
        knownDmReceipts.current.set(item.id, receipt);
        item.dmReadReceipt = receipt;
      }
      setConversations(previous => {
        const readByConversation = new Map(previous.map(item => [item.id, item]));
        const merged = new Map(older ? previous.map(item => [item.id, item]) : []);
        for (const item of items) {
          const preview = item.lastMessagePreview;
          const mutation = preview?.messageId ? confirmedMutations.current.get(preview.messageId)?.delta : undefined;
          const projected = preview && mutation ? { ...item, lastMessagePreview: applyPreviewMutation(preview, mutation) } : item;
          const prior = readByConversation.get(item.id);
          const newerMark = prior && (prior.manualUnreadVersion ?? 0) > (projected.manualUnreadVersion ?? 0);
          merged.set(item.id, newerMark
            ? applyReadState(projected, prior.lastReadSeq, prior.unreadCount, {
              manualUnreadFromSeq: prior.manualUnreadFromSeq ?? null, manualUnreadVersion: prior.manualUnreadVersion ?? 0
            })
            : applyReadState(projected, prior?.lastReadSeq ?? 0));
        }
        return [...merged.values()].sort((a, b) => {
          const diff = Date.parse(b.updatedAt ?? '') - Date.parse(a.updatedAt ?? '');
          return diff || (b.updatedAt ?? '').localeCompare(a.updatedAt ?? '') || b.id.localeCompare(a.id);
        });
      });
      if (!activeConvRef.current && !skipAutoSelect.current && items.length > 0 && window.innerWidth > 860) {
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

  const displayedConversations = useMemo(() => conversations.map(item => {
    const from = pendingUnread.get(item.id);
    if (from === undefined) return item;
    const visibleUnread = messagesCacheRef.current.get(item.id)?.items.filter(message => message.seq >= from).length ?? 0;
    return { ...item, unreadCount: Math.max(item.unreadCount, visibleUnread, 1) };
  }), [conversations, pendingUnread]);

  useEffect(() => {
    onUnreadTotalChange(displayedConversations.reduce((sum, item) => sum + item.unreadCount, 0));
  }, [displayedConversations, onUnreadTotalChange]);

  const markRead = useCallback(async (convId: string, seq: number, manualUnreadVersion?: number) => {
    const response = await fetch(`/api/conversations/${convId}/read`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ lastReadSeq: seq, manualUnreadVersion })
    });
    if ([401, 403, 404].includes(response.status)) discardConversation(convId);
    if (!response.ok) throw new Error('Unable to update the read cursor.');
    const state: ChatReadState = await response.json();
    const { lastReadSeq, unreadCount } = state;
    const cached = messagesCacheRef.current.get(convId);
    if (cached && lastReadSeq > cached.lastRead) messagesCacheRef.current.set(convId, { ...cached, lastRead: lastReadSeq });
    setConversations(items => items.map(item => item.id === convId ? applyReadState(item, lastReadSeq, unreadCount, state) : item));
    return lastReadSeq;
  }, [discardConversation]);

  const applyDmReadReceipt = useCallback((conversationId: string, incoming: DmReadReceipt | null) => {
    if (discarded.current.has(conversationId)) return;
    const current = knownDmReceipts.current.get(conversationId);
    const next = incoming ? mergeDmReadReceipt(current, incoming) : null;
    if (next) knownDmReceipts.current.set(conversationId, next);
    else knownDmReceipts.current.delete(conversationId);
    setConversations(items => items.map(item => item.id === conversationId && item.kind === 'dm' && item.dmReadReceipt !== next
      ? { ...item, dmReadReceipt: next } : item));
  }, []);

  const fetchMessagePage = useCallback(async (convId: string, query = ''): Promise<ChatMessagePage | null> => {
    while (activeConvRef.current === convId) {
      const res = await fetch(`/api/conversations/${convId}/messages?limit=50${query}`, { credentials: 'same-origin' });
      if ([401, 403, 404].includes(res.status)) discardConversation(convId);
      if (!res.ok || activeConvRef.current !== convId) return null;
      const data: ChatMessagePage = await res.json();
      if (activeConvRef.current !== convId) return null;
      // A live delta can arrive ahead of the response for a page that was not loaded yet.
      if (data.changeSeq < (cursorCacheRef.current.get(convId) ?? 0)) continue;
      if (data.dmReadReceipt !== undefined) applyDmReadReceipt(convId, data.dmReadReceipt);
      return {
        ...data,
        items: (data.items ?? []).map(message => {
          const mutation = confirmedMutations.current.get(message.id)?.delta;
          let items: ChatMessageItem[] = [{ ...(mutation ? applyMessageMutation(message, mutation) : message), reactionSnapshotSeq: data.changeSeq }];
          for (const type of ['message_pin', 'message_save']) {
            const bookmark = bookmarkStates.current.get(`${type}:${message.id}`);
            if (bookmark) items = applyChatDelta(items, bookmark.delta, currentUserIdRef.current, bookmark.changeSeq);
          }
          return items[0];
        })
      };
    }
    return null;
  }, [applyDmReadReceipt, discardConversation]);

  const loadMessagesFull = useCallback(async (convId: string) => {
    const data = await fetchMessagePage(convId);
    if (!data || activeConvRef.current !== convId) return;
    const serverItems = data.items;
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
  }, [fetchMessagePage, offlineScope]);

  const loadMessagesAround = useCallback(async (convId: string, seq: number): Promise<boolean> => {
    const data = await fetchMessagePage(convId, `&aroundSeq=${seq}`);
    if (!data || activeConvRef.current !== convId) return false;
    const serverItems = data.items;
    if (!cursorCacheRef.current.has(convId)) {
      cursorCacheRef.current.set(convId, data.changeSeq);
      void setConversationCursor(offlineScope, convId, data.changeSeq);
    }
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
  }, [fetchMessagePage, offlineScope]);

  const loadNewerMessages = useCallback(async () => {
    if (!selectedConversationId || !nextAfterSeq) return;
    const data = await fetchMessagePage(selectedConversationId, `&afterSeq=${nextAfterSeq}`);
    if (!data || activeConvRef.current !== selectedConversationId) return;
    const newer = data.items;
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
  }, [fetchMessagePage, hasMoreOlder, lastReadSnapshot, nextAfterSeq, nextBeforeSeq, selectedConversationId]);

  const loadOlderMessages = useCallback(async () => {
    if (!selectedConversationId || !nextBeforeSeq) return;
    const data = await fetchMessagePage(selectedConversationId, `&beforeSeq=${nextBeforeSeq}`);
    if (!data || activeConvRef.current !== selectedConversationId) return;
    const older = data.items;
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
  }, [fetchMessagePage, nextBeforeSeq, selectedConversationId]);

  const loadGroupMembers = useCallback(async (convId: string) => {
    if (activeConvRef.current === convId) await refreshGroupMembers.current();
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
      }
    } catch {
      if (!controller.signal.aborted && activeConvRef.current === target) setError('Upload interrupted. Try attaching the file again.');
    } finally {
      uploadRequests.current.delete(target);
      setUploads(new Set(uploadRequests.current.keys()));
    }
  }, [changeDraft, loadingDraft, selectedConversationId]);

  const uploadVoiceNoteToConversation = useCallback(async (file: File, durationMs: number): Promise<{ ok: true } | { ok: false; error: string }> => {
    const target = selectedConversationId;
    if (!target || loadingDraft || staging.current.has(target) || uploadRequests.current.has(target)) {
      return { ok: false, error: 'Please wait for the current upload to finish.' };
    }
    setError('');
    const controller = new AbortController();
    uploadRequests.current.set(target, controller);
    setUploads(new Set(uploadRequests.current.keys()));
    try {
      const csrfRes = await window.fetch('/api/auth/csrf', { signal: controller.signal });
      const csrfData = csrfRes.ok ? await csrfRes.json().catch(() => ({})) : {};
      const csrfToken = typeof csrfData.token === 'string' ? csrfData.token : '';

      const form = new FormData();
      form.append('clientUploadId', generateClientMsgId());
      form.append('file', file, file.name);
      form.append('durationMs', String(durationMs));

      const headers: Record<string, string> = {};
      if (csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;

      const res = await fetch(`/api/conversations/${target}/attachments`, {
        method: 'POST',
        credentials: 'same-origin',
        headers,
        body: form,
        signal: controller.signal
      });

      const data = await res.json().catch(() => ({}));
      if (controller.signal.aborted) return { ok: false, error: 'Upload cancelled.' };
      if (!res.ok) {
        return { ok: false, error: data.message || 'Unable to upload voice note.' };
      }
      if (data.attachment) {
        const { id, fileName, sizeBytes, thumbnailUrl } = data.attachment as AttachmentItem;
        changeDraft(target, current => ({
          attachments: [...current.attachments, { id, fileName, sizeBytes, thumbnailUrl }]
        }));
      }
      return { ok: true };
    } catch {
      if (controller.signal.aborted) return { ok: false, error: 'Upload cancelled.' };
      return { ok: false, error: 'Upload interrupted. Your recording is still ready to retry.' };
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
      const events: Array<{ changeSeq: number; eventType: string; payload: unknown }> = data.items ?? [];
      // A sequential SignalR delta can advance this cursor while HTTP is in flight.
      cursor = Math.max(cursor, cursorCacheRef.current.get(convId) ?? 0);
      const relevantEvents = events.filter(event => event.changeSeq > cursor);
      const deltas = new Map<number, ChatDelta>();
      for (const event of relevantEvents) {
        const delta = deltaFromConversationEvent(event.eventType, event.payload);
        if (delta) {
          deltas.set(event.changeSeq, delta);
          if (isMessageMutation(delta)) applyConfirmedMutation(convId, delta, event.changeSeq);
          else if (delta.type === 'message_pin') confirmBookmark(convId, delta, event.changeSeq);
          else dispatchThreadDelta(convId, delta, event.changeSeq);
        }
      }

      const applySyncedEvents = (prev: ChatMessageItem[], includeCreated: boolean): ChatMessageItem[] => {
        let next = prev;
        for (const event of relevantEvents) {
          if (event.eventType === 'message.created' && event.payload) {
            const original = event.payload as ChatMessageItem;
            const mutation = confirmedMutations.current.get(original.id)?.delta;
            const incomingMsg = mutation ? applyMessageMutation(original, mutation) : original;
            if (includeCreated && !incomingMsg.parentMessageId && !next.some(message => message.id === incomingMsg.id)) {
              next = [...next, incomingMsg].sort((a, b) => a.seq - b.seq);
            }
            continue;
          }
          const delta = deltas.get(event.changeSeq);
          if (delta) {
            next = applyChatDelta(next, delta, currentUserIdRef.current, event.changeSeq);
          }
        }
        return next;
      };

      if (relevantEvents.length > 0) {
        const cached = messagesCacheRef.current.get(convId);
        if (cached) {
          const nextCached = applySyncedEvents(cached.items, true);
          if (nextCached !== cached.items) {
            messagesCacheRef.current.set(convId, { ...cached, items: nextCached });
          }
        }
        setMessages(prev => applySyncedEvents(prev, !hasNewerRef.current));
      }

      const lastEventSeq = events.at(-1)?.changeSeq ?? cursor;
      const nextCursor = Math.max(lastEventSeq, typeof data.nextAfter === 'number' ? data.nextAfter : cursor);
      if (nextCursor <= cursor) return;
      cursorCacheRef.current.set(convId, nextCursor);
      void setConversationCursor(offlineScope, convId, nextCursor);
      cursor = nextCursor;
      if (!data.hasMore) return;
    }
  }, [applyConfirmedMutation, confirmBookmark, discardConversation, dispatchThreadDelta, loadMessagesFull, offlineScope]);

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

  const confirmThreadReply = useCallback((convId: string, clientMsgId: string) => {
    if (activeConvRef.current === convId && pendingThreadReplyRef.current.has(clientMsgId)) {
      return requestConversationDelta(convId);
    }
    return Promise.resolve();
  }, [requestConversationDelta]);

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
        if (attachments?.length && rightDrawerRef.current === 'files') setSharedRefreshNonce(n => n + 1);
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
  }, [checkPendingAttachments, offlineScope, onStorageError, scheduleConversationRefresh, updateOutgoing]);

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
    savedSyncNeeded.current = new Set(messagesCacheRef.current.keys());
    if (activeConvRef.current) {
      await requestConversationDelta(activeConvRef.current);
      if (activeConvRef.current) await refreshSavedState(activeConvRef.current).catch(() => {});
      if (rightDrawerRef.current === 'members') await loadGroupMembers(activeConvRef.current);
    }
    setReconnectCount(count => count + 1);
    setBookmarkRevision(current => current + 1);
  }, [checkApiVersion, flushOfflineQueue, loadConversations, loadGroupMembers, refreshSavedState, requestConversationDelta]);

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
    setThreadTarget(null);
    setMutationTarget(null);
    threadOriginRef.current = null;
    setVoiceRecorderOpen(false);
    setReactionPicker(null);
    setOwnershipTransfer(null);
    compositionActive.current = false;
    setMoreMenuOpen(false);
    setGroupMemberSearch('');
    setAddMemberId('');
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
      void requestConversationDelta(selectedConversationId, !cached)
        .then(() => refreshSavedState(selectedConversationId).catch(() => {})).catch(() => {
        if (activeConvRef.current === selectedConversationId) setError('Unable to load messages.');
      });
    }
  }, [selectedConversationId, refreshSavedState, requestConversationDelta]);

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
      const existing = messagesCacheRef.current.get(target.conversationId)?.items.some(m => m.id === (target.parentMessageId ?? target.messageId));
      if (!existing) {
        await loadMessagesAround(target.conversationId, target.parentSeq ?? target.seq);
      }
      if (pendingJumpRef.current?.nonce === target.nonce) pendingJumpRef.current = null;
      if (cancelled) return;
      if (target.parentMessageId) {
        threadOriginRef.current = null;
        setThreadTarget({
          conversationId: target.conversationId, parentMessageId: target.parentMessageId,
          replyId: target.messageId, replySeq: target.seq
        });
        setRightDrawer('thread');
        return;
      }
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

    connection.on('ConversationChanged', (evt: {
      conversationId: string;
      changeSeq: number;
      message?: ChatMessageItem | null;
      delta?: ChatDelta | null;
    }) => {
      const mutation = evt.message ? confirmedMutations.current.get(evt.message.id)?.delta : undefined;
      const incoming = evt.message && mutation ? applyMessageMutation(evt.message, mutation) : evt.message;
      const isActive = evt.conversationId === activeConvRef.current;

      if (incoming && !incoming.parentMessageId) {
        const mergeMessage = (prev: ChatMessageItem[]) => {
          if (prev.some(message => message.id === incoming.id)) return prev;
          return [...prev, incoming].sort((a, b) => a.seq - b.seq);
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
            const latestMainSeq = c.latestMainSeq ?? c.nextSeq;
            const isNewRoot = incoming.seq > latestMainSeq;
            return {
              ...c,
              nextSeq: Math.max(c.nextSeq, incoming.seq),
              latestMainSeq: Math.max(latestMainSeq, incoming.seq),
              changeSeq: Math.max(c.changeSeq, evt.changeSeq),
              unreadCount: c.unreadCount + (isNewRoot && incoming.seq > c.lastReadSeq ? 1 : 0),
              updatedAt: isNewRoot ? incoming.createdAt : c.updatedAt,
              lastMessagePreview: isNewRoot ? {
                messageId: incoming.id, version: incoming.version, seq: incoming.seq,
                senderDisplayName: incoming.senderDisplayName, createdAt: incoming.createdAt,
                bodyPreview: incoming.deletedAt ? RECALLED_MESSAGE_LABEL : formatMessagePreview(incoming.body)
              } : c.lastMessagePreview
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

      if (evt.delta) {
        const delta = evt.delta;
        const messageMutation = isMessageMutation(delta);
        if (messageMutation) applyConfirmedMutation(evt.conversationId, delta, evt.changeSeq);
        if (delta.type === 'message_pin') confirmBookmark(evt.conversationId, delta, evt.changeSeq);
        const knownSeq = cursorCacheRef.current.get(evt.conversationId);
        if (knownSeq !== undefined && evt.changeSeq <= knownSeq) return;

        if (knownSeq !== undefined && evt.changeSeq === knownSeq + 1) {
          if (!messageMutation && delta.type !== 'message_pin') {
            dispatchThreadDelta(evt.conversationId, delta, evt.changeSeq);
            const apply = (items: ChatMessageItem[]) => applyChatDelta(items, delta, currentUserIdRef.current, evt.changeSeq);
            const cached = messagesCacheRef.current.get(evt.conversationId);
            if (cached) {
              messagesCacheRef.current.set(evt.conversationId, { ...cached, items: apply(cached.items) });
            }
            if (isActive) setMessages(apply);
          }
          cursorCacheRef.current.set(evt.conversationId, evt.changeSeq);
          void setConversationCursor(offlineScope, evt.conversationId, evt.changeSeq);
          return;
        }

        if (isActive) void syncDeltaRef.current(evt.conversationId).catch(() => {});
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

    connection.on('ReadStateUpdated', (evt: ChatReadState) => {
      const cached = messagesCacheRef.current.get(evt.conversationId);
      if (cached && evt.lastReadSeq > cached.lastRead) {
        messagesCacheRef.current.set(evt.conversationId, { ...cached, lastRead: evt.lastReadSeq });
      }
      setConversations(previous => previous.map(item =>
        item.id === evt.conversationId ? applyReadState(item, evt.lastReadSeq, evt.unreadCount, evt) : item
      ));
      if (evt.manualUnreadFromSeq && unreadOnlyRef.current) scheduleConversationRefresh();
    });

    const receiveDmReceipt = (event: { conversationId: string; receipt: DmReadReceipt }) => {
      applyDmReadReceipt(event.conversationId, event.receipt);
    };
    connection.on('DmReadReceiptUpdated', receiveDmReceipt);
    connection.on('DmReadReceiptPrivacyChanged', receiveDmReceipt);
    connection.on('DmReadReceiptPreferenceChanged', (preference: DmReadReceiptPreference) => {
      dmReceiptPreferenceCallback.current(preference);
    });

    connection.on('ThreadStateUpdated', (evt: ThreadState) => {
      if (evt.parentMessageId && !discarded.current.has(evt.conversationId)) updateThreadState(evt);
    });

    connection.on('SavedMessageChanged', (event: Omit<MessageSaveDelta, 'type'>) => {
      confirmBookmark(event.conversationId, { ...event, type: 'message_save' });
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

    connection.on('AvailabilityChanged', (evt: AvailabilityChangedEvent) => {
      if (!evt?.userId) return;
      const cleanEvt: AvailabilityChangedEvent = {
        userId: evt.userId,
        availability: normalizeAvailability(evt.availability),
        isOnline: Boolean(evt.isOnline),
        updatedAt: evt.updatedAt ?? new Date().toISOString()
      };
      availabilityChangedCbRef.current(cleanEvt);
      setConversations(prev => prev.map(c =>
        c.peer?.id === cleanEvt.userId
          ? { ...c, peer: { ...c.peer, isOnline: cleanEvt.isOnline, availability: cleanEvt.availability } }
          : c
      ));
    });

    connection.onreconnecting(() => {
      if (!disposed) {
        knownDmReceipts.current.clear();
        setConversations(items => items.map(item => item.dmReadReceipt ? { ...item, dmReadReceipt: null } : item));
        connStateCbRef.current('reconnecting');
      }
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
  }, [applyConfirmedMutation, applyDmReadReceipt, confirmBookmark, dispatchThreadDelta, loadGroupMembers, scheduleConversationRefresh, updateThreadState]);

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

  async function handleCopyMessageLink(message: ChatMessageItem, announce = true) {
    const result = await copyContentLink({
      kind: 'message',
      organizationId: null,
      conversationId: message.conversationId,
      messageId: message.id
    });
    if (announce) setNotice(result.ok ? 'Message link copied.' : 'Unable to copy link automatically.');
    return result.ok;
  }

  function openThread(message: ChatMessageItem, trigger: HTMLElement) {
    if (rightDrawer === 'thread' && threadTarget?.parentMessageId === message.id) {
      closeThread();
      return;
    }
    threadOriginRef.current = trigger;
    setReactionPicker(null);
    setThreadTarget({ conversationId: message.conversationId, parentMessageId: message.id });
    setRightDrawer('thread');
  }

  function closeThread() {
    setRightDrawer('none');
    const origin = threadOriginRef.current;
    threadOriginRef.current = null;
    requestAnimationFrame(() => {
      if (origin?.isConnected) origin.focus();
      else composerRef.current?.focus();
    });
  }

  const openMessageMutation: OpenMessageMutation = (kind, message, trigger) => {
    if (inFlightMessageIdsRef.current.has(message.id)) return;
    mutationTrigger.current = trigger;
    setMutationError(null);
    setReactionPicker(null);
    setMutationTarget({ kind, message });
  };

  function closeMessageMutation() {
    const trigger = mutationTrigger.current;
    mutationTrigger.current = null;
    const fallback = trigger?.closest<HTMLElement>('[data-thread-reply-id], [data-thread-message-id]')
      ?? trigger?.closest('.nx-msg-row')?.querySelector<HTMLElement>('[data-message-id]');
    setMutationTarget(null);
    requestAnimationFrame(() => {
      if (trigger?.isConnected && !trigger.disabled) trigger.focus();
      else fallback?.focus({ preventScroll: true });
    });
  }

  function beginMessageMutation(delta: MessageMutationDelta) {
    if (inFlightMessageIdsRef.current.has(delta.messageId)) return false;
    inFlightMessageIdsRef.current.set(delta.messageId, delta);
    setPendingMutations(new Map(inFlightMessageIdsRef.current));
    if (delta.type === 'message_recalled') closeRecalledMedia(delta.messageId);
    return true;
  }

  function completeMessageMutation(message: ChatMessageItem, result: MessageMutationResult) {
    const { conversationId, id } = message;
    const pending = inFlightMessageIdsRef.current.get(id);
    inFlightMessageIdsRef.current.delete(id);
    setPendingMutations(new Map(inFlightMessageIdsRef.current));
    if (discarded.current.has(conversationId)) return;
    if ('delta' in result) {
      applyConfirmedMutation(conversationId, result.delta);
      return;
    }
    if (result.recalled && pending) {
      applyConfirmedMutation(conversationId, { ...pending, type: 'message_recalled', deletedAt: new Date().toISOString(), body: '' });
    }
    if (activeConvRef.current === conversationId) {
      setMutationError({ conversationId, message: result.error });
      if (result.refresh) void requestConversationDelta(conversationId).catch(() => {});
    }
  }

  const toggleBookmark: ToggleBookmark = (message, kind: BookmarkKind) => {
    if (message.deletedAt || message.pending || bookmarkRequests.current.has(message.id)) return;
    const value = kind === 'pin' ? !message.isPinned : !message.isSaved;
    bookmarkRequests.current.set(message.id, { kind, value });
    setPendingBookmarks(new Map(bookmarkRequests.current));
    setMutationError(null);
    void (async () => {
      try {
        const path = personalApiPath(`/api/conversations/${message.conversationId}/messages/${message.id}/${kind}`);
        const response = await protectedRequest(path, value ? 'PUT' : 'DELETE');
        if (!response.ok) throw new Error('Unable to update the message.');
        if (kind === 'pin') {
          const result = await response.json() as { delta: MessagePinDelta; changeSeq: number };
          confirmBookmark(message.conversationId, result.delta, result.changeSeq);
        } else {
          const result = await response.json() as Omit<MessageSaveDelta, 'type'>;
          confirmBookmark(message.conversationId, { ...result, type: 'message_save' });
        }
      } catch {
        if (activeConvRef.current === message.conversationId) {
          setMutationError({ conversationId: message.conversationId, message: 'Unable to update the message.' });
        }
      } finally {
        bookmarkRequests.current.delete(message.id);
        setPendingBookmarks(new Map(bookmarkRequests.current));
      }
    })();
  };

  async function markUnread(message: ChatMessageItem) {
    const { conversationId, seq } = message;
    if (message.pending || message.parentMessageId || unreadRequests.current.has(conversationId)) return;
    unreadRequests.current.set(conversationId, seq);
    setPendingUnread(new Map(unreadRequests.current));
    skipAutoSelect.current = true;
    setError('');
    onSelectConversation(null);
    requestAnimationFrame(() => document.getElementById(`nx-conversation-${conversationId}`)?.focus());
    try {
      const response = await protectedRequest(personalApiPath(`/api/conversations/${conversationId}/unread`), 'PUT', { fromSeq: seq });
      if ([401, 403, 404].includes(response.status)) discardConversation(conversationId);
      if (!response.ok) throw new Error('Unable to mark the conversation as unread.');
      const state: ChatReadState = await response.json();
      setConversations(items => items.map(item => item.id === conversationId
        ? applyReadState(item, state.lastReadSeq, state.unreadCount, state) : item));
      if (unreadOnlyRef.current) scheduleConversationRefresh();
    } catch {
      setError('Unable to mark the conversation as unread.');
      if (!activeConvRef.current && !discarded.current.has(conversationId)) onSelectConversation(conversationId);
    } finally {
      unreadRequests.current.delete(conversationId);
      setPendingUnread(new Map(unreadRequests.current));
    }
  }

  function focusAddReactionButton(messageId: string) {
    requestAnimationFrame(() => {
      addReactionWrapRefs.current.get(messageId)?.querySelector('button')?.focus();
    });
  }

  function handleToggleReactionPicker(messageId: string, triggerEl: HTMLElement) {
    if (reactionPicker?.messageId === messageId) {
      setReactionPicker(null);
      return;
    }
    setReactionPicker({
      messageId,
      style: computeReactionPopoverStyle(triggerEl)
    });
  }

  async function handleToggleMessageReaction(message: ChatMessageItem, rawEmoji: string) {
    const emoji = rawEmoji.trim();
    if (!emoji || message.pending || message.deletedAt) return;

    const key = `${message.id}:${emoji}`;
    if (inFlightReactionKeysRef.current.has(key)) return;
    inFlightReactionKeysRef.current.add(key);

    const latestMessage = messagesRef.current.find(item => item.id === message.id) ?? message;
    const nextReacted = !latestMessage.reactions?.find(r => r.emoji === emoji)?.reactedByMe;

    setError('');
    setPendingReactions(pending => ({
      ...pending,
      [message.id]: { ...pending[message.id], [emoji]: nextReacted }
    }));

    try {
      const res = await protectedRequest(
        personalApiPath(`/api/conversations/${message.conversationId}/messages/${message.id}/reactions/${encodeURIComponent(emoji)}`),
        nextReacted ? 'PUT' : 'DELETE'
      );
      if (!res.ok) {
        const errData = await res.json().catch(() => ({}));
        if (activeConvRef.current === message.conversationId) {
          setError(typeof errData.message === 'string' && errData.message ? errData.message : 'Unable to update reaction.');
        }
        return;
      }
      const { changeSeq } = await res.json();
      if (activeConvRef.current === message.conversationId && changeSeq > (cursorCacheRef.current.get(message.conversationId) ?? 0)) {
        await requestConversationDelta(message.conversationId);
      }
    } catch (err) {
      if (activeConvRef.current === message.conversationId) {
        setError(err instanceof Error && err.message ? err.message : 'Unable to update reaction.');
        void requestConversationDelta(message.conversationId).catch(() => {});
      }
    } finally {
      inFlightReactionKeysRef.current.delete(key);
      setPendingReactions(pending => {
        const next = { ...pending };
        const messageReactions = { ...next[message.id] };
        delete messageReactions[emoji];
        if (Object.keys(messageReactions).length > 0) next[message.id] = messageReactions;
        else delete next[message.id];
        return next;
      });
    }
  }

  function handleComposerKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (
      selectedConversationId &&
      handleComposerFormattingKeyDown(
        e,
        compositionActive.current,
        composer,
        nextBody => changeDraft(selectedConversationId, () => ({ body: nextBody })),
        () => setLinkTriggerSignal(n => n + 1)
      )
    ) {
      return;
    }
    if (shouldSendOnEnter(e.nativeEvent, compositionActive.current)) {
      e.preventDefault();
      void sendCurrentMessage();
    }
  }

  async function handleOpenSelfChat() {
    const existing = conversations.find(c => c.kind === 'self');
    if (existing) {
      if (rightDrawer === 'members') setRightDrawer('none');
      onSelectConversation(existing.id);
      requestAnimationFrame(() => composerRef.current?.focus());
      return;
    }
    if (openingSelfChatRef.current) return;
    openingSelfChatRef.current = true;
    setOpeningSelfChat(true);
    setError('');
    try {
      const res = await protectedRequest(personalApiPath('/api/conversations/self'), 'POST');
      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        setError(typeof err.message === 'string' && err.message ? err.message : 'Unable to open Notes to self.');
        return;
      }
      const data = await res.json();
      const selfConv: ConversationListItem = {
        id: data.id,
        kind: 'self',
        title: typeof data.title === 'string' && data.title ? data.title : 'Notes to self',
        role: 'Owner',
        nextSeq: typeof data.nextSeq === 'number' ? data.nextSeq : 0,
        changeSeq: typeof data.changeSeq === 'number' ? data.changeSeq : 0,
        lastReadSeq: typeof data.nextSeq === 'number' ? data.nextSeq : 0,
        unreadCount: 0,
        memberCount: 1,
        updatedAt: new Date().toISOString(),
        lastMessagePreview: null,
        peer: null
      };
      setConversations(prev => (prev.some(c => c.id === selfConv.id) ? prev : [selfConv, ...prev]));
      if (rightDrawer === 'members') setRightDrawer('none');
      onSelectConversation(selfConv.id);
      requestAnimationFrame(() => composerRef.current?.focus());
    } catch {
      setError('Unable to open Notes to self.');
    } finally {
      openingSelfChatRef.current = false;
      setOpeningSelfChat(false);
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
  useEffect(() => {
    setObservedUnreadVersion(visible && activeConv ? activeConv.manualUnreadVersion ?? 0 : null);
  }, [activeConv?.id, visible]);
  const activeId = activeConv?.id;
  const isSelfConv = activeConv?.kind === 'self';
  const blockedPeer = activeConv?.peer?.isBlockedByMe ?? false;

  useEffect(() => {
    if (isSelfConv && rightDrawer === 'members') {
      setRightDrawer('none');
    }
  }, [isSelfConv, rightDrawer]);

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

  const activeMessages = useMemo(() => messages.filter(message => message.conversationId === selectedConversationId
    && !message.parentMessageId), [messages, selectedConversationId]);
  const receiptSeq = showDmReadReceipts && activeConv?.kind === 'dm' && !blockedPeer ? activeConv.dmReadReceipt?.peerLastReadSeq : undefined;
  const seenMessageId = receiptSeq === undefined ? undefined : activeMessages.reduceRight<string | undefined>((id, message) =>
    id ?? (message.senderId === currentUserId && message.seq <= receiptSeq && !message.pending && !message.deletedAt ? message.id : undefined), undefined);
  const confirmedOwnMessages = new Set(activeMessages.filter(message => message.senderId === currentUserId).map(message => message.clientMsgId));
  const visibleOutgoing = outgoing.filter(item => item.conversationId === selectedConversationId && !confirmedOwnMessages.has(item.clientMsgId));
  const openThreadTarget = rightDrawer === 'thread' && threadTarget?.conversationId === selectedConversationId ? threadTarget : null;
  const activeMutationError = mutationError?.conversationId === selectedConversationId ? mutationError?.message : undefined;
  const hiddenSharedMessageIds = useMemo(() => {
    const ids = new Set(recalledMessageIds);
    for (const delta of pendingMutations.values()) {
      if (delta.type === 'message_recalled') ids.add(delta.messageId);
    }
    return ids;
  }, [pendingMutations, recalledMessageIds]);
  const manualRead = activeConv?.manualUnreadFromSeq && typeof observedUnreadVersion === 'number' &&
    observedUnreadVersion === activeConv.manualUnreadVersion
    ? { fromSeq: activeConv.manualUnreadFromSeq, version: observedUnreadVersion } : undefined;
  const reading = useChatReading(activeConv?.id ?? null, visible && !openThreadTarget, activeMessages, visibleOutgoing.length,
    activeConv?.lastReadSeq ?? 0, markRead, reconnectCount, manualRead);
  const pendingThreadCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const id of pendingThreadReplies.values()) counts.set(id, (counts.get(id) ?? 0) + 1);
    return counts;
  }, [pendingThreadReplies]);
  const canPin = !!activeConv && !blockedPeer &&
    (activeConv.kind !== 'group' || activeConv.role === 'Owner' || activeConv.role === 'Admin');
  const hiddenBookmarks = new Set(hiddenSharedMessageIds);
  if (bookmarkPanel) {
    const kind = bookmarkPanel.conversationId ? 'pin' : 'save';
    for (const { delta } of bookmarkStates.current.values()) {
      if ((kind === 'pin' && delta.type === 'message_pin' && !delta.pinned) ||
          (kind === 'save' && delta.type === 'message_save' && !delta.saved)) hiddenBookmarks.add(delta.messageId);
    }
    for (const [id, pending] of pendingBookmarks) {
      if (pending.kind === kind && !pending.value) hiddenBookmarks.add(id);
    }
  }

  useEffect(() => {
    if (!reactionPicker) return;
    const targetId = reactionPicker.messageId;

    const handleOutsideClick = (ev: MouseEvent) => {
      const wrap = addReactionWrapRefs.current.get(targetId);
      if (!wrap || !wrap.contains(ev.target as Node)) {
        setReactionPicker(null);
      }
    };

    const handleEsc = (ev: globalThis.KeyboardEvent) => {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        setReactionPicker(null);
        requestAnimationFrame(() => {
          addReactionWrapRefs.current.get(targetId)?.querySelector('button')?.focus();
        });
      }
    };

    const updatePosition = () => {
      const wrap = addReactionWrapRefs.current.get(targetId);
      const btn = wrap?.querySelector('button');
      const feed = reading.feedRef.current;
      if (!btn || !feed) {
        setReactionPicker(null);
        return;
      }
      const btnRect = btn.getBoundingClientRect();
      const feedRect = feed.getBoundingClientRect();
      if (btnRect.bottom < feedRect.top || btnRect.top > feedRect.bottom) {
        setReactionPicker(null);
        return;
      }
      setReactionPicker(prev =>
        prev && prev.messageId === targetId
          ? { messageId: targetId, style: computeReactionPopoverStyle(btn) }
          : prev
      );
    };

    const feed = reading.feedRef.current;
    document.addEventListener('mousedown', handleOutsideClick);
    document.addEventListener('keydown', handleEsc);
    window.addEventListener('resize', updatePosition);
    feed?.addEventListener('scroll', updatePosition, { passive: true });
    return () => {
      document.removeEventListener('mousedown', handleOutsideClick);
      document.removeEventListener('keydown', handleEsc);
      window.removeEventListener('resize', updatePosition);
      feed?.removeEventListener('scroll', updatePosition);
    };
  }, [reactionPicker?.messageId, reading.feedRef]);

  const filteredConvs = useMemo(() => {
    const q = convSearch.trim().toLowerCase();
    return displayedConversations.filter(c => {
      if (unreadOnly && c.unreadCount <= 0) return false;
      if (chatFilter === 'dm' && c.kind !== 'dm') return false;
      if (chatFilter === 'group' && c.kind !== 'group') return false;
      if (!q) return true;
      return c.title.toLowerCase().includes(q)
        || (c.kind === 'self' && ('notes to self'.includes(q) || 'self'.includes(q)))
        || (c.peer?.username ?? '').toLowerCase().includes(q)
        || (c.lastMessagePreview?.bodyPreview ?? '').toLowerCase().includes(q);
    });
  }, [chatFilter, convSearch, displayedConversations, unreadOnly]);

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

  const visibleSelfConvs = useMemo(() => sortBySidebarOrder(ungroupedConvs.filter(c => c.kind === 'self')), [sortBySidebarOrder, ungroupedConvs]);
  const visibleDmConvs = useMemo(() => sortBySidebarOrder(ungroupedConvs.filter(c => c.kind === 'dm')), [sortBySidebarOrder, ungroupedConvs]);
  const visibleGroupConvs = useMemo(() => sortBySidebarOrder(ungroupedConvs.filter(c => c.kind !== 'dm' && c.kind !== 'self')), [sortBySidebarOrder, ungroupedConvs]);
  const visibleConvCount = favoriteConvs.length
    + customSectionGroups.reduce((sum, g) => sum + g.items.length, 0)
    + visibleSelfConvs.length
    + visibleDmConvs.length
    + visibleGroupConvs.length
    + hiddenConvs.length;

  const renderConvItem = (c: ConversationListItem, groupList: ConversationListItem[]) => {
    const isActive = c.id === selectedConversationId;
    const isDm = c.kind === 'dm';
    const isSelf = c.kind === 'self';
    const pref = sidebarOrg.itemByTargetId.get(c.id);
    const groupIds = groupList.map(item => item.id);
    const idx = groupIds.indexOf(c.id);
    const savedPreview = c.lastMessagePreview;
    const pending = savedPreview?.messageId ? pendingMutations.get(savedPreview.messageId) : undefined;
    const preview = savedPreview && pending ? applyPreviewMutation(savedPreview, pending) : savedPreview;
    const peerStatus = isDm && c.peer
      ? resolveUserAvailability(c.peer.id, c.peer, availabilityMap, nowMs)
      : null;
    const previewText = preview
      ? (isSelf ? preview.bodyPreview : `${preview.senderDisplayName}: ${preview.bodyPreview}`)
      : isSelf
        ? 'Private notes and files'
        : isDm
          ? (peerStatus ? `${peerStatus.label}${peerStatus.expiryText ? ` · ${peerStatus.expiryText}` : ''}` : `@${c.peer?.username ?? 'direct'}`)
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
          id={`nx-conversation-${c.id}`}
          className={`nx-conv-item ${isActive ? 'active' : ''}`}
          onClick={() => onSelectConversation(c.id)}
        >
          {isSelf ? (
            <span className="nx-avatar self" aria-hidden="true">
              <Bookmark size={16} />
            </span>
          ) : (
            <Avatar
              displayName={c.title}
              avatarUrl={isDm ? c.peer?.avatarUrl : null}
              variant={isDm ? 'user' : 'group'}
              presence={peerStatus?.dotTone}
            />
          )}
          <div className="nx-conv-meta">
            <div className="nx-conv-top">
              <span className="nx-conv-name">
                {pref?.isFavorite && <Star size={12} aria-label="Favorite" />}
                <span>{c.title}</span>
                {isSelf && <Badge variant="role">Private</Badge>}
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
    <div className={`nx-chat-layout ${activeConv ? 'has-selection' : ''} ${openThreadTarget ? 'has-thread' : ''}`}>
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
            <Button size="sm" disabled={openingSelfChat} onClick={() => void handleOpenSelfChat()}>
              <Bookmark size={14} aria-hidden="true" /> Notes to self
            </Button>
            <Button variant="ghost" size="sm" iconOnly aria-label="Saved messages" title="Saved messages"
              onClick={() => setBookmarkPanel({})}>
              <Bookmark size={14} aria-hidden="true" />
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
              {visibleSelfConvs.length > 0 && (
                <>
                  <div className="nx-section-label">Personal</div>
                  {visibleSelfConvs.map(c => renderConvItem(c, visibleSelfConvs))}
                </>
              )}
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
        {!openThreadTarget && activeMutationError && <ToastBar variant="error" message={activeMutationError} onDismiss={() => setMutationError(null)} />}
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
                {activeConv.kind === 'dm' && activeConv.peer ? (() => {
                  const activePeer = activeConv.peer;
                  const peerStatus = resolveUserAvailability(activePeer.id, activePeer, availabilityMap, nowMs);
                  return (
                    <button
                      type="button"
                      className="nx-profile-trigger nx-stage-profile-btn"
                      aria-label={`View profile for ${activeConv.title}`}
                      onClick={() =>
                        onOpenProfileCard({
                          userId: activePeer.id,
                          organizationId: null,
                          fallbackDisplayName: activePeer.displayName,
                          fallbackUsername: activePeer.username,
                          fallbackAvatarUrl: activePeer.avatarUrl
                        })
                      }
                    >
                      <Avatar
                        displayName={activeConv.title}
                        avatarUrl={activePeer.avatarUrl}
                        variant="user"
                        presence={peerStatus.dotTone}
                      />
                      <div className="nx-stage-identity">
                        <h3>
                          <span className="nx-stage-name">{activeConv.title}</span>
                        </h3>
                        <div className="nx-stage-subtitle">
                          <span className={`nx-dot ${peerStatus.dotTone}`} aria-hidden="true" />
                          <span className="nx-stage-status-label">{peerStatus.label}</span>
                          {peerStatus.expiryText && <span className="nx-stage-handle">· {peerStatus.expiryText}</span>}
                          {activePeer.username && <span className="nx-stage-handle">· @{activePeer.username}</span>}
                        </div>
                      </div>
                    </button>
                  );
                })() : isSelfConv ? (
                  <>
                    <span className="nx-avatar self" aria-hidden="true">
                      <Bookmark size={16} />
                    </span>
                    <div className="nx-stage-identity">
                      <h3>
                        <span className="nx-stage-name">{activeConv.title}</span>
                      </h3>
                      <div className="nx-stage-subtitle">
                        <span className="nx-stage-status-label">Private notes and files · Only visible to you</span>
                      </div>
                    </div>
                  </>
                ) : (
                  <>
                    <Avatar
                      displayName={activeConv.title}
                      avatarUrl={null}
                      variant="group"
                    />
                    <div className="nx-stage-identity">
                      <h3>
                        <span className="nx-stage-name">{activeConv.title}</span>
                      </h3>
                      <div className="nx-stage-subtitle">
                        <span className="nx-stage-status-label">
                          {activeConv.memberCount} {activeConv.memberCount === 1 ? 'member' : 'members'} · Role: {activeConv.role}
                        </span>
                      </div>
                    </div>
                  </>
                )}
              </div>

              <div className="nx-inline-actions nx-stage-actions">
                {!isSelfConv && (
                  <ConversationNotificationButton key={activeConv.id} conversationId={activeConv.id} />
                )}
                <span ref={sharedBtnWrapRef}>
                  <Button
                    variant={rightDrawer === 'files' ? 'primary' : 'default'}
                    size="sm"
                    aria-label="Shared content"
                    title="Shared"
                    onClick={() => {
                      const next = rightDrawer === 'files' ? 'none' : 'files';
                      setRightDrawer(next);
                    }}
                  >
                    <FolderOpen size={15} aria-hidden="true" />
                    <span className="nx-stage-action-label">Shared</span>
                  </Button>
                </span>
                <Button size="sm" iconOnly aria-label="Pinned messages" title="Pinned messages"
                  onClick={() => setBookmarkPanel({ conversationId: activeConv.id })}>
                  <Pin size={15} aria-hidden="true" />
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
                ) : !isSelfConv ? (
                  <Button
                    variant={rightDrawer === 'members' ? 'primary' : 'default'}
                    size="sm"
                    aria-label={`Members (${activeConv.memberCount})`}
                    title={`Members (${activeConv.memberCount})`}
                    onClick={() => {
                      const next = rightDrawer === 'members' ? 'none' : 'members';
                      setRightDrawer(next);
                    }}
                  >
                    <Users size={15} aria-hidden="true" />
                    <span className="nx-stage-action-label">Members ({activeConv.memberCount})</span>
                  </Button>
                ) : null}
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
                  {isSelfConv ? <Bookmark size={28} aria-hidden="true" /> : <MessageSquare size={28} aria-hidden="true" />}
                  <p style={{ margin: '8px 0 0' }}>
                    {isSelfConv
                      ? 'Save private notes, links, and files here. Only visible to you.'
                      : 'No messages yet. Say hello!'}
                  </p>
                </div>
              )}

              {activeMessages.map((saved, idx) => {
                const pendingMutation = pendingMutations.get(saved.id);
                const m = pendingMutation ? applyMessageMutation(saved, pendingMutation) : saved;
                const prev = idx > 0 ? activeMessages[idx - 1] : null;
                const prevSeq = prev ? prev.seq : 0;
                const showDivider = lastReadSnapshot > 0 && prevSeq <= lastReadSnapshot && m.seq > lastReadSnapshot && !m.pending;
                const prevDate = prev ? formatDateLabel(prev.createdAt) : '';
                const curDate = formatDateLabel(m.createdAt);
                const showDate = curDate && curDate !== prevDate;
                const isOwn = m.senderId === currentUserId;
                const compact = Boolean(prev && !showDate && !showDivider && prev.senderId === m.senderId);
                const isInteractiveMessage = !m.pending && !m.deletedAt;
                const isPickerOpen = isInteractiveMessage && reactionPicker?.messageId === m.id;
                const isThreadOpen = openThreadTarget?.parentMessageId === m.id;
                const currentThreadState = threadStates[m.id];
                const threadMessage = currentThreadState ? applyThreadStateToMessage(m, currentThreadState) : m;
                const replyCount = (m.replyCount ?? 0) + (pendingThreadCounts.get(m.id) ?? 0);
                const hasUnreadReplies = threadMessage.isFollowing && (m.lastReplySeq ?? 0) > (threadMessage.lastReadReplySeq ?? 0);
                const optimisticMessage = Object.entries(pendingReactions[m.id] ?? {}).reduce(
                  (message, [emoji, reacted]) => applyReactionToMessage(message, { emoji, reacted, isMine: true }),
                  m
                );
                const reactions = isInteractiveMessage ? (optimisticMessage.reactions ?? []) : [];
                const reactedSet = new Set(reactions.filter(r => r.reactedByMe).map(r => r.emoji));

                return (
                  <div key={m.id} className="nx-msg-group-wrap">
                    {showDate && <div className="nx-date-sep">{curDate}</div>}
                    {showDivider && <div className="nx-new-divider" role="separator">New messages</div>}
                    <div className={`nx-msg-row ${isOwn ? 'own' : ''} ${compact ? 'compact' : ''} ${m.pending ? 'pending' : ''} ${isPickerOpen ? 'picker-open' : ''}`}>
                      {!isOwn && (
                        compact
                          ? <div className="nx-msg-avatar-spacer" />
                          : (
                            <button
                              type="button"
                              className="nx-profile-trigger nx-avatar-trigger"
                              aria-label={`View profile for ${m.senderDisplayName}`}
                              onClick={() =>
                                onOpenProfileCard({
                                  userId: m.senderId,
                                  organizationId: null,
                                  fallbackDisplayName: m.senderDisplayName
                                })
                              }
                            >
                              <Avatar
                                displayName={m.senderDisplayName}
                                size="sm"
                                presence={availabilityMap[m.senderId]
                                  ? resolveUserAvailability(m.senderId, {}, availabilityMap, nowMs).dotTone
                                  : undefined}
                              />
                            </button>
                          )
                      )}
                      <div className="nx-msg-main">
                        <div className="nx-msg-bubble-line">
                          <div
                            className={`nx-msg-bubble ${highlightedMessageId === m.id ? 'nx-highlight-target' : ''}`}
                            data-message-seq={m.seq}
                            data-message-id={m.id}
                            tabIndex={-1}
                          >
                            {!compact && (
                              <div className="nx-msg-head">
                                {isOwn ? (
                                  <strong>You</strong>
                                ) : (
                                  <button
                                    type="button"
                                    className="nx-profile-trigger nx-name-trigger"
                                    onClick={() =>
                                      onOpenProfileCard({
                                        userId: m.senderId,
                                        organizationId: null,
                                        fallbackDisplayName: m.senderDisplayName
                                      })
                                    }
                                  >
                                    <strong>{m.senderDisplayName}</strong>
                                  </button>
                                )}
                                <span className="nx-msg-time">
                                  {m.pending ? 'Sending…' : formatTime(m.createdAt)}
                                  {m.editedAt && !m.deletedAt && <> · <span>Edited</span></>}
                                </span>
                              </div>
                            )}
                            {compact && m.editedAt && !m.deletedAt && <span className="nx-msg-time">Edited</span>}
                            {m.deletedAt ? <span className="nx-muted">{RECALLED_MESSAGE_LABEL}</span> : m.body && (
                              <div
                                className="nx-msg-body"
                                onClick={handleInlineContentLinkClick}
                                dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(m.body) }}
                              />
                            )}
                            {!m.deletedAt && m.attachments && m.attachments.length > 0 && (
                              <AttachmentGallery attachments={m.attachments} onPreviewImage={setLightboxImage} />
                            )}
                          </div>
                          {isInteractiveMessage && (
                            <div className="nx-msg-hover-bar" role="group" aria-label="Message actions">
                              {QUICK_CHAT_REACTIONS.map(emoji => {
                                const reactedByMe = reactedSet.has(emoji);
                                return (
                                  <button
                                    key={emoji}
                                    type="button"
                                    className={`nx-hover-react-btn ${reactedByMe ? 'active' : ''}`}
                                    aria-label={`React with ${QUICK_REACTION_LABELS[emoji]} ${emoji}`}
                                    title={`React with ${QUICK_REACTION_LABELS[emoji]}`}
                                    aria-pressed={reactedByMe}
                                    onClick={() => void handleToggleMessageReaction(m, emoji)}
                                  >
                                    <span aria-hidden="true">{emoji}</span>
                                  </button>
                                );
                              })}
                              <span
                                className="nx-msg-react-add-wrap"
                                ref={el => {
                                  if (el) addReactionWrapRefs.current.set(m.id, el);
                                  else addReactionWrapRefs.current.delete(m.id);
                                }}
                              >
                                <Button
                                  variant={isPickerOpen ? 'primary' : 'ghost'}
                                  size="sm"
                                  iconOnly
                                  aria-label="Add reaction"
                                  title="Add reaction"
                                  aria-haspopup="dialog"
                                  aria-expanded={isPickerOpen}
                                  onClick={e => handleToggleReactionPicker(m.id, e.currentTarget)}
                                >
                                  <Plus size={13} aria-hidden="true" />
                                </Button>
                                {isPickerOpen && reactionPicker && (
                                  <div className="nx-msg-reaction-popover" style={reactionPicker.style}>
                                    <Suspense fallback={<div className="nx-emoji-loading" role="status">Loading emoji…</div>}>
                                      <LazyEmojiPicker
                                        onSelect={emoji => {
                                          setReactionPicker(null);
                                          void handleToggleMessageReaction(m, emoji);
                                          focusAddReactionButton(m.id);
                                        }}
                                        onClose={() => {
                                          setReactionPicker(null);
                                          focusAddReactionButton(m.id);
                                        }}
                                      />
                                    </Suspense>
                                  </div>
                                )}
                              </span>
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
                              <Button
                                variant="ghost"
                                size="sm"
                                iconOnly
                                aria-label="Reply in thread"
                                aria-expanded={isThreadOpen}
                                title="Reply in thread"
                                onClick={event => openThread(m, event.currentTarget)}
                              >
                                <MessageSquare size={13} aria-hidden="true" />
                              </Button>
                              <MessageMutationActions message={saved} currentUserId={currentUserId} blocked={blockedPeer}
                                pending={Boolean(pendingMutation)} onOpen={openMessageMutation} />
                              <MessageBookmarkActions message={saved} canPin={canPin} pending={pendingBookmarks.get(saved.id)}
                                onToggle={toggleBookmark} />
                              <Button variant="ghost" size="sm" iconOnly aria-label="Mark unread" title="Mark unread"
                                onClick={() => void markUnread(saved)}>
                                <Mail size={13} aria-hidden="true" />
                              </Button>
                            </div>
                          )}
                        </div>
                        {reactions.length > 0 && (
                          <div className="nx-reactions-row" role="group" aria-label="Message reactions">
                            {reactions.map(r => (
                              <button
                                key={r.emoji}
                                type="button"
                                className={`nx-react-pill ${r.reactedByMe ? 'active' : ''}`}
                                aria-pressed={r.reactedByMe}
                                aria-label={`${r.emoji} reaction, ${r.count} ${r.count === 1 ? 'person' : 'people'}${r.reactedByMe ? ', you reacted' : ''}`}
                                title={r.reactedByMe ? `Remove ${r.emoji} reaction` : `React with ${r.emoji}`}
                                onClick={() => void handleToggleMessageReaction(m, r.emoji)}
                              >
                                <span aria-hidden="true">{r.emoji}</span>
                                <span className="nx-react-count">{r.count}</span>
                              </button>
                            ))}
                          </div>
                        )}
                        {replyCount > 0 && (
                          <Button
                            variant="ghost"
                            size="sm"
                            className="nx-thread-summary"
                            aria-expanded={isThreadOpen}
                            onClick={event => openThread(m, event.currentTarget)}
                          >
                            <MessageSquare size={13} aria-hidden="true" />
                            {replyCount === 1 ? '1 reply' : `${replyCount} replies`}
                            {hasUnreadReplies && <span> · Unread</span>}
                          </Button>
                        )}
                        {m.id === seenMessageId && !m.deletedAt && (
                          <span className="nx-message-receipt nx-muted" role="status" aria-label="Seen by your chat partner">Seen</span>
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

              <ComposerToolbar
                key={activeConv.id}
                textareaRef={composerRef}
                value={composer}
                onChange={nextBody => changeDraft(activeConv.id, () => ({ body: nextBody }))}
                disabled={blockedPeer || loadingDraft}
                placement="top"
                openLinkSignal={linkTriggerSignal}
                ariaLabel="Message formatting"
              />

              {voiceRecorderOpen && !blockedPeer && (
                <Suspense fallback={<div className="nx-voice-recorder-loading" role="status">Loading recorder…</div>}>
                  <LazyVoiceNoteRecorder
                    disabled={loadingDraft || uploadingFiles}
                    onUpload={uploadVoiceNoteToConversation}
                    onClose={() => {
                      setVoiceRecorderOpen(false);
                      window.requestAnimationFrame(() => {
                        voiceBtnWrapRef.current?.querySelector('button')?.focus();
                      });
                    }}
                  />
                </Suspense>
              )}

              <div className="nx-composer-box">
                <Button
                  type="button"
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
                <span ref={voiceBtnWrapRef}>
                  <Button
                    type="button"
                    variant={voiceRecorderOpen ? 'primary' : 'ghost'}
                    size="sm"
                    iconOnly
                    aria-label="Record voice note"
                    title="Record voice note"
                    disabled={blockedPeer || loadingDraft || uploadingFiles}
                    onClick={() => setVoiceRecorderOpen(v => !v)}
                  >
                    <Mic size={16} aria-hidden="true" />
                  </Button>
                </span>
                <textarea
                  ref={composerRef}
                  rows={1}
                  aria-label="Message"
                  placeholder={
                    blockedPeer
                      ? undefined
                      : loadingDraft
                        ? 'Loading draft…'
                        : uploadingFiles
                          ? 'Uploading…'
                          : isSelfConv
                            ? 'Write a note to yourself…'
                            : 'Write a message…'
                  }
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

      {activeConv && openThreadTarget && (
        <Suspense fallback={<aside className="nx-drawer nx-thread-pane" aria-label="Thread"><div className="nx-drawer-body"><p role="status">Loading replies…</p></div></aside>}>
          <LazyThreadPane
            key={openThreadTarget.parentMessageId + ':' + (openThreadTarget.replyId ?? '')}
            target={openThreadTarget}
            currentUserId={currentUserId}
            visible={visible && !lightboxImage}
            blocked={blockedPeer}
            reconnectCount={reconnectCount}
            deltaRef={threadDeltaRef}
            liveState={threadStates[openThreadTarget.parentMessageId]}
            onState={updateThreadState}
            onPendingReply={updatePendingThreadReply}
            onConfirmReply={confirmThreadReply}
            onClose={closeThread}
            onCopyLink={message => handleCopyMessageLink(message, false)}
            onInlineLink={handleInlineContentLinkClick}
            onPreviewImage={setLightboxImage}
            mutationError={activeMutationError}
            pendingMutations={pendingMutations}
            onDismissMutationError={() => setMutationError(null)}
            onOpenMutation={openMessageMutation}
            canPin={canPin}
            pendingBookmarks={pendingBookmarks}
            onToggleBookmark={toggleBookmark}
          />
        </Suspense>
      )}

      {activeConv && activeConv.kind !== 'dm' && activeConv.kind !== 'self' && rightDrawer === 'members' && (
        <ChatMembersDrawer
          activeConv={activeConv}
          currentUserId={currentUserId}
          headingRef={groupMembersHeading}
          groupMembers={groupMembers}
          loadingMembers={loadingMembers}
          memberError={groupMemberPage.error}
          memberHasMore={groupMemberPage.next !== null}
          onLoadMoreMembers={groupMemberPage.loadMore}
          onRetryMembers={groupMemberPage.retry}
          groupMemberSearch={groupMemberSearch}
          setGroupMemberSearch={setGroupMemberSearch}
          addMemberId={addMemberId}
          setAddMemberId={setAddMemberId}
          addMemberCandidates={addMemberCandidates}
          availabilityMap={availabilityMap}
          nowMs={nowMs}
          onOpenProfileCard={onOpenProfileCard}
          onAddMember={() => void handleAddMember()}
          onRemoveMember={uid => void handleRemoveMember(uid)}
          onLeaveGroup={() => void handleLeaveGroup()}
          onSetOwnershipTransfer={setOwnershipTransfer}
          onClose={() => setRightDrawer('none')}
        />
      )}

      {activeConv && rightDrawer === 'files' && (
        <Suspense fallback={<aside className="nx-drawer" aria-label="Shared content"><div className="nx-drawer-body"><p className="nx-muted" role="status">Loading shared content…</p></div></aside>}>
          <LazySharedContentDrawer
            key={`${activeConv.id}:${sharedRefreshNonce}`}
            scope={{ kind: 'personal' }}
            conversationId={activeConv.id}
            originLabel="message"
            lightboxOpen={Boolean(lightboxImage)}
            onClose={() => {
              setRightDrawer('none');
              requestAnimationFrame(() => {
                sharedBtnWrapRef.current?.querySelector('button')?.focus();
              });
            }}
            onPreviewImage={setLightboxImage}
            onOpenContentLink={onOpenContentLink}
            hiddenMessageIds={hiddenSharedMessageIds}
            linkRevision={sharedLinkRevision}
          />
        </Suspense>
      )}

      {(showNewGroupModal || blockTarget || showReportModal || ownershipTransfer || sectionModal) && (
        <Suspense fallback={null}>
          <LazyChatModals
            activeConv={activeConv}
            showNewGroupModal={showNewGroupModal}
            setShowNewGroupModal={setShowNewGroupModal}
            newGroupTitle={newGroupTitle}
            setNewGroupTitle={setNewGroupTitle}
            newGroupSearch={newGroupSearch}
            setNewGroupSearch={setNewGroupSearch}
            selectedGroupMemberIds={selectedGroupMemberIds}
            setSelectedGroupMemberIds={setSelectedGroupMemberIds}
            groupCandidates={groupCandidates}
            onCreateGroup={handleCreateGroup}
            blockTarget={blockTarget}
            setBlockTarget={setBlockTarget}
            blockingPeer={blockingPeer}
            blockError={blockError}
            onBlockPeer={() => void handleBlockPeer()}
            showReportModal={showReportModal}
            setShowReportModal={setShowReportModal}
            reportReason={reportReason}
            setReportReason={setReportReason}
            reportDetails={reportDetails}
            setReportDetails={setReportDetails}
            onSubmitReport={handleSubmitReport}
            ownershipTransfer={ownershipTransfer}
            onCloseOwnershipTransfer={() => setOwnershipTransfer(null)}
            onRefreshOwnership={() => {
              if (activeConv) {
                void loadConversations();
                void loadGroupMembers(activeConv.id);
              }
            }}
            onTransferredOwnership={() => {
              if (activeConv && ownershipTransfer) {
                const transferredName = ownershipTransfer.displayName;
                const resourceName = ownershipTransfer.resourceName;
                setOwnershipTransfer(null);
                void loadConversations();
                void loadGroupMembers(activeConv.id);
                setNotice(`${transferredName} is now Owner of ${resourceName}.`);
                requestAnimationFrame(() => groupMembersHeading.current?.focus());
              }
            }}
            sectionModal={sectionModal}
            setSectionModal={setSectionModal}
            sidebarBusy={sidebarOrg.busy}
            onSubmitSection={async name => {
              if (sectionModal?.mode === 'rename') {
                const ok = await sidebarOrg.renameSection(sectionModal.section.id, name);
                if (ok) setSectionModal(null);
              } else {
                const created = await sidebarOrg.createSection(name);
                if (created) setSectionModal(null);
              }
            }}
          />
        </Suspense>
      )}

      {bookmarkPanel && (
        <Suspense fallback={null}>
          <LazyBookmarksDialog conversationId={bookmarkPanel.conversationId} revision={bookmarkRevision}
            hiddenIds={hiddenBookmarks} hiddenConversations={discarded.current} onClose={() => setBookmarkPanel(null)}
            onOpenMessage={token => onOpenContentLink?.(token)} />
        </Suspense>
      )}
      {mutationTarget && (
        <Suspense fallback={null}>
          <LazyMessageMutationDialog
            key={mutationTarget.message.id + ':' + mutationTarget.kind}
            target={mutationTarget}
            onClose={closeMessageMutation}
            onBegin={beginMessageMutation}
            onResult={result => completeMessageMutation(mutationTarget.message, result)}
          />
        </Suspense>
      )}
      <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />
    </div>
  );
}
