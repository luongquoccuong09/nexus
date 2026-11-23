export interface QueuedMessage {
  clientMsgId: string;
  conversationId: string;
  body: string;
  createdAt: string;
}

const DB_NAME = 'nexus-chat-v1';
const DB_VERSION = 1;
const OUTBOX_STORE = 'outbox';
const CURSOR_STORE = 'cursors';

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

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(OUTBOX_STORE)) {
        db.createObjectStore(OUTBOX_STORE, { keyPath: 'clientMsgId' });
      }
      if (!db.objectStoreNames.contains(CURSOR_STORE)) {
        db.createObjectStore(CURSOR_STORE, { keyPath: 'conversationId' });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

export async function enqueueOfflineMessage(item: QueuedMessage): Promise<void> {
  if (typeof indexedDB === 'undefined') return;
  const db = await openDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction(OUTBOX_STORE, 'readwrite');
    tx.objectStore(OUTBOX_STORE).put(item);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

export async function dequeueOfflineMessage(clientMsgId: string): Promise<void> {
  if (typeof indexedDB === 'undefined') return;
  const db = await openDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction(OUTBOX_STORE, 'readwrite');
    tx.objectStore(OUTBOX_STORE).delete(clientMsgId);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

export async function listQueuedMessages(): Promise<QueuedMessage[]> {
  if (typeof indexedDB === 'undefined') return [];
  const db = await openDb();
  return new Promise<QueuedMessage[]>((resolve, reject) => {
    const tx = db.transaction(OUTBOX_STORE, 'readonly');
    const req = tx.objectStore(OUTBOX_STORE).getAll();
    req.onsuccess = () => resolve((req.result as QueuedMessage[]) ?? []);
    req.onerror = () => reject(req.error);
  });
}

export async function getConversationCursor(conversationId: string): Promise<number> {
  if (typeof indexedDB === 'undefined') return 0;
  const db = await openDb();
  return new Promise<number>((resolve, reject) => {
    const tx = db.transaction(CURSOR_STORE, 'readonly');
    const req = tx.objectStore(CURSOR_STORE).get(conversationId);
    req.onsuccess = () => resolve((req.result as { changeSeq?: number } | undefined)?.changeSeq ?? 0);
    req.onerror = () => reject(req.error);
  });
}

export async function setConversationCursor(conversationId: string, changeSeq: number): Promise<void> {
  if (typeof indexedDB === 'undefined') return;
  const db = await openDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction(CURSOR_STORE, 'readwrite');
    tx.objectStore(CURSOR_STORE).put({ conversationId, changeSeq });
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}
