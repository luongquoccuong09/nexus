export type ContentLinkInput =
  | { kind: 'message'; organizationId?: string | null; conversationId: string; messageId: string }
  | { kind: 'post'; organizationId: string; conversationId: string; postId: string }
  | { kind: 'channel'; organizationId: string; conversationId: string };

export type ForwardableContent = Exclude<ContentLinkInput, { kind: 'channel' }>;

export type ResolvedContentLink =
  | { kind: 'message'; organizationId: string | null; conversationId: string; messageId: string; seq: number; parentMessageId?: string | null; parentSeq?: number | null }
  | { kind: 'post'; organizationId: string; communityId: string; conversationId: string; postId: string }
  | { kind: 'channel'; organizationId: string; communityId: string; conversationId: string };

export interface ChatJumpTarget {
  conversationId: string;
  messageId: string;
  seq: number;
  parentMessageId?: string | null;
  parentSeq?: number | null;
  nonce: number;
}

export interface CommunityJumpTarget {
  organizationId: string;
  communityId: string;
  conversationId: string;
  postId?: string | null;
  nonce: number;
}

function toBase64Url(text: string): string {
  const bytes = new TextEncoder().encode(text);
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

export function encodeContentLink(input: ContentLinkInput): string {
  const payload = input.kind === 'message'
    ? { k: 'm', o: input.organizationId ?? null, c: input.conversationId, t: input.messageId }
    : input.kind === 'post'
      ? { k: 'p', o: input.organizationId, c: input.conversationId, t: input.postId }
      : { k: 'c', o: input.organizationId, c: input.conversationId, t: null };
  return toBase64Url(JSON.stringify(payload));
}

export function buildContentUrl(token: string, origin = window.location.origin): string {
  return `${origin}/?link=${encodeURIComponent(token)}`;
}

export function extractContentLinkToken(href: string, currentOrigin = window.location.origin): string | null {
  try {
    const parsed = new URL(href, currentOrigin);
    if (parsed.origin !== currentOrigin) return null;
    const token = parsed.searchParams.get('link')?.trim() ?? '';
    return token.length > 0 && token.length <= 512 ? token : null;
  } catch {
    return null;
  }
}

export function readInitialContentLink(): string | null {
  if (typeof window === 'undefined') return null;
  const token = new URLSearchParams(window.location.search).get('link')?.trim() ?? '';
  return token.length > 0 && token.length <= 512 ? token : null;
}

export function clearContentLinkFromUrl(): void {
  if (typeof window === 'undefined') return;
  const params = new URLSearchParams(window.location.search);
  if (!params.has('link')) return;
  params.delete('link');
  const query = params.toString();
  const next = `${window.location.pathname}${query ? `?${query}` : ''}${window.location.hash}`;
  window.history.replaceState(null, '', next);
}

export async function resolveContentLink(token: string, signal?: AbortSignal): Promise<ResolvedContentLink> {
  const res = await fetch(`/api/links/resolve?target=${encodeURIComponent(token)}`, {
    credentials: 'same-origin',
    signal
  });
  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    throw new Error(data.message || 'This link is unavailable or you do not have access to it.');
  }
  return await res.json() as ResolvedContentLink;
}

export async function copyContentLink(input: ContentLinkInput): Promise<{ ok: boolean; url: string }> {
  const url = buildContentUrl(encodeContentLink(input));
  try {
    await navigator.clipboard.writeText(url);
    return { ok: true, url };
  } catch {
    return { ok: false, url };
  }
}
