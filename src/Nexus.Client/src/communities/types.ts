import { RefObject } from 'react';
import { AttachmentItem } from '../attachments/attachmentUtils';
import { AvailabilityDto, LiveAvailabilityEntry } from '../availability';
import { CommunityFeedDelta } from '../chat/types';
import { CommunityJumpTarget, type ForwardableContent } from '../contentLinks';
import { OrganizationItem } from '../organizationControls';
import { ProfileCardTarget } from '../ProfileCardModal';

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

export interface PostCommentDto {
  id: string;
  postId: string;
  authorUserId: string;
  authorDisplayName: string;
  body: string;
  createdAt: string;
  pending?: boolean;
}

export interface PostReactionDto {
  emoji: string;
  count: number;
  reactedByMe: boolean;
}

export interface ReactionChange {
  postId: string;
  emoji: string;
  reactedByMe: boolean;
}

export interface PollOptionDto {
  id: string;
  label: string;
  votes: number;
  votedByMe: boolean;
}

export interface PostPollDto {
  id: string;
  question: string;
  allowMultiple: boolean;
  closesAt?: string | null;
  totalVoters: number;
  options: PollOptionDto[];
}

export interface ChannelPostDto {
  id: string;
  conversationId: string;
  authorUserId: string;
  authorDisplayName: string;
  authorAvatarUrl?: string | null;
  title?: string | null;
  body: string;
  isForwarded?: boolean;
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

export interface ChannelFileItem extends AttachmentItem {
  postId?: string | null;
  postTitle?: string | null;
  uploaderDisplayName?: string | null;
  sourceLink?: string | null;
}

export interface CommunityMemberDto {
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

export interface CommunitiesWorkspaceProps {
  visible: boolean;
  currentUserId: string;
  currentDisplayName: string;
  organizations: OrganizationItem[];
  requestedChannel: { id: string } | null;
  jumpTarget?: CommunityJumpTarget | null;
  feedListenerRef: RefObject<((conversationId?: string, delta?: CommunityFeedDelta | null) => void) | null>;
  onOpenContentLink?: (token: string) => void;
  onForwardContent: (source: ForwardableContent) => void;
  onOpenCreateWorkspace: () => void;
  onOpenJoinWorkspace: () => void;
  onOpenWorkspaceSettings: (org: OrganizationItem) => void;
  availabilityMap: Record<string, LiveAvailabilityEntry>;
  nowMs: number;
  onOpenProfileCard: (target: ProfileCardTarget) => void;
}

export const QUICK_REACTIONS = ['👍', '❤️', '🎉', '👀'];

export function applyReaction(post: ChannelPostDto, change: ReactionChange): ChannelPostDto {
  const existing = post.reactions.find(reaction => reaction.emoji === change.emoji);
  if (Boolean(existing?.reactedByMe) === change.reactedByMe) return post;

  const reactions = post.reactions.filter(reaction => reaction.emoji !== change.emoji);
  const count = Math.max(0, (existing?.count ?? 0) + (change.reactedByMe ? 1 : -1));
  if (count > 0) {
    reactions.push({ emoji: change.emoji, count, reactedByMe: change.reactedByMe });
  }
  return { ...post, reactions };
}

export function formatPostTime(iso: string): string {
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
