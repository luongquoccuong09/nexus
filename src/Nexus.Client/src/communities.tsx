import { orgApiPath, orgFetch } from './scope';
import { FormEvent, RefObject, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ArrowLeft,
  BarChart2,
  Bell,
  Check,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Compass,
  Download,
  Hash,
  Lock,
  Megaphone,
  MessageSquare,
  Pin,
  Plus,
  RefreshCw,
  Search,
  Send,
  Settings,
  ShieldCheck,
  Trash2,
  UserPlus,
  Users
} from 'lucide-react';
import { CommunityFeedDelta } from './chat';
import { OrganizationItem } from './organizationControls';
import { renderSafeMarkdown } from './markdown';
import { Avatar, Badge, Button, EmptyState, Field, Modal, ToastBar } from './ui';

export interface CommunityChannelDto {
  id: string;
  name: string;
  postingPolicy: string;
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
  createdAt: string;
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
  poll?: PostPollDto | null;
  pending?: boolean;
}

interface CommunityMemberDto {
  userId: string;
  username: string;
  displayName: string;
  avatarUrl?: string | null;
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
  feedListenerRef: RefObject<((conversationId?: string, delta?: CommunityFeedDelta | null) => void) | null>;
  onOpenCreateWorkspace: () => void;
  onOpenJoinWorkspace: () => void;
  onOpenWorkspaceSettings: (org: OrganizationItem) => void;
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
  feedListenerRef,
  onOpenCreateWorkspace,
  onOpenJoinWorkspace,
  onOpenWorkspaceSettings
}: CommunitiesWorkspaceProps) {
  const [communities, setCommunities] = useState<CommunityItemDto[]>([]);
  const [discoverCommunities, setDiscoverCommunities] = useState<CommunityItemDto[]>([]);
  const [discoverMode, setDiscoverMode] = useState(false);
  const [loadingDiscover, setLoadingDiscover] = useState(false);
  const [sidebarFilter, setSidebarFilter] = useState('');
  const [collapsedWorkspaces, setCollapsedWorkspaces] = useState<Record<string, boolean>>({});
  const [orgDirectoryByOrg, setOrgDirectoryByOrg] = useState<Record<string, DirectoryUserLite[]>>({});
  const [orgDepartmentsByOrg, setOrgDepartmentsByOrg] = useState<Record<string, DepartmentLite[]>>({});
  const [createOrgId, setCreateOrgId] = useState<string>('');

  const [selectedCommunityId, setSelectedCommunityId] = useState<string | null>(null);
  const [selectedConversationId, setSelectedConversationId] = useState<string | null>(null);
  const activeFeedRef = useRef(selectedConversationId);
  activeFeedRef.current = selectedConversationId;
  const orgById = useMemo(() => new Map(organizations.map(o => [o.id, o])), [organizations]);
  const communityOrgByConvRef = useRef(new Map<string, string>());

  const [posts, setPosts] = useState<ChannelPostDto[]>([]);
  const postsCacheRef = useRef(new Map<string, { items: ChannelPostDto[]; canPost: boolean; canManage: boolean }>());
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
  const [loadingPosts, setLoadingPosts] = useState(false);

  const [membersOpen, setMembersOpen] = useState(false);
  const [members, setMembers] = useState<CommunityMemberDto[]>([]);
  const [loadingMembers, setLoadingMembers] = useState(false);
  const [addMemberUserId, setAddMemberUserId] = useState('');
  const [mappedDeptId, setMappedDeptId] = useState('');

  // Composer state
  const [postTitle, setPostTitle] = useState('');
  const [postBody, setPostBody] = useState('');
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
  const [newChannelPolicy, setNewChannelPolicy] = useState<'Members' | 'AdminsOnly'>('Members');

  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  const selectedCommunity = useMemo(
    () => communities.find(c => c.id === selectedCommunityId) ?? null,
    [communities, selectedCommunityId]
  );

  const activeOrgId = selectedCommunity?.organizationId ?? null;
  const activeOrg = activeOrgId ? orgById.get(activeOrgId) ?? null : null;
  const directoryUsers = activeOrgId ? (orgDirectoryByOrg[activeOrgId] ?? []) : [];
  const departments = activeOrgId ? (orgDepartmentsByOrg[activeOrgId] ?? []) : [];

  const selectedSubChannel = useMemo(() => {
    if (!selectedCommunity || !selectedConversationId) return null;
    if (selectedConversationId === selectedCommunity.mainFeedConversationId) return null;
    return selectedCommunity.channels.find(ch => ch.id === selectedConversationId) ?? null;
  }, [selectedCommunity, selectedConversationId]);

  const loadCommunities = useCallback(async () => {
    if (organizations.length === 0) {
      setCommunities([]);
      communityOrgByConvRef.current.clear();
      return [];
    }
    const results = await Promise.all(
      organizations.map(async org => {
        const res = await orgFetch(org.id, '/api/communities?limit=50', { credentials: 'same-origin' });
        if (!res.ok) return [] as CommunityItemDto[];
        const data = await res.json();
        return ((data.items ?? []) as Omit<CommunityItemDto, 'organizationId' | 'organizationName'>[]).map(item => ({
          ...item,
          organizationId: org.id,
          organizationName: org.name
        }));
      })
    );
    const items = results.flat();
    const convMap = new Map<string, string>();
    for (const comm of items) {
      if (comm.mainFeedConversationId) convMap.set(comm.mainFeedConversationId, comm.organizationId);
      for (const ch of comm.channels) convMap.set(ch.id, comm.organizationId);
    }
    communityOrgByConvRef.current = convMap;
    setCommunities(items);
    return items;
  }, [organizations]);

  const loadDiscover = useCallback(async () => {
    if (organizations.length === 0) {
      setDiscoverCommunities([]);
      return;
    }
    setLoadingDiscover(true);
    try {
      const results = await Promise.all(
        organizations.map(async org => {
          const res = await orgFetch(org.id, '/api/communities?discover=true&limit=50', { credentials: 'same-origin' });
          if (!res.ok) return [] as CommunityItemDto[];
          const data = await res.json();
          return ((data.items ?? []) as Omit<CommunityItemDto, 'organizationId' | 'organizationName'>[]).map(item => ({
            ...item,
            organizationId: org.id,
            organizationName: org.name
          }));
        })
      );
      setDiscoverCommunities(results.flat());
    } finally {
      setLoadingDiscover(false);
    }
  }, [organizations]);

  const loadPosts = useCallback(async function loadPosts(conversationId: string, background = false, explicitOrgId?: string) {
    if (activeFeedRef.current !== conversationId) return;
    const orgId = explicitOrgId ?? communityOrgByConvRef.current.get(conversationId);
    if (!orgId) return;

    const previous = feedRequestRef.current;
    if (background && previous?.conversationId === conversationId) {
      previous.refreshQueued = true;
      return;
    }

    previous?.controller.abort();
    const request = { conversationId, controller: new AbortController(), refreshQueued: false, confirmedReactions: [] as ReactionChange[] };
    feedRequestRef.current = request;
    if (!background) setLoadingPosts(true);

    try {
      const res = await orgFetch(orgId, `/api/channels/${conversationId}/posts?limit=50`, {
        credentials: 'same-origin',
        signal: request.controller.signal
      });
      const data = await res.json().catch(() => ({}));
      if (request.controller.signal.aborted || activeFeedRef.current !== conversationId) return;

      if (res.ok) {
        const items: ChannelPostDto[] = data.items ?? [];
        const nextPosts = items.map(post => request.confirmedReactions.reduce(
          (current, change) => change.postId === current.id ? applyReaction(current, change) : current,
          post
        ));
        const nextCanPost = Boolean(data.canPost);
        const nextCanManage = Boolean(data.canManage);
        postsCacheRef.current.set(conversationId, { items: nextPosts, canPost: nextCanPost, canManage: nextCanManage });
        setPosts(nextPosts);
        setCanPost(nextCanPost);
        setCanManage(nextCanManage);
      } else {
        if (res.status === 401 || res.status === 403 || res.status === 404) {
          setPosts([]);
          setCanPost(false);
          setCanManage(false);
        }
        setError(data.message || 'Unable to load posts.');
      }
    } catch {
      if (!request.controller.signal.aborted && activeFeedRef.current === conversationId) {
        setError('Unable to load posts.');
      }
    } finally {
      if (feedRequestRef.current === request) {
        feedRequestRef.current = null;
        setLoadingPosts(false);
        if (request.refreshQueued) void loadPosts(conversationId, true, orgId);
      }
    }
  }, []);

  const loadMembers = useCallback(async (orgId: string, communityId: string) => {
    setLoadingMembers(true);
    try {
      const [membersRes, dirRes, deptRes] = await Promise.all([
        orgFetch(orgId, `/api/communities/${communityId}/members?limit=50`, { credentials: 'same-origin' }),
        orgDirectoryByOrg[orgId]
          ? Promise.resolve(null)
          : orgFetch(orgId, '/api/people/directory?limit=50', { credentials: 'same-origin' }),
        orgDepartmentsByOrg[orgId]
          ? Promise.resolve(null)
          : orgFetch(orgId, '/api/org/departments?limit=50', { credentials: 'same-origin' })
      ]);
      if (membersRes.ok) {
        const data = await membersRes.json();
        setMembers(data.items ?? []);
      }
      if (dirRes && dirRes.ok) {
        const dirData = await dirRes.json();
        setOrgDirectoryByOrg(prev => ({ ...prev, [orgId]: dirData.items ?? [] }));
      }
      if (deptRes && deptRes.ok) {
        const deptData = await deptRes.json();
        setOrgDepartmentsByOrg(prev => ({ ...prev, [orgId]: deptData.items ?? [] }));
      }
    } finally {
      setLoadingMembers(false);
    }
  }, [orgDepartmentsByOrg, orgDirectoryByOrg]);

  useEffect(() => {
    void loadCommunities().then(items => {
      if (items.length === 0) {
        setSelectedCommunityId(null);
        setSelectedConversationId(null);
        initialCommunitySelectedRef.current = false;
        return;
      }
      if (!initialCommunitySelectedRef.current || !items.some(c => c.id === selectedCommunityId)) {
        initialCommunitySelectedRef.current = true;
        setSelectedCommunityId(items[0].id);
        if (items[0].mainFeedConversationId) {
          setSelectedConversationId(items[0].mainFeedConversationId);
        }
      }
    });
  }, [loadCommunities]);

  useEffect(() => {
    if (discoverMode) {
      void loadDiscover();
    }
  }, [discoverMode, loadDiscover]);

  useEffect(() => {
    const cached = selectedConversationId ? postsCacheRef.current.get(selectedConversationId) : undefined;
    if (cached) {
      setPosts(cached.items);
      setCanPost(cached.canPost);
      setCanManage(cached.canManage);
      setLoadingPosts(false);
    } else {
      setPosts([]);
      setCanPost(false);
      setCanManage(false);
    }
    if (selectedConversationId) {
      void loadPosts(selectedConversationId, Boolean(cached));
    }

    return () => {
      feedRequestRef.current?.controller.abort();
      feedRequestRef.current = null;
    };
  }, [selectedConversationId, loadPosts]);

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
      const targetId = changedConversationId ?? activeFeedRef.current;
      if (!targetId) return;

      if (delta && changedConversationId) {
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
  }, [currentUserId, feedListenerRef, loadPosts, updateFeedPosts]);

  useEffect(() => {
    if (selectedCommunity) {
      setMappedDeptId(selectedCommunity.mappedDepartmentId ?? '');
      if (membersOpen) {
        void loadMembers(selectedCommunity.organizationId, selectedCommunity.id);
      }
    }
  }, [selectedCommunity, membersOpen, loadMembers]);

  function selectCommunityMainFeed(comm: CommunityItemDto) {
    setError('');
    setNotice('');
    setSelectedCommunityId(comm.id);
    setSelectedConversationId(comm.mainFeedConversationId ?? null);
    setDiscoverMode(false);
  }

  function selectSubChannel(comm: CommunityItemDto, ch: CommunityChannelDto) {
    setError('');
    setNotice('');
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
    const items = await loadCommunities();
    const created = items.find(c => c.id === data.id);
    if (created) {
      selectCommunityMainFeed(created);
    }
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
    const items = await loadCommunities();
    await loadDiscover();
    const joined = items.find(c => c.id === communityId);
    if (joined) {
      selectCommunityMainFeed(joined);
    }
    setNotice('Joined community.');
  }

  async function handleCreateChannel(e: FormEvent) {
    e.preventDefault();
    if (!addChannelForId) return;
    const comm = communities.find(c => c.id === addChannelForId);
    if (!comm) return;
    setError('');
    setNotice('');
    const res = await orgFetch(comm.organizationId, `/api/communities/${addChannelForId}/channels`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name: newChannelName,
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
    setNewChannelPolicy('Members');
    await loadCommunities();
    setSelectedCommunityId(targetCommId);
    setSelectedConversationId(data.id);
    setNotice(`Channel "#${data.name}" created.`);
  }

  async function handleCreatePost(e: FormEvent) {
    e.preventDefault();
    if (!selectedConversationId || !postBody.trim() || posting) return;
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
      pending: true
    };

    // Show the new post immediately in dimmed pending state and clear the composer in 0ms.
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
          poll: pollPayload
        })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        updateFeedPosts(targetConvId, prev => prev.filter(p => p.id !== tempPostId));
        setPostTitle(savedTitle);
        setPostBody(savedBody);
        setIsAnnouncement(savedIsAnnouncement);
        setRequiresAck(savedRequiresAck);
        setIncludePoll(savedIncludePoll);
        setPollQuestion(savedPollQuestion);
        setPollOptions(savedPollOptions);
        setPollAllowMultiple(savedPollAllowMultiple);
        setError(data.message || 'Unable to publish post.');
        return;
      }

      updateFeedPosts(targetConvId, prev => prev.map(p =>
        p.id === tempPostId
          ? {
              ...p,
              id: data.id ?? p.id,
              createdAt: data.createdAt ?? p.createdAt,
              pending: false
            }
          : p
      ));
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
      setError('Unable to publish post. Check your connection and try again.');
    } finally {
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
      updateFeedPosts(post.conversationId, prev => prev
        .map(p => p.id === post.id ? { ...p, isPinned: post.isPinned } : p)
        .sort((a, b) => Number(b.isPinned) - Number(a.isPinned) || Date.parse(b.createdAt) - Date.parse(a.createdAt))
      );
      setError('Unable to update pin state.');
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
      if (convId) void loadPosts(convId, true);
      setError('Unable to record acknowledgment.');
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
      setError(data.message || 'Unable to send reminder.');
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
      if (!res.ok) throw new Error('Reaction request failed');

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
      setError(data.message || 'Unable to submit vote.');
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
      setError(data.message || 'Unable to post comment.');
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
    await Promise.all([loadMembers(selectedCommunity.organizationId, selectedCommunity.id), loadCommunities()]);
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
    await Promise.all([loadMembers(selectedCommunity.organizationId, selectedCommunity.id), loadCommunities()]);
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
    await loadMembers(selectedCommunity.organizationId, selectedCommunity.id);
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
      await Promise.all([loadMembers(selectedCommunity.organizationId, selectedCommunity.id), loadCommunities()]);
      setNotice(`Department synced · Added ${data.addedCount ?? 0}, unchanged ${data.unchangedCount ?? 0}.`);
    } else {
      await loadCommunities();
      setNotice('Department mapping cleared.');
    }
  }

  const availableToAdd = useMemo(() => {
    const memberIds = new Set(members.map(m => m.userId));
    return directoryUsers.filter(u => !memberIds.has(u.id));
  }, [directoryUsers, members]);

  const activePolicy = selectedSubChannel
    ? selectedSubChannel.postingPolicy
    : selectedCommunity?.mainFeedPostingPolicy ?? 'Members';

  const normalizedFilter = sidebarFilter.trim().toLowerCase();

  const filteredDiscover = useMemo(() => {
    if (!normalizedFilter) return discoverCommunities;
    return discoverCommunities.filter(comm =>
      comm.name.toLowerCase().includes(normalizedFilter) ||
      comm.organizationName.toLowerCase().includes(normalizedFilter) ||
      (comm.description ?? '').toLowerCase().includes(normalizedFilter)
    );
  }, [discoverCommunities, normalizedFilter]);

  const workspaceGroups = useMemo(() => {
    return organizations.map(org => {
      const orgComms = communities.filter(c => c.organizationId === org.id);
      const matchesOrg = !normalizedFilter || org.name.toLowerCase().includes(normalizedFilter);
      const visibleComms = matchesOrg
        ? orgComms
        : orgComms.filter(comm =>
            comm.name.toLowerCase().includes(normalizedFilter) ||
            comm.channels.some(ch => ch.name.toLowerCase().includes(normalizedFilter))
          );
      return { org, allCount: orgComms.length, communities: visibleComms };
    }).filter(group => !normalizedFilter || group.communities.length > 0 || group.org.name.toLowerCase().includes(normalizedFilter));
  }, [organizations, communities, normalizedFilter]);

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
            )}
          </div>
        </div>

        <div className="nx-conv-scroll">
          {organizations.length === 0 ? (
            <div className="nx-empty-side">
              <p style={{ margin: 0 }}>Create or join a workspace to organize communities and channels.</p>
            </div>
          ) : discoverMode ? (
            loadingDiscover ? (
              <div className="nx-empty-side" role="status">Loading communities…</div>
            ) : filteredDiscover.length === 0 ? (
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
          ) : workspaceGroups.length === 0 ? (
            <div className="nx-empty-side">No matching workspaces or communities.</div>
          ) : (
            <div className="nx-ws-list">
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
                          {allCount === 0 ? 'No communities yet.' : 'No matches in this workspace.'}
                        </div>
                      ) : (
                        orgComms.map(comm => {
                          const isMainActive =
                            selectedCommunityId === comm.id &&
                            selectedConversationId === comm.mainFeedConversationId;
                          const isManager = comm.myRole === 'Owner' || comm.myRole === 'Admin';

                          return (
                            <div className="nx-comm-tree-group" key={comm.id}>
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
                                      <span>{comm.name}</span>
                                      {comm.visibility === 'Private' && <Lock size={12} aria-label="Private Community" />}
                                    </span>
                                    {comm.isOfficial && <Badge variant="role">Official</Badge>}
                                  </div>
                                  <div className="nx-conv-sub">
                                    {comm.memberCount} {comm.memberCount === 1 ? 'member' : 'members'}
                                    {comm.channels.length > 0 ? ` · ${comm.channels.length} ${comm.channels.length === 1 ? 'channel' : 'channels'}` : ''}
                                  </div>
                                </div>
                              </button>

                              {comm.channels.length > 0 && (
                                <div className="nx-comm-channels">
                                  {comm.channels.map(ch => {
                                    const isChActive = selectedConversationId === ch.id;
                                    return (
                                      <button
                                        type="button"
                                        key={ch.id}
                                        className={`nx-channel-row ${isChActive ? 'active' : ''}`}
                                        onClick={() => selectSubChannel(comm, ch)}
                                      >
                                        {ch.postingPolicy === 'AdminsOnly' ? (
                                          <Megaphone size={14} aria-hidden="true" />
                                        ) : (
                                          <Hash size={14} aria-hidden="true" />
                                        )}
                                        <span className="nx-channel-name">{ch.name}</span>
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
                                    setNewChannelPolicy('Members');
                                  }}
                                >
                                  <Plus size={13} aria-hidden="true" /> Add channel
                                </button>
                              )}
                            </div>
                          );
                        })
                      )
                    )}
                  </section>
                );
              })}
            </div>
          )}
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
            <header className="nx-stage-header">
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
                    {selectedCommunity.description && (
                      <>
                        <span>·</span>
                        <span>{selectedCommunity.description}</span>
                      </>
                    )}
                  </div>
                </div>
              </div>

              <div className="nx-inline-actions">
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
                <Button
                  variant={membersOpen ? 'primary' : 'default'}
                  size="sm"
                  onClick={() => {
                    if (!membersOpen) setLoadingMembers(true);
                    setMembersOpen(v => !v);
                  }}
                >
                  <Users size={15} aria-hidden="true" /> Members
                </Button>
              </div>
            </header>

            <div className="nx-comm-body">
              <div className="nx-comm-feed">
                {/* Post Composer */}
                {canPost ? (
                  <form className="nx-post-composer" onSubmit={handleCreatePost}>
                    <input
                      type="text"
                      className="nx-post-title-input"
                      aria-label="Post title (optional)"
                      placeholder="Add a title (optional)…"
                      maxLength={200}
                      value={postTitle}
                      onChange={e => setPostTitle(e.target.value)}
                    />
                    <textarea
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
                      onKeyDown={e => {
                        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && postBody.trim() && !posting) {
                          e.preventDefault();
                          e.currentTarget.form?.requestSubmit();
                        }
                      }}
                      required
                    />

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
                          variant={includePoll ? 'primary' : 'ghost'}
                          size="sm"
                          onClick={() => setIncludePoll(v => !v)}
                        >
                          <BarChart2 size={14} aria-hidden="true" /> Poll
                        </Button>
                      </div>

                      <Button type="submit" variant="primary" size="sm" disabled={posting || !postBody.trim()}>
                        <Send size={14} aria-hidden="true" /> {posting ? 'Posting…' : 'Post'}
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
                {loadingPosts && posts.length === 0 ? (
                  <div className="nx-empty-side">Loading posts…</div>
                ) : posts.length === 0 ? (
                  <div className="nx-empty-side">No posts yet. Start the first conversation above.</div>
                ) : (
                  posts.map(post => {
                    const commentsOpen = Boolean(expandedComments[post.id]) || post.comments.length > 0;
                    const reactionMap = new Map(post.reactions.map(r => [r.emoji, r]));

                    return (
                      <article
                        key={post.id}
                        className={`nx-post-card ${post.isAnnouncement ? 'announcement' : ''} ${post.isPinned ? 'pinned' : ''} ${post.pending ? 'pending' : ''}`}
                      >
                        <header className="nx-post-head">
                          <div className="nx-post-author">
                            <Avatar
                              displayName={post.authorDisplayName}
                              avatarUrl={post.authorAvatarUrl}
                              size="sm"
                            />
                            <div>
                              <div className="nx-post-author-row">
                                <strong>{post.authorDisplayName}</strong>
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
                            {canManage && (
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

                        <div
                          className="nx-msg-body nx-post-content"
                          dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(post.body) }}
                        />

                        {/* Announcement Acknowledgment Bar */}
                        {post.requiresAcknowledgment && (
                          <div className="nx-ack-bar">
                            <div className="nx-ack-status">
                              {post.acknowledgedByMe ? (
                                <Badge variant="success">
                                  <CheckCircle2 size={12} aria-hidden="true" /> Acknowledged
                                </Badge>
                              ) : (
                                <Button variant="primary" size="sm" onClick={() => void handleAcknowledge(post.id)}>
                                  <CheckCircle2 size={14} aria-hidden="true" /> Acknowledge
                                </Button>
                              )}
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
                                <Button variant="default" size="sm" onClick={() => void handleRemind(post.id)}>
                                  <Bell size={13} aria-hidden="true" /> Remind
                                </Button>
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
                                  onClick={() => void handleToggleReaction(post, emoji)}
                                  aria-label={`React with ${emoji}`}
                                  aria-pressed={Boolean(r?.reactedByMe)}
                                  aria-disabled={Boolean(pending)}
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
                                  <strong>{c.authorDisplayName}</strong>
                                  {c.pending
                                    ? <span className="nx-pending-label">Sending…</span>
                                    : <span className="nx-msg-time">{formatPostTime(c.createdAt)}</span>}
                                </div>
                                <div
                                  className="nx-msg-body"
                                  dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(c.body) }}
                                />
                              </div>
                            ))}

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
                          </div>
                        )}
                      </article>
                    );
                  })
                )}
              </div>

              {/* Members & Governance Drawer */}
              {membersOpen && (
                <aside className="nx-drawer" aria-label="Community members">
                  <div className="nx-drawer-head">
                    <h4>{loadingMembers ? 'Members' : `Members (${members.length})`}</h4>
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
                    {canManage && availableToAdd.length > 0 && (
                      <form className="nx-drawer-section" onSubmit={handleAddMember}>
                        <Field label="Add member" htmlFor="comm-add-member" compact>
                          <select
                            id="comm-add-member"
                            className="nx-select"
                            value={addMemberUserId}
                            onChange={e => setAddMemberUserId(e.target.value)}
                          >
                            <option value="">Select a person</option>
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
                      </form>
                    )}

                    {/* Members list */}
                    <div className="nx-drawer-section">
                      {loadingMembers ? (
                        <p className="nx-muted" role="status" style={{ textAlign: 'center', margin: '12px 0' }}>
                          Loading members…
                        </p>
                      ) : members.map(m => (
                        <div className="nx-member-row" key={m.userId}>
                          <Avatar displayName={m.displayName} avatarUrl={m.avatarUrl} size="sm" />
                          <div style={{ minWidth: 0, flex: 1 }}>
                            <strong>{m.displayName}</strong>
                            <div className="nx-muted">
                              @{m.username} · <Badge variant="role">{m.role}</Badge>
                            </div>
                          </div>
                          {canManage && m.userId !== currentUserId && m.role !== 'Owner' && (
                            <div className="nx-inline-actions">
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
                      ))}
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
    </div>
  );
}
