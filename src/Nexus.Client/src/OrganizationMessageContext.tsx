import { useEffect, useRef, useState } from 'react';
import { AttachmentGallery } from './attachments/AttachmentGallery';
import DirectCallButtons from './calls/DirectCallButtons';
import { ImageLightboxModal } from './attachments/ImageLightboxModal';
import type { AttachmentItem } from './attachments/attachmentUtils';
import type { ChatMessageItem } from './chat/types';
import type { ForwardableContent, ResolvedContentLink } from './contentLinks';
import { renderSafeMarkdown } from './markdown';
import { orgApiPath } from './scope';
import { Button, Modal } from './ui';

export type OrganizationMessageTarget = Extract<ResolvedContentLink, { kind: 'message' }> & { organizationId: string };

export default function OrganizationMessageContext({ target, onClose, onForwardContent }: {
  target: OrganizationMessageTarget;
  onClose: () => void;
  onForwardContent: (source: ForwardableContent) => void;
}) {
  const [messages, setMessages] = useState<ChatMessageItem[] | null>(null);
  const [error, setError] = useState('');
  const [image, setImage] = useState<AttachmentItem | null>(null);
  const chosen = useRef<HTMLElement>(null);

  useEffect(() => {
    const request = new AbortController();
    const base = `/api/conversations/${target.conversationId}/messages`;
    const path = target.parentMessageId ? `${base}/${target.parentMessageId}/thread` : base;
    void (async () => {
      try {
        const response = await fetch(orgApiPath(target.organizationId, `${path}?aroundSeq=${target.seq}&limit=50`), { signal: request.signal });
        if (!response.ok) throw new Error();
        const data = await response.json() as { items: ChatMessageItem[]; parent?: ChatMessageItem };
        if (!data.items.some(item => item.id === target.messageId)) throw new Error();
        if (!request.signal.aborted) setMessages(data.parent ? [data.parent, ...data.items] : data.items);
      } catch {
        if (!request.signal.aborted) setError('This message is unavailable.');
      }
    })();
    return () => request.abort();
  }, [target]);

  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      chosen.current?.scrollIntoView({ block: 'center' });
      chosen.current?.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [messages]);

  return (
    <>
      <Modal open title={target.parentMessageId ? 'Thread context' : 'Message context'}
        description="Messages around the linked message." onOpenChange={open => { if (!open) onClose(); }}>
        {error ? <p className="nx-error" role="alert">{error}</p> : !messages && <p role="status">Loading messages…</p>}
        {messages && target.conversationKind === 'Dm' && (
          <div className="nx-direct-call-context">
            <DirectCallButtons organizationId={target.organizationId} conversationId={target.conversationId} peerName="contact" onStart={onClose} />
          </div>
        )}
        <div className="nx-organization-message-context">
          {messages?.map(message => (
            <article key={message.id} ref={message.id === target.messageId ? chosen : undefined}
              tabIndex={-1} className={message.id === target.messageId ? 'nx-highlight-target' : undefined}>
              <strong>{message.senderDisplayName}</strong>
              {message.deletedAt ? <p className="nx-muted">This message was recalled</p> : <>
                {message.isForwarded && <span className="nx-forward-label nx-muted">Forwarded</span>}
                <div className="nx-msg-body" dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(message.body) }} />
                {!!message.attachments?.length && <AttachmentGallery attachments={message.attachments} onPreviewImage={setImage} />}
                {message.id === target.messageId && <Button size="sm" onClick={() => onForwardContent({
                  kind: 'message', organizationId: target.organizationId, conversationId: target.conversationId, messageId: message.id
                })}>Forward message</Button>}
              </>}
            </article>
          ))}
        </div>
      </Modal>
      <ImageLightboxModal image={image} onClose={() => setImage(null)} />
    </>
  );
}
