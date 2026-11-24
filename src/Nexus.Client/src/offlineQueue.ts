export interface QueuedMessage {
  clientMsgId: string;
  conversationId: string;
  body: string;
  createdAt: string;
  attachments?: DraftAttachment[];
  // Older entries have no failure and can be replayed as queued messages.
  failure?: string;
}

export interface DraftAttachment {
  id: string;
  fileName: string;
  sizeBytes: number;
  thumbnailUrl?: string | null;
}

export interface ChatDraft {
  conversationId: string;
  revision: string;
  body: string;
  attachments: DraftAttachment[];
  retry?: QueuedMessage;
}

const DB_NAME = 'nexus-chat-v2';
const DB_VERSION = 2;
const OUTBOX_STORE = 'outbox';
const CURSOR_STORE = 'cursors';
const DRAFT_STORE = 'drafts';
const signedOutScopes = new Set<string>();

export function generateClientMsgId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return `${Date.now().toString(36)}-${crypto.randomUUID()}`;
  }
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

export function reconnectDelayWithJitter(retryCount: number): number {
  const baseMs = Math.min(30_000, 1000 * Math.pow(2, Math.min(retryCount, 5)));
  const jitter = Math.floor(Math.random() * 1000);
  return baseMs + jitter;
}

function openDb(scope: string): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(`${DB_NAME}:${scope}`, DB_VERSION);
    let blocked = false;
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(OUTBOX_STORE)) {
        db.createObjectStore(OUTBOX_STORE, { keyPath: 'clientMsgId' });
      }
      if (!db.objectStoreNames.contains(CURSOR_STORE)) {
        db.createObjectStore(CURSOR_STORE, { keyPath: 'conversationId' });
      }
      if (!db.objectStoreNames.contains(DRAFT_STORE)) {
        db.createObjectStore(DRAFT_STORE, { keyPath: 'conversationId' });
      }
    };
    req.onblocked = () => {
      blocked = true;
      reject(new Error('Another Nexus tab is using an older version of chat storage.'));
    };
    req.onsuccess = () => {
      const db = req.result;
      db.onversionchange = () => db.close();
      if (blocked) db.close();
      else resolve(db);
    };
    req.onerror = () => reject(req.error);
  });
}

async function write(scope: string, stores: string[], operation: (tx: IDBTransaction) => void, clearing = false) {
  if (!clearing && signedOutScopes.has(scope)) return;
  const db = await openDb(scope);
  try {
    if (!clearing && signedOutScopes.has(scope)) return;
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(stores, 'readwrite');
      tx.oncomplete = () => resolve();
      tx.onabort = () => reject(tx.error);
      operation(tx);
    });
  } finally {
    db.close();
  }
}

async function read<T>(scope: string, store: string, operation: (store: IDBObjectStore) => IDBRequest<T>): Promise<T | undefined> {
  if (typeof indexedDB === 'undefined' || signedOutScopes.has(scope)) return undefined;
  const db = await openDb(scope);
  try {
    if (signedOutScopes.has(scope)) return undefined;
    return await new Promise<T>((resolve, reject) => {
      const req = operation(db.transaction(store).objectStore(store));
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  } finally {
    db.close();
  }
}

export function stageMessage(scope: string, item: QueuedMessage, revision: string) {
  return write(scope, [OUTBOX_STORE, DRAFT_STORE], tx => {
    tx.objectStore(OUTBOX_STORE).put(item);
    const drafts = tx.objectStore(DRAFT_STORE);
    const request = drafts.get(item.conversationId);
    request.onsuccess = () => {
      if (request.result?.revision === revision) drafts.delete(item.conversationId);
    };
  });
}

export function recordSendFailure(scope: string, clientMsgId: string, failure: string) {
  return write(scope, [OUTBOX_STORE], tx => {
    const outbox = tx.objectStore(OUTBOX_STORE);
    const request = outbox.get(clientMsgId);
    request.onsuccess = () => {
      if (request.result) outbox.put({ ...request.result, failure });
    };
  });
}

export function restoreQueuedDraft(scope: string, draft: ChatDraft, clientMsgId: string) {
  let restored = false;
  return write(scope, [OUTBOX_STORE, DRAFT_STORE], tx => {
    const drafts = tx.objectStore(DRAFT_STORE);
    const request = drafts.get(draft.conversationId);
    request.onsuccess = () => {
      if (request.result) return;
      drafts.put(draft);
      tx.objectStore(OUTBOX_STORE).delete(clientMsgId);
      restored = true;
    };
  }).then(() => restored);
}

export function dequeueOfflineMessage(scope: string, clientMsgId: string) {
  return write(scope, [OUTBOX_STORE], tx => { tx.objectStore(OUTBOX_STORE).delete(clientMsgId); });
}

export async function listQueuedMessages(scope: string): Promise<QueuedMessage[]> {
  return await read<QueuedMessage[]>(scope, OUTBOX_STORE, store => store.getAll()) ?? [];
}

export function readDraft(scope: string, conversationId: string) {
  return read<ChatDraft>(scope, DRAFT_STORE, store => store.get(conversationId));
}

export async function listDraftConversationIds(scope: string) {
  const ids = await read<IDBValidKey[]>(scope, DRAFT_STORE, store => store.getAllKeys());
  return (ids ?? []).map(String);
}

export function saveDraft(scope: string, draft: ChatDraft) {
  return write(scope, [DRAFT_STORE], tx => {
    const drafts = tx.objectStore(DRAFT_STORE);
    if (draft.body || draft.attachments.length > 0) drafts.put(draft);
    else drafts.delete(draft.conversationId);
  });
}

export function clearConversationWork(scope: string, conversationId: string) {
  return write(scope, [DRAFT_STORE, OUTBOX_STORE, CURSOR_STORE], tx => {
    tx.objectStore(DRAFT_STORE).delete(conversationId);
    tx.objectStore(CURSOR_STORE).delete(conversationId);
    const cursor = tx.objectStore(OUTBOX_STORE).openCursor();
    cursor.onsuccess = () => {
      const item = cursor.result;
      if (!item) return;
      if (item.value.conversationId === conversationId) item.delete();
      item.continue();
    };
  });
}

export async function clearChatWork(scope: string) {
  signedOutScopes.add(scope);
  if (typeof indexedDB === 'undefined') return;
  await new Promise<void>((resolve, reject) => {
    const request = indexedDB.deleteDatabase(`${DB_NAME}:${scope}`);
    request.onsuccess = () => resolve();
    request.onerror = () => reject(request.error);
    request.onblocked = () => resolve();
  });
}

export async function getConversationCursor(scope: string, conversationId: string): Promise<number> {
  return (await read<{ changeSeq: number }>(scope, CURSOR_STORE, store => store.get(conversationId)).catch(() => undefined))?.changeSeq ?? 0;
}

export function setConversationCursor(scope: string, conversationId: string, changeSeq: number): Promise<void> {
  return write(scope, [CURSOR_STORE], tx => {
    tx.objectStore(CURSOR_STORE).put({ conversationId, changeSeq });
  }).catch(() => {});
}
