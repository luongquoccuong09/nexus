import { useRef, useState } from 'react';
import { personalApiPath, protectedRequest } from '../scope';
import { Button, Field, Modal } from '../ui';
import { MESSAGE_CHANGE_WINDOW_MS, MessageMutationKind } from './MessageMutationActions';
import type { ChatMessageItem, MessageMutationDelta } from './types';

export interface MessageMutationTarget {
  message: ChatMessageItem;
  kind: MessageMutationKind;
}

export type MessageMutationResult =
  | { delta: MessageMutationDelta }
  | { error: string; refresh?: boolean; recalled?: boolean };

export default function MessageMutationDialog({
  target, onClose, onBegin, onResult
}: {
  target: MessageMutationTarget;
  onClose: () => void;
  onBegin: (delta: MessageMutationDelta) => boolean;
  onResult: (result: MessageMutationResult) => void;
}) {
  const { kind, message } = target;
  const [body, setBody] = useState(message.body);
  const [error, setError] = useState('');
  const editField = useRef<HTMLTextAreaElement | null>(null);
  const cancelButton = useRef<HTMLButtonElement | null>(null);
  const edit = kind === 'edit';
  const validBody = body.trim() !== message.body && (body.trim().length > 0 || !!message.attachments?.length);

  async function submit() {
    if (edit && !validBody) return;
    const now = new Date();
    if (now.getTime() >= Date.parse(message.createdAt) + MESSAGE_CHANGE_WINDOW_MS) {
      setError('The 15-minute window for changing this message has expired.');
      return;
    }
    const version = message.version ?? 1;
    const pending: MessageMutationDelta = {
      type: edit ? 'message_edited' : 'message_recalled',
      messageId: message.id,
      seq: message.seq,
      parentMessageId: message.parentMessageId,
      body: edit ? body.trim() : '',
      editedAt: edit ? now.toISOString() : message.editedAt ?? null,
      deletedAt: edit ? null : now.toISOString(),
      version: version + 1
    };
    if (!onBegin(pending)) return;
    onClose();

    try {
      const response = await protectedRequest(
        personalApiPath('/api/conversations/' + message.conversationId + '/messages/' + message.id),
        edit ? 'PATCH' : 'DELETE',
        edit ? { body: pending.body } : undefined,
        undefined,
        { 'If-Match': '"' + version + '"' }
      );
      const data = await response.json().catch(() => ({}));
      if (response.ok) {
        onResult({ delta: data as MessageMutationDelta });
      } else if (response.status === 412) {
        onResult({ error: 'This message changed on another device. Open it again to make a new change.', refresh: true });
      } else if (data.code === 'message_recalled') {
        onResult({ error: 'This message was already recalled.', refresh: true, recalled: true });
      } else if (data.code === 'message_edit_window_expired') {
        onResult({ error: 'The 15-minute window for changing this message has expired.' });
      } else {
        onResult({ error: data.message || (edit ? 'Unable to edit the message.' : 'Unable to recall the message.') });
      }
    } catch {
      onResult({ error: edit ? 'Unable to edit the message.' : 'Unable to recall the message.', refresh: true });
    }
  }

  return (
    <Modal
      open
      title={edit ? 'Edit message' : 'Recall message'}
      description={edit ? 'Changes appear for everyone in the conversation.' : 'Recall this message for everyone in the conversation?'}
      onOpenChange={open => { if (!open) onClose(); }}
      initialFocusRef={edit ? editField : cancelButton}
      footer={
        <>
          <button className="nx-btn sm" type="button" onClick={onClose} ref={cancelButton}>Cancel</button>
          {edit
            ? <Button type="submit" form="nx-edit-message-form" size="sm" variant="primary" disabled={!validBody}>Save changes</Button>
            : <Button size="sm" variant="danger" onClick={() => void submit()}>Recall message</Button>}
        </>
      }
    >
      {error && <p className="nx-error" role="alert">{error}</p>}
      {edit && (
        <form id="nx-edit-message-form" className="nx-message-edit-form"
          onSubmit={event => { event.preventDefault(); void submit(); }}>
          <Field label="Message" htmlFor="nx-edit-message" compact>
            <textarea id="nx-edit-message" ref={editField} rows={4} maxLength={4000} value={body}
              onChange={event => setBody(event.target.value)} />
          </Field>
        </form>
      )}
    </Modal>
  );
}
