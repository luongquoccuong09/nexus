import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react';
import {
  ArrowLeft,
  BarChart2,
  Bell,
  Check,
  CheckCircle2,
  Compass,
  Download,
  Hash,
  Lock,
  Megaphone,
  MessageSquare,
  Pin,
  Plus,
  RefreshCw,
  Send,
  ShieldCheck,
  Trash2,
  UserPlus,
  Users
} from 'lucide-react';
import { renderSafeMarkdown } from './markdown';
import { Avatar, Badge, Button, EmptyState, Field, Modal, ToastBar, UserNameWithBadge } from './ui';

export interface CommunityChannelDto {
  id: string;
  name: string;
  postingPolicy: string;
  createdAt: string;
}

export interface CommunityItemDto {
  id: string;
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
  authorIsUnverified: boolean;
  body: string;
  createdAt: string;
}

interface PostReactionDto {
  emoji: string;
  count: number;
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
  authorIsUnverified: boolean;
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
}

interface CommunityMemberDto {
  userId: string;
  username: string;
  displayName: string;
  isUnverified: boolean;
  avatarUrl?: string | null;
  role: string;
  joinedAt: string;
}

export interface DirectoryUserLite {
  id: string;
  username: string;
  displayName: string;
  isUnverified: boolean;
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
  currentOrgRole: string;
  currentIsUnverified: boolean;
  directoryUsers: DirectoryUserLite[];
  departments: DepartmentLite[];
}

const QUICK_REACTIONS = ['👍', '❤️', '🎉', '👀'];

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
  currentOrgRole,
  currentIsUnverified,
  directoryUsers,
  departments
}: CommunitiesWorkspaceProps) {
  const [communities, setCommunities] = useState<CommunityItemDto[]>([]);
  const [discoverCommunities, setDiscoverCommunities] = useState<CommunityItemDto[]>([]);
  const [discoverMode, setDiscoverMode] = useState(false);

  const [selectedCommunityId, setSelectedCommunityId] = useState<string | null>(null);
  const [selectedConversationId, setSelectedConversationId] = useState<string | null>(null);

  const [posts, setPosts] = useState<ChannelPostDto[]>([]);
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

  const selectedSubChannel = useMemo(() => {
    if (!selectedCommunity || !selectedConversationId) return null;
    if (selectedConversationId === selectedCommunity.mainFeedConversationId) return null;
    return selectedCommunity.channels.find(ch => ch.id === selectedConversationId) ?? null;
  }, [selectedCommunity, selectedConversationId]);

  const loadCommunities = useCallback(async () => {
    const res = await fetch('/api/communities?limit=50', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      const items: CommunityItemDto[] = data.items ?? [];
      setCommunities(items);
      return items;
    }
    return [];
  }, []);

  const loadDiscover = useCallback(async () => {
    const res = await fetch('/api/communities?discover=true&limit=50', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setDiscoverCommunities(data.items ?? []);
    }
  }, []);

  const loadPosts = useCallback(async (conversationId: string) => {
    setLoadingPosts(true);
    try {
      const res = await fetch(`/api/channels/${conversationId}/posts?limit=50`, { credentials: 'same-origin' });
      if (res.ok) {
        const data = await res.json();
        setPosts(data.items ?? []);
        setCanPost(Boolean(data.canPost));
        setCanManage(Boolean(data.canManage));
      } else {
        const err = await res.json().catch(() => ({}));
        setError(err.message || 'Unable to load posts.');
      }
    } finally {
      setLoadingPosts(false);
    }
  }, []);

  const loadMembers = useCallback(async (communityId: string) => {
    setLoadingMembers(true);
    try {
      const res = await fetch(`/api/communities/${communityId}/members?limit=50`, { credentials: 'same-origin' });
      if (res.ok) {
        const data = await res.json();
        setMembers(data.items ?? []);
      }
    } finally {
      setLoadingMembers(false);
    }
  }, []);

  useEffect(() => {
    void loadCommunities().then(items => {
      if (items.length > 0 && !selectedCommunityId) {
        setSelectedCommunityId(items[0].id);
        if (items[0].mainFeedConversationId) {
          setSelectedConversationId(items[0].mainFeedConversationId);
        }
      }
    });
  }, [loadCommunities, selectedCommunityId]);

  useEffect(() => {
    if (discoverMode) {
      void loadDiscover();
    }
  }, [discoverMode, loadDiscover]);

  useEffect(() => {
    if (selectedConversationId) {
      void loadPosts(selectedConversationId);
    } else {
      setPosts([]);
      setCanPost(false);
      setCanManage(false);
    }
  }, [selectedConversationId, loadPosts]);

  useEffect(() => {
    if (selectedCommunity) {
      setMappedDeptId(selectedCommunity.mappedDepartmentId ?? '');
      if (membersOpen) {
        void loadMembers(selectedCommunity.id);
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

  async function handleCreateCommunity(e: FormEvent) {
    e.preventDefault();
    setError('');
    setNotice('');
    const res = await fetch('/api/communities', {
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

  async function handleJoinCommunity(communityId: string) {
    setError('');
    setNotice('');
    const res = await fetch(`/api/communities/${communityId}/join`, {
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
    setError('');
    setNotice('');
    const res = await fetch(`/api/communities/${addChannelForId}/channels`, {
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
    if (!selectedConversationId || !postBody.trim()) return;
    setError('');
    setNotice('');
    setPosting(true);
    try {
      const cleanedPollOptions = pollOptions.map(o => o.trim()).filter(Boolean);
      const pollPayload = includePoll && pollQuestion.trim() && cleanedPollOptions.length >= 2
        ? {
            question: pollQuestion.trim(),
            options: cleanedPollOptions,
            allowMultiple: pollAllowMultiple
          }
        : null;

      const res = await fetch(`/api/channels/${selectedConversationId}/posts`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          title: postTitle.trim() || null,
          body: postBody.trim(),
          isAnnouncement: canManage && isAnnouncement,
          requiresAcknowledgment: canManage && isAnnouncement && requiresAck,
          poll: pollPayload
        })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setError(data.message || 'Unable to publish post.');
        return;
      }
      setPostTitle('');
      setPostBody('');
      setIsAnnouncement(false);
      setRequiresAck(false);
      setIncludePoll(false);
      setPollQuestion('');
      setPollOptions(['', '']);
      setPollAllowMultiple(false);
      await loadPosts(selectedConversationId);
    } finally {
      setPosting(false);
    }
  }

  async function handleTogglePin(post: ChannelPostDto) {
    setError('');
    const res = await fetch(`/api/posts/${post.id}/pin`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ isPinned: !post.isPinned })
    });
    if (!res.ok) {
      setError('Unable to update pin state.');
      return;
    }
    if (selectedConversationId) await loadPosts(selectedConversationId);
  }

  async function handleAcknowledge(postId: string) {
    setError('');
    const res = await fetch(`/api/posts/${postId}/acknowledge`, {
      method: 'PUT',
      credentials: 'same-origin'
    });
    if (!res.ok) {
      setError('Unable to record acknowledgment.');
      return;
    }
    if (selectedConversationId) await loadPosts(selectedConversationId);
  }

  async function handleRemind(postId: string) {
    setError('');
    setNotice('');
    const res = await fetch(`/api/posts/${postId}/remind`, {
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
    const existing = post.reactions.find(r => r.emoji === emoji);
    const method = existing?.reactedByMe ? 'DELETE' : 'PUT';
    const res = await fetch(`/api/posts/${post.id}/reactions/${encodeURIComponent(emoji)}`, {
      method,
      credentials: 'same-origin'
    });
    if (res.ok && selectedConversationId) {
      await loadPosts(selectedConversationId);
    }
  }

  async function handleVotePoll(poll: PostPollDto, optionId: string) {
    let nextIds: string[];
    if (poll.allowMultiple) {
      const current = poll.options.filter(o => o.votedByMe).map(o => o.id);
      nextIds = current.includes(optionId)
        ? current.filter(id => id !== optionId)
        : [...current, optionId];
      if (nextIds.length === 0) return;
    } else {
      nextIds = [optionId];
    }

    const res = await fetch(`/api/polls/${poll.id}/vote`, {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ optionIds: nextIds })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Unable to submit vote.');
      return;
    }
    if (selectedConversationId) await loadPosts(selectedConversationId);
  }

  async function handleAddComment(e: FormEvent, postId: string) {
    e.preventDefault();
    const draft = (commentDrafts[postId] ?? '').trim();
    if (!draft) return;
    const res = await fetch(`/api/posts/${postId}/comments`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ body: draft })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Unable to post comment.');
      return;
    }
    setCommentDrafts(prev => ({ ...prev, [postId]: '' }));
    setExpandedComments(prev => ({ ...prev, [postId]: true }));
    if (selectedConversationId) await loadPosts(selectedConversationId);
  }

  async function handleAddMember(e: FormEvent) {
    e.preventDefault();
    if (!selectedCommunity || !addMemberUserId) return;
    setError('');
    const res = await fetch(`/api/communities/${selectedCommunity.id}/members`, {
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
    const res = await fetch(`/api/communities/${selectedCommunity.id}/members/${targetUserId}`, {
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
    const res = await fetch(`/api/communities/${selectedCommunity.id}/admins/${member.userId}`, {
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
    const res = await fetch(`/api/communities/${selectedCommunity.id}/leave`, {
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
    const res = await fetch(`/api/communities/${selectedCommunity.id}/official`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ isOfficial: nextOfficial })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Only Verified Comms Admins with MFA can change Official status.');
      return;
    }
    await loadCommunities();
    setNotice(nextOfficial ? 'Community designated as Official.' : 'Official designation removed.');
  }

  async function handleSyncDepartment() {
    if (!selectedCommunity) return;
    setError('');
    setNotice('');
    const mapRes = await fetch(`/api/communities/${selectedCommunity.id}/department-mapping`, {
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
      const syncRes = await fetch(`/api/communities/${selectedCommunity.id}/sync-department`, {
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

  const availableToAdd = useMemo(() => {
    const memberIds = new Set(members.map(m => m.userId));
    return directoryUsers.filter(u => !memberIds.has(u.id));
  }, [directoryUsers, members]);

  const activePolicy = selectedSubChannel
    ? selectedSubChannel.postingPolicy
    : selectedCommunity?.mainFeedPostingPolicy ?? 'Members';

  return (
    <div className={`nx-chat-layout ${selectedConversationId ? 'has-selection' : ''}`}>
      {/* Left Sidebar: Teams Communities Tree & Discover */}
      <aside className="nx-chat-sidebar" aria-label="Communities list">
        <div className="nx-sidebar-head">
          <h2>{discoverMode ? 'Discover' : 'Communities'}</h2>
          <div className="nx-sidebar-head-actions">
            <Button
              variant={discoverMode ? 'primary' : 'ghost'}
              size="sm"
              iconOnly
              aria-label={discoverMode ? 'Show my communities' : 'Discover public communities'}
              title={discoverMode ? 'My communities' : 'Discover public communities'}
              onClick={() => setDiscoverMode(v => !v)}
            >
              <Compass size={16} aria-hidden="true" />
            </Button>
            <Button
              variant="ghost"
              size="sm"
              iconOnly
              aria-label="Create community"
              title="Create community"
              onClick={() => setCreateOpen(true)}
            >
              <Plus size={16} aria-hidden="true" />
            </Button>
          </div>
        </div>

        <div className="nx-conv-list">
          {discoverMode ? (
            discoverCommunities.length === 0 ? (
              <div className="nx-empty-side">No public communities available to discover.</div>
            ) : (
              discoverCommunities.map(comm => (
                <div className="nx-discover-card" key={comm.id}>
                  <div className="nx-discover-head">
                    <strong className="nx-conv-name">
                      {comm.name}
                      {comm.isOfficial && <Badge variant="role">Official</Badge>}
                    </strong>
                    <span className="nx-muted">{comm.memberCount} members</span>
                  </div>
                  {comm.description && <p className="nx-discover-desc">{comm.description}</p>}
                  <div className="nx-discover-actions">
                    {comm.isMember ? (
                      <Button size="sm" variant="default" onClick={() => selectCommunityMainFeed(comm)}>
                        Open
                      </Button>
                    ) : (
                      <Button size="sm" variant="primary" onClick={() => void handleJoinCommunity(comm.id)}>
                        Join
                      </Button>
                    )}
                  </div>
                </div>
              ))
            )
          ) : communities.length === 0 ? (
            <div className="nx-empty-side">
              <p>You have not joined any communities yet.</p>
              <div className="nx-empty-actions" style={{ marginTop: 10 }}>
                <Button size="sm" variant="primary" onClick={() => setCreateOpen(true)}>
                  Create community
                </Button>
                <Button size="sm" variant="default" onClick={() => setDiscoverMode(true)}>
                  Discover
                </Button>
              </div>
            </div>
          ) : (
            communities.map(comm => {
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
                          {comm.visibility === 'Private' && <Lock size={12} aria-label="Private" />}
                        </span>
                        {comm.isOfficial && <Badge variant="role">Official</Badge>}
                      </div>
                      <div className="nx-conv-sub">
                        {comm.memberCount} {comm.memberCount === 1 ? 'member' : 'members'}
                        {comm.channels.length > 0 ? ` · ${comm.channels.length} sub-ch` : ''}
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
          )}
        </div>
      </aside>

      {/* Main Feed Stage */}
      <section className="nx-chat-stage" aria-label="Community feed">
        {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        {notice && <ToastBar variant="ok" message={notice} onDismiss={() => setNotice('')} />}

        {!selectedCommunity || !selectedConversationId ? (
          <EmptyState
            icon={<Users size={36} aria-hidden="true" />}
            title="Welcome to Nexus Communities"
            description="Create a community for your team or department, post announcements with read acknowledgments, and run quick polls."
            actions={
              <>
                <Button variant="primary" onClick={() => setCreateOpen(true)}>
                  <Plus size={15} aria-hidden="true" /> Create community
                </Button>
                <Button variant="default" onClick={() => setDiscoverMode(true)}>
                  <Compass size={15} aria-hidden="true" /> Discover public
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
                    {activePolicy === 'AdminsOnly' && <Badge variant="warning">Admins only</Badge>}
                  </h3>
                  <div className="nx-stage-subtitle">
                    <span>{selectedCommunity.visibility}</span>
                    <span>·</span>
                    <span>{selectedCommunity.memberCount} members</span>
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
                ) : (
                  <div className="nx-readonly-banner" role="note">
                    <Megaphone size={15} aria-hidden="true" />
                    <span>Only community owners and admins can publish new posts in this channel. You can still react, comment, and acknowledge announcements.</span>
                  </div>
                )}

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
                        className={`nx-post-card ${post.isAnnouncement ? 'announcement' : ''} ${post.isPinned ? 'pinned' : ''}`}
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
                                <UserNameWithBadge
                                  user={{ displayName: post.authorDisplayName, isUnverified: post.authorIsUnverified }}
                                />
                                <span className="nx-msg-time">{formatPostTime(post.createdAt)}</span>
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
                              <Badge variant="warning">
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
                                  href={`/api/posts/${post.id}/acknowledgments?format=csv`}
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
                              const r = reactionMap.get(emoji);
                              return (
                                <button
                                  type="button"
                                  key={emoji}
                                  className={`nx-react-pill ${r?.reactedByMe ? 'active' : ''}`}
                                  onClick={() => void handleToggleReaction(post, emoji)}
                                  aria-label={`React with ${emoji}`}
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
                              <div className="nx-comment-item" key={c.id}>
                                <div className="nx-comment-head">
                                  <UserNameWithBadge
                                    user={{ displayName: c.authorDisplayName, isUnverified: c.authorIsUnverified }}
                                  />
                                  <span className="nx-msg-time">{formatPostTime(c.createdAt)}</span>
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
                    {/* Official designation for Verified CommsAdmin */}
                    {currentOrgRole === 'CommsAdmin' && !currentIsUnverified && (
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
                            <option value="">Select colleague…</option>
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
                            <UserNameWithBadge user={m} />
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
        description="Starts with a main community feed. You can add optional sub-channels anytime."
      >
        <form onSubmit={handleCreateCommunity}>
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
          <Field label="Visibility" htmlFor="new-comm-vis">
            <select
              id="new-comm-vis"
              className="nx-select"
              value={newCommVisibility}
              onChange={e => setNewCommVisibility(e.target.value as 'Public' | 'Private')}
            >
              <option value="Public">Public — Discoverable by colleagues</option>
              <option value="Private">Private — Invite only</option>
            </select>
          </Field>
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

      {/* Add Sub-Channel Modal */}
      <Modal
        open={Boolean(addChannelForId)}
        onOpenChange={open => {
          if (!open) setAddChannelForId(null);
        }}
        title="Add sub-channel"
        description="Sub-channels inherit this community's members and roles automatically."
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
