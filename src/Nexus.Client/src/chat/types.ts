import { MutableRefObject } from 'react';
import { AttachmentItem } from '../attachments/attachmentUtils';
import { AvailabilityChangedEvent, AvailabilityDto, LiveAvailabilityEntry } from '../availability';
import { ChatJumpTarget } from '../contentLinks';
import { ProfileCardTarget } from '../ProfileCardModal';
import type { DmReadReceipt, DmReadReceiptPreference } from './dmReadReceipts';

export interface ConversationListItem {
  id: string;
  kind: string;
  title: string;
  role: string;
  nextSeq: number;
  latestMainSeq?: number;
  changeSeq: number;
  lastReadSeq: number;
  manualUnreadFromSeq?: number | null;
  manualUnreadVersion?: number;
  dmReadReceipt?: DmReadReceipt | null;
  unreadCount: number;
  memberCount: number;
  updatedAt?: string;
  lastMessagePreview?: {
    bodyPreview: string;
    senderDisplayName: string;
    createdAt: string;
    seq: number;
    messageId?: string;
    version?: number;
  } | null;
  peer?: {
    id: string;
    username: string;
    displayName: string;
    isOnline: boolean;
    availability?: AvailabilityDto | null;
    isBlockedByMe?: boolean;
    avatarUrl?: string | null;
  } | null;
}

export interface ChatReadState {
  conversationId: string;
  lastReadSeq: number;
  unreadCount: number;
  manualUnreadFromSeq: number | null;
  manualUnreadVersion: number;
}

export interface ReactionSummary {
  emoji: string;
  count: number;
  reactedByMe: boolean;
}

export interface ChatReactionDelta {
  type: 'reaction';
  messageId: string;
  seq?: number;
  parentMessageId?: string | null;
  emoji: string;
  userId: string;
  reacted: boolean;
}

export interface ThreadReplyDelta {
  type: 'thread_reply_created';
  messageId: string;
  seq: number;
  message: ChatMessageItem;
  replyCount: number;
  lastReplyAt: string;
  lastReplySeq: number;
}

export interface ThreadState {
  conversationId: string;
  parentMessageId: string;
  isFollowing: boolean;
  lastReadReplySeq: number;
  lastReplySeq?: number | null;
  hasUnread: boolean;
  version: number;
}

export interface MessageMutationDelta {
  type: 'message_edited' | 'message_recalled';
  messageId: string;
  seq: number;
  parentMessageId?: string | null;
  version: number;
  body: string;
  editedAt: string | null;
  deletedAt: string | null;
}

export interface MessagePinDelta {
  type: 'message_pin';
  messageId: string;
  parentMessageId?: string | null;
  pinned: boolean;
}

export interface MessageSaveDelta {
  type: 'message_save';
  conversationId: string;
  messageId: string;
  parentMessageId?: string | null;
  saved: boolean;
  version: number;
}

export type ChatDelta = ChatReactionDelta | ThreadReplyDelta | MessageMutationDelta | MessagePinDelta | MessageSaveDelta;

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
  version?: number;
  isPinned?: boolean;
  pinSnapshotSeq?: number;
  isSaved?: boolean;
  savedVersion?: number;
  pending?: boolean;
  attachments?: AttachmentItem[];
  reactions?: ReactionSummary[];
  reactionSnapshotSeq?: number;
  parentMessageId?: string | null;
  replyCount?: number;
  lastReplyAt?: string | null;
  lastReplySeq?: number | null;
  isFollowing?: boolean;
  lastReadReplySeq?: number;
  threadStateVersion?: number;
  hasUnread?: boolean;
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

export interface GroupMemberItem {
  userId: string;
  username: string;
  displayName: string;
  role: string;
  isActive?: boolean;
  isOnline?: boolean;
  availability?: AvailabilityDto | null;
}

export interface ConversationCursor {
  updatedAt: string;
  id: string;
}

export interface FilesCursor {
  createdAt: string;
  id: string;
}

export interface CommunityFeedDelta {
  type:
    | 'reaction'
    | 'comment'
    | 'pin'
    | 'ownership'
    | 'post_created'
    | 'community_updated'
    | 'channel_updated'
    | 'channel_archived'
    | 'channel_restored';
  postId?: string;
  communityId?: string;
  channelId?: string;
  conversationId?: string;
  name?: string;
  description?: string | null;
  updatedAt?: string;
  archivedAt?: string | null;
  archivedByUserId?: string | null;
  restoredAt?: string | null;
  restoredByUserId?: string | null;
  emoji?: string;
  userId?: string;
  reacted?: boolean;
  isPinned?: boolean;
  post?: {
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
    attachments?: AttachmentItem[];
  };
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

export interface ChatWorkspaceProps {
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
  onMessageRecalled?: (messageId: string) => void;
  showDmReadReceipts: boolean;
  onDmReadReceiptPreference: (preference: DmReadReceiptPreference) => void;
  onConnectionStateChange: (state: 'connected' | 'reconnecting' | 'offline') => void;
  onUnreadTotalChange: (total: number) => void;
  onCommunityFeedChanged: (conversationId?: string, delta?: CommunityFeedDelta | null) => void;
  onVersionStale: () => void;
  availabilityMap: Record<string, LiveAvailabilityEntry>;
  nowMs: number;
  onAvailabilityChanged: (evt: AvailabilityChangedEvent) => void;
  onOpenProfileCard: (target: ProfileCardTarget) => void;
}
