import { useCallback, useEffect, useRef, useState } from 'react';
import { ChatDraft, DraftAttachment, generateClientMsgId, readDraft, saveDraft } from './offlineQueue';

const noAttachments: DraftAttachment[] = [];

export function useChatDrafts(scope: string, conversationId: string | null, onStorageError: () => void) {
  const drafts = useRef(new Map<string, ChatDraft | null>());
  const [, render] = useState(0);

  const setLocalDraft = useCallback((id: string, draft: ChatDraft | null) => {
    drafts.current.set(id, draft);
    render(version => version + 1);
  }, []);

  useEffect(() => {
    if (!conversationId || drafts.current.has(conversationId)) return;
    let cancelled = false;
    void readDraft(scope, conversationId)
      .catch(() => {
        if (!cancelled) onStorageError();
        return undefined;
      })
      .then(draft => {
        if (!cancelled && !drafts.current.has(conversationId)) setLocalDraft(conversationId, draft ?? null);
      });
    return () => { cancelled = true; };
  }, [scope, conversationId, onStorageError, setLocalDraft]);

  const readLocalDraft = useCallback((id: string) => drafts.current.get(id), []);

  const changeDraft = useCallback((id: string, change: (current: ChatDraft) => Partial<ChatDraft>) => {
    const current = drafts.current.get(id);
    if (current === undefined) return;
    const draft = current ?? { conversationId: id, revision: '', body: '', attachments: noAttachments };
    const next = { ...draft, ...change(draft), revision: generateClientMsgId() };
    setLocalDraft(id, next.body || next.attachments.length > 0 ? next : null);
    void saveDraft(scope, next).catch(onStorageError);
  }, [scope, setLocalDraft, onStorageError]);

  const draft = conversationId ? drafts.current.get(conversationId) : undefined;
  return { draft, loadingDraft: Boolean(conversationId && draft === undefined), changeDraft, setLocalDraft, readLocalDraft };
}
