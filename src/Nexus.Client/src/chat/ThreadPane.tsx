import { FormEvent, MouseEvent, MutableRefObject, useCallback, useEffect, useRef, useState } from 'react';
import { Bell, BellOff, Link2, Send, X } from 'lucide-react';
import { AttachmentGallery } from '../attachments/AttachmentGallery';
import { AttachmentItem } from '../attachments/attachmentUtils';
import { useChatReading } from '../chatReading';
import { shouldSendOnEnter } from '../composerKeys';
import { renderSafeMarkdown } from '../markdown';
import { generateClientMsgId } from '../offlineQueue';
import { chatFetch, personalApiPath, protectedRequest } from '../scope';
import { Button, Field, ToastBar } from '../ui';
import { applyChatDelta, applyMessageMutation, isMessageMutation, RECALLED_MESSAGE_LABEL } from './chatDeltaReducer';
import { MessageMutationActions, OpenMessageMutation } from './MessageMutationActions';
import { ChatDelta, ChatMessageItem, MessageMutationDelta, ThreadState } from './types';

export interface ThreadTarget {
  conversationId: string;
  parentMessageId: string;
  replyId?: string;
  replySeq?: number;
}

interface ThreadPage {
  parent: ChatMessageItem;
  state: ThreadState;
  items: ChatMessageItem[];
  changeSeq: number;
  hasMore: boolean;
  hasNewer: boolean;
  nextBeforeSeq?: number | null;
  nextAfterSeq?: number | null;
}

type PageCursor = { kind: 'beforeSeq' | 'afterSeq' | 'aroundSeq'; seq: number };

interface PageRequest {
  controller: AbortController;
  events: { delta: ChatDelta; changeSeq: number }[];
}

export type ThreadDeltaReceiver = (conversationId: string, delta: ChatDelta, changeSeq: number) => void;

interface PendingReply {
  clientMsgId: string;
  body: string;
  failed?: boolean;
}

interface ThreadPaneProps {
  target: ThreadTarget;
  currentUserId: string;
  visible: boolean;
  blocked: boolean;
  reconnectCount: number;
  deltaRef: MutableRefObject<ThreadDeltaReceiver | null>;
  liveState?: ThreadState;
  onState: (state: ThreadState) => void;
  onPendingReply: (parentMessageId: string, clientMsgId: string, pending: boolean) => void;
  onConfirmReply: (conversationId: string, clientMsgId: string) => Promise<void>;
  onClose: () => void;
  onCopyLink: (message: ChatMessageItem) => Promise<boolean>;
  onInlineLink: (event: MouseEvent<HTMLDivElement>) => void;
  onPreviewImage: (attachment: AttachmentItem) => void;
  mutationError?: string;
  pendingMutations: ReadonlyMap<string, MessageMutationDelta>;
  onDismissMutationError: () => void;
  onOpenMutation: OpenMessageMutation;
}

const replyTime = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' });

function mergeReplies(current: ChatMessageItem[], incoming: ChatMessageItem[]) {
  const byId = new Map(current.map(item => [item.id, item]));
  for (const reply of incoming) {
    const existing = byId.get(reply.id);
    const version = reply.version ?? 1;
    const existingVersion = existing?.version ?? 1;
    if (!existing || version > existingVersion || (version === existingVersion &&
      (reply.reactionSnapshotSeq ?? 0) > (existing.reactionSnapshotSeq ?? 0))) {
      byId.set(reply.id, reply);
    }
  }
  return [...byId.values()].sort((a, b) => a.seq - b.seq);
}

function applyThreadDelta(
  page: ThreadPage,
  delta: ChatDelta,
  changeSeq: number,
  currentUserId: string
): ThreadPage {
  if (delta.type === 'thread_reply_created' && delta.messageId === page.parent.id) {
    const [parent] = applyChatDelta([page.parent], delta, currentUserId, changeSeq);
    const items = page.nextAfterSeq ? page.items : mergeReplies(page.items, [delta.message]);
    return { ...page, parent, items };
  }
  if (delta.type === 'reaction' || isMessageMutation(delta)) {
    const [parent] = applyChatDelta([page.parent], delta, currentUserId, changeSeq);
    const items = applyChatDelta(page.items, delta, currentUserId, changeSeq);
    if (parent !== page.parent || items !== page.items) return { ...page, parent, items };
  }
  return page;
}

export function ThreadPane({
  target, currentUserId, visible, blocked, reconnectCount, deltaRef, liveState, onState,
  onPendingReply, onConfirmReply, onClose, onCopyLink, onInlineLink, onPreviewImage,
  mutationError, pendingMutations, onDismissMutationError, onOpenMutation
}: ThreadPaneProps) {
  const { conversationId, parentMessageId, replyId, replySeq } = target;
  const endpoint = '/api/conversations/' + conversationId + '/messages/' + parentMessageId + '/thread';
  const [page, setPage] = useState<ThreadPage | null>(null);
  const [draft, setDraft] = useState('');
  const [pendingReplies, setPendingReplies] = useState<PendingReply[]>([]);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState('');
  const [actionError, setActionError] = useState('');
  const [copied, setCopied] = useState<boolean | null>(null);
  const [sending, setSending] = useState(false);
  const [followingOverride, setFollowingOverride] = useState<boolean | null>(null);
  const inFlightIdsRef = useRef(new Set<string>());
  const pageRequest = useRef<PageRequest | null>(null);
  const failedCursor = useRef<PageCursor | undefined>(undefined);
  const heading = useRef<HTMLHeadingElement>(null);
  const compositionActive = useRef(false);
  const jumpHandled = useRef(false);
  const mounted = useRef(false);

  const load = useCallback(async (cursor?: PageCursor, refresh = false) => {
    if (pageRequest.current) return;
    const controller = new AbortController();
    const request: PageRequest = { controller, events: [] };
    pageRequest.current = request;
    setLoading(true);
    setPageError('');
    failedCursor.current = cursor;
    try {
      const query = new URLSearchParams({ limit: '50' });
      if (cursor) query.set(cursor.kind, String(cursor.seq));
      const response = await chatFetch(endpoint + '?' + query, { signal: controller.signal });
      if (!response.ok) throw new Error(response.status === 404 || response.status === 403
        ? 'This thread is unavailable.' : 'Unable to load replies.');
      const loaded: ThreadPage = await response.json();
      if (controller.signal.aborted) return;
      onState(loaded.state);
      const items = loaded.items.map(reply => ({ ...reply, reactionSnapshotSeq: loaded.changeSeq }));
      const parent = { ...loaded.parent, reactionSnapshotSeq: loaded.changeSeq };
      setPage(previous => {
        const extendsPage = previous && cursor && cursor.kind !== 'aroundSeq';
        const preservePage = refresh && previous && !previous.nextAfterSeq
          && (loaded.items.length === 0 || previous.items.some(reply => reply.id === loaded.items[0]?.id));
        let result: ThreadPage = {
          ...loaded,
          parent,
          items: extendsPage || preservePage ? mergeReplies(previous.items, items) : items,
          nextBeforeSeq: cursor?.kind === 'afterSeq' || preservePage ? previous?.nextBeforeSeq : loaded.hasMore ? loaded.nextBeforeSeq : null,
          nextAfterSeq: cursor?.kind === 'beforeSeq' ? previous?.nextAfterSeq : loaded.hasNewer ? loaded.nextAfterSeq : null
        };
        for (const event of request.events) {
          if (event.changeSeq > loaded.changeSeq || isMessageMutation(event.delta)) {
            result = applyThreadDelta(result, event.delta, event.changeSeq, currentUserId);
          }
        }
        return result;
      });
    } catch (error) {
      if (!controller.signal.aborted) setPageError(error instanceof Error ? error.message : 'Unable to load replies.');
    } finally {
      if (pageRequest.current === request) {
        pageRequest.current = null;
        if (!controller.signal.aborted) setLoading(false);
      }
    }
  }, [currentUserId, endpoint, onState]);

  useEffect(() => {
    mounted.current = true;
    const cursor = replySeq ? { kind: 'aroundSeq' as const, seq: replySeq } : undefined;
    heading.current?.focus();
    void load(cursor);
    return () => {
      mounted.current = false;
      pageRequest.current?.controller.abort();
      pageRequest.current = null;
    };
  }, [load, replySeq]);

  useEffect(() => {
    const receive: ThreadDeltaReceiver = (id, delta, changeSeq) => {
      if (id !== conversationId || (delta.type === 'thread_reply_created'
        ? delta.messageId !== parentMessageId
        : (delta.parentMessageId ?? delta.messageId) !== parentMessageId)) return;
      pageRequest.current?.events.push({ delta, changeSeq });
      setPage(current => current ? applyThreadDelta(current, delta, changeSeq, currentUserId) : current);
    };
    deltaRef.current = receive;
    return () => {
      if (deltaRef.current === receive) deltaRef.current = null;
    };
  }, [conversationId, currentUserId, deltaRef, parentMessageId]);

  const previousReconnect = useRef(reconnectCount);
  useEffect(() => {
    if (reconnectCount === previousReconnect.current) return;
    previousReconnect.current = reconnectCount;
    void load(replySeq ? { kind: 'aroundSeq', seq: replySeq } : undefined, !replySeq);
  }, [load, reconnectCount, replySeq]);

  const state = liveState && liveState.version >= (page?.state.version ?? 0) ? liveState : page?.state;
  const following = followingOverride ?? state?.isFollowing ?? false;
  const trackRead = useCallback(async (_parentId: string, seq: number) => {
    const response = await protectedRequest(personalApiPath(endpoint + '/read'), 'PUT', { lastReadReplySeq: seq });
    if (!response.ok) throw new Error('Unable to update thread read state.');
    const updated: ThreadState = await response.json();
    onState(updated);
    return updated.lastReadReplySeq;
  }, [endpoint, onState]);
  const outgoing = pendingReplies.filter(pending => !page?.items.some(reply =>
    reply.senderId === currentUserId && reply.clientMsgId === pending.clientMsgId));
  const reading = useChatReading(parentMessageId, visible, page?.items, outgoing.length,
    state?.lastReadReplySeq ?? 0, trackRead, reconnectCount);

  useEffect(() => {
    if (!replyId || jumpHandled.current || !page?.items.some(item => item.id === replyId)) return;
    const frame = requestAnimationFrame(() => {
      const reply = reading.feedRef.current?.querySelector<HTMLElement>('[data-thread-reply-id="' + replyId + '"]');
      if (!reply) return;
      jumpHandled.current = true;
      reply.scrollIntoView({ block: 'center' });
      reply.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [page?.items, reading.feedRef, replyId]);

  async function toggleFollowing() {
    if (!page || inFlightIdsRef.current.has('following')) return;
    inFlightIdsRef.current.add('following');
    setFollowingOverride(!following);
    setActionError('');
    try {
      const response = await protectedRequest(personalApiPath(endpoint + '/follow'), following ? 'DELETE' : 'PUT');
      if (!response.ok) throw new Error('Unable to update thread following.');
      onState(await response.json() as ThreadState);
    } catch {
      setActionError('Unable to update thread following.');
    } finally {
      inFlightIdsRef.current.delete('following');
      setFollowingOverride(null);
    }
  }

  async function sendReply(reply: PendingReply) {
    if (!page || blocked || visibleMessage(page.parent).deletedAt || inFlightIdsRef.current.has(parentMessageId)) return;
    inFlightIdsRef.current.add(parentMessageId);
    setSending(true);
    onPendingReply(parentMessageId, reply.clientMsgId, true);
    setPendingReplies(current => [...current.filter(item => item.clientMsgId !== reply.clientMsgId), { ...reply, failed: false }]);
    setActionError('');
    try {
      const response = await protectedRequest(
        personalApiPath('/api/conversations/' + conversationId + '/messages'),
        'POST',
        { clientMsgId: reply.clientMsgId, body: reply.body, parentMessageId, preserveReadState: true }
      );
      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || 'Unable to send the reply.');
      }
      const saved: ChatMessageItem = await response.json();
      setPage(current => current && !current.nextAfterSeq
        ? { ...current, items: mergeReplies(current.items, [saved]) } : current);
      setPendingReplies(current => current.filter(item => item.clientMsgId !== reply.clientMsgId));
      await onConfirmReply(conversationId, reply.clientMsgId).catch(() => {});
    } catch (error) {
      if (mounted.current) {
        setActionError(error instanceof Error ? error.message : 'Unable to send the reply.');
        setPendingReplies(current => current.map(item => item.clientMsgId === reply.clientMsgId ? { ...item, failed: true } : item));
      }
    } finally {
      inFlightIdsRef.current.delete(parentMessageId);
      if (mounted.current) setSending(false);
      onPendingReply(parentMessageId, reply.clientMsgId, false);
    }
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!draft.trim() || !page || page.nextAfterSeq || blocked || page.parent.deletedAt || inFlightIdsRef.current.has(parentMessageId)) return;
    void sendReply({ clientMsgId: generateClientMsgId(), body: draft.trim() });
    setDraft('');
  }

  const beforeSeq = page?.nextBeforeSeq;
  const afterSeq = page?.nextAfterSeq;
  const root = page ? visibleMessage(page.parent) : null;

  function visibleMessage(item: ChatMessageItem) {
    const pending = pendingMutations.get(item.id);
    return pending ? applyMessageMutation(item, pending) : item;
  }

  function actions(saved: ChatMessageItem) {
    return <MessageMutationActions message={saved} currentUserId={currentUserId} blocked={blocked}
      pending={pendingMutations.has(saved.id)} onOpen={onOpenMutation} />;
  }

  function messageBody(item: ChatMessageItem) {
    return (
      <>
        {item.deletedAt ? <p className="nx-muted">{RECALLED_MESSAGE_LABEL}</p> : (
          <>
            {item.body && <div className="nx-msg-body" onClick={onInlineLink} dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(item.body) }} />}
            {!!item.attachments?.length && <AttachmentGallery attachments={item.attachments} onPreviewImage={onPreviewImage} />}
          </>
        )}
      </>
    );
  }

  return (
    <aside id="nx-thread-pane" className="nx-drawer nx-thread-pane" aria-labelledby="nx-thread-title" onKeyDown={event => {
      if (event.key === 'Escape' && !event.defaultPrevented && !event.nativeEvent.isComposing) {
        event.stopPropagation();
        onClose();
      }
    }}>
      <div className="nx-drawer-head">
        <h2 id="nx-thread-title" ref={heading} tabIndex={-1}>Thread</h2>
        <div className="nx-inline-actions">
          <Button size="sm" variant="ghost" disabled={!page || followingOverride !== null} onClick={() => void toggleFollowing()}>
            {following ? <BellOff size={14} aria-hidden="true" /> : <Bell size={14} aria-hidden="true" />}
            {following ? 'Unfollow' : 'Follow'}
          </Button>
          <Button size="sm" variant="ghost" iconOnly aria-label="Close thread" onClick={onClose}>
            <X size={16} aria-hidden="true" />
          </Button>
        </div>
      </div>
      {copied !== null && <ToastBar variant={copied ? 'ok' : 'error'}
        message={copied ? 'Reply link copied.' : 'Unable to copy link automatically.'}
        onDismiss={() => setCopied(null)} />}
      {mutationError && <ToastBar variant="error" message={mutationError} onDismiss={onDismissMutationError} />}
      <div className="nx-drawer-body nx-thread-feed" ref={reading.feedRef} onScroll={reading.onScroll}
        role="region" aria-label="Thread replies" tabIndex={0}>
        {page && root && (
          <div className="nx-thread-parent" data-thread-message-id={root.id} tabIndex={-1}>
            <div className="nx-thread-message-head">
              <strong>{root.senderDisplayName}</strong>
              {root.editedAt && !root.deletedAt && <span className="nx-muted">Edited</span>}
              {!root.deletedAt && actions(page.parent)}
            </div>
            {messageBody(root)}
          </div>
        )}
        {beforeSeq ? (
          <Button size="sm" disabled={loading} onClick={() => void load({ kind: 'beforeSeq', seq: beforeSeq })}>
            Load older replies
          </Button>
        ) : null}
        {loading && <p className="nx-muted" role="status">Loading replies…</p>}
        {pageError && (
          <div role="alert">
            <p className="nx-error">{pageError}</p>
            <Button size="sm" onClick={() => void load(failedCursor.current)}>Retry</Button>
          </div>
        )}
        {page?.items.length === 0 && outgoing.length === 0 && <p className="nx-muted">No replies yet.</p>}
        {page?.items.map(saved => {
          const item = visibleMessage(saved);
          return (
            <article key={item.id} className={'nx-thread-reply' + (item.id === replyId ? ' nx-highlight-target' : '')}
              tabIndex={-1} data-thread-reply-id={item.id} data-message-seq={item.seq}>
              <div className="nx-thread-message-head">
                <strong>{item.senderId === currentUserId ? 'You' : item.senderDisplayName}</strong>
                <time className="nx-muted" dateTime={item.createdAt}>{replyTime.format(new Date(item.createdAt))}
                  {item.editedAt && !item.deletedAt && <> · <span>Edited</span></>}</time>
                {!item.deletedAt && actions(saved)}
                {!item.deletedAt && (
                  <Button size="sm" variant="ghost" iconOnly className="nx-thread-copy" aria-label="Copy link to reply"
                    onClick={() => void onCopyLink(item).then(setCopied)}>
                    <Link2 size={13} aria-hidden="true" />
                  </Button>
                )}
              </div>
              {messageBody(item)}
            </article>
          );
        })}
        {outgoing.map(item => (
          <article key={item.clientMsgId} className="nx-thread-reply">
            <div className="nx-thread-message-head">
              <strong>You</strong>
              <span className="nx-muted" role="status">{item.failed ? 'Send failed' : 'Sending…'}</span>
            </div>
            <div className="nx-msg-body" onClick={onInlineLink} dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(item.body) }} />
            {item.failed && <Button size="sm" disabled={blocked || !!root?.deletedAt || sending} onClick={() => void sendReply(item)}>Retry send</Button>}
          </article>
        ))}
        {afterSeq ? (
          <Button size="sm" disabled={loading} onClick={() => void load({ kind: 'afterSeq', seq: afterSeq })}>
            Load newer replies
          </Button>
        ) : null}
      </div>
      {reading.showNewMessages && !page?.nextAfterSeq && (
        <div className="nx-thread-new">
          <Button size="sm" onClick={reading.showNewest}>New replies</Button>
        </div>
      )}
      {page && (
        <form className="nx-thread-composer" onSubmit={submit}>
          {actionError && <p className="nx-error" role="alert">{actionError}</p>}
          {blocked && <p className="nx-muted">Replies are unavailable in this chat.</p>}
          <Field label="Reply" htmlFor="nx-thread-reply" compact>
            <textarea id="nx-thread-reply" rows={2} maxLength={4000} value={draft}
              disabled={blocked || !!root?.deletedAt || !!page.nextAfterSeq}
              onChange={event => setDraft(event.target.value)}
              onCompositionStart={() => { compositionActive.current = true; }}
              onCompositionEnd={() => { compositionActive.current = false; }}
              onKeyDown={event => {
                if (shouldSendOnEnter(event.nativeEvent, compositionActive.current)) {
                  event.preventDefault();
                  event.currentTarget.form?.requestSubmit();
                }
              }} />
          </Field>
          <Button type="submit" size="sm" variant="primary"
            disabled={blocked || !!root?.deletedAt || !!page.nextAfterSeq || !draft.trim() || sending}>
            <Send size={14} aria-hidden="true" /> Send reply
          </Button>
        </form>
      )}
    </aside>
  );
}
