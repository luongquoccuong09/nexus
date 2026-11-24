import { chatFetch, orgFetch } from './scope';
import { readCursorPage } from './cursorList';
import type { DirectoryUserLite } from './chat';

export function personKey(person: { id: string }) { return person.id; }

export async function searchChatCandidates(query: string, cursor: string | null, signal?: AbortSignal, excludeConversationId?: string) {
  const params = new URLSearchParams({ limit: '50' });
  const name = query.trim().replace(/^@/, '');
  if (name) params.set('q', name);
  if (cursor) params.set('cursor', cursor);
  if (excludeConversationId) params.set('excludeConversationId', excludeConversationId);
  return readCursorPage<DirectoryUserLite>(
    await chatFetch(`/api/people/chat-candidates?${params}`, { signal }),
    'Unable to load people you can message.'
  );
}

export async function searchPeopleScope<T>(scope: string, query: string, departmentId: string, cursor: string | null,
  signal?: AbortSignal, excludeCommunityId?: string) {
  const params = new URLSearchParams({ limit: '50' });
  const name = query.trim().replace(/^@/, '');
  if (name) params.set('q', name);
  if (scope !== 'contacts' && departmentId) params.set('departmentId', departmentId);
  if (cursor) params.set(scope === 'contacts' ? 'after' : 'cursor', cursor);
  if (excludeCommunityId) params.set('excludeCommunityId', excludeCommunityId);
  const path = `/api/people/directory?${params}`;
  const response = scope === 'contacts' ? await chatFetch(path, { signal }) : await orgFetch(scope, path, { signal });
  return readCursorPage<T>(response, scope === 'contacts' ? 'Unable to load contacts.' : 'Unable to load workspace members.');
}

export async function loadOrganizationDepartments<T>(orgId: string, signal?: AbortSignal): Promise<T[]> {
  const items: T[] = [];
  let cursor: string | null = null;
  do {
    const params = new URLSearchParams({ limit: '50' });
    if (cursor) params.set('cursor', cursor);
    const response = await orgFetch(orgId, `/api/org/departments?${params}`, { signal });
    if (!response.ok) return [];
    const page = await readCursorPage<T>(response, 'Unable to load departments.');
    items.push(...page.items);
    cursor = page.next;
  } while (cursor);
  return items;
}
