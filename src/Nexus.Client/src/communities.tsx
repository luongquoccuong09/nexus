import { orgApiPath, orgFetch, protectedRequest } from './scope';
import { ChangeEvent, ClipboardEvent, DragEvent, FormEvent, MouseEvent, Suspense, lazy, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Archive,
  ArrowLeft,
  BarChart2,
  ChevronDown,
  ChevronRight,
  Compass,
  Eye,
  FileText,
  FolderOpen,
  FolderPlus,
  Hash,
  Link2,
  Lock,
  Megaphone,
  Mic,
  Paperclip,
  Pencil,
  Plus,
  RefreshCw,
  RotateCcw,
  Search,
  Send,
  Settings,
  Star,
  Tag,
  Trash2,
  Users,
  X
} from 'lucide-react';
import { AttachmentItem, formatBytes } from './attachments/attachmentUtils';
import { ImageLightboxModal } from './attachments/ImageLightboxModal';
import { CommunityJumpTarget, copyContentLink, extractContentLinkToken } from './contentLinks';
import { moveIdByOffset, reorderIdList, SidebarSectionDto, useSidebarOrganization } from './sidebarPrefs';
import { SidebarItemActions, SidebarSectionHeader, SidebarSectionModal } from './sidebarControls';
import { OrganizationItem } from './organizationControls';
import { ComposerToolbar, handleComposerFormattingKeyDown } from './ComposerToolbar';
import { ConversationNotificationButton } from './notificationPreferences';
import { OwnershipTransfer, TransferOwnershipDialog } from './transferOwnership';
import { CursorPage, ListPaging, readCursorPage, useCursorList } from './cursorList';
import { loadOrganizationDepartments, personKey, searchPeopleScope } from './peopleDirectory';
import ConversationMeetingButton from './meetings/ConversationMeetingButton';
import { Badge, Button, EmptyState, ToastBar } from './ui';
import { CommunityMembersDrawer } from './communities/CommunityMembersDrawer';
import { CommunityModals } from './communities/CommunityModals';
import { CommunityPostCard } from './communities/CommunityPostCard';
import { TagMentionPopover } from './tags/TagMentionPopover';
import {
  ChannelPostDto,
  CommunitiesWorkspaceProps,
  CommunityChannelDto,
  CommunityItemDto,
  CommunityMemberDto,
  DepartmentLite,
  DirectoryUserLite,
  PostCommentDto,
  PostPollDto,
  ReactionChange,
  applyReaction
} from './communities/types';

const LazySharedContentDrawer = lazy(() => import('./shared/SharedContentDrawer'));
const LazyPeopleTagsModal = lazy(() => import('./tags/PeopleTagsModal'));
const LazyVoiceNoteRecorder = lazy(() => import('./voice/VoiceNoteRecorder'));

export type { CommunityChannelDto, CommunityItemDto, DepartmentLite, DirectoryUserLite };

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

export function CommunitiesWorkspace({
  currentUserId,
  currentDisplayName,
  visible,
  organizations,
  requestedChannel,
  jumpTarget,
  feedListenerRef,
  onOpenContentLink,
  onForwardContent,
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
  const communitiesRef = useRef(communities);
  communitiesRef.current = communities;
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
  const filesBtnWrapRef = useRef<HTMLSpanElement | null>(null);
  const [sharedRefreshNonce, setSharedRefreshNonce] = useState(0);
  const [addMemberUserId, setAddMemberUserId] = useState('');
  const [memberSearchQuery, setMemberSearchQuery] = useState('');
  const [mappedDeptId, setMappedDeptId] = useState('');

  // M6.31 Feed post attachments state
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
  const [tagsModalOpen, setTagsModalOpen] = useState(false);
  const [voiceRecorderOpen, setVoiceRecorderOpen] = useState(false);
  const voiceBtnWrapRef = useRef<HTMLSpanElement | null>(null);
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

  useEffect(() => {
    uploadControllerRef.current?.abort();
    uploadControllerRef.current = null;
    setUploadingFiles(false);
    setPendingAttachments([]);
    setQuotaWarning('');
    setIsComposerDragOver(false);
  }, [activeOrgId, selectedConversationId]);

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
    if (jumpTarget && jumpTarget.nonce !== handledJumpNonceRef.current) return;
    const first = communities[0];
    if (!first) return;
    initialCommunitySelectedRef.current = true;
    setFocusedCommunity(first);
    setSelectedCommunityId(first.id);
    setSelectedConversationId(first.mainFeedConversationId ?? null);
  }, [communities, communitiesPage.loading, discoverMode, jumpTarget, memberSearch, selectedCommunityId]);

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
    setVoiceRecorderOpen(false);
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
      const request = feedRequestRef.current;
      if (request?.conversationId === selectedConversationId) {
        request.controller.abort();
        feedRequestRef.current = null;
      }
    };
  }, [selectedConversationId, loadPosts]);

  useEffect(() => {
    if (!jumpTarget || jumpTarget.nonce === handledJumpNonceRef.current) return;
    pendingJumpRef.current = jumpTarget;
    const controller = new AbortController();
    let highlightTimer: number | null = null;

    async function runJump(target: CommunityJumpTarget) {
      const cachedComm = communitiesRef.current.find(c => c.id === target.communityId)
        ?? await lookupCommunity(target.organizationId, target.communityId, controller.signal).catch(() => null);
      if (controller.signal.aborted) return;
      handledJumpNonceRef.current = target.nonce;
      if (!cachedComm) {
        pendingJumpRef.current = null;
        return;
      }
      initialCommunitySelectedRef.current = true;
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
  }, [jumpTarget, loadPostsAround, lookupCommunity]);

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
                isForwarded: incoming.isForwarded,
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
            setSharedRefreshNonce(n => n + 1);
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

  async function uploadPostVoiceNote(file: File, durationMs: number): Promise<{ ok: true } | { ok: false; error: string }> {
    const targetConvId = selectedConversationId;
    const targetOrgId = activeOrgId ?? (targetConvId ? communityOrgByConvRef.current.get(targetConvId) : undefined);
    if (!targetConvId || !targetOrgId || !canPost || activeIsArchived) {
      return { ok: false, error: 'You cannot attach a voice note to this channel right now.' };
    }
    if (uploadingFiles) {
      return { ok: false, error: 'Please wait for the current upload to finish.' };
    }
    if (pendingAttachments.length >= 10) {
      return { ok: false, error: 'You can attach up to 10 files per post.' };
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

      const form = new FormData();
      form.append('clientUploadId', crypto.randomUUID());
      form.append('file', file, file.name);
      form.append('durationMs', String(durationMs));

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
      if (controller.signal.aborted || activeFeedRef.current !== targetConvId) {
        return { ok: false, error: 'Upload cancelled.' };
      }

      if (!res.ok) {
        surfaceArchivedConflictIfNeeded(res, data, targetConvId);
        return { ok: false, error: data.message || 'Unable to upload voice note.' };
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
          durationMs: rawAtt.durationMs ?? durationMs,
          state: rawAtt.state ?? 'pending',
          scanStatus: rawAtt.scanStatus ?? 'clean',
          downloadUrl: rawAtt.downloadUrl ?? '',
          thumbnailUrl: rawAtt.thumbnailUrl ?? null,
          previewUrl: rawAtt.previewUrl ?? null,
          audioUrl: rawAtt.audioUrl ?? null,
          createdAt: rawAtt.createdAt ?? new Date().toISOString()
        };
        setPendingAttachments(prev => (prev.some(a => a.id === normalized.id) ? prev : [...prev, normalized]));
      }
      if (typeof data.quotaWarning === 'string' && data.quotaWarning) {
        setQuotaWarning(data.quotaWarning);
      }
      return { ok: true };
    } catch {
      if (controller.signal.aborted) return { ok: false, error: 'Upload cancelled.' };
      return { ok: false, error: 'Upload interrupted. Your recording is still ready to retry.' };
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
        setSharedRefreshNonce(n => n + 1);
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
                {!activeIsArchived && <ConversationMeetingButton key={`meeting:${selectedConversationId}`}
                  organizationId={selectedCommunity.organizationId} conversationId={selectedConversationId}
                  title={selectedSubChannel?.name ?? selectedCommunity.name} visible={visible} />}
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
                    aria-label="Shared content"
                    title="Shared"
                    onClick={() => {
                      const next = filesOpen ? 'none' : 'files';
                      setRightDrawer(next);
                    }}
                  >
                    <FolderOpen size={15} aria-hidden="true" />
                    <span className="nx-stage-action-label">Shared</span>
                  </Button>
                </span>
                <Button
                  variant={tagsModalOpen ? 'primary' : 'default'}
                  size="sm"
                  aria-label="Community tags"
                  title="Tags"
                  onClick={() => setTagsModalOpen(true)}
                >
                  <Tag size={15} aria-hidden="true" />
                  <span className="nx-stage-action-label">Tags</span>
                </Button>
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

                    {voiceRecorderOpen && canPost && !activeIsArchived && (
                      <Suspense fallback={<div className="nx-voice-recorder-loading" role="status">Loading recorder…</div>}>
                        <LazyVoiceNoteRecorder
                          disabled={posting || uploadingFiles}
                          onUpload={uploadPostVoiceNote}
                          onClose={() => {
                            setVoiceRecorderOpen(false);
                            window.requestAnimationFrame(() => {
                              voiceBtnWrapRef.current?.querySelector('button')?.focus();
                            });
                          }}
                        />
                      </Suspense>
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
                        <span ref={voiceBtnWrapRef}>
                          <Button
                            type="button"
                            variant={voiceRecorderOpen ? 'primary' : 'ghost'}
                            size="sm"
                            aria-label="Record voice note"
                            title="Record voice note"
                            disabled={posting || uploadingFiles || pendingAttachments.length >= 10}
                            onClick={() => setVoiceRecorderOpen(v => !v)}
                          >
                            <Mic size={14} aria-hidden="true" /> Voice
                          </Button>
                        </span>
                        <TagMentionPopover
                          organizationId={selectedCommunity.organizationId}
                          communityId={selectedCommunity.id}
                          disabled={posting}
                          textareaRef={postBodyRef}
                          value={postBody}
                          onChange={setPostBody}
                        />
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
                  posts.map(post => (
                    <CommunityPostCard
                      key={post.id}
                      post={post}
                      organizationId={selectedCommunity.organizationId}
                      canManage={canManage}
                      activeIsArchived={activeIsArchived}
                      highlighted={highlightedPostId === post.id}
                      commentsOpen={Boolean(expandedComments[post.id]) || post.comments.length > 0}
                      commentDraft={commentDrafts[post.id] ?? ''}
                      pendingReactions={pendingReactions}
                      availabilityMap={availabilityMap}
                      nowMs={nowMs}
                      onInlineLinkClick={handleInlineContentLinkClick}
                      onOpenProfileCard={onOpenProfileCard}
                      onCopyPostLink={p => void handleCopyPostLink(p)}
                      onForwardPost={p => {
                        if (activeOrgId) onForwardContent({ kind: 'post', organizationId: activeOrgId, conversationId: p.conversationId, postId: p.id });
                      }}
                      onTogglePin={p => void handleTogglePin(p)}
                      onPreviewImage={setLightboxImage}
                      onAcknowledge={id => void handleAcknowledge(id)}
                      onRemind={id => void handleRemind(id)}
                      onVotePoll={(poll, optId) => void handleVotePoll(poll, optId)}
                      onToggleReaction={(p, emoji) => void handleToggleReaction(p, emoji)}
                      onToggleComments={(postId, nextOpen) =>
                        setExpandedComments(prev => ({ ...prev, [postId]: nextOpen }))
                      }
                      onCommentDraftChange={(postId, value) =>
                        setCommentDrafts(prev => ({ ...prev, [postId]: value }))
                      }
                      onAddComment={(e, postId) => void handleAddComment(e, postId)}
                    />
                  ))
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

              {filesOpen && selectedCommunity && selectedConversationId && (
                <Suspense fallback={<aside className="nx-drawer" aria-label="Shared content"><div className="nx-drawer-body"><p className="nx-muted" role="status">Loading shared content…</p></div></aside>}>
                  <LazySharedContentDrawer
                    key={`${selectedConversationId}:${sharedRefreshNonce}`}
                    scope={{ kind: 'organization', organizationId: selectedCommunity.organizationId }}
                    conversationId={selectedConversationId}
                    originLabel="post"
                    lightboxOpen={Boolean(lightboxImage)}
                    onClose={() => {
                      setRightDrawer('none');
                      requestAnimationFrame(() => {
                        filesBtnWrapRef.current?.querySelector('button')?.focus();
                      });
                    }}
                    onPreviewImage={setLightboxImage}
                    onOpenContentLink={onOpenContentLink}
                  />
                </Suspense>
              )}

              {membersOpen && (
                <CommunityMembersDrawer
                  headingRef={communityMembersHeading}
                  currentUserId={currentUserId}
                  selectedCommunity={selectedCommunity}
                  activeOrg={activeOrg}
                  canManage={canManage}
                  departments={departments}
                  mappedDeptId={mappedDeptId}
                  setMappedDeptId={setMappedDeptId}
                  onSyncDepartment={() => void handleSyncDepartment()}
                  onToggleOfficial={() => void handleToggleOfficial()}
                  memberSearchQuery={memberSearchQuery}
                  setMemberSearchQuery={setMemberSearchQuery}
                  addMemberUserId={addMemberUserId}
                  setAddMemberUserId={setAddMemberUserId}
                  addCandidates={addCandidates}
                  onAddMember={handleAddMember}
                  members={members}
                  loadingMembers={loadingMembers}
                  memberListError={memberList.error}
                  memberListHasMore={memberList.next !== null}
                  onLoadMoreMembers={memberList.loadMore}
                  onRetryMembers={memberList.retry}
                  availabilityMap={availabilityMap}
                  nowMs={nowMs}
                  onOpenProfileCard={onOpenProfileCard}
                  onSetOwnershipTransfer={setOwnershipTransfer}
                  onToggleAdmin={m => void handleToggleAdmin(m)}
                  onRemoveMember={uid => void handleRemoveMember(uid)}
                  onLeaveCommunity={() => void handleLeaveCommunity()}
                  onClose={() => setMembersOpen(false)}
                />
              )}
            </div>
          </>
        )}
      </section>

      {ownershipTransfer && selectedCommunity && (
        <TransferOwnershipDialog
          target={ownershipTransfer}
          onClose={() => setOwnershipTransfer(null)}
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
          }}
        />
      )}

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

      <CommunityModals
        organizations={organizations}
        selectedCommunity={selectedCommunity}
        selectedSubChannel={selectedSubChannel}
        createOpen={createOpen}
        setCreateOpen={setCreateOpen}
        createOrgId={createOrgId}
        setCreateOrgId={setCreateOrgId}
        newCommName={newCommName}
        setNewCommName={setNewCommName}
        newCommDesc={newCommDesc}
        setNewCommDesc={setNewCommDesc}
        newCommVisibility={newCommVisibility}
        setNewCommVisibility={setNewCommVisibility}
        onCreateCommunity={handleCreateCommunity}
        addChannelForId={addChannelForId}
        setAddChannelForId={setAddChannelForId}
        newChannelName={newChannelName}
        setNewChannelName={setNewChannelName}
        newChannelDesc={newChannelDesc}
        setNewChannelDesc={setNewChannelDesc}
        newChannelPolicy={newChannelPolicy}
        setNewChannelPolicy={setNewChannelPolicy}
        onCreateChannel={handleCreateChannel}
        editCommOpen={editCommOpen}
        setEditCommOpen={setEditCommOpen}
        editCommName={editCommName}
        setEditCommName={setEditCommName}
        editCommDesc={editCommDesc}
        setEditCommDesc={setEditCommDesc}
        onSaveCommunityMeta={handleSaveCommunityMeta}
        editChannelOpen={editChannelOpen}
        setEditChannelOpen={setEditChannelOpen}
        editChannelName={editChannelName}
        setEditChannelName={setEditChannelName}
        editChannelDesc={editChannelDesc}
        setEditChannelDesc={setEditChannelDesc}
        onSaveChannelMeta={handleSaveChannelMeta}
        archiveConfirmOpen={archiveConfirmOpen}
        setArchiveConfirmOpen={setArchiveConfirmOpen}
        onArchiveChannel={() => void handleArchiveChannel()}
        savingMeta={savingMeta}
      />

      <ImageLightboxModal image={lightboxImage} onClose={() => setLightboxImage(null)} />

      {tagsModalOpen && selectedCommunity && (
        <Suspense fallback={null}>
          <LazyPeopleTagsModal
            open={tagsModalOpen}
            onClose={() => setTagsModalOpen(false)}
            scope={{
              kind: 'community',
              organizationId: selectedCommunity.organizationId,
              communityId: selectedCommunity.id,
              communityName: selectedCommunity.name
            }}
          />
        </Suspense>
      )}
    </div>
  );
}

export default CommunitiesWorkspace;
