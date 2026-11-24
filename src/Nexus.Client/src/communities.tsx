import { orgApiPath, orgFetch, protectedRequest } from './scope';
import { ChangeEvent, ClipboardEvent, DragEvent, FormEvent, MouseEvent, RefObject, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Archive,
  ArrowLeft,
  BarChart2,
  Bell,
  Check,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Compass,
  Download,
  ExternalLink,
  Eye,
  FileText,
  FolderOpen,
  FolderPlus,
  Hash,
  Link2,
  Lock,
  Megaphone,
  MessageSquare,
  Paperclip,
  Pencil,
  Pin,
  Plus,
  RefreshCw,
  RotateCcw,
  Search,
  Send,
  Settings,
  ShieldCheck,
  Star,
  Trash2,
  UserPlus,
  Users,
  X
} from 'lucide-react';
import { AttachmentItem, CommunityFeedDelta, StorageQuotaSummary } from './chat';
import { CommunityJumpTarget, copyContentLink, extractContentLinkToken } from './contentLinks';
import { moveIdByOffset, reorderIdList, SidebarSectionDto, useSidebarOrganization } from './sidebarPrefs';
import { SidebarItemActions, SidebarSectionHeader, SidebarSectionModal } from './sidebarControls';
import { OrganizationItem } from './organizationControls';
import { renderSafeMarkdown } from './markdown';
import { ComposerToolbar, handleComposerFormattingKeyDown } from './ComposerToolbar';
import { AvailabilityDto, LiveAvailabilityEntry, resolveUserAvailability } from './availability';
import { ProfileCardTarget } from './ProfileCardModal';
import { ConversationNotificationButton } from './notificationPreferences';
import { OwnershipTransfer, TransferOwnershipDialog } from './transferOwnership';
import { CursorPage, ListPaging, readCursorPage, useCursorList } from './cursorList';
import { loadOrganizationDepartments, personKey, searchPeopleScope } from './peopleDirectory';
import { Avatar, Badge, Button, EmptyState, Field, Modal, ToastBar } from './ui';

export interface CommunityChannelDto {
  id: string;
  name: string;
  description?: string | null;
  postingPolicy: string;
  isArchived?: boolean;
  archivedAt?: string | null;
  createdAt: string;
}

export interface CommunityItemDto {
  id: string;
  organizationId: string;
  organizationName: string;
  name: string;
  description?: string | null;
  visibility: string;
  isOfficial: boolean;
  mappedDepartmentId?: string | null;
  memberCount: number;
  isMember: boolean;
  myRole?: string | null;
  mainFeedConversationId?: string | null;
  mainFeedPostingPolicy: string;
  channels: CommunityChannelDto[];
  latestPostPreview?: {
    bodyPreview: string;
    authorDisplayName: string;
    createdAt: string;
  } | null;
  createdAt: string;
}

const communityKey = (community: CommunityItemDto) => community.id;
const communityMemberKey = (member: CommunityMemberDto) => member.userId;

async function readCommunityPage(org: OrganizationItem, params: URLSearchParams, signal?: AbortSignal) {
  const response = await orgFetch(org.id, `/api/communities?${params}`, { signal });
  const page = await readCursorPage<Omit<CommunityItemDto, 'organizationId' | 'organizationName'>>(response, 'Unable to load communities.');
  return {
    items: page.items.map(item => ({ ...item, organizationId: org.id, organizationName: org.name })),
    next: page.next
  };
}

async function readCommunityPages(organizations: OrganizationItem[], discover: boolean, query: string,
  after: Record<string, string> | null, signal: AbortSignal): Promise<CursorPage<CommunityItemDto, Record<string, string>>> {
  const pages = await Promise.all(organizations.filter(org => !after || after[org.id]).map(async org => {
    const params = new URLSearchParams({ limit: '50' });
    if (discover) params.set('discover', 'true');
    if (query) params.set('q', query);
    if (after?.[org.id]) params.set('cursor', after[org.id]);
    return { org, ...await readCommunityPage(org, params, signal) };
  }));
  const next = Object.fromEntries(pages.filter(page => page.next).map(page => [page.org.id, page.next as string]));
  return { items: pages.flatMap(page => page.items), next: Object.keys(next).length ? next : null };
}

interface PostCommentDto {
  id: string;
  postId: string;
  authorUserId: string;
  authorDisplayName: string;
  body: string;
  createdAt: string;
  pending?: boolean;
}

interface PostReactionDto {
  emoji: string;
  count: number;
  reactedByMe: boolean;
}

interface ReactionChange {
  postId: string;
  emoji: string;
  reactedByMe: boolean;
}

interface PollOptionDto {
  id: string;
  label: string;
  votes: number;
  votedByMe: boolean;
}

interface PostPollDto {
  id: string;
  question: string;
  allowMultiple: boolean;
  closesAt?: string | null;
  totalVoters: number;
  options: PollOptionDto[];
}

interface ChannelPostDto {
  id: string;
  conversationId: string;
  authorUserId: string;
  authorDisplayName: string;
  authorAvatarUrl?: string | null;
  title?: string | null;
  body: string;
  isAnnouncement: boolean;
  requiresAcknowledgment: boolean;
  acknowledgedCount: number;
  totalRecipients: number;
  acknowledgedByMe: boolean;
  isPinned: boolean;
  createdAt: string;
  comments: PostCommentDto[];
  reactions: PostReactionDto[];
  attachments?: AttachmentItem[];
  poll?: PostPollDto | null;
  pending?: boolean;
}

interface ChannelFileItem extends AttachmentItem {
  postId?: string | null;
  postTitle?: string | null;
  uploaderDisplayName?: string | null;
  sourceLink?: string | null;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

interface CommunityMemberDto {
  userId: string;
  username: string;
  displayName: string;
  avatarUrl?: string | null;
  isUnverified: boolean;
  isActive?: boolean;
  isOnline?: boolean;
  availability?: AvailabilityDto | null;
  role: string;
  joinedAt: string;
}

export interface DirectoryUserLite {
  id: string;
  username: string;
  displayName: string;
  departmentName?: string | null;
  avatarUrl?: string | null;
}

export interface DepartmentLite {
  id: string;
  code: string;
  name: string;
}

interface CommunitiesWorkspaceProps {
  currentUserId: string;
  currentDisplayName: string;
  organizations: OrganizationItem[];
  requestedChannel: { id: string } | null;
  jumpTarget?: CommunityJumpTarget | null;
  feedListenerRef: RefObject<((conversationId?: string, delta?: CommunityFeedDelta | null) => void) | null>;
  onOpenContentLink?: (token: string) => void;
  onOpenCreateWorkspace: () => void;
  onOpenJoinWorkspace: () => void;
  onOpenWorkspaceSettings: (org: OrganizationItem) => void;
  availabilityMap: Record<string, LiveAvailabilityEntry>;
  nowMs: number;
  onOpenProfileCard: (target: ProfileCardTarget) => void;
}

const QUICK_REACTIONS = ['👍', '❤️', '🎉', '👀'];

function applyReaction(post: ChannelPostDto, change: ReactionChange): ChannelPostDto {
  const existing = post.reactions.find(reaction => reaction.emoji === change.emoji);
  if (Boolean(existing?.reactedByMe) === change.reactedByMe) return post;

  const reactions = post.reactions.filter(reaction => reaction.emoji !== change.emoji);
  const count = Math.max(0, (existing?.count ?? 0) + (change.reactedByMe ? 1 : -1));
  if (count > 0) {
    reactions.push({ emoji: change.emoji, count, reactedByMe: change.reactedByMe });
  }
  return { ...post, reactions };
}

function formatPostTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString([], {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit'
    });
  } catch {
    return iso;
  }
}

export function CommunitiesWorkspace({
  currentUserId,
  currentDisplayName,
  organizations,
  requestedChannel,
  jumpTarget,
  feedListenerRef,
  onOpenContentLink,
  onOpenCreateWorkspace,
  onOpenJoinWorkspace,
  onOpenWorkspaceSettings,
  availabilityMap,
  nowMs,
  onOpenProfileCard
}: CommunitiesWorkspaceProps) {
  const [discoverMode, setDiscoverMode] = useState(false);
  const [sidebarFilter, setSidebarFilter] = useState('');
  const normalizedFilter = sidebarFilter.trim().toLowerCase();
  const workspaceKey = JSON.stringify([currentUserId, organizations.map(org => org.id)]);
  const memberSearch = discoverMode ? '' : normalizedFilter;
  const communitiesPage = useCursorList<CommunityItemDto, Record<string, string>>(
    organizations.length ? JSON.stringify([workspaceKey, memberSearch]) : null,
    (cursor, signal) => readCommunityPages(organizations, false, memberSearch, cursor, signal),
    communityKey, memberSearch ? 180 : 0);
  const discoverPage = useCursorList<CommunityItemDto, Record<string, string>>(
    discoverMode && organizations.length ? JSON.stringify([workspaceKey, normalizedFilter]) : null,
    (cursor, signal) => readCommunityPages(organizations, true, normalizedFilter, cursor, signal),
    communityKey, normalizedFilter ? 180 : 0);
  const communities = communitiesPage.items;
  const discoverCommunities = discoverPage.items;
  const [focusedCommunity, setFocusedCommunity] = useState<CommunityItemDto | null>(null);
  const [collapsedWorkspaces, setCollapsedWorkspaces] = useState<Record<string, boolean>>({});
  const [orgDepartmentsByOrg, setOrgDepartmentsByOrg] = useState<Record<string, DepartmentLite[]>>({});
  const [createOrgId, setCreateOrgId] = useState<string>('');

  const [selectedCommunityId, setSelectedCommunityId] = useState<string | null>(null);
  const activeCommunityRef = useRef(selectedCommunityId);
  activeCommunityRef.current = selectedCommunityId;
  const [selectedConversationId, setSelectedConversationId] = useState<string | null>(null);
  const openedChannel = useRef<{ id: string } | null>(null);
  const activeFeedRef = useRef(selectedConversationId);
  activeFeedRef.current = selectedConversationId;
  const orgById = useMemo(() => new Map(organizations.map(o => [o.id, o])), [organizations]);
  const communityOrgByConvRef = useRef(new Map<string, string>());

  const [posts, setPosts] = useState<ChannelPostDto[]>([]);
  const [postsNextCursor, setPostsNextCursor] = useState<string | null>(null);
  const [postsHasNewer, setPostsHasNewer] = useState(false);
  const [highlightedPostId, setHighlightedPostId] = useState<string | null>(null);
  const [highlightedHeader, setHighlightedHeader] = useState(false);
  const feedContainerRef = useRef<HTMLDivElement | null>(null);
  const stageHeaderRef = useRef<HTMLElement | null>(null);
  const handledJumpNonceRef = useRef(0);
  const pendingJumpRef = useRef<CommunityJumpTarget | null>(null);
  const [loadingMorePosts, setLoadingMorePosts] = useState(false);
  const [postsError, setPostsError] = useState<{ message: string; append: boolean } | null>(null);
  const inFlightPostIdsRef = useRef(new Set<string>());
  const postsCacheRef = useRef(new Map<string, {
    items: ChannelPostDto[];
    nextCursor: string | null;
    pages: number;
    canPost: boolean;
    canManage: boolean;
    isArchived: boolean;
    archivedAt: string | null;
    channelDescription: string | null;
  }>());
  const initialCommunitySelectedRef = useRef(false);
  const [pendingReactions, setPendingReactions] = useState<Record<string, ReactionChange>>({});
  const reactionRequests = useRef(new Set<string>());
  const feedRequestRef = useRef<{
    conversationId: string;
    controller: AbortController;
    refreshQueued: boolean;
    confirmedReactions: ReactionChange[];
  } | null>(null);
  const [canPost, setCanPost] = useState(false);
  const [canManage, setCanManage] = useState(false);
  const [feedIsArchived, setFeedIsArchived] = useState(false);
  const [feedChannelDescription, setFeedChannelDescription] = useState<string | null>(null);
  const [loadingPosts, setLoadingPosts] = useState(false);

  const [rightDrawer, setRightDrawer] = useState<'none' | 'members' | 'files'>('none');
  const membersOpen = rightDrawer === 'members';
  const filesOpen = rightDrawer === 'files';
  const rightDrawerRef = useRef(rightDrawer);
  rightDrawerRef.current = rightDrawer;
  const setMembersOpen = useCallback((next: boolean | ((prev: boolean) => boolean)) => {
    setRightDrawer(prev => {
      const nextBool = typeof next === 'function' ? next(prev === 'members') : next;
      return nextBool ? 'members' : (prev === 'members' ? 'none' : prev);
    });
  }, []);
  const [ownershipTransfer, setOwnershipTransfer] = useState<OwnershipTransfer | null>(null);
  const communityMembersHeading = useRef<HTMLHeadingElement | null>(null);
  const channelFilesHeadingRef = useRef<HTMLHeadingElement | null>(null);
  const filesBtnWrapRef = useRef<HTMLSpanElement | null>(null);
  const [addMemberUserId, setAddMemberUserId] = useState('');
  const [memberSearchQuery, setMemberSearchQuery] = useState('');
  const [mappedDeptId, setMappedDeptId] = useState('');

  // M6.31 Channel Files drawer & Feed post attachments state
  const [channelFiles, setChannelFiles] = useState<ChannelFileItem[]>([]);
  const [loadingChannelFiles, setLoadingChannelFiles] = useState(false);
  const [channelFilesError, setChannelFilesError] = useState<'refresh' | 'older' | null>(null);
  const [channelFilesNext, setChannelFilesNext] = useState<{ before: string; beforeId: string } | null>(null);
  const [channelFilesQuota, setChannelFilesQuota] = useState<StorageQuotaSummary | null>(null);
  const channelFilesReqRef = useRef<AbortController | null>(null);
  const [pendingAttachments, setPendingAttachments] = useState<AttachmentItem[]>([]);
  const [uploadingFiles, setUploadingFiles] = useState(false);
  const [isComposerDragOver, setIsComposerDragOver] = useState(false);
  const [quotaWarning, setQuotaWarning] = useState('');
  const [lightboxImage, setLightboxImage] = useState<AttachmentItem | null>(null);
  const postFileInputRef = useRef<HTMLInputElement | null>(null);
  const uploadControllerRef = useRef<AbortController | null>(null);

  // Composer state
  const [postTitle, setPostTitle] = useState('');
  const [postBody, setPostBody] = useState('');
  const postBodyRef = useRef<HTMLTextAreaElement | null>(null);
  const postCompositionActive = useRef(false);
  const [postLinkSignal, setPostLinkSignal] = useState(0);
  const [isAnnouncement, setIsAnnouncement] = useState(false);
  const [requiresAck, setRequiresAck] = useState(false);
  const [includePoll, setIncludePoll] = useState(false);
  const [pollQuestion, setPollQuestion] = useState('');
  const [pollOptions, setPollOptions] = useState<string[]>(['', '']);
  const [pollAllowMultiple, setPollAllowMultiple] = useState(false);
  const [posting, setPosting] = useState(false);

  // Expanded comments & per-post comment drafts
  const [expandedComments, setExpandedComments] = useState<Record<string, boolean>>({});
  const [commentDrafts, setCommentDrafts] = useState<Record<string, string>>({});

  // Modals
  const [createOpen, setCreateOpen] = useState(false);
  const [newCommName, setNewCommName] = useState('');
  const [newCommDesc, setNewCommDesc] = useState('');
  const [newCommVisibility, setNewCommVisibility] = useState<'Public' | 'Private'>('Public');

  const [addChannelForId, setAddChannelForId] = useState<string | null>(null);
  const [newChannelName, setNewChannelName] = useState('');
  const [newChannelDesc, setNewChannelDesc] = useState('');
  const [newChannelPolicy, setNewChannelPolicy] = useState<'Members' | 'AdminsOnly'>('Members');

  // M6.30 Community/Channel metadata editing & channel archive/restore modals
  const [editCommOpen, setEditCommOpen] = useState(false);
  const [editCommName, setEditCommName] = useState('');
  const [editCommDesc, setEditCommDesc] = useState('');
  const [editChannelOpen, setEditChannelOpen] = useState(false);
  const [editChannelName, setEditChannelName] = useState('');
  const [editChannelDesc, setEditChannelDesc] = useState('');
  const [archiveConfirmOpen, setArchiveConfirmOpen] = useState(false);
  const [savingMeta, setSavingMeta] = useState(false);
  const savingMetaRef = useRef(false);

  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  const selectedCommunity = useMemo(
    () => communities.find(c => c.id === selectedCommunityId) ??
      (focusedCommunity?.id === selectedCommunityId && orgById.has(focusedCommunity.organizationId) ? focusedCommunity : null),
    [communities, focusedCommunity, orgById, selectedCommunityId]
  );
  const selectedSummaryRef = useRef(selectedCommunity);
  selectedSummaryRef.current = selectedCommunity;

  const activeOrgId = selectedCommunity?.organizationId ?? null;
  const activeOrg = activeOrgId ? orgById.get(activeOrgId) ?? null : null;
  const departments = activeOrgId ? (orgDepartmentsByOrg[activeOrgId] ?? []) : [];
  const memberList = useCursorList(membersOpen && activeOrgId && selectedCommunityId
    ? JSON.stringify([activeOrgId, selectedCommunityId]) : null,
    async (cursor, signal) => {
      const params = new URLSearchParams({ limit: '50' });
      if (cursor) params.set('cursor', cursor);
      const response = await orgFetch(activeOrgId!, `/api/communities/${selectedCommunityId}/members?${params}`, { signal });
      return readCursorPage<CommunityMemberDto>(response, 'Unable to load community members.');
    }, communityMemberKey);
  const { items: members, loading: loadingMembers, refresh: refreshMemberList } = memberList;
  const addCandidates = useCursorList(membersOpen && activeOrgId && selectedCommunityId && canManage
    ? JSON.stringify([activeOrgId, selectedCommunityId, memberSearchQuery]) : null,
    (cursor, signal) => searchPeopleScope<DirectoryUserLite>(activeOrgId!, memberSearchQuery, '', cursor, signal, selectedCommunityId ?? undefined),
    personKey, memberSearchQuery.trim() ? 180 : 0);
  const { items: availableToAdd, refresh: refreshAddCandidates } = addCandidates;

  const selectedSubChannel = useMemo(() => {
    if (!selectedCommunity || !selectedConversationId) return null;
    if (selectedConversationId === selectedCommunity.mainFeedConversationId) return null;
    return selectedCommunity.channels.find(ch => ch.id === selectedConversationId) ?? null;
  }, [selectedCommunity, selectedConversationId]);

  useEffect(() => {
    const convMap = new Map<string, string>();
    const accessible = selectedCommunity ? [...communities, selectedCommunity] : communities;
    for (const comm of accessible) {
      if (comm.mainFeedConversationId) convMap.set(comm.mainFeedConversationId, comm.organizationId);
      for (const ch of comm.channels) convMap.set(ch.id, comm.organizationId);
    }
    communityOrgByConvRef.current = convMap;
  }, [communities, selectedCommunity]);

  const lookupCommunity = useCallback(async (orgId: string, id: string, signal?: AbortSignal) => {
    const org = orgById.get(orgId);
    if (!org) return null;
    const page = await readCommunityPage(org, new URLSearchParams({ limit: '1', communityId: id }), signal);
    return page.items[0] ?? null;
  }, [orgById]);

  const { refresh: refreshCommunityPage, updateItems: updateCommunityItems } = communitiesPage;
  const { refresh: loadDiscover } = discoverPage;

  const patchCommunityInState = useCallback(
    (communityId: string, updater: (comm: CommunityItemDto) => CommunityItemDto) => {
      updateCommunityItems(prev => prev.map(c => (c.id === communityId ? updater(c) : c)));
      setFocusedCommunity(prev => (prev && prev.id === communityId ? updater(prev) : prev));
    },
    [updateCommunityItems]
  );
  const loadCommunities = useCallback(async () => {
    const visible = await refreshCommunityPage();
    const selected = selectedSummaryRef.current;
    if (!selected || activeCommunityRef.current !== selected.id) return;
    const refreshed = visible?.find(item => item.id === selected.id) ??
      await lookupCommunity(selected.organizationId, selected.id).catch(() => undefined);
    if (refreshed === undefined || activeCommunityRef.current !== selected.id) return;
    setFocusedCommunity(refreshed);
    if (!refreshed) {
      setSelectedCommunityId(null);
      setSelectedConversationId(null);
      setMembersOpen(false);
    }
  }, [lookupCommunity, refreshCommunityPage]);

  const loadPosts = useCallback(async function loadPosts(
    conversationId: string,
    background = false,
    explicitOrgId?: string,
    append = false
  ) {
    if (activeFeedRef.current !== conversationId) return;
    const orgId = explicitOrgId ?? communityOrgByConvRef.current.get(conversationId);
    if (!orgId) return;

    const cached = postsCacheRef.current.get(conversationId);
    if (append && (!cached?.nextCursor || feedRequestRef.current?.conversationId === conversationId)) return;

    const previous = feedRequestRef.current;
    if (background && !append && previous?.conversationId === conversationId) {
      previous.refreshQueued = true;
      return;
    }

    previous?.controller.abort();
    const request = { conversationId, controller: new AbortController(), refreshQueued: false, confirmedReactions: [] as ReactionChange[] };
    feedRequestRef.current = request;
    setPostsError(null);
    if (append) setLoadingMorePosts(true);
    else if (!background) setLoadingPosts(true);

    try {
      const merged = new Map<string, ChannelPostDto>(
        (append ? cached?.items ?? [] : []).map(post => [post.id, post])
      );
      const pagesToRead = append ? 1 : Math.max(1, cached?.pages ?? 1);
      let cursor: string | null = append ? cached?.nextCursor ?? null : null;
      let pagesRead = 0;
      let nextCanPost = cached?.canPost ?? false;
      let nextCanManage = cached?.canManage ?? false;
      let nextIsArchived = cached?.isArchived ?? false;
      let nextArchivedAt: string | null = cached?.archivedAt ?? null;
      let nextChannelDescription: string | null = cached?.channelDescription ?? null;

      do {
        const params = new URLSearchParams({ limit: '50' });
        if (cursor) params.set('cursor', cursor);
        const res = await orgFetch(orgId, `/api/channels/${conversationId}/posts?${params}`, {
          credentials: 'same-origin',
          signal: request.controller.signal
        });
        const data = await res.json().catch(() => ({}));
        if (request.controller.signal.aborted || activeFeedRef.current !== conversationId) return;

        if (!res.ok) {
          const message = data.message || 'Unable to load posts.';
          const lostAccess = res.status === 401 || res.status === 403 || res.status === 404;
          if (lostAccess) {
            postsCacheRef.current.delete(conversationId);
            setPosts([]);
            setPostsNextCursor(null);
            setCanPost(false);
            setCanManage(false);
            setFeedIsArchived(false);
            setFeedChannelDescription(null);
          }
          setPostsError({ message, append: !lostAccess && append });
          setError(message);
          return;
        }

        const items: ChannelPostDto[] = data.items ?? [];
        for (const post of items) {
          const adjusted = request.confirmedReactions.reduce(
            (current, change) => change.postId === current.id ? applyReaction(current, change) : current,
            post
          );
          merged.set(adjusted.id, adjusted);
        }
        nextCanPost = Boolean(data.canPost);
        nextCanManage = Boolean(data.canManage);
        nextIsArchived = Boolean(data.isArchived);
        nextArchivedAt = typeof data.archivedAt === 'string' ? data.archivedAt : null;
        nextChannelDescription = typeof data.channelDescription === 'string' ? data.channelDescription : null;
        cursor = typeof data.nextCursor === 'string' && data.nextCursor.length > 0 ? data.nextCursor : null;
        pagesRead += 1;
      } while (cursor !== null && pagesRead < pagesToRead);

      if (request.controller.signal.aborted || activeFeedRef.current !== conversationId) return;

      let nextPosts = [...merged.values()];
      if (!append) {
        const stillInFlight = (postsCacheRef.current.get(conversationId)?.items ?? [])
          .filter(post => post.pending && inFlightPostIdsRef.current.has(post.id));
        if (stillInFlight.length > 0) {
          const pinned = nextPosts.filter(p => p.isPinned);
          const regular = nextPosts.filter(p => !p.isPinned);
          nextPosts = [...pinned, ...stillInFlight, ...regular];
        }
      }
      const totalPages = append ? (cached?.pages ?? 0) + pagesRead : pagesRead;
      postsCacheRef.current.set(conversationId, {
        items: nextPosts,
        nextCursor: cursor,
        pages: totalPages,
        canPost: nextCanPost,
        canManage: nextCanManage,
        isArchived: nextIsArchived,
        archivedAt: nextArchivedAt,
        channelDescription: nextChannelDescription
      });
      setPosts(nextPosts);
      setPostsNextCursor(cursor);
      if (!append) setPostsHasNewer(false);
      setCanPost(nextCanPost);
      setCanManage(nextCanManage);
      setFeedIsArchived(nextIsArchived);
      setFeedChannelDescription(nextChannelDescription);
    } catch {
      if (!request.controller.signal.aborted && activeFeedRef.current === conversationId) {
        setPostsError({ message: 'Unable to load posts.', append });
        setError('Unable to load posts.');
      }
    } finally {
      if (feedRequestRef.current === request) {
        feedRequestRef.current = null;
        setLoadingPosts(false);
        setLoadingMorePosts(false);
        if (request.refreshQueued) void loadPosts(conversationId, true, orgId);
      }
    }
  }, []);

  const loadPostsAround = useCallback(async (orgId: string, conversationId: string, postId: string): Promise<boolean> => {
    feedRequestRef.current?.controller.abort();
    const request = { conversationId, controller: new AbortController(), refreshQueued: false, confirmedReactions: [] as ReactionChange[] };
    feedRequestRef.current = request;
    setPostsError(null);
    setLoadingPosts(true);
    try {
      const res = await orgFetch(orgId, `/api/channels/${conversationId}/posts?aroundPostId=${encodeURIComponent(postId)}&limit=50`, {
        credentials: 'same-origin',
        signal: request.controller.signal
      });
      const data = await res.json().catch(() => ({}));
      if (request.controller.signal.aborted || activeFeedRef.current !== conversationId) return false;
      if (!res.ok) {
        const message = data.message || 'Unable to load the linked post.';
        setPostsError({ message, append: false });
        setError(message);
        return false;
      }
      const items: ChannelPostDto[] = data.items ?? [];
      const cursor = typeof data.nextCursor === 'string' && data.nextCursor.length > 0 ? data.nextCursor : null;
      const hasNewer = Boolean(data.hasNewer);
      const nextCanPost = Boolean(data.canPost);
      const nextCanManage = Boolean(data.canManage);
      const nextIsArchived = Boolean(data.isArchived);
      const nextArchivedAt = typeof data.archivedAt === 'string' ? data.archivedAt : null;
      const nextChannelDescription = typeof data.channelDescription === 'string' ? data.channelDescription : null;
      if (!hasNewer) {
        postsCacheRef.current.set(conversationId, {
          items,
          nextCursor: cursor,
          pages: 1,
          canPost: nextCanPost,
          canManage: nextCanManage,
          isArchived: nextIsArchived,
          archivedAt: nextArchivedAt,
          channelDescription: nextChannelDescription
        });
      }
      setPosts(items);
      setPostsNextCursor(cursor);
      setPostsHasNewer(hasNewer);
      setCanPost(nextCanPost);
      setCanManage(nextCanManage);
      setFeedIsArchived(nextIsArchived);
      setFeedChannelDescription(nextChannelDescription);
      return true;
    } catch {
      if (!request.controller.signal.aborted && activeFeedRef.current === conversationId) {
        setPostsError({ message: 'Unable to load the linked post.', append: false });
        setError('Unable to load the linked post.');
      }
      return false;
    } finally {
      if (feedRequestRef.current === request) {
        feedRequestRef.current = null;
        setLoadingPosts(false);
      }
    }
  }, []);

  const loadMembers = useCallback(async (communityId: string) => {
    if (activeCommunityRef.current === communityId) {
      await Promise.all([refreshMemberList(), refreshAddCandidates()]);
    }
  }, [refreshAddCandidates, refreshMemberList]);

  const loadChannelFiles = useCallback(async (orgId: string, conversationId: string, older = false) => {
    if (older && !channelFilesNext) return;
    channelFilesReqRef.current?.abort();
    const controller = new AbortController();
    channelFilesReqRef.current = controller;
    setLoadingChannelFiles(true);
    setChannelFilesError(null);
    try {
      const params = new URLSearchParams({ limit: '50' });
      if (older && channelFilesNext) {
        params.set('before', channelFilesNext.before);
        params.set('beforeId', channelFilesNext.beforeId);
      }
      const res = await orgFetch(orgId, `/api/channels/${conversationId}/files?${params}`, {
        credentials: 'same-origin',
        signal: controller.signal
      });
      if (controller.signal.aborted || activeFeedRef.current !== conversationId) return;
      if ([401, 403, 404].includes(res.status)) {
        setChannelFiles([]);
        setChannelFilesNext(null);
        setChannelFilesQuota(null);
        setChannelFilesError(older ? 'older' : 'refresh');
        return;
      }
      if (!res.ok) throw new Error('Unable to load channel files.');
      const data = await res.json();
      if (controller.signal.aborted || activeFeedRef.current !== conversationId) return;
      const incoming: ChannelFileItem[] = data.items ?? [];
      setChannelFiles(prev => {
        const merged = new Map(older ? prev.map(item => [item.id, item]) : []);
        for (const item of incoming) merged.set(item.id, item);
        return [...merged.values()];
      });
      setChannelFilesNext(
        data.hasMore && data.nextBefore && data.nextBeforeId
          ? { before: data.nextBefore, beforeId: data.nextBeforeId }
          : null
      );
      setChannelFilesQuota(data.quota ?? null);
    } catch {
      if (!controller.signal.aborted && activeFeedRef.current === conversationId) {
        setChannelFilesError(older ? 'older' : 'refresh');
      }
    } finally {
      if (channelFilesReqRef.current === controller) {
        channelFilesReqRef.current = null;
        if (!controller.signal.aborted && activeFeedRef.current === conversationId) {
          setLoadingChannelFiles(false);
        }
      }
    }
  }, [channelFilesNext]);

  const loadChannelFilesRef = useRef(loadChannelFiles);
  loadChannelFilesRef.current = loadChannelFiles;

  useEffect(() => {
    uploadControllerRef.current?.abort();
    uploadControllerRef.current = null;
    channelFilesReqRef.current?.abort();
    channelFilesReqRef.current = null;
    setUploadingFiles(false);
    setPendingAttachments([]);
    setQuotaWarning('');
    setIsComposerDragOver(false);
    setChannelFiles([]);
    setChannelFilesNext(null);
    setChannelFilesError(null);
    setChannelFilesQuota(null);
    if (rightDrawerRef.current === 'files' && selectedConversationId && activeOrgId) {
      void loadChannelFilesRef.current(activeOrgId, selectedConversationId, false);
    }
  }, [activeOrgId, selectedConversationId]);

  useEffect(() => {
    if (!filesOpen) return;
    requestAnimationFrame(() => channelFilesHeadingRef.current?.focus());
    const handleEsc = (ev: globalThis.KeyboardEvent) => {
      if (ev.key === 'Escape' && !lightboxImage) {
        ev.preventDefault();
        setRightDrawer('none');
        requestAnimationFrame(() => {
          filesBtnWrapRef.current?.querySelector('button')?.focus();
        });
      }
    };
    document.addEventListener('keydown', handleEsc);
    return () => document.removeEventListener('keydown', handleEsc);
  }, [filesOpen, lightboxImage]);

  useEffect(() => {
    if (!membersOpen || !activeOrgId || orgDepartmentsByOrg[activeOrgId]) return;
    const controller = new AbortController();
    void loadOrganizationDepartments<DepartmentLite>(activeOrgId, controller.signal)
      .then(items => {
        if (!controller.signal.aborted) setOrgDepartmentsByOrg(previous => ({ ...previous, [activeOrgId]: items }));
      })
      .catch(() => {});
    return () => controller.abort();
  }, [membersOpen, activeOrgId, orgDepartmentsByOrg]);

  useEffect(() => {
    if (discoverMode || memberSearch || communitiesPage.loading || initialCommunitySelectedRef.current || selectedCommunityId) return;
    const first = communities[0];
    if (!first) return;
    initialCommunitySelectedRef.current = true;
    setFocusedCommunity(first);
    setSelectedCommunityId(first.id);
    setSelectedConversationId(first.mainFeedConversationId ?? null);
  }, [communities, communitiesPage.loading, discoverMode, memberSearch, selectedCommunityId]);

  useEffect(() => {
    if (!requestedChannel || openedChannel.current === requestedChannel) return;
    const controller = new AbortController();
    async function openRequestedChannel(channel: { id: string }) {
      const cached = communities.find(item => item.mainFeedConversationId === channel.id ||
        item.channels.some(candidate => candidate.id === channel.id));
      const community = cached ?? (await Promise.all(organizations.map(async org => {
        const page = await readCommunityPage(org, new URLSearchParams({ limit: '1', conversationId: channel.id }), controller.signal)
          .catch(() => null);
        return page?.items[0];
      }))).find(item => item);
      if (!community || controller.signal.aborted) return;
      openedChannel.current = channel;
      setDiscoverMode(false);
      setFocusedCommunity(community);
      setSelectedCommunityId(community.id);
      setSelectedConversationId(channel.id);
    }
    void openRequestedChannel(requestedChannel);
    return () => controller.abort();
  }, [communities, organizations, requestedChannel]);

  useEffect(() => {
    setPostsError(null);
    setLoadingMorePosts(false);
    setPostsHasNewer(false);
    const cached = selectedConversationId ? postsCacheRef.current.get(selectedConversationId) : undefined;
    if (cached) {
      setPosts(cached.items);
      setPostsNextCursor(cached.nextCursor);
      setCanPost(cached.canPost);
      setCanManage(cached.canManage);
      setFeedIsArchived(cached.isArchived);
      setFeedChannelDescription(cached.channelDescription);
      setLoadingPosts(false);
    } else {
      setPosts([]);
      setPostsNextCursor(null);
      setCanPost(false);
      setCanManage(false);
      setFeedIsArchived(false);
      setFeedChannelDescription(null);
    }
    if (selectedConversationId && pendingJumpRef.current?.conversationId !== selectedConversationId) {
      void loadPosts(selectedConversationId, Boolean(cached));
    }

    return () => {
      feedRequestRef.current?.controller.abort();
      feedRequestRef.current = null;
    };
  }, [selectedConversationId, loadPosts]);

  useEffect(() => {
    if (!jumpTarget || jumpTarget.nonce === handledJumpNonceRef.current) return;
    handledJumpNonceRef.current = jumpTarget.nonce;
    pendingJumpRef.current = jumpTarget;
    const controller = new AbortController();
    let highlightTimer: number | null = null;

    async function runJump(target: CommunityJumpTarget) {
      const cachedComm = communities.find(c => c.id === target.communityId)
        ?? await lookupCommunity(target.organizationId, target.communityId, controller.signal).catch(() => null);
      if (!cachedComm || controller.signal.aborted) {
        if (pendingJumpRef.current?.nonce === target.nonce) pendingJumpRef.current = null;
        return;
      }
      setDiscoverMode(false);
      setFocusedCommunity(cachedComm);
      setSelectedCommunityId(cachedComm.id);
      activeCommunityRef.current = cachedComm.id;
      setSelectedConversationId(target.conversationId);
      activeFeedRef.current = target.conversationId;

      if (target.postId) {
        const alreadyLoaded = postsCacheRef.current.get(target.conversationId)?.items.some(p => p.id === target.postId);
        if (!alreadyLoaded) {
          await loadPostsAround(target.organizationId, target.conversationId, target.postId);
        }
        if (pendingJumpRef.current?.nonce === target.nonce) pendingJumpRef.current = null;
        if (controller.signal.aborted) return;
        setHighlightedPostId(target.postId);
        requestAnimationFrame(() => {
          requestAnimationFrame(() => {
            const el = feedContainerRef.current?.querySelector<HTMLElement>(`[data-post-id="${target.postId}"]`);
            if (el) {
              el.scrollIntoView({ block: 'center', behavior: 'smooth' });
              el.focus({ preventScroll: true });
            }
          });
        });
        highlightTimer = window.setTimeout(() => {
          setHighlightedPostId(current => current === target.postId ? null : current);
        }, 4000);
      } else {
        if (pendingJumpRef.current?.nonce === target.nonce) pendingJumpRef.current = null;
        void loadPosts(target.conversationId, false, target.organizationId);
        setHighlightedHeader(true);
        requestAnimationFrame(() => {
          stageHeaderRef.current?.focus({ preventScroll: true });
        });
        highlightTimer = window.setTimeout(() => setHighlightedHeader(false), 3000);
      }
    }

    void runJump(jumpTarget);
    return () => {
      controller.abort();
      if (highlightTimer !== null) window.clearTimeout(highlightTimer);
    };
  }, [communities, jumpTarget, loadPosts, loadPostsAround, lookupCommunity]);

  const updateFeedPosts = useCallback((conversationId: string, updater: (prev: ChannelPostDto[]) => ChannelPostDto[]) => {
    const cached = postsCacheRef.current.get(conversationId);
    if (cached) {
      const nextCached = updater(cached.items);
      postsCacheRef.current.set(conversationId, { ...cached, items: nextCached });
    }
    if (activeFeedRef.current === conversationId) {
      setPosts(prev => updater(prev));
    }
  }, []);

  useEffect(() => {
    feedListenerRef.current = (changedConversationId, delta) => {
      if (!changedConversationId) void loadCommunities().catch(() => setError('Unable to refresh community roles.'));
      const targetId = changedConversationId ?? activeFeedRef.current;
      if (!targetId) return;

      if (delta && changedConversationId) {
        if (delta.type === 'post_created' && delta.post) {
          const incoming = delta.post;
          updateFeedPosts(changedConversationId, prev => {
            if (prev.some(p => p.id === incoming.id)) return prev;
            const nextPost: ChannelPostDto = {
              id: incoming.id,
              conversationId: incoming.conversationId || changedConversationId,
              authorUserId: incoming.authorUserId,
              authorDisplayName: incoming.authorDisplayName,
              authorAvatarUrl: incoming.authorAvatarUrl ?? null,
              title: incoming.title ?? null,
              body: incoming.body,
              isAnnouncement: Boolean(incoming.isAnnouncement),
              requiresAcknowledgment: Boolean(incoming.requiresAcknowledgment),
              acknowledgedCount: incoming.acknowledgedCount ?? 0,
              totalRecipients: incoming.totalRecipients ?? 0,
              acknowledgedByMe: Boolean(incoming.acknowledgedByMe),
              isPinned: Boolean(incoming.isPinned),
              createdAt: incoming.createdAt,
              comments: [],
              reactions: [],
              attachments: incoming.attachments ?? [],
              poll: null
            };
            const pinned = prev.filter(p => p.isPinned);
            const regular = prev.filter(p => !p.isPinned);
            return [...pinned, nextPost, ...regular];
          });
          if (rightDrawerRef.current === 'files' && activeFeedRef.current === changedConversationId && incoming.attachments?.length) {
            const orgId = communityOrgByConvRef.current.get(changedConversationId);
            if (orgId) void loadChannelFilesRef.current(orgId, changedConversationId, false);
          }
          return;
        }

        if (delta.type === 'community_updated' && delta.communityId) {
          patchCommunityInState(delta.communityId, comm => ({
            ...comm,
            name: typeof delta.name === 'string' && delta.name ? delta.name : comm.name,
            description: delta.description !== undefined ? delta.description : comm.description
          }));
          return;
        }

        if (delta.type === 'channel_updated' && delta.communityId && delta.channelId) {
          const chId = delta.channelId;
          patchCommunityInState(delta.communityId, comm => ({
            ...comm,
            channels: comm.channels.map(ch =>
              ch.id === chId
                ? {
                    ...ch,
                    name: typeof delta.name === 'string' && delta.name ? delta.name : ch.name,
                    description: delta.description !== undefined ? delta.description : ch.description
                  }
                : ch
            )
          }));
          const cached = postsCacheRef.current.get(chId);
          if (cached && delta.description !== undefined) {
            postsCacheRef.current.set(chId, { ...cached, channelDescription: delta.description ?? null });
          }
          if (activeFeedRef.current === chId && delta.description !== undefined) {
            setFeedChannelDescription(delta.description ?? null);
          }
          return;
        }

        if (delta.type === 'channel_archived' && delta.communityId && delta.channelId) {
          const chId = delta.channelId;
          const archivedAt = delta.archivedAt ?? new Date().toISOString();
          patchCommunityInState(delta.communityId, comm => ({
            ...comm,
            channels: comm.channels.map(ch =>
              ch.id === chId ? { ...ch, isArchived: true, archivedAt } : ch
            )
          }));
          const cached = postsCacheRef.current.get(chId);
          if (cached) {
            postsCacheRef.current.set(chId, { ...cached, isArchived: true, archivedAt, canPost: false });
          }
          if (activeFeedRef.current === chId) {
            setFeedIsArchived(true);
            setCanPost(false);
          }
          return;
        }

        if (delta.type === 'channel_restored' && delta.communityId && delta.channelId) {
          const chId = delta.channelId;
          patchCommunityInState(delta.communityId, comm => ({
            ...comm,
            channels: comm.channels.map(ch =>
              ch.id === chId ? { ...ch, isArchived: false, archivedAt: null } : ch
            )
          }));
          const cached = postsCacheRef.current.get(chId);
          if (cached) {
            postsCacheRef.current.set(chId, { ...cached, isArchived: false, archivedAt: null });
          }
          if (activeFeedRef.current === chId) {
            setFeedIsArchived(false);
            void loadPosts(chId, true);
          }
          return;
        }

        if (delta.type === 'ownership') {
          void loadCommunities().catch(() => setError('Unable to refresh community roles.'));
          if (activeCommunityRef.current === delta.communityId && activeFeedRef.current) {
            void loadPosts(activeFeedRef.current, true);
            if (delta.communityId) void loadMembers(delta.communityId);
          }
          return;
        }

        if (delta.type === 'reaction' && delta.emoji && typeof delta.reacted === 'boolean') {
          if (delta.userId === currentUserId) return;
          updateFeedPosts(changedConversationId, prev => prev.map(post => {
            if (post.id !== delta.postId) return post;
            const existing = post.reactions.find(r => r.emoji === delta.emoji);
            const others = post.reactions.filter(r => r.emoji !== delta.emoji);
            const nextCount = Math.max(0, (existing?.count ?? 0) + (delta.reacted ? 1 : -1));
            if (nextCount > 0) {
              others.push({ emoji: delta.emoji!, count: nextCount, reactedByMe: Boolean(existing?.reactedByMe) });
            }
            return { ...post, reactions: others.sort((a, b) => b.count - a.count) };
          }));
          return;
        }

        if (delta.type === 'comment' && delta.comment) {
          const incoming = delta.comment;
          updateFeedPosts(changedConversationId, prev => prev.map(post => {
            if (post.id !== delta.postId) return post;
            if (post.comments.some(c => c.id === incoming.id)) return post;
            return { ...post, comments: [...post.comments, incoming] };
          }));
          return;
        }

        if (delta.type === 'pin' && typeof delta.isPinned === 'boolean') {
          updateFeedPosts(changedConversationId, prev => prev
            .map(post => post.id === delta.postId ? { ...post, isPinned: delta.isPinned! } : post)
            .sort((a, b) => Number(b.isPinned) - Number(a.isPinned) || Date.parse(b.createdAt) - Date.parse(a.createdAt))
          );
          return;
        }
      }

      const activeConversationId = activeFeedRef.current;
      if (activeConversationId && (!changedConversationId || changedConversationId === activeConversationId)) {
        void loadPosts(activeConversationId, true);
      }
    };
    return () => { feedListenerRef.current = null; };
  }, [currentUserId, feedListenerRef, loadCommunities, loadMembers, loadPosts, patchCommunityInState, updateFeedPosts]);

  useEffect(() => {
    setMemberSearchQuery('');
    setAddMemberUserId('');
    setOwnershipTransfer(null);
  }, [selectedCommunityId]);

  useEffect(() => {
    if (selectedCommunity) {
      setMappedDeptId(selectedCommunity.mappedDepartmentId ?? '');
    }
  }, [selectedCommunity]);

  function handleInlineContentLinkClick(e: MouseEvent<HTMLElement>) {
    if (!onOpenContentLink) return;
    const anchor = (e.target as HTMLElement | null)?.closest('a');
    if (!anchor?.href) return;
    const token = extractContentLinkToken(anchor.href);
    if (!token) return;
    e.preventDefault();
    onOpenContentLink(token);
  }

  async function handleCopyChannelLink() {
    if (!selectedCommunity || !selectedConversationId) return;
    const result = await copyContentLink({
      kind: 'channel',
      organizationId: selectedCommunity.organizationId,
      conversationId: selectedConversationId
    });
    setNotice(result.ok ? 'Channel link copied.' : 'Unable to copy link automatically.');
  }

  async function handleCopyPostLink(post: ChannelPostDto) {
    if (!selectedCommunity) return;
    const result = await copyContentLink({
      kind: 'post',
      organizationId: selectedCommunity.organizationId,
      conversationId: post.conversationId,
      postId: post.id
    });
    setNotice(result.ok ? 'Post link copied.' : 'Unable to copy link automatically.');
  }

  function selectCommunityMainFeed(comm: CommunityItemDto) {
    setError('');
    setNotice('');
    setFocusedCommunity(comm);
    setSelectedCommunityId(comm.id);
    setSelectedConversationId(comm.mainFeedConversationId ?? null);
    setDiscoverMode(false);
  }

  function selectSubChannel(comm: CommunityItemDto, ch: CommunityChannelDto) {
    setError('');
    setNotice('');
    setFocusedCommunity(comm);
    setSelectedCommunityId(comm.id);
    setSelectedConversationId(ch.id);
  }

  function openCreateCommunity(orgId?: string) {
    const target = orgId || activeOrgId || organizations[0]?.id || '';
    setCreateOrgId(target);
    setCreateOpen(true);
  }

  async function handleCreateCommunity(e: FormEvent) {
    e.preventDefault();
    const targetOrgId = createOrgId || organizations[0]?.id;
    if (!targetOrgId) return;
    setError('');
    setNotice('');
    const res = await orgFetch(targetOrgId, '/api/communities', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': crypto.randomUUID()
      },
      body: JSON.stringify({
        name: newCommName,
        description: newCommDesc || null,
        visibility: newCommVisibility
      })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Unable to create community.');
      return;
    }
    setCreateOpen(false);
    setNewCommName('');
    setNewCommDesc('');
    setNewCommVisibility('Public');
    const created = await lookupCommunity(targetOrgId, data.id).catch(() => null);
    if (created) {
      selectCommunityMainFeed(created);
    }
    await loadCommunities();
    setNotice(`Community "${data.name}" created.`);
  }

  async function handleJoinCommunity(orgId: string, communityId: string) {
    setError('');
    setNotice('');
    const res = await orgFetch(orgId, `/api/communities/${communityId}/join`, {
      method: 'POST',
      credentials: 'same-origin'
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Unable to join community.');
      return;
    }
    const joined = await lookupCommunity(orgId, communityId).catch(() => null);
    await loadDiscover();
    if (joined) {
      selectCommunityMainFeed(joined);
    }
    await loadCommunities();
    setNotice('Joined community.');
  }

  async function handleCreateChannel(e: FormEvent) {
    e.preventDefault();
    if (!addChannelForId) return;
    const comm = communities.find(c => c.id === addChannelForId) ?? (selectedCommunity?.id === addChannelForId ? selectedCommunity : null);
    if (!comm) return;
    setError('');
    setNotice('');
    const res = await orgFetch(comm.organizationId, `/api/communities/${addChannelForId}/channels`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name: newChannelName,
        description: newChannelDesc.trim() || null,
        postingPolicy: newChannelPolicy
      })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Unable to create channel.');
      return;
    }
    const targetCommId = addChannelForId;
    setAddChannelForId(null);
    setNewChannelName('');
    setNewChannelDesc('');
    setNewChannelPolicy('Members');
    const updated = await lookupCommunity(comm.organizationId, targetCommId).catch(() => null);
    if (updated) setFocusedCommunity(updated);
    await loadCommunities();
    setSelectedCommunityId(targetCommId);
    setSelectedConversationId(data.id);
    setNotice(`Channel "#${data.name}" created.`);
  }

  function surfaceArchivedConflictIfNeeded(res: Response, data: { code?: string; message?: string }, convId?: string | null) {
    if (res.status === 409 && data?.code === 'channel_archived') {
      const targetConv = convId ?? selectedConversationId;
      if (targetConv && selectedCommunity) {
        patchCommunityInState(selectedCommunity.id, comm => ({
          ...comm,
          channels: comm.channels.map(ch =>
            ch.id === targetConv ? { ...ch, isArchived: true } : ch
          )
        }));
      }
      if (targetConv && activeFeedRef.current === targetConv) {
        setFeedIsArchived(true);
        setCanPost(false);
      }
      setError(data.message || 'This channel is archived and read-only.');
      return true;
    }
    return false;
  }

  function openEditCommunityModal() {
    if (!selectedCommunity) return;
    setEditCommName(selectedCommunity.name);
    setEditCommDesc(selectedCommunity.description ?? '');
    setEditCommOpen(true);
  }

  async function handleSaveCommunityMeta(e: FormEvent) {
    e.preventDefault();
    if (!selectedCommunity || savingMetaRef.current) return;
    const trimmedName = editCommName.trim();
    if (!trimmedName) return;
    savingMetaRef.current = true;
    setSavingMeta(true);
    setError('');
    setNotice('');
    try {
      const res = await protectedRequest(
        orgApiPath(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}`),
        'PUT',
        { name: trimmedName, description: editCommDesc.trim() || null }
      );
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setError(data.message || 'Unable to update community.');
        return;
      }
      patchCommunityInState(selectedCommunity.id, comm => ({
        ...comm,
        name: data.name ?? trimmedName,
        description: data.description !== undefined ? data.description : (editCommDesc.trim() || null)
      }));
      setEditCommOpen(false);
      setNotice('Community updated.');
    } catch {
      setError('Unable to update community.');
    } finally {
      savingMetaRef.current = false;
      setSavingMeta(false);
    }
  }

  function openEditChannelModal() {
    if (!selectedSubChannel) return;
    setEditChannelName(selectedSubChannel.name);
    setEditChannelDesc(selectedSubChannel.description ?? feedChannelDescription ?? '');
    setEditChannelOpen(true);
  }

  async function handleSaveChannelMeta(e: FormEvent) {
    e.preventDefault();
    if (!selectedCommunity || !selectedSubChannel || savingMetaRef.current) return;
    const trimmedName = editChannelName.trim();
    if (!trimmedName) return;
    savingMetaRef.current = true;
    setSavingMeta(true);
    setError('');
    setNotice('');
    try {
      const res = await protectedRequest(
        orgApiPath(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/channels/${selectedSubChannel.id}`),
        'PUT',
        { name: trimmedName, description: editChannelDesc.trim() || null }
      );
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        if (!surfaceArchivedConflictIfNeeded(res, data, selectedSubChannel.id)) {
          setError(data.message || 'Unable to update channel.');
        }
        return;
      }
      const nextDesc = data.description !== undefined ? data.description : (editChannelDesc.trim() || null);
      patchCommunityInState(selectedCommunity.id, comm => ({
        ...comm,
        channels: comm.channels.map(ch =>
          ch.id === selectedSubChannel.id
            ? { ...ch, name: data.name ?? trimmedName, description: nextDesc }
            : ch
        )
      }));
      setFeedChannelDescription(nextDesc);
      const cached = postsCacheRef.current.get(selectedSubChannel.id);
      if (cached) postsCacheRef.current.set(selectedSubChannel.id, { ...cached, channelDescription: nextDesc });
      setEditChannelOpen(false);
      setNotice('Channel updated.');
    } catch {
      setError('Unable to update channel.');
    } finally {
      savingMetaRef.current = false;
      setSavingMeta(false);
    }
  }

  async function handleArchiveChannel() {
    if (!selectedCommunity || !selectedSubChannel || savingMetaRef.current) return;
    savingMetaRef.current = true;
    setSavingMeta(true);
    setError('');
    setNotice('');
    try {
      const res = await protectedRequest(
        orgApiPath(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/channels/${selectedSubChannel.id}/archive`),
        'POST'
      );
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        if (!surfaceArchivedConflictIfNeeded(res, data, selectedSubChannel.id)) {
          setError(data.message || 'Unable to archive channel.');
        }
        return;
      }
      const archivedAt = data.archivedAt ?? new Date().toISOString();
      patchCommunityInState(selectedCommunity.id, comm => ({
        ...comm,
        channels: comm.channels.map(ch =>
          ch.id === selectedSubChannel.id ? { ...ch, isArchived: true, archivedAt } : ch
        )
      }));
      const cached = postsCacheRef.current.get(selectedSubChannel.id);
      if (cached) postsCacheRef.current.set(selectedSubChannel.id, { ...cached, isArchived: true, archivedAt, canPost: false });
      setFeedIsArchived(true);
      setCanPost(false);
      setArchiveConfirmOpen(false);
      setNotice(`Channel "#${selectedSubChannel.name}" archived.`);
    } catch {
      setError('Unable to archive channel.');
    } finally {
      savingMetaRef.current = false;
      setSavingMeta(false);
    }
  }

  async function handleRestoreChannel() {
    if (!selectedCommunity || !selectedSubChannel || savingMetaRef.current) return;
    savingMetaRef.current = true;
    setSavingMeta(true);
    setError('');
    setNotice('');
    try {
      const res = await protectedRequest(
        orgApiPath(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/channels/${selectedSubChannel.id}/restore`),
        'POST'
      );
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setError(data.message || 'Unable to restore channel.');
        return;
      }
      patchCommunityInState(selectedCommunity.id, comm => ({
        ...comm,
        channels: comm.channels.map(ch =>
          ch.id === selectedSubChannel.id ? { ...ch, isArchived: false, archivedAt: null } : ch
        )
      }));
      const cached = postsCacheRef.current.get(selectedSubChannel.id);
      if (cached) postsCacheRef.current.set(selectedSubChannel.id, { ...cached, isArchived: false, archivedAt: null, canPost: true });
      setFeedIsArchived(false);
      setCanPost(true);
      setNotice(`Channel "#${selectedSubChannel.name}" restored.`);
      void loadPosts(selectedSubChannel.id, true);
    } catch {
      setError('Unable to restore channel.');
    } finally {
      savingMetaRef.current = false;
      setSavingMeta(false);
    }
  }

  async function uploadPostAttachments(fileList: File[]) {
    const targetConvId = selectedConversationId;
    const targetOrgId = activeOrgId ?? (targetConvId ? communityOrgByConvRef.current.get(targetConvId) : undefined);
    if (!targetConvId || !targetOrgId || !canPost || activeIsArchived || fileList.length === 0 || uploadingFiles) return;

    if (pendingAttachments.length + fileList.length > 10) {
      setError('You can attach up to 10 files per post.');
      return;
    }

    setError('');
    setQuotaWarning('');
    const controller = new AbortController();
    uploadControllerRef.current = controller;
    setUploadingFiles(true);

    try {
      const csrfRes = await window.fetch('/api/auth/csrf', { signal: controller.signal });
      const csrfData = csrfRes.ok ? await csrfRes.json().catch(() => ({})) : {};
      const csrfToken = typeof csrfData.token === 'string' ? csrfData.token : '';

      for (const file of fileList) {
        const form = new FormData();
        form.append('clientUploadId', crypto.randomUUID());
        form.append('file', file, file.name || 'pasted-image.png');

        const headers: Record<string, string> = {};
        if (csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;

        const res = await orgFetch(targetOrgId, `/api/conversations/${targetConvId}/attachments`, {
          method: 'POST',
          credentials: 'same-origin',
          headers,
          body: form,
          signal: controller.signal
        });

        const data = await res.json().catch(() => ({}));
        if (controller.signal.aborted || activeFeedRef.current !== targetConvId) return;

        if (!res.ok) {
          if (!surfaceArchivedConflictIfNeeded(res, data, targetConvId)) {
            setError(data.message || `Failed to upload ${file.name}.`);
          }
          continue;
        }

        const rawAtt = (data.attachment ?? data) as Partial<AttachmentItem> & { width?: number | null; height?: number | null };
        if (rawAtt?.id) {
          const normalized: AttachmentItem = {
            id: rawAtt.id,
            conversationId: rawAtt.conversationId ?? targetConvId,
            messageId: rawAtt.messageId ?? null,
            postId: rawAtt.postId ?? null,
            uploaderUserId: rawAtt.uploaderUserId ?? currentUserId,
            clientUploadId: rawAtt.clientUploadId ?? '',
            fileName: rawAtt.fileName ?? file.name,
            contentType: rawAtt.contentType ?? file.type,
            sizeBytes: typeof rawAtt.sizeBytes === 'number' ? rawAtt.sizeBytes : file.size,
            sha256Hex: rawAtt.sha256Hex ?? '',
            imageWidth: rawAtt.imageWidth ?? rawAtt.width ?? null,
            imageHeight: rawAtt.imageHeight ?? rawAtt.height ?? null,
            state: rawAtt.state ?? 'pending',
            scanStatus: rawAtt.scanStatus ?? 'clean',
            downloadUrl: rawAtt.downloadUrl ?? '',
            thumbnailUrl: rawAtt.thumbnailUrl ?? null,
            previewUrl: rawAtt.previewUrl ?? null,
            createdAt: rawAtt.createdAt ?? new Date().toISOString()
          };
          setPendingAttachments(prev => (prev.some(a => a.id === normalized.id) ? prev : [...prev, normalized]));
        }
        if (data.quota) {
          setChannelFilesQuota(data.quota as StorageQuotaSummary);
        }
        if (typeof data.quotaWarning === 'string' && data.quotaWarning) {
          setQuotaWarning(data.quotaWarning);
        }
      }
    } catch {
      if (!controller.signal.aborted && activeFeedRef.current === targetConvId) {
        setError('Upload interrupted. Try attaching the file again.');
      }
    } finally {
      if (uploadControllerRef.current === controller) {
        uploadControllerRef.current = null;
        setUploadingFiles(false);
      }
    }
  }

  function handlePostFileInputChange(e: ChangeEvent<HTMLInputElement>) {
    const files = e.target.files ? Array.from(e.target.files) : [];
    e.target.value = '';
    if (files.length > 0) {
      void uploadPostAttachments(files);
    }
  }

  function handlePostComposerPaste(e: ClipboardEvent<HTMLTextAreaElement>) {
    if (!canPost || activeIsArchived) return;
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
      void uploadPostAttachments(pastedFiles);
    }
  }

  function handlePostComposerDragOver(e: DragEvent<HTMLFormElement>) {
    if (!canPost || activeIsArchived) return;
    if (e.dataTransfer?.types && Array.from(e.dataTransfer.types).includes('Files')) {
      e.preventDefault();
      setIsComposerDragOver(true);
    }
  }

  function handlePostComposerDrop(e: DragEvent<HTMLFormElement>) {
    e.preventDefault();
    setIsComposerDragOver(false);
    if (!canPost || activeIsArchived) return;
    const dropped = e.dataTransfer?.files ? Array.from(e.dataTransfer.files) : [];
    if (dropped.length > 0) {
      void uploadPostAttachments(dropped);
    }
  }

  async function handleCreatePost(e: FormEvent) {
    e.preventDefault();
    if (!selectedConversationId || (!postBody.trim() && pendingAttachments.length === 0) || posting || uploadingFiles) return;
    setError('');
    setNotice('');
    setPosting(true);

    const targetConvId = selectedConversationId;
    const savedTitle = postTitle;
    const savedBody = postBody;
    const savedIsAnnouncement = isAnnouncement;
    const savedRequiresAck = requiresAck;
    const savedIncludePoll = includePoll;
    const savedPollQuestion = pollQuestion;
    const savedPollOptions = pollOptions;
    const savedPollAllowMultiple = pollAllowMultiple;
    const savedAttachments = pendingAttachments;

    const cleanedPollOptions = pollOptions.map(o => o.trim()).filter(Boolean);
    const pollPayload = includePoll && pollQuestion.trim() && cleanedPollOptions.length >= 2
      ? {
          question: pollQuestion.trim(),
          options: cleanedPollOptions,
          allowMultiple: pollAllowMultiple
        }
      : null;

    const tempPostId = `temp-post-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const nowIso = new Date().toISOString();
    const optimisticPost: ChannelPostDto = {
      id: tempPostId,
      conversationId: targetConvId,
      authorUserId: currentUserId,
      authorDisplayName: currentDisplayName,
      title: savedTitle.trim() || null,
      body: savedBody.trim(),
      isAnnouncement: canManage && savedIsAnnouncement,
      requiresAcknowledgment: canManage && savedIsAnnouncement && savedRequiresAck,
      isPinned: false,
      createdAt: nowIso,
      acknowledgedCount: 0,
      totalRecipients: 0,
      acknowledgedByMe: false,
      poll: pollPayload
        ? {
            id: `temp-poll-${tempPostId}`,
            question: pollPayload.question,
            allowMultiple: pollPayload.allowMultiple,
            totalVoters: 0,
            options: pollPayload.options.map((label, idx) => ({
              id: `opt-${idx}`,
              label,
              votes: 0,
              votedByMe: false
            }))
          }
        : null,
      comments: [],
      reactions: [],
      attachments: savedAttachments,
      pending: true
    };

    // Show the new post immediately in dimmed pending state and clear the composer in 0ms.
    inFlightPostIdsRef.current.add(tempPostId);
    updateFeedPosts(targetConvId, prev => {
      const pinned = prev.filter(p => p.isPinned);
      const regular = prev.filter(p => !p.isPinned);
      return [...pinned, optimisticPost, ...regular];
    });
    setPostTitle('');
    setPostBody('');
    setIsAnnouncement(false);
    setRequiresAck(false);
    setIncludePoll(false);
    setPollQuestion('');
    setPollOptions(['', '']);
    setPollAllowMultiple(false);
    setPendingAttachments([]);
    setQuotaWarning('');

    try {
      const targetOrgId = activeOrgId ?? communityOrgByConvRef.current.get(targetConvId);
      if (!targetOrgId) return;
      const res = await orgFetch(targetOrgId, `/api/channels/${targetConvId}/posts`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title: savedTitle.trim() || null,
          body: savedBody.trim(),
          isAnnouncement: canManage && savedIsAnnouncement,
          requiresAcknowledgment: canManage && savedIsAnnouncement && savedRequiresAck,
          poll: pollPayload,
          attachmentIds: savedAttachments.length > 0 ? savedAttachments.map(a => a.id) : undefined
        })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        inFlightPostIdsRef.current.delete(tempPostId);
        updateFeedPosts(targetConvId, prev => prev.filter(p => p.id !== tempPostId));
        setPostTitle(savedTitle);
        setPostBody(savedBody);
        setIsAnnouncement(savedIsAnnouncement);
        setRequiresAck(savedRequiresAck);
        setIncludePoll(savedIncludePoll);
        setPollQuestion(savedPollQuestion);
        setPollOptions(savedPollOptions);
        setPollAllowMultiple(savedPollAllowMultiple);
        setPendingAttachments(savedAttachments);
        if (!surfaceArchivedConflictIfNeeded(res, data, targetConvId)) {
          setError(data.message || 'Unable to publish post.');
        }
        return;
      }

      inFlightPostIdsRef.current.delete(tempPostId);
      const serverAttachments: AttachmentItem[] = Array.isArray(data.attachments) ? data.attachments : savedAttachments;
      updateFeedPosts(targetConvId, prev => {
        if (data.id && prev.some(p => p.id === data.id)) {
          return prev.filter(p => p.id !== tempPostId);
        }
        return prev.map(p =>
          p.id === tempPostId
            ? {
                ...p,
                id: data.id ?? p.id,
                createdAt: data.createdAt ?? p.createdAt,
                attachments: serverAttachments,
                pending: false
              }
            : p
        );
      });
      if (rightDrawerRef.current === 'files' && serverAttachments.length > 0) {
        void loadChannelFilesRef.current(targetOrgId, targetConvId, false);
      }
      if (pollPayload || (canManage && savedIsAnnouncement && savedRequiresAck)) {
        void loadPosts(targetConvId, true);
      }
    } catch {
      updateFeedPosts(targetConvId, prev => prev.filter(p => p.id !== tempPostId));
      setPostTitle(savedTitle);
      setPostBody(savedBody);
      setIsAnnouncement(savedIsAnnouncement);
      setRequiresAck(savedRequiresAck);
      setIncludePoll(savedIncludePoll);
      setPollQuestion(savedPollQuestion);
      setPollOptions(savedPollOptions);
      setPollAllowMultiple(savedPollAllowMultiple);
      setPendingAttachments(savedAttachments);
      setError('Unable to publish post. Check your connection and try again.');
    } finally {
      inFlightPostIdsRef.current.delete(tempPostId);
      setPosting(false);
    }
  }

  async function handleTogglePin(post: ChannelPostDto) {
    setError('');
    const nextPinned = !post.isPinned;
    updateFeedPosts(post.conversationId, prev => prev
      .map(p => p.id === post.id ? { ...p, isPinned: nextPinned } : p)
      .sort((a, b) => Number(b.isPinned) - Number(a.isPinned) || Date.parse(b.createdAt) - Date.parse(a.createdAt))
    );
    const orgId = activeOrgId ?? communityOrgByConvRef.current.get(post.conversationId);
    if (!orgId) return;
    const res = await orgFetch(orgId, `/api/posts/${post.id}/pin`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ isPinned: nextPinned })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      updateFeedPosts(post.conversationId, prev => prev
        .map(p => p.id === post.id ? { ...p, isPinned: post.isPinned } : p)
        .sort((a, b) => Number(b.isPinned) - Number(a.isPinned) || Date.parse(b.createdAt) - Date.parse(a.createdAt))
      );
      if (!surfaceArchivedConflictIfNeeded(res, data, post.conversationId)) {
        setError(data.message || 'Unable to update pin state.');
      }
    }
  }

  async function handleAcknowledge(postId: string) {
    setError('');
    const convId = selectedConversationId;
    if (convId) {
      updateFeedPosts(convId, prev => prev.map(p =>
        p.id === postId && !p.acknowledgedByMe
          ? { ...p, acknowledgedByMe: true, acknowledgedCount: p.acknowledgedCount + 1 }
          : p
      ));
    }
    const orgId = activeOrgId ?? (convId ? communityOrgByConvRef.current.get(convId) : undefined);
    if (!orgId) return;
    const res = await orgFetch(orgId, `/api/posts/${postId}/acknowledge`, {
      method: 'PUT',
      credentials: 'same-origin'
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      if (convId) void loadPosts(convId, true);
      if (!surfaceArchivedConflictIfNeeded(res, data, convId)) {
        setError(data.message || 'Unable to record acknowledgment.');
      }
      return;
    }
    const data = await res.json().catch(() => null);
    if (convId && data && typeof data.acknowledgedCount === 'number') {
      updateFeedPosts(convId, prev => prev.map(p =>
        p.id === postId
          ? { ...p, acknowledgedByMe: true, acknowledgedCount: data.acknowledgedCount, totalRecipients: data.totalRecipients ?? p.totalRecipients }
          : p
      ));
    }
  }

  async function handleRemind(postId: string) {
    setError('');
    setNotice('');
    if (!activeOrgId) return;
    const res = await orgFetch(activeOrgId, `/api/posts/${postId}/remind`, {
      method: 'POST',
      credentials: 'same-origin'
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      if (!surfaceArchivedConflictIfNeeded(res, data)) {
        setError(data.message || 'Unable to send reminder.');
      }
      return;
    }
    setNotice(`Reminder recorded for ${data.remindedCount ?? 0} outstanding member(s).`);
  }

  async function handleToggleReaction(post: ChannelPostDto, emoji: string) {
    const key = `${post.id}:${emoji}`;
    if (reactionRequests.current.has(key)) return;

    const reactedByMe = !post.reactions.find(reaction => reaction.emoji === emoji)?.reactedByMe;
    const change = { postId: post.id, emoji, reactedByMe };
    reactionRequests.current.add(key);
    setPendingReactions(previous => ({ ...previous, [key]: change }));
    setError('');

    try {
      const orgId = activeOrgId ?? communityOrgByConvRef.current.get(post.conversationId);
      if (!orgId) throw new Error('Missing workspace');
      const res = await orgFetch(orgId, `/api/posts/${post.id}/reactions/${encodeURIComponent(emoji)}`, {
        method: reactedByMe ? 'PUT' : 'DELETE',
        credentials: 'same-origin'
      });
      if (!res.ok) {
        const errData = await res.json().catch(() => ({}));
        if (surfaceArchivedConflictIfNeeded(res, errData, post.conversationId)) return;
        throw new Error('Reaction request failed');
      }

      const request = feedRequestRef.current;
      if (request?.conversationId === post.conversationId) request.confirmedReactions.push(change);
      setPosts(previous => {
        const next = previous.map(item => item.id === post.id ? applyReaction(item, change) : item);
        const cached = postsCacheRef.current.get(post.conversationId);
        if (cached) postsCacheRef.current.set(post.conversationId, { ...cached, items: next });
        return next;
      });
    } catch {
      if (activeFeedRef.current === post.conversationId) setError('Unable to update reaction. Try again.');
    } finally {
      reactionRequests.current.delete(key);
      setPendingReactions(previous => {
        const next = { ...previous };
        delete next[key];
        return next;
      });
    }
  }

  async function handleVotePoll(poll: PostPollDto, optionId: string) {
    let nextIds: string[];
    const previousIds = poll.options.filter(o => o.votedByMe).map(o => o.id);
    if (poll.allowMultiple) {
      nextIds = previousIds.includes(optionId)
        ? previousIds.filter(id => id !== optionId)
        : [...previousIds, optionId];
      if (nextIds.length === 0) return;
    } else {
      nextIds = [optionId];
    }

    const convId = selectedConversationId;
    if (convId) {
      const hadVoted = previousIds.length > 0;
      const nextTotalVoters = hadVoted ? poll.totalVoters : poll.totalVoters + 1;
      updateFeedPosts(convId, prev => prev.map(p => {
        if (!p.poll || p.poll.id !== poll.id) return p;
        const nextOptions = p.poll.options.map(opt => {
          const wasSelected = previousIds.includes(opt.id);
          const nowSelected = nextIds.includes(opt.id);
          const nextVotes = Math.max(0, opt.votes + (nowSelected ? 1 : 0) - (wasSelected ? 1 : 0));
          return {
            ...opt,
            votedByMe: nowSelected,
            votes: nextVotes
          };
        });
        return { ...p, poll: { ...p.poll, totalVoters: nextTotalVoters, options: nextOptions } };
      }));
    }

    const orgId = activeOrgId ?? (convId ? communityOrgByConvRef.current.get(convId) : undefined);
    if (!orgId) return;
    const res = await orgFetch(orgId, `/api/polls/${poll.id}/vote`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ optionIds: nextIds })
    });
    if (!res.ok) {
      if (convId) void loadPosts(convId, true);
      const data = await res.json().catch(() => ({}));
      if (!surfaceArchivedConflictIfNeeded(res, data, convId)) {
        setError(data.message || 'Unable to submit vote.');
      }
    }
  }

  async function handleAddComment(e: FormEvent, postId: string) {
    e.preventDefault();
    const draft = (commentDrafts[postId] ?? '').trim();
    if (!draft) return;

    const convId = selectedConversationId;
    const tempId = `temp-comment-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const optimisticComment: PostCommentDto = {
      id: tempId,
      postId,
      authorUserId: currentUserId,
      authorDisplayName: currentDisplayName,
      body: draft,
      createdAt: new Date().toISOString(),
      pending: true
    };

    setCommentDrafts(prev => ({ ...prev, [postId]: '' }));
    setExpandedComments(prev => ({ ...prev, [postId]: true }));
    if (convId) {
      updateFeedPosts(convId, prev => prev.map(p =>
        p.id === postId ? { ...p, comments: [...p.comments, optimisticComment] } : p
      ));
    }

    const orgId = activeOrgId ?? (convId ? communityOrgByConvRef.current.get(convId) : undefined);
    if (!orgId) return;
    const res = await orgFetch(orgId, `/api/posts/${postId}/comments`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ body: draft })
    });
    if (!res.ok) {
      if (convId) {
        updateFeedPosts(convId, prev => prev.map(p =>
          p.id === postId ? { ...p, comments: p.comments.filter(c => c.id !== tempId) } : p
        ));
      }
      setCommentDrafts(prev => ({ ...prev, [postId]: draft }));
      const data = await res.json().catch(() => ({}));
      if (!surfaceArchivedConflictIfNeeded(res, data, convId)) {
        setError(data.message || 'Unable to post comment.');
      }
      return;
    }

    const created: PostCommentDto | null = await res.json().catch(() => null);
    if (convId && created) {
      updateFeedPosts(convId, prev => prev.map(p => {
        if (p.id !== postId) return p;
        const withoutTemp = p.comments.filter(c => c.id !== tempId && c.id !== created.id);
        return { ...p, comments: [...withoutTemp, created] };
      }));
    }
  }

  async function handleAddMember(e: FormEvent) {
    e.preventDefault();
    if (!selectedCommunity || !addMemberUserId) return;
    setError('');
    const res = await orgFetch(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/members`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userId: addMemberUserId })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Unable to add member.');
      return;
    }
    setAddMemberUserId('');
    await Promise.all([loadMembers(selectedCommunity.id), loadCommunities()]);
    setNotice('Member added to community.');
  }

  async function handleRemoveMember(targetUserId: string) {
    if (!selectedCommunity) return;
    setError('');
    const res = await orgFetch(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/members/${targetUserId}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Unable to remove member.');
      return;
    }
    await Promise.all([loadMembers(selectedCommunity.id), loadCommunities()]);
    setNotice('Member removed. Access revoked across all channels.');
  }

  async function handleToggleAdmin(member: CommunityMemberDto) {
    if (!selectedCommunity) return;
    setError('');
    const res = await orgFetch(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/admins/${member.userId}`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ isAdmin: member.role !== 'Admin' })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Unable to update member role.');
      return;
    }
    await loadMembers(selectedCommunity.id);
  }

  async function handleLeaveCommunity() {
    if (!selectedCommunity) return;
    setError('');
    const res = await orgFetch(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/leave`, {
      method: 'POST',
      credentials: 'same-origin'
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Unable to leave community.');
      return;
    }
    setMembersOpen(false);
    setFocusedCommunity(null);
    setSelectedCommunityId(null);
    setSelectedConversationId(null);
    await loadCommunities();
    setNotice('Left community.');
  }

  async function handleToggleOfficial() {
    if (!selectedCommunity) return;
    setError('');
    setNotice('');
    const nextOfficial = !selectedCommunity.isOfficial;
    const res = await orgFetch(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/official`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ isOfficial: nextOfficial })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Changing Official status requires a communications admin with a confirmed employee profile and an authenticator.');
      return;
    }
    await loadCommunities();
    setNotice(nextOfficial ? 'Community designated as Official.' : 'Official designation removed.');
  }

  async function handleSyncDepartment() {
    if (!selectedCommunity) return;
    setError('');
    setNotice('');
    const mapRes = await orgFetch(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/department-mapping`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ departmentId: mappedDeptId || null })
    });
    if (!mapRes.ok) {
      setError('Unable to update department mapping.');
      return;
    }
    if (mappedDeptId) {
      const syncRes = await orgFetch(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/sync-department`, {
        method: 'POST',
        credentials: 'same-origin'
      });
      const data = await syncRes.json().catch(() => ({}));
      if (!syncRes.ok) {
        setError(data.message || 'Unable to sync department members.');
        return;
      }
      await Promise.all([loadMembers(selectedCommunity.id), loadCommunities()]);
      setNotice(`Department synced · Added ${data.addedCount ?? 0}, unchanged ${data.unchangedCount ?? 0}.`);
    } else {
      await loadCommunities();
      setNotice('Department mapping cleared.');
    }
  }


  const activePolicy = selectedSubChannel
    ? selectedSubChannel.postingPolicy
    : selectedCommunity?.mainFeedPostingPolicy ?? 'Members';
  const activeIsArchived = Boolean(selectedSubChannel && (selectedSubChannel.isArchived || feedIsArchived));
  const activeChannelDescription = selectedSubChannel
    ? (selectedSubChannel.description ?? feedChannelDescription ?? null)
    : null;

  const filteredDiscover = discoverCommunities;
  const sidebarPage = discoverMode ? discoverPage : communitiesPage;
  const [showHiddenComms, setShowHiddenComms] = useState(false);
  const [sectionModal, setSectionModal] = useState<{ mode: 'create' } | { mode: 'rename'; section: SidebarSectionDto } | null>(null);
  const [draggedCommId, setDraggedCommId] = useState<string | null>(null);
  const sidebarOrg = useSidebarOrganization('communities', organizations.length > 0, workspaceKey);

  const sortCommunitiesByOrder = useCallback((list: CommunityItemDto[]) => {
    return [...list].sort((a, b) => {
      const pa = sidebarOrg.itemByTargetId.get(a.id);
      const pb = sidebarOrg.itemByTargetId.get(b.id);
      if (pa && pb && pa.sortOrder !== pb.sortOrder) return pa.sortOrder - pb.sortOrder;
      return Number(b.isOfficial) - Number(a.isOfficial) || a.name.localeCompare(b.name) || a.id.localeCompare(b.id);
    });
  }, [sidebarOrg.itemByTargetId]);

  const hiddenCommunities = useMemo(
    () => sortCommunitiesByOrder(communities.filter(c => sidebarOrg.itemByTargetId.get(c.id)?.isHidden)),
    [communities, sidebarOrg.itemByTargetId, sortCommunitiesByOrder]
  );
  const visibleCommunities = useMemo(
    () => communities.filter(c => !sidebarOrg.itemByTargetId.get(c.id)?.isHidden),
    [communities, sidebarOrg.itemByTargetId]
  );
  const favoriteCommunities = useMemo(
    () => sortCommunitiesByOrder(visibleCommunities.filter(c => sidebarOrg.itemByTargetId.get(c.id)?.isFavorite)),
    [sidebarOrg.itemByTargetId, sortCommunitiesByOrder, visibleCommunities]
  );
  const customSectionGroups = useMemo(() => {
    return sidebarOrg.sections.map(section => ({
      section,
      items: sortCommunitiesByOrder(visibleCommunities.filter(c => {
        const pref = sidebarOrg.itemByTargetId.get(c.id);
        return !pref?.isFavorite && pref?.sectionId === section.id;
      }))
    }));
  }, [sidebarOrg.itemByTargetId, sidebarOrg.sections, sortCommunitiesByOrder, visibleCommunities]);
  const ungroupedCommunities = useMemo(() => {
    const validSectionIds = new Set(sidebarOrg.sections.map(s => s.id));
    return visibleCommunities.filter(c => {
      const pref = sidebarOrg.itemByTargetId.get(c.id);
      if (pref?.isFavorite) return false;
      return !pref?.sectionId || !validSectionIds.has(pref.sectionId);
    });
  }, [sidebarOrg.itemByTargetId, sidebarOrg.sections, visibleCommunities]);

  const workspaceGroups = useMemo(() => {
    return organizations.map(org => {
      const orgComms = sortCommunitiesByOrder(ungroupedCommunities.filter(c => c.organizationId === org.id));
      const allCount = communities.filter(c => c.organizationId === org.id).length;
      return { org, allCount, communities: orgComms };
    }).filter(group => !normalizedFilter || group.communities.length > 0 || group.org.name.toLowerCase().includes(normalizedFilter));
  }, [organizations, communities, ungroupedCommunities, normalizedFilter, sortCommunitiesByOrder]);

  const renderCommunityTree = (comm: CommunityItemDto, groupList: CommunityItemDto[]) => {
    const isMainActive =
      selectedCommunityId === comm.id &&
      selectedConversationId === comm.mainFeedConversationId;
    const isManager = comm.myRole === 'Owner' || comm.myRole === 'Admin';
    const pref = sidebarOrg.itemByTargetId.get(comm.id);
    const groupIds = groupList.map(item => item.id);
    const idx = groupIds.indexOf(comm.id);
    const preview = comm.latestPostPreview;
    const subText = preview
      ? `${preview.authorDisplayName}: ${preview.bodyPreview}`
      : `${comm.memberCount} ${comm.memberCount === 1 ? 'member' : 'members'}${comm.channels.length > 0 ? ` · ${comm.channels.length} ${comm.channels.length === 1 ? 'channel' : 'channels'}` : ''}`;

    return (
      <div
        className={`nx-comm-tree-group ${draggedCommId === comm.id ? 'dragging' : ''}`}
        key={comm.id}
        draggable
        onDragStart={e => {
          setDraggedCommId(comm.id);
          e.dataTransfer.effectAllowed = 'move';
          e.dataTransfer.setData('text/plain', comm.id);
        }}
        onDragEnd={() => setDraggedCommId(null)}
        onDragOver={e => {
          if (draggedCommId && draggedCommId !== comm.id && groupIds.includes(draggedCommId)) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
          }
        }}
        onDrop={e => {
          e.preventDefault();
          if (!draggedCommId || draggedCommId === comm.id || !groupIds.includes(draggedCommId)) return;
          const nextOrder = reorderIdList(groupIds, draggedCommId, comm.id);
          setDraggedCommId(null);
          void sidebarOrg.persistReorder(nextOrder);
        }}
      >
        <div className="nx-conv-row-wrap">
          <button
            type="button"
            className={`nx-comm-row ${isMainActive ? 'active' : ''}`}
            onClick={() => selectCommunityMainFeed(comm)}
          >
            <span className="nx-comm-emblem" aria-hidden="true">
              {comm.name.trim().slice(0, 2).toUpperCase()}
            </span>
            <div className="nx-conv-meta">
              <div className="nx-conv-top">
                <span className="nx-conv-name">
                  {pref?.isFavorite && <Star size={12} aria-label="Favorite" />}
                  <span>{comm.name}</span>
                  {comm.visibility === 'Private' && <Lock size={12} aria-label="Private Community" />}
                </span>
                {comm.isOfficial && <Badge variant="role">Official</Badge>}
              </div>
              <div className="nx-conv-sub nx-conv-preview" title={subText}>
                {subText}
              </div>
            </div>
          </button>
          <SidebarItemActions
            itemLabel={comm.name}
            pref={pref}
            sections={sidebarOrg.sections}
            canMoveUp={idx > 0}
            canMoveDown={idx >= 0 && idx < groupIds.length - 1}
            onToggleFavorite={() => void sidebarOrg.updateItem(comm.id, { isFavorite: !pref?.isFavorite })}
            onToggleHidden={() => void sidebarOrg.updateItem(comm.id, { isHidden: !pref?.isHidden })}
            onAssignSection={secId => void sidebarOrg.updateItem(comm.id, secId ? { sectionId: secId, isHidden: false } : { clearSection: true })}
            onMoveUp={() => void sidebarOrg.persistReorder(moveIdByOffset(groupIds, comm.id, -1))}
            onMoveDown={() => void sidebarOrg.persistReorder(moveIdByOffset(groupIds, comm.id, 1))}
          />
        </div>

        {comm.channels.length > 0 && (
          <div className="nx-comm-channels">
            {comm.channels.map(ch => {
              const isChActive = selectedConversationId === ch.id;
              return (
                <button
                  type="button"
                  key={ch.id}
                  className={`nx-channel-row ${isChActive ? 'active' : ''} ${ch.isArchived ? 'archived' : ''}`}
                  onClick={() => selectSubChannel(comm, ch)}
                >
                  {ch.isArchived ? (
                    <Archive size={13} aria-hidden="true" />
                  ) : ch.postingPolicy === 'AdminsOnly' ? (
                    <Megaphone size={14} aria-hidden="true" />
                  ) : (
                    <Hash size={14} aria-hidden="true" />
                  )}
                  <span className="nx-channel-name">{ch.name}</span>
                  {ch.isArchived && <Badge variant="role">Archived</Badge>}
                </button>
              );
            })}
          </div>
        )}

        {isManager && (
          <button
            type="button"
            className="nx-add-channel-btn"
            onClick={() => {
              setAddChannelForId(comm.id);
              setNewChannelName('');
              setNewChannelDesc('');
              setNewChannelPolicy('Members');
            }}
          >
            <Plus size={13} aria-hidden="true" /> Add channel
          </button>
        )}
      </div>
    );
  };

  return (
    <div className={`nx-chat-layout ${selectedConversationId ? 'has-selection' : ''}`}>
      {/* Left Sidebar: Multi-Workspace Communities Tree & Browse */}
      <aside className="nx-chat-sidebar" aria-label="Workspaces and communities">
        <div className="nx-comm-sidebar-head">
          <div className="nx-comm-sidebar-row">
            <div className="nx-search-input-wrap" style={{ flex: 1 }}>
              <Search size={14} aria-hidden="true" />
              <input
                type="search"
                aria-label="Filter workspaces and communities"
                placeholder="Filter communities…"
                value={sidebarFilter}
                onChange={e => setSidebarFilter(e.target.value)}
              />
            </div>
            {organizations.length > 0 && (
              <>
                {!discoverMode && (
                  <Button
                    variant="ghost"
                    size="sm"
                    iconOnly
                    aria-label="New sidebar section"
                    title="New section"
                    onClick={() => setSectionModal({ mode: 'create' })}
                  >
                    <FolderPlus size={15} aria-hidden="true" />
                  </Button>
                )}
                <Button
                  variant={discoverMode ? 'primary' : 'default'}
                  size="sm"
                  iconOnly
                  aria-label={discoverMode ? 'Back to my communities' : 'Browse shared communities'}
                  title={discoverMode ? 'My communities' : 'Browse shared communities'}
                  onClick={() => setDiscoverMode(v => !v)}
                >
                  {discoverMode ? <ArrowLeft size={15} aria-hidden="true" /> : <Compass size={15} aria-hidden="true" />}
                </Button>
              </>
            )}
          </div>
        </div>

        <div className="nx-conv-scroll">
          {organizations.length === 0 ? (
            <div className="nx-empty-side">
              <p style={{ margin: 0 }}>Create or join a workspace to organize communities and channels.</p>
            </div>
          ) : discoverMode ? (
            discoverPage.loading && filteredDiscover.length === 0 ? (
              <div className="nx-empty-side" role="status">Loading communities…</div>
            ) : filteredDiscover.length === 0 && !discoverPage.error ? (
              <div className="nx-empty-side">No shared communities found.</div>
            ) : (
              filteredDiscover.map(comm => (
                <div className="nx-discover-card" key={comm.id}>
                  <div className="nx-discover-head">
                    <strong className="nx-conv-name">
                      {comm.name}
                      {comm.isOfficial && <Badge variant="role">Official</Badge>}
                    </strong>
                    <span className="nx-muted">{comm.memberCount} {comm.memberCount === 1 ? 'member' : 'members'}</span>
                  </div>
                  {organizations.length > 1 && (
                    <div className="nx-muted" style={{ fontSize: 12 }}>{comm.organizationName}</div>
                  )}
                  {comm.description && <p className="nx-discover-desc">{comm.description}</p>}
                  <div className="nx-discover-actions">
                    {comm.isMember ? (
                      <Button size="sm" variant="default" onClick={() => selectCommunityMainFeed(comm)}>
                        Open
                      </Button>
                    ) : (
                      <Button size="sm" variant="primary" onClick={() => void handleJoinCommunity(comm.organizationId, comm.id)}>
                        Join
                      </Button>
                    )}
                  </div>
                </div>
              ))
            )
          ) : communitiesPage.loading && communities.length === 0 ? (
            <div className="nx-empty-side" role="status">Loading communities…</div>
          ) : workspaceGroups.length === 0 && favoriteCommunities.length === 0 && hiddenCommunities.length === 0 && !communitiesPage.error ? (
            <div className="nx-empty-side">No matching workspaces or communities.</div>
          ) : (
            <div className="nx-ws-list">
              {sidebarOrg.error && (
                <div className="nx-empty-side">
                  <p className="nx-error" role="alert">{sidebarOrg.error}</p>
                </div>
              )}
              {favoriteCommunities.length > 0 && (
                <section className="nx-ws-group" aria-label="Favorite communities">
                  <div className="nx-section-label">Favorites ({favoriteCommunities.length})</div>
                  {favoriteCommunities.map(comm => renderCommunityTree(comm, favoriteCommunities))}
                </section>
              )}
              {customSectionGroups.map(({ section, items }) => (
                <section className="nx-ws-group" key={section.id} aria-label={section.name}>
                  <SidebarSectionHeader
                    section={section}
                    count={items.length}
                    onRename={sec => setSectionModal({ mode: 'rename', section: sec })}
                    onDelete={sec => void sidebarOrg.deleteSection(sec.id)}
                  />
                  {items.length === 0 ? (
                    <div className="nx-ws-empty">No communities in this section yet.</div>
                  ) : (
                    items.map(comm => renderCommunityTree(comm, items))
                  )}
                </section>
              ))}
              {workspaceGroups.map(({ org, allCount, communities: orgComms }) => {
                const isCollapsed = Boolean(collapsedWorkspaces[org.id]) && !normalizedFilter;
                const canAdminWorkspace = org.isOwner || org.role === 'SystemAdmin';
                return (
                  <section className="nx-ws-group" key={org.id} aria-label={org.name}>
                    <div className="nx-ws-header">
                      <button
                        type="button"
                        className="nx-ws-toggle"
                        aria-expanded={!isCollapsed}
                        onClick={() =>
                          setCollapsedWorkspaces(prev => ({ ...prev, [org.id]: !prev[org.id] }))
                        }
                      >
                        {isCollapsed ? (
                          <ChevronRight size={14} aria-hidden="true" />
                        ) : (
                          <ChevronDown size={14} aria-hidden="true" />
                        )}
                        <span className="nx-ws-name">{org.name}</span>
                      </button>
                      <div className="nx-ws-actions">
                        {canAdminWorkspace && (
                          <Button
                            variant="ghost"
                            size="sm"
                            iconOnly
                            aria-label={`${org.name} workspace settings`}
                            title="Workspace settings"
                            onClick={() => onOpenWorkspaceSettings(org)}
                          >
                            <Settings size={14} aria-hidden="true" />
                          </Button>
                        )}
                        <Button
                          variant="ghost"
                          size="sm"
                          iconOnly
                          aria-label={`Create community in ${org.name}`}
                          title="Create community"
                          onClick={() => openCreateCommunity(org.id)}
                        >
                          <Plus size={14} aria-hidden="true" />
                        </Button>
                      </div>
                    </div>

                    {!isCollapsed && (
                      orgComms.length === 0 ? (
                        <div className="nx-ws-empty">
                          {allCount === 0 ? 'No communities yet.' : 'Organized in Favorites or sections.'}
                        </div>
                      ) : (
                        orgComms.map(comm => renderCommunityTree(comm, orgComms))
                      )
                    )}
                  </section>
                );
              })}
              {hiddenCommunities.length > 0 && (
                <div className="nx-hidden-group">
                  <button
                    type="button"
                    className="nx-hidden-toggle"
                    aria-expanded={showHiddenComms}
                    onClick={() => setShowHiddenComms(v => !v)}
                  >
                    {showHiddenComms ? <ChevronDown size={13} aria-hidden="true" /> : <ChevronRight size={13} aria-hidden="true" />}
                    <Eye size={13} aria-hidden="true" />
                    <span>Hidden ({hiddenCommunities.length})</span>
                  </button>
                  {showHiddenComms && hiddenCommunities.map(comm => renderCommunityTree(comm, hiddenCommunities))}
                </div>
              )}
            </div>
          )}
          <ListPaging loading={sidebarPage.loading} error={sidebarPage.error} hasMore={sidebarPage.next !== null}
            onMore={sidebarPage.loadMore} onRetry={sidebarPage.retry} moreLabel="Load more communities" />
        </div>

        <div className="nx-ws-footer">
          <Button variant="default" size="sm" onClick={onOpenCreateWorkspace}>
            <Plus size={14} aria-hidden="true" /> Create workspace
          </Button>
          <Button variant="ghost" size="sm" onClick={onOpenJoinWorkspace}>
            Join
          </Button>
        </div>
      </aside>

      {/* Main Feed Stage */}
      <section className="nx-chat-stage" aria-label="Community feed">
        {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        {notice && <ToastBar variant="ok" message={notice} onDismiss={() => setNotice('')} />}

        {!selectedCommunity || !selectedConversationId ? (
          organizations.length === 0 ? (
            <EmptyState
              icon={<Users size={36} aria-hidden="true" />}
              title="Workspaces and Communities"
              description="Create a workspace for your company or team, or join one with an invitation link to collaborate in shared and private communities."
              actions={
                <div className="nx-inline-actions">
                  <Button variant="primary" onClick={onOpenCreateWorkspace}>
                    <Plus size={15} aria-hidden="true" /> Create workspace
                  </Button>
                  <Button variant="default" onClick={onOpenJoinWorkspace}>
                    Join a workspace
                  </Button>
                </div>
              }
            />
          ) : discoverMode ? (
            <EmptyState
              icon={<Compass size={36} aria-hidden="true" />}
              title={
                filteredDiscover.length === 0
                  ? 'No shared communities yet'
                  : 'Browse shared communities'
              }
              description={
                filteredDiscover.length === 0
                  ? 'Shared communities across your workspaces will appear here.'
                  : 'Select a community from the list on the left to join or open its feed.'
              }
              actions={
                filteredDiscover.length === 0 ? (
                  <Button variant="primary" onClick={() => openCreateCommunity()}>
                    <Plus size={15} aria-hidden="true" /> Create community
                  </Button>
                ) : undefined
              }
            />
          ) : (
            <EmptyState
              icon={<Users size={36} aria-hidden="true" />}
              title="Welcome to Nexus Communities"
              description="Create a community for your team or department to share updates, announcements with read acknowledgments, and quick polls."
              actions={
                <Button variant="primary" onClick={() => openCreateCommunity()}>
                  <Plus size={15} aria-hidden="true" /> Create community
                </Button>
              }
            />
          )
        ) : (
          <>
            <header
              ref={stageHeaderRef}
              tabIndex={-1}
              className={`nx-stage-header nx-community-stage-header ${highlightedHeader ? 'nx-highlight-target' : ''}`}
            >
              <div className="nx-stage-title">
                <Button
                  variant="ghost"
                  size="sm"
                  iconOnly
                  className="nx-mobile-back"
                  aria-label="Back to communities"
                  onClick={() => setSelectedConversationId(null)}
                >
                  <ArrowLeft size={16} aria-hidden="true" />
                </Button>
                <span className="nx-comm-emblem sm" aria-hidden="true">
                  {selectedCommunity.name.trim().slice(0, 2).toUpperCase()}
                </span>
                <div style={{ minWidth: 0 }}>
                  <h3>
                    <span>{selectedCommunity.name}</span>
                    {selectedSubChannel && (
                      <span className="nx-stage-channel-sep">
                        / {selectedSubChannel.postingPolicy === 'AdminsOnly' ? <Megaphone size={14} aria-hidden="true" /> : <Hash size={14} aria-hidden="true" />} {selectedSubChannel.name}
                      </span>
                    )}
                    {activeIsArchived && (
                      <Badge variant="role">
                        <Archive size={11} aria-hidden="true" /> Archived
                      </Badge>
                    )}
                    {selectedCommunity.isOfficial && <Badge variant="role">Official</Badge>}
                    {activePolicy === 'AdminsOnly' && <Badge variant="role">Admins only</Badge>}
                  </h3>
                  <div className="nx-stage-subtitle">
                    <span>{selectedCommunity.organizationName}</span>
                    <span>·</span>
                    <span>{selectedCommunity.visibility === 'Private' ? 'Private Community' : 'Shared Community'}</span>
                    <span>·</span>
                    <span>Your role: {selectedCommunity.myRole ?? 'Member'}</span>
                    <span>·</span>
                    <span>{selectedCommunity.memberCount} {selectedCommunity.memberCount === 1 ? 'member' : 'members'}</span>
                    {!selectedSubChannel && selectedCommunity.description && (
                      <>
                        <span>·</span>
                        <span>{selectedCommunity.description}</span>
                      </>
                    )}
                  </div>
                  {selectedSubChannel && activeChannelDescription && (
                    <div className="nx-channel-desc">{activeChannelDescription}</div>
                  )}
                </div>
              </div>

              <div className="nx-inline-actions">
                <ConversationNotificationButton key={selectedConversationId} conversationId={selectedConversationId} />
                {canManage && !selectedSubChannel && (
                  <Button
                    variant="ghost"
                    size="sm"
                    iconOnly
                    aria-label="Edit community"
                    title="Edit community"
                    onClick={openEditCommunityModal}
                  >
                    <Pencil size={15} aria-hidden="true" />
                  </Button>
                )}
                {canManage && selectedSubChannel && !activeIsArchived && (
                  <>
                    <Button
                      variant="ghost"
                      size="sm"
                      iconOnly
                      aria-label="Edit channel"
                      title="Edit channel"
                      onClick={openEditChannelModal}
                    >
                      <Pencil size={15} aria-hidden="true" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      iconOnly
                      aria-label="Archive channel"
                      title="Archive channel"
                      onClick={() => setArchiveConfirmOpen(true)}
                    >
                      <Archive size={15} aria-hidden="true" />
                    </Button>
                  </>
                )}
                {canManage && selectedSubChannel && activeIsArchived && (
                  <Button
                    variant="primary"
                    size="sm"
                    iconOnly
                    aria-label="Restore channel"
                    title="Restore channel"
                    disabled={savingMeta}
                    onClick={() => void handleRestoreChannel()}
                  >
                    <RotateCcw size={15} aria-hidden="true" />
                  </Button>
                )}
                <Button
                  variant="ghost"
                  size="sm"
                  iconOnly
                  aria-label="Copy link to channel"
                  title="Copy link"
                  onClick={() => void handleCopyChannelLink()}
                >
                  <Link2 size={15} aria-hidden="true" />
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  iconOnly
                  aria-label="Refresh feed"
                  title="Refresh feed"
                  onClick={() => void loadPosts(selectedConversationId)}
                >
                  <RefreshCw size={16} aria-hidden="true" />
                </Button>
                <span ref={filesBtnWrapRef}>
                  <Button
                    variant={filesOpen ? 'primary' : 'default'}
                    size="sm"
                    aria-label="Channel files"
                    title="Files"
                    onClick={() => {
                      const next = filesOpen ? 'none' : 'files';
                      setRightDrawer(next);
                      if (next === 'files' && selectedCommunity && selectedConversationId) {
                        void loadChannelFiles(selectedCommunity.organizationId, selectedConversationId, false);
                      }
                    }}
                  >
                    <FolderOpen size={15} aria-hidden="true" />
                    <span className="nx-stage-action-label">Files</span>
                  </Button>
                </span>
                <Button
                  variant={membersOpen ? 'primary' : 'default'}
                  size="sm"
                  aria-label="Community members"
                  title="Members"
                  onClick={() => {
                    setMembersOpen(v => !v);
                  }}
                >
                  <Users size={15} aria-hidden="true" />
                  <span className="nx-stage-action-label">Members</span>
                </Button>
              </div>
            </header>

            <div className="nx-comm-body">
              <div className="nx-comm-feed" ref={feedContainerRef}>
                {/* Post Composer or Read-Only / Archived Banner */}
                {activeIsArchived ? (
                  <div className="nx-readonly-banner nx-archived-banner" role="note">
                    <div className="nx-archived-banner-text">
                      <Archive size={15} aria-hidden="true" />
                      <span>This channel is archived and read-only. Past posts and files remain available.</span>
                    </div>
                    {canManage && selectedSubChannel && (
                      <Button
                        variant="primary"
                        size="sm"
                        disabled={savingMeta}
                        onClick={() => void handleRestoreChannel()}
                      >
                        <RotateCcw size={14} aria-hidden="true" /> {savingMeta ? 'Restoring…' : 'Restore channel'}
                      </Button>
                    )}
                  </div>
                ) : canPost ? (
                  <form
                    className={`nx-post-composer ${isComposerDragOver ? 'drag-over' : ''}`}
                    onSubmit={handleCreatePost}
                    onDragOver={handlePostComposerDragOver}
                    onDragLeave={() => setIsComposerDragOver(false)}
                    onDrop={handlePostComposerDrop}
                  >
                    <input
                      ref={postFileInputRef}
                      type="file"
                      multiple
                      style={{ display: 'none' }}
                      onChange={handlePostFileInputChange}
                    />
                    <input
                      type="text"
                      className="nx-post-title-input"
                      aria-label="Post title (optional)"
                      placeholder="Add a title (optional)…"
                      maxLength={200}
                      value={postTitle}
                      onChange={e => setPostTitle(e.target.value)}
                    />
                    <ComposerToolbar
                      key={selectedConversationId}
                      textareaRef={postBodyRef}
                      value={postBody}
                      onChange={setPostBody}
                      disabled={posting}
                      placement="bottom"
                      openLinkSignal={postLinkSignal}
                      ariaLabel="Post formatting"
                    />
                    <textarea
                      ref={postBodyRef}
                      className="nx-post-body-input"
                      aria-label="Post content"
                      rows={3}
                      placeholder={
                        selectedSubChannel
                          ? `Start a post in #${selectedSubChannel.name} (Markdown supported)…`
                          : `Share an update with ${selectedCommunity.name} (Markdown supported)…`
                      }
                      value={postBody}
                      onChange={e => setPostBody(e.target.value)}
                      onPaste={handlePostComposerPaste}
                      onCompositionStart={() => { postCompositionActive.current = true; }}
                      onCompositionEnd={() => { postCompositionActive.current = false; }}
                      onBlur={() => { postCompositionActive.current = false; }}
                      onKeyDown={e => {
                        if (
                          handleComposerFormattingKeyDown(
                            e,
                            postCompositionActive.current,
                            postBody,
                            setPostBody,
                            () => setPostLinkSignal(n => n + 1)
                          )
                        ) {
                          return;
                        }
                        if (
                          !postCompositionActive.current &&
                          !e.nativeEvent.isComposing &&
                          e.nativeEvent.keyCode !== 229 &&
                          (e.ctrlKey || e.metaKey) &&
                          e.key === 'Enter' &&
                          (postBody.trim() || pendingAttachments.length > 0) &&
                          !posting &&
                          !uploadingFiles
                        ) {
                          e.preventDefault();
                          e.currentTarget.form?.requestSubmit();
                        }
                      }}
                      required={pendingAttachments.length === 0}
                    />

                    {quotaWarning && (
                      <p className="nx-draft-notice" role="status">{quotaWarning}</p>
                    )}

                    {(pendingAttachments.length > 0 || uploadingFiles) && (
                      <div className="nx-pending-attachments" aria-label="Pending post attachments">
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
                              onClick={() => setPendingAttachments(prev => prev.filter(item => item.id !== att.id))}
                            >
                              <X size={13} aria-hidden="true" />
                            </Button>
                          </div>
                        ))}
                        {uploadingFiles && (
                          <span className="nx-muted" role="status" style={{ alignSelf: 'center' }}>
                            Uploading…
                          </span>
                        )}
                      </div>
                    )}

                    {includePoll && (
                      <div className="nx-poll-builder">
                        <div className="nx-poll-builder-head">
                          <strong>Embedded poll</strong>
                          <Button type="button" variant="ghost" size="sm" onClick={() => setIncludePoll(false)}>
                            Remove poll
                          </Button>
                        </div>
                        <input
                          type="text"
                          aria-label="Poll question"
                          placeholder="Poll question…"
                          maxLength={240}
                          value={pollQuestion}
                          onChange={e => setPollQuestion(e.target.value)}
                          required
                        />
                        <div className="nx-poll-builder-opts">
                          {pollOptions.map((opt, idx) => (
                            <div className="nx-poll-opt-row" key={idx}>
                              <input
                                type="text"
                                aria-label={`Poll option ${idx + 1}`}
                                placeholder={`Option ${idx + 1}`}
                                maxLength={120}
                                value={opt}
                                onChange={e => {
                                  const next = [...pollOptions];
                                  next[idx] = e.target.value;
                                  setPollOptions(next);
                                }}
                                required={idx < 2}
                              />
                              {pollOptions.length > 2 && (
                                <Button
                                  type="button"
                                  variant="ghost"
                                  size="sm"
                                  iconOnly
                                  aria-label={`Remove option ${idx + 1}`}
                                  onClick={() => setPollOptions(prev => prev.filter((_, i) => i !== idx))}
                                >
                                  <Trash2 size={14} aria-hidden="true" />
                                </Button>
                              )}
                            </div>
                          ))}
                        </div>
                        <div className="nx-poll-builder-foot">
                          {pollOptions.length < 6 && (
                            <Button
                              type="button"
                              variant="default"
                              size="sm"
                              onClick={() => setPollOptions(prev => [...prev, ''])}
                            >
                              <Plus size={14} aria-hidden="true" /> Add option
                            </Button>
                          )}
                          <label className="nx-check-inline">
                            <input
                              type="checkbox"
                              checked={pollAllowMultiple}
                              onChange={e => setPollAllowMultiple(e.target.checked)}
                            />
                            <span>Allow multiple choices</span>
                          </label>
                        </div>
                      </div>
                    )}

                    <div className="nx-post-composer-foot">
                      <div className="nx-post-composer-toggles">
                        {canManage && (
                          <>
                            <label className="nx-check-inline">
                              <input
                                type="checkbox"
                                checked={isAnnouncement}
                                onChange={e => {
                                  setIsAnnouncement(e.target.checked);
                                  if (!e.target.checked) setRequiresAck(false);
                                }}
                              />
                              <span>Announcement</span>
                            </label>
                            {isAnnouncement && (
                              <label className="nx-check-inline">
                                <input
                                  type="checkbox"
                                  checked={requiresAck}
                                  onChange={e => setRequiresAck(e.target.checked)}
                                />
                                <span>Require acknowledgment</span>
                              </label>
                            )}
                          </>
                        )}
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          disabled={posting || uploadingFiles || pendingAttachments.length >= 10}
                          onClick={() => postFileInputRef.current?.click()}
                        >
                          <Paperclip size={14} aria-hidden="true" /> Attach
                        </Button>
                        <Button
                          type="button"
                          variant={includePoll ? 'primary' : 'ghost'}
                          size="sm"
                          onClick={() => setIncludePoll(v => !v)}
                        >
                          <BarChart2 size={14} aria-hidden="true" /> Poll
                        </Button>
                      </div>

                      <Button
                        type="submit"
                        variant="primary"
                        size="sm"
                        disabled={posting || uploadingFiles || (!postBody.trim() && pendingAttachments.length === 0)}
                      >
                        <Send size={14} aria-hidden="true" /> {posting ? 'Posting…' : uploadingFiles ? 'Uploading…' : 'Post'}
                      </Button>
                    </div>
                  </form>
                ) : !loadingPosts ? (
                  <div className="nx-readonly-banner" role="note">
                    <Megaphone size={15} aria-hidden="true" />
                    <span>Only community owners and admins can publish new posts in this channel. You can still react, comment, and acknowledge announcements.</span>
                  </div>
                ) : null}

                {/* Posts List */}
                {postsHasNewer && selectedConversationId && (
                  <div className="nx-jump-banner" role="region" aria-label="Viewing older posts">
                    <Button size="sm" variant="primary" onClick={() => void loadPosts(selectedConversationId, false, selectedCommunity.organizationId)}>
                      Jump to latest posts
                    </Button>
                  </div>
                )}
                {loadingPosts && posts.length === 0 ? (
                  <div className="nx-empty-side" role="status">Loading posts…</div>
                ) : posts.length === 0 && !postsError ? (
                  <div className="nx-empty-side">
                    {canPost ? 'No posts yet. Start the first conversation above.' : 'No posts in this channel yet.'}
                  </div>
                ) : (
                  posts.map(post => {
                    const commentsOpen = Boolean(expandedComments[post.id]) || post.comments.length > 0;
                    const reactionMap = new Map(post.reactions.map(r => [r.emoji, r]));

                    return (
                      <article
                        key={post.id}
                        data-post-id={post.id}
                        tabIndex={-1}
                        className={`nx-post-card ${post.isAnnouncement ? 'announcement' : ''} ${post.isPinned ? 'pinned' : ''} ${post.pending ? 'pending' : ''} ${highlightedPostId === post.id ? 'nx-highlight-target' : ''}`}
                      >
                        <header className="nx-post-head">
                          <div className="nx-post-author">
                            <button
                              type="button"
                              className="nx-profile-trigger nx-avatar-trigger"
                              aria-label={`View profile for ${post.authorDisplayName}`}
                              onClick={() =>
                                onOpenProfileCard({
                                  userId: post.authorUserId,
                                  organizationId: selectedCommunity.organizationId,
                                  fallbackDisplayName: post.authorDisplayName,
                                  fallbackAvatarUrl: post.authorAvatarUrl
                                })
                              }
                            >
                              <Avatar
                                displayName={post.authorDisplayName}
                                avatarUrl={post.authorAvatarUrl}
                                size="sm"
                                presence={availabilityMap[post.authorUserId]
                                  ? resolveUserAvailability(post.authorUserId, {}, availabilityMap, nowMs).dotTone
                                  : undefined}
                              />
                            </button>
                            <div>
                              <div className="nx-post-author-row">
                                <button
                                  type="button"
                                  className="nx-profile-trigger nx-name-trigger"
                                  onClick={() =>
                                    onOpenProfileCard({
                                      userId: post.authorUserId,
                                      organizationId: selectedCommunity.organizationId,
                                      fallbackDisplayName: post.authorDisplayName,
                                      fallbackAvatarUrl: post.authorAvatarUrl
                                    })
                                  }
                                >
                                  <strong>{post.authorDisplayName}</strong>
                                </button>
                                {post.pending
                                  ? <span className="nx-pending-label">Publishing…</span>
                                  : <span className="nx-msg-time">{formatPostTime(post.createdAt)}</span>}
                              </div>
                            </div>
                          </div>

                          <div className="nx-post-badges">
                            {post.isPinned && (
                              <Badge variant="role">
                                <Pin size={11} aria-hidden="true" /> Pinned
                              </Badge>
                            )}
                            {post.isAnnouncement && (
                              <Badge variant="role">
                                <Megaphone size={11} aria-hidden="true" /> Announcement
                              </Badge>
                            )}
                            {!post.pending && (
                              <Button
                                variant="ghost"
                                size="sm"
                                iconOnly
                                aria-label="Copy link to post"
                                title="Copy link"
                                onClick={() => void handleCopyPostLink(post)}
                              >
                                <Link2 size={14} aria-hidden="true" />
                              </Button>
                            )}
                            {canManage && !activeIsArchived && (
                              <Button
                                variant="ghost"
                                size="sm"
                                iconOnly
                                aria-label={post.isPinned ? 'Unpin post' : 'Pin post'}
                                title={post.isPinned ? 'Unpin post' : 'Pin post'}
                                onClick={() => void handleTogglePin(post)}
                              >
                                <Pin size={14} aria-hidden="true" />
                              </Button>
                            )}
                          </div>
                        </header>

                        {post.title && <h4 className="nx-post-title">{post.title}</h4>}

                        {post.body && (
                          <div
                            className="nx-msg-body nx-post-content"
                            onClick={handleInlineContentLinkClick}
                            dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(post.body) }}
                          />
                        )}

                        {/* Post Attachments (M6.31) */}
                        {post.attachments && post.attachments.length > 0 && (
                          <div className="nx-msg-attachments">
                            {post.attachments.map(att =>
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
                            )}
                          </div>
                        )}

                        {/* Announcement Acknowledgment Bar */}
                        {post.requiresAcknowledgment && (
                          <div className="nx-ack-bar">
                            <div className="nx-ack-status">
                              {post.acknowledgedByMe ? (
                                <Badge variant="success">
                                  <CheckCircle2 size={12} aria-hidden="true" /> Acknowledged
                                </Badge>
                              ) : !activeIsArchived ? (
                                <Button variant="primary" size="sm" onClick={() => void handleAcknowledge(post.id)}>
                                  <CheckCircle2 size={14} aria-hidden="true" /> Acknowledge
                                </Button>
                              ) : null}
                              <span className="nx-muted">
                                {post.acknowledgedCount} / {post.totalRecipients} acknowledged
                              </span>
                            </div>

                            {canManage && (
                              <div className="nx-inline-actions">
                                <a
                                  className="nx-btn nx-btn-sm"
                                  href={orgApiPath(selectedCommunity.organizationId, `/api/posts/${post.id}/acknowledgments?format=csv`)}
                                  download
                                >
                                  <Download size={13} aria-hidden="true" /> Export CSV
                                </a>
                                {!activeIsArchived && (
                                  <Button variant="default" size="sm" onClick={() => void handleRemind(post.id)}>
                                    <Bell size={13} aria-hidden="true" /> Remind
                                  </Button>
                                )}
                              </div>
                            )}
                          </div>
                        )}

                        {/* Embedded Poll */}
                        {post.poll && (
                          <div className="nx-poll-card">
                            <div className="nx-poll-head">
                              <strong>{post.poll.question}</strong>
                              <span className="nx-muted">
                                {post.poll.allowMultiple ? 'Multiple choices' : 'Single choice'} · {post.poll.totalVoters}{' '}
                                {post.poll.totalVoters === 1 ? 'voter' : 'voters'}
                              </span>
                            </div>
                            <div className="nx-poll-options">
                              {post.poll.options.map(opt => {
                                const pct =
                                  post.poll && post.poll.totalVoters > 0
                                    ? Math.round((opt.votes / post.poll.totalVoters) * 100)
                                    : 0;
                                return (
                                  <button
                                    type="button"
                                    key={opt.id}
                                    className={`nx-poll-opt-btn ${opt.votedByMe ? 'voted' : ''}`}
                                    disabled={activeIsArchived}
                                    onClick={() => void handleVotePoll(post.poll!, opt.id)}
                                  >
                                    <span className="nx-poll-opt-fill" style={{ width: `${pct}%` }} />
                                    <span className="nx-poll-opt-label">
                                      {opt.votedByMe && <Check size={13} aria-hidden="true" />}
                                      <span>{opt.label}</span>
                                    </span>
                                    <span className="nx-poll-opt-stat">
                                      {opt.votes} ({pct}%)
                                    </span>
                                  </button>
                                );
                              })}
                            </div>
                          </div>
                        )}

                        {/* Reactions & Comment Toggle */}
                        <div className="nx-post-actions">
                          <div className="nx-reactions-row">
                            {QUICK_REACTIONS.map(emoji => {
                              const pending = pendingReactions[`${post.id}:${emoji}`];
                              const r = pending
                                ? applyReaction(post, pending).reactions.find(reaction => reaction.emoji === emoji)
                                : reactionMap.get(emoji);
                              return (
                                <button
                                  type="button"
                                  key={emoji}
                                  className={`nx-react-pill ${r?.reactedByMe ? 'active' : ''}`}
                                  disabled={activeIsArchived}
                                  onClick={() => void handleToggleReaction(post, emoji)}
                                  aria-label={`React with ${emoji}`}
                                  aria-pressed={Boolean(r?.reactedByMe)}
                                  aria-disabled={activeIsArchived || Boolean(pending)}
                                >
                                  <span>{emoji}</span>
                                  {r && r.count > 0 && <span>{r.count}</span>}
                                </button>
                              );
                            })}
                          </div>

                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() =>
                              setExpandedComments(prev => ({ ...prev, [post.id]: !commentsOpen }))
                            }
                          >
                            <MessageSquare size={14} aria-hidden="true" />{' '}
                            {post.comments.length === 0
                              ? 'Comment'
                              : `${post.comments.length} ${post.comments.length === 1 ? 'comment' : 'comments'}`}
                          </Button>
                        </div>

                        {/* Comments Section */}
                        {commentsOpen && (
                          <div className="nx-comments-box">
                            {post.comments.map(c => (
                              <div className={`nx-comment-item ${c.pending ? 'pending' : ''}`} key={c.id}>
                                <div className="nx-comment-head">
                                  <button
                                    type="button"
                                    className="nx-profile-trigger nx-name-trigger"
                                    onClick={() =>
                                      onOpenProfileCard({
                                        userId: c.authorUserId,
                                        organizationId: selectedCommunity.organizationId,
                                        fallbackDisplayName: c.authorDisplayName
                                      })
                                    }
                                  >
                                    <strong>{c.authorDisplayName}</strong>
                                  </button>
                                  {c.pending
                                    ? <span className="nx-pending-label">Sending…</span>
                                    : <span className="nx-msg-time">{formatPostTime(c.createdAt)}</span>}
                                </div>
                                <div
                                  className="nx-msg-body"
                                  onClick={handleInlineContentLinkClick}
                                  dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(c.body) }}
                                />
                              </div>
                            ))}

                            {!activeIsArchived && (
                              <form className="nx-comment-form" onSubmit={e => void handleAddComment(e, post.id)}>
                                <input
                                  type="text"
                                  aria-label="Write a comment"
                                  placeholder="Write a comment…"
                                  maxLength={4000}
                                  value={commentDrafts[post.id] ?? ''}
                                  onChange={e =>
                                    setCommentDrafts(prev => ({ ...prev, [post.id]: e.target.value }))
                                  }
                                />
                                <Button
                                  type="submit"
                                  variant="default"
                                  size="sm"
                                  disabled={!(commentDrafts[post.id] ?? '').trim()}
                                >
                                  Reply
                                </Button>
                              </form>
                            )}
                          </div>
                        )}
                      </article>
                    );
                  })
                )}
                <ListPaging
                  loading={loadingPosts || loadingMorePosts}
                  error={postsError}
                  hasMore={postsNextCursor !== null}
                  onMore={() => { if (selectedConversationId) void loadPosts(selectedConversationId, false, undefined, true); }}
                  onRetry={() => { if (selectedConversationId) void loadPosts(selectedConversationId, false, undefined, Boolean(postsError?.append)); }}
                  moreLabel="Load older posts"
                />
              </div>

              {/* Channel Files Drawer (M6.31) */}
              {filesOpen && selectedCommunity && selectedConversationId && (
                <aside className="nx-drawer" aria-label="Channel files">
                  <div className="nx-drawer-head">
                    <h4 ref={channelFilesHeadingRef} tabIndex={-1}>
                      {loadingChannelFiles && channelFiles.length === 0 ? 'Files' : `Files (${channelFiles.length})`}
                    </h4>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => {
                        setRightDrawer('none');
                        requestAnimationFrame(() => {
                          filesBtnWrapRef.current?.querySelector('button')?.focus();
                        });
                      }}
                    >
                      Close
                    </Button>
                  </div>

                  <div className="nx-drawer-body">
                    {channelFilesQuota && (
                      <div className="nx-quota-box">
                        <div style={{ display: 'flex', justifyContent: 'space-between', gap: 6 }}>
                          <strong>Channel storage</strong>
                          <span className="nx-muted">
                            {formatBytes(channelFilesQuota.conversationUsedBytes)} / {formatBytes(channelFilesQuota.conversationQuotaBytes)}
                          </span>
                        </div>
                        <div className="nx-quota-bar">
                          <div
                            className="nx-quota-fill"
                            style={{
                              width: `${Math.min(
                                100,
                                Math.round((channelFilesQuota.conversationUsedBytes / Math.max(1, channelFilesQuota.conversationQuotaBytes)) * 100)
                              )}%`
                            }}
                          />
                        </div>
                      </div>
                    )}

                    {loadingChannelFiles && channelFiles.length === 0 ? (
                      <p className="nx-muted" role="status" style={{ textAlign: 'center', marginTop: 18 }}>
                        Loading files…
                      </p>
                    ) : channelFiles.length === 0 && !channelFilesError ? (
                      <p className="nx-muted" style={{ textAlign: 'center', marginTop: 18 }}>
                        No files shared in this channel yet.
                      </p>
                    ) : (
                      <div>
                        {channelFiles.map(f => (
                          <div className="nx-row nx-channel-file-row" key={f.id}>
                            {f.thumbnailUrl ? (
                              <button
                                type="button"
                                className="nx-file-preview-trigger"
                                onClick={() => setLightboxImage(f)}
                                title={`Preview ${f.fileName}`}
                              >
                                <img
                                  src={f.thumbnailUrl}
                                  alt=""
                                  style={{ width: 32, height: 32, borderRadius: 6, objectFit: 'cover', flexShrink: 0 }}
                                />
                                <span style={{ minWidth: 0, flex: 1 }}>
                                  <div className="nx-att-file-title">{f.fileName}</div>
                                  <div className="nx-muted nx-channel-file-sub">
                                    {formatBytes(f.sizeBytes)}
                                    {f.uploaderDisplayName ? ` · ${f.uploaderDisplayName}` : ''}
                                  </div>
                                  {f.postTitle && (
                                    <div className="nx-muted nx-channel-file-sub" title={f.postTitle}>
                                      {f.postTitle}
                                    </div>
                                  )}
                                </span>
                              </button>
                            ) : (
                              <div className="nx-att-file-meta" style={{ flex: 1 }}>
                                <FileText size={18} aria-hidden="true" />
                                <div style={{ minWidth: 0, flex: 1 }}>
                                  <div className="nx-att-file-title">{f.fileName}</div>
                                  <div className="nx-muted nx-channel-file-sub">
                                    {formatBytes(f.sizeBytes)}
                                    {f.uploaderDisplayName ? ` · ${f.uploaderDisplayName}` : ''}
                                  </div>
                                  {f.postTitle && (
                                    <div className="nx-muted nx-channel-file-sub" title={f.postTitle}>
                                      {f.postTitle}
                                    </div>
                                  )}
                                </div>
                              </div>
                            )}
                            <div className="nx-channel-file-actions">
                              {f.sourceLink && onOpenContentLink && (
                                <Button
                                  variant="ghost"
                                  size="sm"
                                  iconOnly
                                  aria-label={`Go to post for ${f.fileName}`}
                                  title="Go to post"
                                  onClick={() => {
                                    if (window.innerWidth <= 860) setRightDrawer('none');
                                    onOpenContentLink(f.sourceLink!);
                                  }}
                                >
                                  <ExternalLink size={13} aria-hidden="true" />
                                </Button>
                              )}
                              <a
                                href={f.downloadUrl}
                                download={f.fileName}
                                className="nx-btn sm"
                                aria-label={`Download ${f.fileName}`}
                                title={`Download ${f.fileName}`}
                              >
                                <Download size={13} aria-hidden="true" />
                              </a>
                            </div>
                          </div>
                        ))}
                      </div>
                    )}

                    {(channelFilesNext || channelFilesError || (loadingChannelFiles && channelFiles.length > 0)) && (
                      <div className="nx-empty-side">
                        {channelFilesError && <p className="nx-error" role="alert">Unable to load files.</p>}
                        {loadingChannelFiles && channelFiles.length > 0 && <span role="status">Loading files…</span>}
                        {!loadingChannelFiles && (
                          <Button
                            size="sm"
                            onClick={() =>
                              void loadChannelFiles(
                                selectedCommunity.organizationId,
                                selectedConversationId,
                                channelFilesError !== 'refresh'
                              )
                            }
                          >
                            {channelFilesError ? 'Retry' : 'Load more files'}
                          </Button>
                        )}
                      </div>
                    )}
                  </div>
                </aside>
              )}

              {/* Members & Governance Drawer */}
              {membersOpen && (
                <aside className="nx-drawer" aria-label="Community members">
                  <div className="nx-drawer-head">
                    <h4 ref={communityMembersHeading} tabIndex={-1}>{loadingMembers ? 'Members' : `Members (${members.length})`}</h4>
                    <Button variant="ghost" size="sm" onClick={() => setMembersOpen(false)}>
                      Close
                    </Button>
                  </div>

                  <div className="nx-drawer-body">
                    {/* Official designation for CommsAdmin */}
                    {activeOrg?.role === 'CommsAdmin' && (
                      <div className="nx-drawer-section">
                        <Button variant="default" size="sm" fullWidth onClick={() => void handleToggleOfficial()}>
                          <ShieldCheck size={14} aria-hidden="true" />{' '}
                          {selectedCommunity.isOfficial ? 'Remove Official status' : 'Designate as Official'}
                        </Button>
                      </div>
                    )}

                    {/* Department sync (CM-02) */}
                    {canManage && departments.length > 0 && (
                      <div className="nx-drawer-section">
                        <Field label="Department membership sync" htmlFor="comm-dept-map" compact>
                          <select
                            id="comm-dept-map"
                            className="nx-select"
                            value={mappedDeptId}
                            onChange={e => setMappedDeptId(e.target.value)}
                          >
                            <option value="">None (manual membership)</option>
                            {departments.map(d => (
                              <option key={d.id} value={d.id}>{d.name}</option>
                            ))}
                          </select>
                        </Field>
                        <Button
                          variant="default"
                          size="sm"
                          fullWidth
                          onClick={() => void handleSyncDepartment()}
                        >
                          Save &amp; sync department
                        </Button>
                      </div>
                    )}

                    {/* Add member */}
                    {canManage && (
                      <form className="nx-drawer-section" onSubmit={handleAddMember}>
                        <Field label="Find people to add" htmlFor="comm-add-search" compact>
                          <input id="comm-add-search" type="search" value={memberSearchQuery} onChange={event => {
                            setMemberSearchQuery(event.target.value);
                            setAddMemberUserId('');
                          }} />
                        </Field>
                        <Field label="Add member" htmlFor="comm-add-member" compact>
                          <select
                            id="comm-add-member"
                            className="nx-select"
                            value={addMemberUserId}
                            onChange={e => setAddMemberUserId(e.target.value)}
                          >
                            <option value="">{addCandidates.loading ? 'Loading people…'
                              : availableToAdd.length ? 'Select a person' : 'No matching people'}</option>
                            {availableToAdd.map(u => (
                              <option key={u.id} value={u.id}>
                                {u.displayName} (@{u.username})
                              </option>
                            ))}
                          </select>
                        </Field>
                        <Button
                          type="submit"
                          variant="primary"
                          size="sm"
                          fullWidth
                          disabled={!addMemberUserId}
                        >
                          <UserPlus size={14} aria-hidden="true" /> Add member
                        </Button>
                        <ListPaging loading={addCandidates.loading} error={addCandidates.error} hasMore={addCandidates.next !== null}
                          onMore={addCandidates.loadMore} onRetry={addCandidates.retry} moreLabel="Load more people to add" />
                      </form>
                    )}

                    {/* Members list */}
                    <div className="nx-drawer-section">
                      {loadingMembers && members.length === 0 ? (
                        <p className="nx-muted" role="status" style={{ textAlign: 'center', margin: '12px 0' }}>
                          Loading members…
                        </p>
                      ) : members.map(m => {
                        const mStatus = resolveUserAvailability(m.userId, m, availabilityMap, nowMs);
                        const openMemberProfile = () =>
                          onOpenProfileCard({
                            userId: m.userId,
                            organizationId: selectedCommunity.organizationId,
                            fallbackDisplayName: m.displayName,
                            fallbackUsername: m.username,
                            fallbackAvatarUrl: m.avatarUrl
                          });
                        return (
                          <div className="nx-member-row nx-managed-member" key={m.userId}>
                            <button
                              type="button"
                              className="nx-profile-trigger nx-avatar-trigger"
                              aria-label={`View profile for ${m.displayName}`}
                              onClick={openMemberProfile}
                            >
                              <Avatar displayName={m.displayName} avatarUrl={m.avatarUrl} size="sm" presence={mStatus.dotTone} />
                            </button>
                            <div style={{ minWidth: 0, flex: 1 }}>
                              <button
                                type="button"
                                className="nx-profile-trigger nx-name-trigger"
                                onClick={openMemberProfile}
                              >
                                <strong>{m.displayName}</strong>
                              </button>
                              <div className="nx-muted nx-inline-status-meta">
                                <span className={`nx-dot ${mStatus.dotTone}`} aria-hidden="true" />
                                <span>{mStatus.label}</span>
                                <span>· @{m.username}</span>
                                <Badge variant="role">{m.role}</Badge>
                              </div>
                            </div>
                            {canManage && m.userId !== currentUserId && m.role !== 'Owner' && (
                              <div className="nx-member-actions">
                                {selectedCommunity.myRole === 'Owner' && m.isActive !== false &&
                                  (!selectedCommunity.isOfficial || !m.isUnverified) && (
                                  <Button variant="ghost" size="sm"
                                    aria-label={`Transfer ownership to ${m.displayName} (@${m.username})`}
                                    onClick={() => setOwnershipTransfer({
                                      path: orgApiPath(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/transfer-ownership`),
                                      resourceLabel: 'Community',
                                      resourceName: selectedCommunity.name,
                                      userId: m.userId,
                                      displayName: m.displayName,
                                      username: m.username
                                    })}
                                  >Transfer ownership</Button>
                                )}
                                {selectedCommunity.myRole === 'Owner' && (
                                  <Button
                                    variant="ghost"
                                    size="sm"
                                    onClick={() => void handleToggleAdmin(m)}
                                  >
                                    {m.role === 'Admin' ? 'Demote' : 'Admin'}
                                  </Button>
                                )}
                                <Button
                                  variant="danger"
                                  size="sm"
                                  onClick={() => void handleRemoveMember(m.userId)}
                                >
                                  Remove
                                </Button>
                              </div>
                            )}
                          </div>
                        );
                      })}
                      <ListPaging loading={loadingMembers} error={memberList.error} hasMore={memberList.next !== null}
                        onMore={memberList.loadMore} onRetry={memberList.retry} moreLabel="Load more members" />
                    </div>

                    {selectedCommunity.myRole !== 'Owner' && (
                      <div className="nx-drawer-section">
                        <Button variant="danger" size="sm" fullWidth onClick={() => void handleLeaveCommunity()}>
                          Leave community
                        </Button>
                      </div>
                    )}
                  </div>
                </aside>
              )}
            </div>
          </>
        )}
      </section>

      {/* Create Community Modal */}
      {ownershipTransfer && selectedCommunity && (
        <TransferOwnershipDialog target={ownershipTransfer} onClose={() => setOwnershipTransfer(null)}
          onRefresh={() => {
            void loadCommunities().catch(() => setError('Unable to refresh community roles.'));
            void loadMembers(selectedCommunity.id);
          }}
          onTransferred={() => {
            setOwnershipTransfer(null);
            void loadCommunities().catch(() => setError('Unable to refresh community roles.'));
            void loadMembers(selectedCommunity.id);
            setNotice(`${ownershipTransfer.displayName} is now Owner of ${ownershipTransfer.resourceName}.`);
            requestAnimationFrame(() => communityMembersHeading.current?.focus());
          }} />
      )}

      <Modal
        open={createOpen}
        onOpenChange={setCreateOpen}
        title="Create community"
        description="Starts with a main community feed. You can add optional channels anytime."
      >
        <form onSubmit={handleCreateCommunity}>
          {organizations.length > 1 && (
            <Field label="Workspace" htmlFor="new-comm-workspace">
              <select
                id="new-comm-workspace"
                className="nx-select"
                value={createOrgId || organizations[0]?.id || ''}
                onChange={e => setCreateOrgId(e.target.value)}
              >
                {organizations.map(org => (
                  <option key={org.id} value={org.id}>{org.name}</option>
                ))}
              </select>
            </Field>
          )}
          <Field label="Community name" htmlFor="new-comm-name">
            <input
              id="new-comm-name"
              type="text"
              required
              maxLength={120}
              placeholder="e.g. Engineering, Company Announcements"
              value={newCommName}
              onChange={e => setNewCommName(e.target.value)}
            />
          </Field>
          <Field label="Description (optional)" htmlFor="new-comm-desc">
            <textarea
              id="new-comm-desc"
              rows={2}
              maxLength={500}
              placeholder="What is this community for?"
              value={newCommDesc}
              onChange={e => setNewCommDesc(e.target.value)}
            />
          </Field>
          <fieldset className="nx-vis-fieldset">
            <legend>Community access</legend>
            <label className={`nx-vis-option ${newCommVisibility === 'Public' ? 'selected' : ''}`}>
              <input
                type="radio"
                name="community-visibility"
                value="Public"
                checked={newCommVisibility === 'Public'}
                onChange={() => setNewCommVisibility('Public')}
              />
              <div>
                <strong>Shared Community</strong>
                <p>Any active member of this workspace can browse and join. Official communities require a confirmed employee profile.</p>
              </div>
            </label>
            <label className={`nx-vis-option ${newCommVisibility === 'Private' ? 'selected' : ''}`}>
              <input
                type="radio"
                name="community-visibility"
                value="Private"
                checked={newCommVisibility === 'Private'}
                onChange={() => setNewCommVisibility('Private')}
              />
              <div>
                <strong>Private Community</strong>
                <p>Hidden from Browse communities. Only people added by a community owner or admin can see and join.</p>
              </div>
            </label>
          </fieldset>
          <div className="nx-modal-footer">
            <Button type="button" variant="default" onClick={() => setCreateOpen(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!newCommName.trim()}>
              Create community
            </Button>
          </div>
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
      {/* Add Channel Modal */}
      <Modal
        open={Boolean(addChannelForId)}
        onOpenChange={open => {
          if (!open) setAddChannelForId(null);
        }}
        title="Add channel"
        description="Channels inherit this community's members and roles automatically."
      >
        <form onSubmit={handleCreateChannel}>
          <Field label="Channel name" htmlFor="new-ch-name">
            <input
              id="new-ch-name"
              type="text"
              required
              maxLength={120}
              placeholder="e.g. announcements, releases, qa"
              value={newChannelName}
              onChange={e => setNewChannelName(e.target.value)}
            />
          </Field>
          <Field label="Description (optional)" htmlFor="new-ch-desc">
            <textarea
              id="new-ch-desc"
              rows={2}
              maxLength={500}
              placeholder="What is this channel for?"
              value={newChannelDesc}
              onChange={e => setNewChannelDesc(e.target.value)}
            />
          </Field>
          <Field label="Posting policy" htmlFor="new-ch-policy">
            <select
              id="new-ch-policy"
              className="nx-select"
              value={newChannelPolicy}
              onChange={e => setNewChannelPolicy(e.target.value as 'Members' | 'AdminsOnly')}
            >
              <option value="Members">Members — Everyone in the community can post</option>
              <option value="AdminsOnly">Admins only — Only owners and admins can publish posts</option>
            </select>
          </Field>
          <div className="nx-modal-footer">
            <Button type="button" variant="default" onClick={() => setAddChannelForId(null)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!newChannelName.trim()}>
              Create channel
            </Button>
          </div>
        </form>
      </Modal>

      {/* Edit Community Modal (M6.30) */}
      <Modal
        open={editCommOpen && Boolean(selectedCommunity)}
        onOpenChange={open => {
          if (!open && !savingMeta) setEditCommOpen(false);
        }}
        title="Edit community"
        description="Update the community name and description."
      >
        <form onSubmit={handleSaveCommunityMeta}>
          <Field label="Community name" htmlFor="edit-comm-name">
            <input
              id="edit-comm-name"
              type="text"
              required
              minLength={1}
              maxLength={120}
              value={editCommName}
              onChange={e => setEditCommName(e.target.value)}
            />
          </Field>
          <Field label="Description (optional)" htmlFor="edit-comm-desc">
            <textarea
              id="edit-comm-desc"
              rows={3}
              maxLength={500}
              placeholder="Describe what this community is for…"
              value={editCommDesc}
              onChange={e => setEditCommDesc(e.target.value)}
            />
          </Field>
          <div className="nx-modal-footer">
            <Button type="button" variant="default" disabled={savingMeta} onClick={() => setEditCommOpen(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={savingMeta || !editCommName.trim()}>
              {savingMeta ? 'Saving…' : 'Save changes'}
            </Button>
          </div>
        </form>
      </Modal>

      {/* Edit Channel Modal (M6.30) */}
      <Modal
        open={editChannelOpen && Boolean(selectedSubChannel)}
        onOpenChange={open => {
          if (!open && !savingMeta) setEditChannelOpen(false);
        }}
        title="Edit channel"
        description="Update the channel name and description without changing its link or history."
      >
        <form onSubmit={handleSaveChannelMeta}>
          <Field label="Channel name" htmlFor="edit-ch-name">
            <input
              id="edit-ch-name"
              type="text"
              required
              minLength={1}
              maxLength={120}
              value={editChannelName}
              onChange={e => setEditChannelName(e.target.value)}
            />
          </Field>
          <Field label="Description (optional)" htmlFor="edit-ch-desc">
            <textarea
              id="edit-ch-desc"
              rows={3}
              maxLength={500}
              placeholder="Describe what this channel is for…"
              value={editChannelDesc}
              onChange={e => setEditChannelDesc(e.target.value)}
            />
          </Field>
          <div className="nx-modal-footer">
            <Button type="button" variant="default" disabled={savingMeta} onClick={() => setEditChannelOpen(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={savingMeta || !editChannelName.trim()}>
              {savingMeta ? 'Saving…' : 'Save changes'}
            </Button>
          </div>
        </form>
      </Modal>

      {/* Archive Channel Confirmation Modal (M6.30) */}
      <Modal
        open={archiveConfirmOpen && Boolean(selectedSubChannel)}
        onOpenChange={open => {
          if (!open && !savingMeta) setArchiveConfirmOpen(false);
        }}
        title={selectedSubChannel ? `Archive #${selectedSubChannel.name}?` : 'Archive channel'}
        footer={
          <>
            <Button disabled={savingMeta} onClick={() => setArchiveConfirmOpen(false)}>
              Cancel
            </Button>
            <Button variant="danger" disabled={savingMeta} onClick={() => void handleArchiveChannel()}>
              <Archive size={14} aria-hidden="true" /> {savingMeta ? 'Archiving…' : 'Archive channel'}
            </Button>
          </>
        }
      >
        <p className="nx-muted" style={{ margin: 0 }}>
          Archiving this channel makes it read-only. Its ID, past posts, comments, polls, and files remain preserved, and an owner or admin can restore it anytime.
        </p>
      </Modal>

      {/* Image Lightbox Modal (M6.31) */}
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
