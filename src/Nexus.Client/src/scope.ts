const match = /^\/o\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:\/|$)/i.exec(window.location.pathname);
export const organizationId = match?.[1].toLowerCase() ?? null;
export const scopeApi = organizationId ? `/api/organizations/${organizationId}` : '/api/personal';
export const messagingHub = organizationId ? `/hubs/organizations/${organizationId}/messaging` : '/hubs/personal/messaging';
const linkParameters = new URLSearchParams(window.location.hash.slice(1));
export const workspaceInvitation = linkParameters.get('organization-invitation') ?? '';
export const personalContactLink = linkParameters.get('personal-contact') ?? '';
export const isWorkspaceHome = window.location.pathname === '/' && !workspaceInvitation && !personalContactLink;

export function lastWorkspace(userId: string): string | null {
  try { return localStorage.getItem(`nexus-last-workspace:${userId}`); } catch { return null; }
}

export function rememberWorkspace(userId: string, id: string) {
  try { localStorage.setItem(`nexus-last-workspace:${userId}`, id); } catch { /* The active route still works without storage. */ }
}

export async function scopedFetch(path: string, init?: RequestInit) {
  const scoped = /^\/api\/(?:people|org|conversations|communities|channels|posts|polls|files|sync|admin)(?:\/|\?|$)/.test(path);
  const response = await window.fetch(scoped ? scopeApi + path.slice(4) : path, init);
  if (scoped && organizationId && response.status === 403) {
    const data = await response.clone().json().catch(() => null);
    if (data?.code === 'organization_access_revoked') window.location.replace('/');
  }
  return response;
}

export async function protectedRequest(path: string, method: string, body?: unknown, signal?: AbortSignal) {
  const csrfResponse = await window.fetch('/api/auth/csrf', { signal });
  if (!csrfResponse.ok) throw new Error('Unable to verify the session.');
  const csrf = await csrfResponse.json();
  return window.fetch(path, {
    method,
    signal,
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.token },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
}
