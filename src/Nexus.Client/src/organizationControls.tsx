import { FormEvent, useCallback, useEffect, useState } from 'react';
import { Button, Card, Field, Modal, ToastBar } from './ui';
import { lastWorkspace, organizationId, personalContactLink, protectedRequest, scopeApi, workspaceInvitation } from './scope';

interface OrganizationItem { id: string; name: string; isOwner: boolean; role: string }
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
    const res = await fetch(`${url}?${query}`);
    if (!res.ok) throw new Error('Unable to load the list.');
    const page: { items: T[]; nextAfter: string | null } = await res.json();
    items.push(...page.items);
    after = page.nextAfter;
  } while (after);
  return items;
}

export function OrganizationControls({ userId, mfaEnabled, home, onSecurity, onSignOut }: {
  userId: string;
  mfaEnabled: boolean;
  home?: boolean;
  onSecurity: () => void;
  onSignOut: () => void;
}) {
  const [organizations, setOrganizations] = useState<OrganizationItem[]>([]);
  const [mode, setMode] = useState<'create' | 'join' | 'settings' | null>(() => workspaceInvitation ? 'join' : null);
  const [name, setName] = useState('');
  const [token, setToken] = useState(workspaceInvitation);
  const [preview, setPreview] = useState<{ name: string; token: string } | null>(null);
  const [link, setLink] = useState('');
  const [copyStatus, setCopyStatus] = useState('');
  const [invites, setInvites] = useState<Invitation[]>([]);
  const [members, setMembers] = useState<Member[]>([]);
  const [pendingRemoval, setPendingRemoval] = useState<Member | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [loadState, setLoadState] = useState<'loading' | 'loaded' | 'error'>('loading');
  const [loadAttempt, setLoadAttempt] = useState(0);
  const active = organizations.find(o => o.id === organizationId);

  const loadSettings = useCallback(async () => {
    const [nextInvites, nextMembers] = await Promise.all([
      readItems<Invitation>(`${scopeApi}/invitations`), readItems<Member>(`${scopeApi}/members`)
    ]);
    setInvites(nextInvites);
    setMembers(nextMembers);
  }, []);

  useEffect(() => {
    let disposed = false;
    void readItems<OrganizationItem>('/api/organizations').then(items => {
      if (disposed) return;
      items.sort((a, b) => a.name.localeCompare(b.name));
      if (home && items.length > 0) {
        const saved = lastWorkspace(userId);
        const workspace = items.find(item => item.id === saved) ?? items[0];
        window.location.replace(`/o/${workspace.id}`);
        return;
      }
      setOrganizations(items);
      setLoadState('loaded');
    }).catch(() => { if (!disposed) setLoadState('error'); });
    return () => { disposed = true; };
  }, [home, loadAttempt, userId]);

  useEffect(() => {
    if (!workspaceInvitation) return;
    const controller = new AbortController();
    setBusy(true);
    void previewInvitation(workspaceInvitation, controller.signal)
      .then(setPreview)
      .catch((failure: Error) => { if (!controller.signal.aborted) setError(failure.message); })
      .finally(() => { if (!controller.signal.aborted) setBusy(false); });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (mode === 'settings') void loadSettings().catch(() => setError('Sign in with your authenticator to manage this workspace.'));
  }, [mode, loadSettings]);

  function open(next: typeof mode) {
    if (next === null && workspaceInvitation && window.location.hash) {
      window.history.replaceState(null, '', window.location.pathname + window.location.search);
    }
    setError('');
    setPreview(null);
    setLink('');
    setCopyStatus('');
    setPendingRemoval(null);
    setMode(next);
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
      window.location.assign(`/o/${(await res.json()).id}`);
    });
  }

  function review(e: FormEvent) {
    e.preventDefault();
    void submit(async () => {
      const input = token.trim();
      const invitation = /^[0-9a-f]{64}$/i.test(input) ? input : new URLSearchParams(input.split('#')[1]).get('organization-invitation');
      if (!invitation) throw new Error('Enter a valid workspace invitation link.');
      setPreview(await previewInvitation(invitation));
    });
  }

  async function join() {
    if (!preview) return;
    const res = await protectedRequest('/api/organizations/invitations/redeem', 'POST', { token: preview.token });
    if (!res.ok) throw new Error(res.status === 409 ? 'You already belong to this workspace.' : 'This invitation is invalid, expired or already used.');
    window.location.assign(`/o/${(await res.json()).id}`);
  }

  async function invite() {
    const res = await protectedRequest(`${scopeApi}/invitations`, 'POST');
    if (!res.ok) throw new Error('Unable to create an invitation.');
    setCopyStatus('');
    setLink(`${window.location.origin}/personal#organization-invitation=${(await res.json()).token}`);
    await loadSettings();
  }

  return (
    <>
      {home ? (
        <main className="nx-workspace-home">
          <section className="nx-workspace-home-card" aria-labelledby="workspace-home-title">
            <span className="nx-brand-mark" aria-hidden="true">N</span>
            <h1 id="workspace-home-title">Welcome to Nexus</h1>
            {loadState === 'loading' ? <p role="status">Opening your workspace…</p> : <>
              {loadState === 'error' && <p role="alert">Unable to load your workspaces.</p>}
              <p>Create a workspace for your team, join with an invitation, or start a personal chat.</p>
              <div className="nx-workspace-home-actions">
                {loadState === 'error' && <Button onClick={() => { setLoadState('loading'); setLoadAttempt(n => n + 1); }}>Retry</Button>}
                <Button variant="primary" onClick={() => open('create')}>Create workspace</Button>
                <Button onClick={() => open('join')}>Join a workspace</Button>
                <Button onClick={() => window.location.assign('/personal')}>Start a personal chat</Button>
              </div>
              <div className="nx-workspace-home-menu">
                <Button variant="ghost" onClick={onSignOut}>Sign out</Button>
              </div>
            </>}
          </section>
        </main>
      ) : <div className="nx-context-picker">
      <select aria-label="Switch workspace" value={organizationId ?? 'personal'} className="nx-select" onChange={e => {
        const value = e.target.value;
        if (value === '+create' || value === '+join' || value === '+settings') open(value.slice(1) as 'create' | 'join' | 'settings');
        else window.location.assign(value === 'personal' ? '/personal' : `/o/${value}`);
      }}>
        <option value="personal">Personal Chat</option>
        {active == null && organizationId && <option value={organizationId}>Workspace</option>}
        {organizations.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
        <option value="+create">Create workspace</option>
        <option value="+join">Join a workspace</option>
        {(active?.isOwner || active?.role === 'SystemAdmin') && <option value="+settings">Workspace settings</option>}
      </select>
      </div>}
      <Modal open={mode !== null} onOpenChange={value => { if (!value) open(null); }} title={
        mode === 'settings' ? 'Workspace settings' : mode === 'join' ? (preview ? `Join ${preview.name}` : 'Join a workspace') : 'Create workspace'
      }>
        {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        {mode === 'create' && <form onSubmit={create}>
          <Field label="Workspace name" htmlFor="organization-name">
            <input id="organization-name" required maxLength={120} value={name} onChange={e => setName(e.currentTarget.value)} />
          </Field>
          <Button type="submit" variant="primary" disabled={busy || !name.trim()}>Create workspace</Button>
        </form>}
        {mode === 'join' && (preview ? <>
          <p>Accept the invitation to access its people, chats, and communities.</p>
          <Button variant="primary" disabled={busy} onClick={() => void submit(join)}>Join {preview.name}</Button>
        </> : <form onSubmit={review}>
          <Field label="Invitation link" htmlFor="organization-link">
            <input id="organization-link" required value={token} onChange={e => setToken(e.currentTarget.value)} />
          </Field>
          <Button type="submit" variant="primary" disabled={busy}>{busy ? 'Checking invitation…' : 'Review invitation'}</Button>
        </form>)}
        {mode === 'settings' && <>
          {!mfaEnabled && error && <Button onClick={() => { open(null); onSecurity(); }}>Open profile &amp; security</Button>}
          <Card title="Invitations">
            <p>Anyone with the link can join once. Invitations expire after seven days.</p>
            <Button onClick={() => void submit(invite)} disabled={busy}>Create invitation link</Button>
            {link && <div style={{ marginTop: 12 }}>
              <Field label="Invitation link" htmlFor="new-invitation-link">
                <input id="new-invitation-link" value={link} readOnly onFocus={e => e.currentTarget.select()} />
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
            </div>}
            {invites.filter(i => !i.revokedAt && !i.redeemedAt && Date.parse(i.expiresAt) > Date.now()).map(invitation => <div className="nx-row" key={invitation.id}>
              <span>Expires {new Date(invitation.expiresAt).toLocaleDateString()}</span>
              <Button size="sm" disabled={busy} onClick={() => void submit(async () => {
                const res = await protectedRequest(`${scopeApi}/invitations/${invitation.id}`, 'DELETE');
                if (!res.ok) throw new Error('Unable to revoke the invitation.');
                setLink('');
                await loadSettings();
              })}>Revoke</Button>
            </div>)}
          </Card>
          <Card title="Members">
            {members.map(member => <div className="nx-row" key={member.id}>
              <span>{member.displayName} · {member.isOwner ? 'Owner' : member.role === 'SystemAdmin' ? 'Admin' : member.role}</span>
              {!member.isOwner && (active?.isOwner || member.role === 'Member') && <Button size="sm" disabled={busy} onClick={() => setPendingRemoval(member)}>Remove</Button>}
              {!member.isOwner && active?.isOwner && ['Member', 'SystemAdmin'].includes(member.role) && <Button size="sm" disabled={busy} onClick={() => void submit(async () => {
                const res = await protectedRequest(`${scopeApi}/members/${member.id}/administrator`, 'PUT', { administrator: member.role === 'Member' });
                if (!res.ok) throw new Error('Unable to change the member role.');
                await loadSettings();
              })}>{member.role === 'Member' ? 'Make admin' : 'Remove admin role'}</Button>}
            </div>)}
            {pendingRemoval && <div className="nx-row">
              <span>Remove {pendingRemoval.displayName} from this workspace?</span>
              <Button size="sm" onClick={() => setPendingRemoval(null)}>Cancel</Button>
              <Button size="sm" variant="danger" disabled={busy} onClick={() => void submit(async () => {
                const res = await protectedRequest(`${scopeApi}/members/${pendingRemoval.id}`, 'DELETE');
                if (!res.ok) throw new Error(res.status === 409 ? 'Transfer this member’s group or Community ownership before removing access.' : 'Unable to remove the member.');
                setPendingRemoval(null);
                await loadSettings();
              })}>Confirm removal</Button>
            </div>}
          </Card>
        </>}
      </Modal>
    </>
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

  return <Card title="Connections">
    <p>Send a connection request. You can chat once the other person accepts.</p>
    {message && <p role="status">{message}</p>}
    {receivedToken && <Button disabled={busy} onClick={async () => {
      if (!await run('links/redeem', 'POST', { token: receivedToken })) return;
      setReceivedToken('');
      window.history.replaceState(null, '', window.location.pathname);
      setMessage('Connection requested. The sender must accept it before you can chat.');
    }}>Request connection from link</Button>}
    <form className="nx-row" style={{ alignItems: 'flex-end', gap: 10 }} onSubmit={async e => {
      e.preventDefault();
      if (!await run('requests', 'POST', { username })) return;
      setUsername('');
      setMessage('If this account can receive requests, your connection request was sent.');
    }}>
      <div style={{ flex: 1, minWidth: 220 }}>
        <Field label="Nexus username" htmlFor="contact-username" compact><input ref={usernameRef} id="contact-username" required value={username} onChange={e => setUsername(e.currentTarget.value)} /></Field>
      </div>
      <Button type="submit" variant="primary" disabled={busy || !username.trim()}>Request connection</Button>
    </form>
    <div style={{ marginTop: 12 }}>
      <Button disabled={busy} onClick={async () => {
        const res = await run('links', 'POST');
        if (res) {
          setCopyStatus('');
          setLink(`${window.location.origin}/personal#personal-contact=${(await res.json()).token}`);
        }
      }}>Create contact link</Button>
    </div>
    {link && <div style={{ marginTop: 12 }}>
      <Field label="Copy contact link" htmlFor="new-contact-link"><input id="new-contact-link" value={link} readOnly onFocus={e => e.currentTarget.select()} /></Field>
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
    </div>}
    {requests.map(request => <div className="nx-row" key={request.id}>
      <span>{request.displayName} · @{request.username}</span>
      {[true, false].map(accept => <Button key={String(accept)} size="sm" disabled={busy} onClick={async () => {
        if (!await run(`requests/${request.id}`, 'PUT', { accept })) return;
        setRequests(previous => previous.filter(item => item.id !== request.id));
        if (accept) onChange();
      }}>{accept ? 'Accept' : 'Decline'}</Button>)}
    </div>)}
  </Card>;
}
