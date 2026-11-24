export type PeopleTagScope = 'organization' | 'community';

export interface PeopleTagDto {
  id: string;
  organizationId: string;
  communityId: string | null;
  scope: PeopleTagScope;
  name: string;
  slug: string;
  description: string | null;
  memberCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface PeopleTagMemberDto {
  userId: string;
  username: string;
  displayName: string;
  isUnverified: boolean;
  addedAt: string;
}

export interface PeopleTagPageResponse {
  items: PeopleTagDto[];
  nextCursor: string | null;
  canManage: boolean;
}

export interface PeopleTagMembersPageResponse {
  items: PeopleTagMemberDto[];
  nextCursor: string | null;
}

export const RESERVED_TAG_SLUGS = ['all', 'channel', 'everyone', 'here'] as const;
const RESERVED_SET = new Set<string>(RESERVED_TAG_SLUGS);

export const TAG_SLUG_PATTERN = /^[a-z0-9][a-z0-9_-]{1,31}$/;

export function slugifyTagName(name: string): string {
  const normalized = name
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/-{2,}/g, '-')
    .replace(/^[^a-z0-9]+/, '')
    .slice(0, 32)
    .replace(/[-_]+$/, '');
  return normalized;
}

export type TagSlugValidation =
  | { valid: true; slug: string }
  | { valid: false; error: string };

export function validateTagSlug(rawSlug: string): TagSlugValidation {
  const slug = rawSlug.trim().toLowerCase();
  if (!slug) {
    return { valid: false, error: 'Tag handle is required.' };
  }
  if (slug.length < 2 || slug.length > 32 || !TAG_SLUG_PATTERN.test(slug)) {
    return {
      valid: false,
      error: 'Use 2–32 lowercase letters, numbers, hyphens, or underscores, starting with a letter or number.'
    };
  }
  if (RESERVED_SET.has(slug)) {
    return {
      valid: false,
      error: `"@${slug}" is a reserved mention handle.`
    };
  }
  return { valid: true, slug };
}

export interface TagMentionInsertionResult {
  value: string;
  selectionStart: number;
  selectionEnd: number;
}

export function insertTagMention(
  value: string,
  selectionStart: number,
  selectionEnd: number,
  slug: string
): TagMentionInsertionResult {
  const len = value.length;
  const rawStart = Number.isFinite(selectionStart) ? Math.max(0, Math.min(len, Math.trunc(selectionStart))) : len;
  const rawEnd = Number.isFinite(selectionEnd) ? Math.max(0, Math.min(len, Math.trunc(selectionEnd))) : rawStart;
  const start = Math.min(rawStart, rawEnd);
  const end = Math.max(rawStart, rawEnd);

  const cleanSlug = slug.trim().replace(/^@+/, '').toLowerCase();
  if (!cleanSlug) {
    return { value, selectionStart: start, selectionEnd: end };
  }

  const before = value.slice(0, start);
  const after = value.slice(end);
  const needsLeadingSpace = before.length > 0 && !/[\s(\[]$/.test(before);
  const needsTrailingSpace = after.length === 0 || !/^\s/.test(after);

  const prefix = needsLeadingSpace ? ' ' : '';
  const suffix = needsTrailingSpace ? ' ' : '';
  const inserted = `${prefix}@${cleanSlug}${suffix}`;
  const nextValue = `${before}${inserted}${after}`;
  const nextCaret = before.length + inserted.length + (needsTrailingSpace ? 0 : 1);
  const clampedCaret = Math.min(nextValue.length, nextCaret);

  return {
    value: nextValue,
    selectionStart: clampedCaret,
    selectionEnd: clampedCaret
  };
}

export function formatTagApiError(status: number, code?: string | null, message?: string | null): string {
  if (status === 409 && code === 'tag_slug_exists') {
    return 'A tag with this handle already exists in this scope.';
  }
  if (status === 403) {
    return message || 'You do not have permission to manage tags here.';
  }
  if (status === 404) {
    return message || 'This tag could not be found.';
  }
  if (status === 400) {
    return message || 'Check the tag details and try again.';
  }
  return message || 'Unable to complete the tag request.';
}
