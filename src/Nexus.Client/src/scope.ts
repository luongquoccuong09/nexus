const match = /^\/o\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:\/|$)/i.exec(window.location.pathname);
export const initialOrganizationId = match?.[1].toLowerCase() ?? null;
export const personalApiBase = '/api/personal';
export const messagingHub = '/hubs/personal/messaging';

const linkParameters = new URLSearchParams(window.location.hash.slice(1));
export const workspaceInvitation = linkParameters.get('organization-invitation') ?? '';
export const personalContactLink = linkParameters.get('personal-contact') ?? '';

export function orgApiPath(orgId: string, path: string): string {
  const suffix = path.startsWith('/api') ? path.slice(4) : path;
  return `/api/organizations/${orgId}${suffix}`;
}

export function personalApiPath(path: string): string {
  const suffix = path.startsWith('/api') ? path.slice(4) : path;
  return `${personalApiBase}${suffix}`;
}

export function chatFetch(path: string, init?: RequestInit) {
  const scoped = /^\/api\/(?:people|conversations|files|sync)(?:\/|\?|$)/.test(path);
  return window.fetch(scoped ? personalApiPath(path) : path, init);
}

export function orgFetch(orgId: string, path: string, init?: RequestInit) {
  const scoped = /^\/api\/(?:me|people|org|tags|conversations|communities|channels|posts|polls|files|sync|admin)(?:\/|\?|$)/.test(path);
  return window.fetch(scoped ? orgApiPath(orgId, path) : path, init);
}

export async function protectedRequest(path: string, method: string, body?: unknown, signal?: AbortSignal, headers?: Record<string, string>) {
  const csrfResponse = await window.fetch('/api/auth/csrf', { signal });
  if (!csrfResponse.ok) throw new Error('Unable to verify the session.');
  const csrf = await csrfResponse.json();
  return window.fetch(path, {
    method,
    signal,
    headers: { ...headers, 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.token },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
}
