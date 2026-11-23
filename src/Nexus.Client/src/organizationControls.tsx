import { FormEvent, useCallback, useEffect, useState } from 'react';
import { Button, Card, Field, Modal, ToastBar } from './ui';
import { orgApiPath, personalContactLink, protectedRequest } from './scope';

export interface OrganizationItem {
  id: string;
  name: string;
  isOwner: boolean;
  role: string;
}

export type WorkspaceModalState =
  | { mode: 'create' }
  | { mode: 'join'; initialToken?: string }
  | { mode: 'settings'; organization: OrganizationItem }
  | null;

interface Invitation { id: string; expiresAt: string; revokedAt?: string; redeemedAt?: string }
interface Member { id: string; displayName: string; userName: string; role: string; isOwner: boolean }

async function previewInvitation(token: string, signal?: AbortSignal) {
  const res = await protectedRequest('/api/organizations/invitations/preview', 'POST', { token }, signal);
  if (!res.ok) throw new Error('This invitation is invalid, expired or already used.');
  return { name: (await res.json()).name as string, token };
}

async function readItems<T>(url: string): Promise<T[]> {
  const items: T[] = [];
  let after: string | null = null;
  do {
    const query = new URLSearchParams({ limit: '50' });
    if (after) query.set('after', after);
    const res = await window.fetch(`${url}?${query}`, { credentials: 'same-origin' });
    if (!res.ok) throw new Error('Unable to load the list.');
    const page: { items: T[]; nextAfter: string | null } = await res.json();
    items.push(...page.items);
    after = page.nextAfter;
  } while (after);
  return items;
}

export async function listOrganizations(): Promise<OrganizationItem[]> {
  const items = await readItems<OrganizationItem>('/api/organizations');
  return items.sort((a, b) => a.name.localeCompare(b.name));
}

export function WorkspaceModals({
  modal,
  mfaEnabled,
  onClose,
  onOrganizationsChanged,
  onSecurity
}: {
  modal: WorkspaceModalState;
  mfaEnabled: boolean;
  onClose: () => void;
  onOrganizationsChanged: (focusOrgId?: string) => Promise<void> | void;
  onSecurity: () => void;
}) {
  const [name, setName] = useState('');
  const [token, setToken] = useState('');
  const [preview, setPreview] = useState<{ name: string; token: string } | null>(null);
  const [link, setLink] = useState('');
  const [copyStatus, setCopyStatus] = useState('');
  const [invites, setInvites] = useState<Invitation[]>([]);
  const [members, setMembers] = useState<Member[]>([]);
  const [pendingRemoval, setPendingRemoval] = useState<Member | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const settingsOrg = modal?.mode === 'settings' ? modal.organization : null;

  const loadSettings = useCallback(async (orgId: string) => {
    const [nextInvites, nextMembers] = await Promise.all([
      readItems<Invitation>(orgApiPath(orgId, '/api/invitations')),
      readItems<Member>(orgApiPath(orgId, '/api/members'))
    ]);
    setInvites(nextInvites);
    setMembers(nextMembers);
  }, []);

  useEffect(() => {
    setError('');
    setPreview(null);
    setLink('');
    setCopyStatus('');
    setPendingRemoval(null);

    if (!modal) return;
    if (modal.mode === 'create') {
      setName('');
      return;
    }
    if (modal.mode === 'join') {
      const initial = modal.initialToken ?? '';
      setToken(initial);
      if (initial) {
        const controller = new AbortController();
        setBusy(true);
        void previewInvitation(initial, controller.signal)
          .then(setPreview)
          .catch((failure: Error) => { if (!controller.signal.aborted) setError(failure.message); })
          .finally(() => { if (!controller.signal.aborted) setBusy(false); });
        return () => controller.abort();
      }
      return;
    }
    if (modal.mode === 'settings') {
      setInvites([]);
      setMembers([]);
      void loadSettings(modal.organization.id).catch(() =>
        setError('Unable to load workspace settings. If your workspace enforces 2FA, enable your authenticator in Profile & security.')
      );
    }
  }, [modal, loadSettings]);

  function handleClose() {
    if (window.location.hash.includes('organization-invitation=')) {
      window.history.replaceState(null, '', window.location.pathname + window.location.search);
    }
    onClose();
  }

  async function submit(action: () => Promise<void>) {
    if (busy) return;
    setBusy(true);
    setError('');
    try { await action(); } catch (failure) { setError((failure as Error).message || 'Please try again.'); }
    finally { setBusy(false); }
  }

  function create(e: FormEvent) {
    e.preventDefault();
    void submit(async () => {
      const res = await protectedRequest('/api/organizations', 'POST', { name });
      if (!res.ok) {
        const problem = await res.json().catch(() => null);
        if (problem?.code === 'organization_creation_unavailable') throw new Error('Workspace creation is currently unavailable.');
        throw new Error('Unable to create the workspace.');
      }
      const created: { id: string } = await res.json();
      await onOrganizationsChanged(created.id);
      handleClose();
    });
  }

  function review(e: FormEvent) {
    e.preventDefault();
    void submit(async () => {
      const input = token.trim();
      const invitation = /^[0-9a-f]{64}$/i.test(input)
        ? input
        : new URLSearchParams(input.split('#')[1] ?? '').get('organization-invitation');
      if (!invitation) throw new Error('Enter a valid workspace invitation link.');
      setPreview(await previewInvitation(invitation));
    });
  }

  async function join() {
    if (!preview) return;
    const res = await protectedRequest('/api/organizations/invitations/redeem', 'POST', { token: preview.token });
    if (!res.ok) throw new Error(res.status === 409 ? 'You already belong to this workspace.' : 'This invitation is invalid, expired or already used.');
    const joined: { id: string } = await res.json();
    await onOrganizationsChanged(joined.id);
    handleClose();
  }

  async function invite() {
    if (!settingsOrg) return;
    const res = await protectedRequest(orgApiPath(settingsOrg.id, '/api/invitations'), 'POST');
    if (!res.ok) throw new Error('Unable to create an invitation.');
    setCopyStatus('');
    setLink(`${window.location.origin}/#organization-invitation=${(await res.json()).token}`);
    await loadSettings(settingsOrg.id);
  }

  const title = modal?.mode === 'settings'
    ? `${modal.organization.name} · Settings`
    : modal?.mode === 'join'
      ? (preview ? `Join ${preview.name}` : 'Join a workspace')
      : 'Create workspace';

  return (
    <Modal open={modal !== null} onOpenChange={value => { if (!value) handleClose(); }} title={title}>
      {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
      {modal?.mode === 'create' && (
        <form onSubmit={create}>
          <Field label="Workspace name" htmlFor="organization-name">
            <input
              id="organization-name"
              type="text"
              required
              maxLength={120}
              placeholder="e.g. Acme Studio, Engineering…"
              value={name}
              onChange={e => setName(e.currentTarget.value)}
            />
          </Field>
          <Button type="submit" variant="primary" disabled={busy || !name.trim()}>
            {busy ? 'Creating…' : 'Create workspace'}
          </Button>
        </form>
      )}
      {modal?.mode === 'join' && (preview ? (
        <>
          <p>Accept the invitation to access <strong>{preview.name}</strong>’s communities and directory.</p>
          <Button variant="primary" disabled={busy} onClick={() => void submit(join)}>
            {busy ? 'Joining…' : `Join ${preview.name}`}
          </Button>
        </>
      ) : (
        <form onSubmit={review}>
          <Field label="Invitation link" htmlFor="organization-link">
            <input
              id="organization-link"
              type="text"
              required
              placeholder="Paste invitation link or token…"
              value={token}
              onChange={e => setToken(e.currentTarget.value)}
            />
          </Field>
          <Button type="submit" variant="primary" disabled={busy || !token.trim()}>
            {busy ? 'Checking invitation…' : 'Review invitation'}
          </Button>
        </form>
      ))}
      {settingsOrg && (
        <>
          {!mfaEnabled && error && (
            <Button onClick={() => { handleClose(); onSecurity(); }}>Open profile &amp; security</Button>
          )}
          <Card title="Invitations">
            <p>Anyone with the link can join once. Invitations expire after seven days.</p>
            <Button onClick={() => void submit(invite)} disabled={busy}>Create invitation link</Button>
            {link && (
              <div style={{ marginTop: 12 }}>
                <Field label="Invitation link" htmlFor="new-invitation-link">
                  <input id="new-invitation-link" type="text" value={link} readOnly onFocus={e => e.currentTarget.select()} />
                </Field>
                <div className="nx-row">
                  <Button size="sm" onClick={async () => {
                    try {
                      await navigator.clipboard.writeText(link);
                      setCopyStatus('Invitation link copied.');
                    } catch {
                      setCopyStatus('Select the invitation link to copy it.');
                    }
                  }}>Copy invitation link</Button>
                  <span role="status">{copyStatus}</span>
                </div>
              </div>
            )}
            {invites.filter(i => !i.revokedAt && !i.redeemedAt && Date.parse(i.expiresAt) > Date.now()).map(invitation => (
              <div className="nx-row" key={invitation.id} style={{ marginTop: 8 }}>
                <span>Expires {new Date(invitation.expiresAt).toLocaleDateString()}</span>
                <Button size="sm" disabled={busy} onClick={() => void submit(async () => {
                  const res = await protectedRequest(orgApiPath(settingsOrg.id, `/api/invitations/${invitation.id}`), 'DELETE');
                  if (!res.ok) throw new Error('Unable to revoke the invitation.');
                  setLink('');
                  await loadSettings(settingsOrg.id);
                })}>Revoke</Button>
              </div>
            ))}
          </Card>
          <Card title="Members">
            {members.map(member => (
              <div className="nx-row" key={member.id} style={{ marginTop: 6 }}>
                <span>{member.displayName} · {member.isOwner ? 'Owner' : member.role === 'SystemAdmin' ? 'Admin' : member.role}</span>
                {!member.isOwner && (settingsOrg.isOwner || member.role === 'Member') && (
                  <Button size="sm" disabled={busy} onClick={() => setPendingRemoval(member)}>Remove</Button>
                )}
                {!member.isOwner && settingsOrg.isOwner && ['Member', 'SystemAdmin'].includes(member.role) && (
                  <Button size="sm" disabled={busy} onClick={() => void submit(async () => {
                    const res = await protectedRequest(
                      orgApiPath(settingsOrg.id, `/api/members/${member.id}/administrator`),
                      'PUT',
                      { administrator: member.role === 'Member' }
                    );
                    if (!res.ok) throw new Error('Unable to change the member role.');
                    await loadSettings(settingsOrg.id);
                  })}>{member.role === 'Member' ? 'Make admin' : 'Remove admin role'}</Button>
                )}
              </div>
            ))}
            {pendingRemoval && (
              <div className="nx-row" style={{ marginTop: 10 }}>
                <span>Remove {pendingRemoval.displayName} from {settingsOrg.name}?</span>
                <Button size="sm" onClick={() => setPendingRemoval(null)}>Cancel</Button>
                <Button size="sm" variant="danger" disabled={busy} onClick={() => void submit(async () => {
                  const res = await protectedRequest(orgApiPath(settingsOrg.id, `/api/members/${pendingRemoval.id}`), 'DELETE');
                  if (!res.ok) throw new Error(res.status === 409 ? 'Transfer this member’s Community ownership before removing access.' : 'Unable to remove the member.');
                  setPendingRemoval(null);
                  await loadSettings(settingsOrg.id);
                })}>Confirm removal</Button>
              </div>
            )}
          </Card>
        </>
      )}
    </Modal>
  );
}

export function ContactControls({ onChange, usernameRef }: { onChange: () => void; usernameRef?: React.Ref<HTMLInputElement> }) {
  const [requests, setRequests] = useState<{ id: string; displayName: string; username: string }[]>([]);
  const [username, setUsername] = useState('');
  const [link, setLink] = useState('');
  const [copyStatus, setCopyStatus] = useState('');
  const [receivedToken, setReceivedToken] = useState(personalContactLink);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);

  const readRequests = () => readItems<{ id: string; displayName: string; username: string }>('/api/personal/contacts/requests').then(setRequests);
  useEffect(() => { void readRequests().catch(() => setMessage('Unable to load contact requests.')); }, []);

  async function run(path: string, method: string, body?: unknown) {
    if (busy) return null;
    setBusy(true);
    setMessage('');
    try {
      const res = await protectedRequest(`/api/personal/contacts/${path}`, method, body);
      if (!res.ok) throw new Error('Unable to update contacts.');
      return res;
    } catch {
      setMessage('Unable to update contacts. Check the request or try again.');
      return null;
    } finally { setBusy(false); }
  }

  return (
    <Card title="External contacts">
      <p>Connect with people outside your workspaces. Once accepted, you can chat in your unified inbox.</p>
      {message && <p role="status">{message}</p>}
      {receivedToken && (
        <Button disabled={busy} onClick={async () => {
          if (!await run('links/redeem', 'POST', { token: receivedToken })) return;
          setReceivedToken('');
          window.history.replaceState(null, '', window.location.pathname);
          setMessage('Connection requested. The sender must accept it before you can chat.');
        }}>Request connection from link</Button>
      )}
      <form className="nx-row" style={{ alignItems: 'flex-end', gap: 10 }} onSubmit={async e => {
        e.preventDefault();
        if (!await run('requests', 'POST', { username })) return;
        setUsername('');
        setMessage('If this account can receive requests, your connection request was sent.');
      }}>
        <div style={{ flex: 1, minWidth: 220 }}>
          <Field label="Nexus username" htmlFor="contact-username" compact>
            <input ref={usernameRef} id="contact-username" type="text" required value={username} onChange={e => setUsername(e.currentTarget.value)} />
          </Field>
        </div>
        <Button type="submit" variant="primary" disabled={busy || !username.trim()}>Request connection</Button>
      </form>
      <div style={{ marginTop: 12 }}>
        <Button disabled={busy} onClick={async () => {
          const res = await run('links', 'POST');
          if (res) {
            setCopyStatus('');
            setLink(`${window.location.origin}/#personal-contact=${(await res.json()).token}`);
          }
        }}>Create contact link</Button>
      </div>
      {link && (
        <div style={{ marginTop: 12 }}>
          <Field label="Copy contact link" htmlFor="new-contact-link">
            <input id="new-contact-link" type="text" value={link} readOnly onFocus={e => e.currentTarget.select()} />
          </Field>
          <div className="nx-row">
            <Button size="sm" onClick={async () => {
              try {
                await navigator.clipboard.writeText(link);
                setCopyStatus('Contact link copied.');
              } catch {
                setCopyStatus('Select the contact link to copy it.');
              }
            }}>Copy contact link</Button>
            <span role="status">{copyStatus}</span>
          </div>
        </div>
      )}
      {requests.map(request => (
        <div className="nx-row" key={request.id} style={{ marginTop: 8 }}>
          <span>{request.displayName} · @{request.username}</span>
          {[true, false].map(accept => (
            <Button key={String(accept)} size="sm" disabled={busy} onClick={async () => {
              if (!await run(`requests/${request.id}`, 'PUT', { accept })) return;
              setRequests(previous => previous.filter(item => item.id !== request.id));
              if (accept) onChange();
            }}>{accept ? 'Accept' : 'Decline'}</Button>
          ))}
        </div>
      ))}
    </Card>
  );
}
