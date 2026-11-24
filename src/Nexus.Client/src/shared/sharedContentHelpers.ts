/**
 * Pure helpers for M6.32 Shared view (Files, Links, Media).
 * Kept free of React/DOM imports so Node --test can unit-test directly.
 */

export type SharedTab = 'files' | 'links' | 'media';

export interface SharedQuotaSummary {
  conversationUsedBytes: number;
  conversationQuotaBytes: number;
}

export interface SharedContentItem {
  id: string;
  conversationId: string;
  senderUserId: string;
  senderDisplayName: string;
  createdAt: string;
  messageId?: string | null;
  postId?: string | null;
  postTitle?: string | null;
  sourceLink?: string | null;
  // Files / Media fields
  fileName?: string | null;
  contentType?: string | null;
  sizeBytes?: number | null;
  width?: number | null;
  height?: number | null;
  imageWidth?: number | null;
  imageHeight?: number | null;
  durationMs?: number | null;
  hasThumbnail?: boolean;
  downloadUrl?: string | null;
  thumbnailUrl?: string | null;
  previewUrl?: string | null;
  audioUrl?: string | null;
  // Links fields
  url?: string | null;
  host?: string | null;
  label?: string | null;
}

export interface SharedContentCursor {
  before: string;
  beforeId: string;
}

export const SHARED_TABS: readonly { id: SharedTab; label: string }[] = [
  { id: 'files', label: 'Files' },
  { id: 'links', label: 'Links' },
  { id: 'media', label: 'Media' }
];

export function resolveSafeSharedHref(rawUrl: string | null | undefined): string | null {
  if (!rawUrl) return null;
  const trimmed = rawUrl.trim();
  if (!trimmed || /\s|[\u0000-\u001F\u007F]/.test(trimmed)) return null;
  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
    return parsed.hostname ? trimmed : null;
  } catch {
    return null;
  }
}

export function formatSharedHost(item: Pick<SharedContentItem, 'host' | 'url'>): string {
  if (item.host && item.host.trim()) return item.host.trim();
  const safe = resolveSafeSharedHref(item.url);
  if (!safe) return '';
  try {
    return new URL(safe).hostname;
  } catch {
    return '';
  }
}

export function mergeSharedItems(
  existing: readonly SharedContentItem[],
  incoming: readonly SharedContentItem[],
  append: boolean
): SharedContentItem[] {
  const seen = new Map<string, SharedContentItem>();
  if (append) {
    for (const item of existing) seen.set(item.id, item);
  }
  for (const item of incoming) {
    seen.set(item.id, item);
  }
  return [...seen.values()];
}

export function buildSharedContentUrl(
  scope: { kind: 'personal' } | { kind: 'organization'; organizationId: string },
  conversationId: string,
  tab: SharedTab,
  cursor: SharedContentCursor | null,
  query: string
): string {
  const params = new URLSearchParams({ tab, limit: '50' });
  if (cursor) {
    params.set('before', cursor.before);
    params.set('beforeId', cursor.beforeId);
  }
  const trimmedQ = query.trim().slice(0, 100);
  if (trimmedQ) {
    params.set('q', trimmedQ);
  }
  const encodedConv = encodeURIComponent(conversationId);
  if (scope.kind === 'organization') {
    const encodedOrg = encodeURIComponent(scope.organizationId);
    return `/api/organizations/${encodedOrg}/channels/${encodedConv}/shared?${params}`;
  }
  return `/api/personal/conversations/${encodedConv}/shared?${params}`;
}
