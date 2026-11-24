import { FormEvent, MouseEvent } from 'react';
import { Bell, Check, CheckCircle2, Download, Link2, Megaphone, MessageSquare, Pin } from 'lucide-react';
import { AttachmentGallery } from '../attachments/AttachmentGallery';
import { AttachmentItem } from '../attachments/attachmentUtils';
import { LiveAvailabilityEntry, resolveUserAvailability } from '../availability';
import { renderSafeMarkdown } from '../markdown';
import { ProfileCardTarget } from '../ProfileCardModal';
import { orgApiPath } from '../scope';
import { Avatar, Badge, Button } from '../ui';
import {
  ChannelPostDto,
  PostPollDto,
  QUICK_REACTIONS,
  ReactionChange,
  applyReaction,
  formatPostTime
} from './types';

export interface CommunityPostCardProps {
  post: ChannelPostDto;
  organizationId: string;
  canManage: boolean;
  activeIsArchived: boolean;
  highlighted: boolean;
  commentsOpen: boolean;
  commentDraft: string;
  pendingReactions: Record<string, ReactionChange>;
  availabilityMap: Record<string, LiveAvailabilityEntry>;
  nowMs: number;
  onInlineLinkClick: (e: MouseEvent<HTMLElement>) => void;
  onOpenProfileCard: (target: ProfileCardTarget) => void;
  onCopyPostLink: (post: ChannelPostDto) => void;
  onTogglePin: (post: ChannelPostDto) => void;
  onPreviewImage: (att: AttachmentItem) => void;
  onAcknowledge: (postId: string) => void;
  onRemind: (postId: string) => void;
  onVotePoll: (poll: PostPollDto, optionId: string) => void;
  onToggleReaction: (post: ChannelPostDto, emoji: string) => void;
  onToggleComments: (postId: string, nextOpen: boolean) => void;
  onCommentDraftChange: (postId: string, value: string) => void;
  onAddComment: (e: FormEvent, postId: string) => void;
}

export function CommunityPostCard({
  post,
  organizationId,
  canManage,
  activeIsArchived,
  highlighted,
  commentsOpen,
  commentDraft,
  pendingReactions,
  availabilityMap,
  nowMs,
  onInlineLinkClick,
  onOpenProfileCard,
  onCopyPostLink,
  onTogglePin,
  onPreviewImage,
  onAcknowledge,
  onRemind,
  onVotePoll,
  onToggleReaction,
  onToggleComments,
  onCommentDraftChange,
  onAddComment
}: CommunityPostCardProps) {
  const reactionMap = new Map(post.reactions.map(r => [r.emoji, r]));

  return (
    <article
      data-post-id={post.id}
      tabIndex={-1}
      className={`nx-post-card ${post.isAnnouncement ? 'announcement' : ''} ${post.isPinned ? 'pinned' : ''} ${post.pending ? 'pending' : ''} ${highlighted ? 'nx-highlight-target' : ''}`}
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
                organizationId,
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
                    organizationId,
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
              onClick={() => onCopyPostLink(post)}
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
              onClick={() => onTogglePin(post)}
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
          onClick={onInlineLinkClick}
          dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(post.body) }}
        />
      )}

      {post.attachments && post.attachments.length > 0 && (
        <AttachmentGallery attachments={post.attachments} onPreviewImage={onPreviewImage} />
      )}

      {post.requiresAcknowledgment && (
        <div className="nx-ack-bar">
          <div className="nx-ack-status">
            {post.acknowledgedByMe ? (
              <Badge variant="success">
                <CheckCircle2 size={12} aria-hidden="true" /> Acknowledged
              </Badge>
            ) : !activeIsArchived ? (
              <Button variant="primary" size="sm" onClick={() => onAcknowledge(post.id)}>
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
                href={orgApiPath(organizationId, `/api/posts/${post.id}/acknowledgments?format=csv`)}
                download
              >
                <Download size={13} aria-hidden="true" /> Export CSV
              </a>
              {!activeIsArchived && (
                <Button variant="default" size="sm" onClick={() => onRemind(post.id)}>
                  <Bell size={13} aria-hidden="true" /> Remind
                </Button>
              )}
            </div>
          )}
        </div>
      )}

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
                  onClick={() => onVotePoll(post.poll!, opt.id)}
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
                onClick={() => onToggleReaction(post, emoji)}
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
          onClick={() => onToggleComments(post.id, !commentsOpen)}
        >
          <MessageSquare size={14} aria-hidden="true" />{' '}
          {post.comments.length === 0
            ? 'Comment'
            : `${post.comments.length} ${post.comments.length === 1 ? 'comment' : 'comments'}`}
        </Button>
      </div>

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
                      organizationId,
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
                onClick={onInlineLinkClick}
                dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(c.body) }}
              />
            </div>
          ))}

          {!activeIsArchived && (
            <form className="nx-comment-form" onSubmit={e => onAddComment(e, post.id)}>
              <input
                type="text"
                aria-label="Write a comment"
                placeholder="Write a comment…"
                maxLength={4000}
                value={commentDraft}
                onChange={e => onCommentDraftChange(post.id, e.target.value)}
              />
              <Button
                type="submit"
                variant="default"
                size="sm"
                disabled={!commentDraft.trim()}
              >
                Reply
              </Button>
            </form>
          )}
        </div>
      )}
    </article>
  );
}
