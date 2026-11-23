import { scopedFetch as fetch, isWorkspaceHome, organizationId, personalContactLink, rememberWorkspace, scopeApi, workspaceInvitation } from "./scope";
import { ContactControls, OrganizationControls } from "./organizationControls";
import { ChangeEvent, FormEvent, KeyboardEvent, useCallback, useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Briefcase, LayoutGrid, LogOut, MessageSquare, Moon, Search, Shield, Sun, Users } from 'lucide-react';
import { ChatWorkspace } from './chat';
import { CommunitiesWorkspace } from './communities';
import { Avatar, Badge, Button, Card, Field, Modal, ToastBar } from './ui';
import './style.css';

interface UserSummary {
  id: string;
  username: string;
  displayName: string;
  status: string;
  isUnverified: boolean;
  orgRole: string;
  mfaEnabled: boolean;
  mfaRequired: boolean;
  mfaSetupRequired: boolean;
  customStatus?: string | null;
  avatarUrl?: string | null;
}

interface DirectoryProfile {
  id: string;
  username: string;
  displayName: string;
  departmentId?: string | null;
  departmentName?: string | null;
  branchName?: string | null;
  customStatus?: string | null;
  avatarUrl?: string | null;
}

interface DepartmentItem {
  id: string;
  code: string;
  name: string;
}

interface SessionItem {
  id: string;
  userAgent: string;
  ipAddress: string;
  createdAt: string;
  lastActivityAt: string;
  isCurrent: boolean;
}

interface RegistrationPolicy {
  mode: string;
}

interface HrImportReport {
  dryRun: boolean;
  totalRows: number;
  addedCount: number;
  updatedCount: number;
  unchangedCount: number;
  deactivatedCount: number;
  errors: string[];
  matchSuggestions: Array<{
    userId: string;
    username: string;
    displayName: string;
    employeeCode: string;
    employeeFullName: string;
    confidence: string;
    reason: string;
  }>;
}

async function searchDirectory(query: string, departmentId: string, signal?: AbortSignal): Promise<DirectoryProfile[]> {
  const params = new URLSearchParams({ limit: '50' });
  const name = query.trim().replace(/^@/, '');
  if (name) params.set('q', name);
  if (departmentId) params.set('departmentId', departmentId);
  const res = await fetch(`/api/people/directory?${params}`, { signal });
  if (!res.ok) throw new Error(organizationId ? 'Unable to load workspace members.' : 'Unable to load contacts.');
  return (await res.json()).items;
}

function NewChatDialog({ currentUserId, onClose, onOpenChat, onOpenContacts }: {
  currentUserId: string;
  onClose: () => void;
  onOpenChat: (userId: string) => Promise<void>;
  onOpenContacts: () => void;
}) {
  const [query, setQuery] = useState('');
  const [people, setPeople] = useState<DirectoryProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchError, setSearchError] = useState('');
  const [openError, setOpenError] = useState('');
  const [opening, setOpening] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const searchInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void searchDirectory(query, '', controller.signal).then(items => {
        if (!controller.signal.aborted) setPeople(items.filter(item => item.id !== currentUserId));
      }).catch((failure: Error) => {
        if (!controller.signal.aborted) setSearchError(failure.message);
      }).finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    }, query.trim() ? 180 : 0);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [attempt, currentUserId, query]);

  async function choose(userId: string) {
    setOpenError('');
    setOpening(true);
    try { await onOpenChat(userId); onClose(); }
    catch (failure) { setOpenError((failure as Error).message); }
    finally { setOpening(false); }
  }

  return <Modal open onOpenChange={open => { if (!open && !opening) onClose(); }} title="New chat"
    description={organizationId ? 'Find someone in this workspace.' : 'Find an accepted contact or send a connection request.'}
    initialFocusRef={searchInput}>
    <Field label={organizationId ? 'Search workspace members' : 'Search contacts'} htmlFor="new-chat-search">
      <input ref={searchInput} id="new-chat-search" type="search" placeholder="Name or username" value={query} onChange={event => {
        setLoading(true);
        setSearchError('');
        setOpenError('');
        setQuery(event.currentTarget.value);
      }} />
    </Field>
    {openError && <p className="nx-error" role="alert">{openError}</p>}
    {loading ? <p className="nx-muted" role="status">Searching…</p> : searchError ? (
      <div className="nx-row">
        <p className="nx-error" role="alert">{searchError}</p>
        <Button size="sm" onClick={() => { setLoading(true); setSearchError(''); setAttempt(n => n + 1); }}>Retry</Button>
      </div>
    ) : people.length > 0 ? (
      <ul className="nx-new-chat-results" aria-label={organizationId ? 'Workspace members' : 'Contacts'}>
        {people.map(person => <li key={person.id}>
          <Button variant="ghost" fullWidth disabled={opening} onClick={() => void choose(person.id)}>
            <Avatar displayName={person.displayName} avatarUrl={person.avatarUrl} />
            <span><strong>{person.displayName}</strong> <span className="nx-muted">@{person.username}</span></span>
          </Button>
        </li>)}
      </ul>
    ) : <p className="nx-muted" role="status">{organizationId ? 'No matching workspace members.' : 'No matching contacts.'}</p>}
    {!organizationId && <Button onClick={onOpenContacts}>Add a contact</Button>}
  </Modal>;
}

function App() {
  const [apiVersion, setApiVersion] = useState<string | null>(null);
  const [versionStale, setVersionStale] = useState(false);
  const [policy, setPolicy] = useState<RegistrationPolicy | null>(null);
  const [user, setUser] = useState<UserSummary | null>(null);
  const [authLoading, setAuthLoading] = useState(true);
  const [authTab, setAuthTab] = useState<'login' | 'register'>('login');
  const loginTabRef = useRef<HTMLButtonElement | null>(null);
  const registerTabRef = useRef<HTMLButtonElement | null>(null);
  const [workspaceTab, setWorkspaceTab] = useState<'chat' | 'communities' | 'directory' | 'security' | 'hr'>(() => {
    if (personalContactLink) return 'directory';
    return new URLSearchParams(window.location.search).get('view') === 'security' ? 'security' : 'chat';
  });
  const [newChatOpen, setNewChatOpen] = useState(false);
  const [activeConvId, setActiveConvId] = useState<string | null>(null);
  const communityFeedListenerRef = useRef<((conversationId?: string) => void) | null>(null);
  const [connState, setConnState] = useState<'connected' | 'reconnecting' | 'offline'>('reconnecting');
  const [unreadTotal, setUnreadTotal] = useState(0);
  const [darkMode, setDarkMode] = useState(() => localStorage.getItem('nexus-theme') === 'dark');

  const [sessions, setSessions] = useState<SessionItem[]>([]);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [avatarBust, setAvatarBust] = useState(0);

  // Login & Register state
  const [loginUsername, setLoginUsername] = useState('');
  const [loginPassword, setLoginPassword] = useState('');
  const [loginTotp, setLoginTotp] = useState('');
  const [mfaChallenge, setMfaChallenge] = useState(false);

  const [regUsername, setRegUsername] = useState('');
  const [regDisplayName, setRegDisplayName] = useState('');
  const [regPassword, setRegPassword] = useState('');
  const [regJoinCode, setRegJoinCode] = useState('');
  const [regTerms, setRegTerms] = useState(false);
  const [regHoneypot, setRegHoneypot] = useState('');

  // Profile, Password & MFA state
  const [customStatusInput, setCustomStatusInput] = useState('');
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [mfaSecret, setMfaSecret] = useState<string | null>(null);
  const [mfaCode, setMfaCode] = useState('');
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);

  // Directory state
  const [dirQuery, setDirQuery] = useState('');
  const [dirDeptId, setDirDeptId] = useState('');
  const [departments, setDepartments] = useState<DepartmentItem[]>([]);
  const [directory, setDirectory] = useState<DirectoryProfile[]>([]);
  const contactUsernameRef = useRef<HTMLInputElement | null>(null);

  // HR CSV Import state
  const [hrCsv, setHrCsv] = useState('employee_code,full_name,email,department_code,department_name,branch_code,branch_name,is_active\n');
  const [hrReport, setHrReport] = useState<HrImportReport | null>(null);

  useEffect(() => {
    document.body.classList.toggle('dark', darkMode);
    localStorage.setItem('nexus-theme', darkMode ? 'dark' : 'light');
  }, [darkMode]);

  // Prevent accidental file drops outside the chat dropzone from navigating WebView2/browser away.
  useEffect(() => {
    const preventFileNav = (ev: DragEvent) => {
      if (ev.dataTransfer?.types && Array.from(ev.dataTransfer.types).includes('Files')) {
        ev.preventDefault();
      }
    };
    window.addEventListener('dragover', preventFileNav);
    window.addEventListener('drop', preventFileNav);
    return () => {
      window.removeEventListener('dragover', preventFileNav);
      window.removeEventListener('drop', preventFileNav);
    };
  }, []);

  const loadSessions = useCallback(async () => {
    const res = await fetch('/api/auth/sessions', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setSessions(data.items ?? []);
    }
  }, []);

  const loadDirectory = useCallback(async (q: string, deptId: string) => {
    try { setDirectory(await searchDirectory(q, deptId)); } catch { setDirectory([]); }
  }, []);

  const loadDepartments = useCallback(async () => {
    if (!organizationId) return;
    const res = await fetch('/api/org/departments?limit=50', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setDepartments(data.items ?? []);
    }
  }, []);

  const refreshMe = useCallback(async () => {
    const res = await fetch('/api/auth/me', { credentials: 'same-origin' });
    if (res.ok) {
      const account: UserSummary = await res.json();
      let me = account;
      if (organizationId) {
        const profile = await fetch(`${scopeApi}/me`);
        if (profile.status === 403 || profile.status === 404) {
          window.location.replace('/');
          return;
        }
        if (!profile.ok) return;
        me = { ...account, ...await profile.json() };
        rememberWorkspace(me.id, organizationId);
      }
      setUser(me);
      setCustomStatusInput(me.customStatus ?? '');
      if (!isWorkspaceHome) await Promise.all([loadSessions(), loadDepartments(), loadDirectory('', '')]);
    } else {
      setUser(null);
      setSessions([]);
      setDirectory([]);
    }
  }, [loadSessions, loadDepartments, loadDirectory]);

  useEffect(() => {
    const controller = new AbortController();
    async function bootstrap() {
      try {
        const verRes = await fetch('/api/version', { signal: controller.signal });
        if (verRes.ok) {
          const ver = await verRes.json();
          setApiVersion(ver.version ?? null);
        }

        const polRes = await fetch('/api/auth/registration-policy', { signal: controller.signal });
        if (polRes.ok) setPolicy(await polRes.json());

        await refreshMe();
      } catch {
        // Offline or aborted.
      } finally {
        if (!controller.signal.aborted) setAuthLoading(false);
      }
    }
    void bootstrap();
    return () => controller.abort();
  }, [refreshMe]);

  async function handleLogin(e: FormEvent) {
    e.preventDefault();
    setError('');
    setNotice('');
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username: loginUsername,
        password: loginPassword,
        totpCode: mfaChallenge ? loginTotp : null
      })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      if (data.requiresMfa) {
        setMfaChallenge(true);
        setError('Enter your 6-digit authenticator code to complete sign in.');
      } else {
        setError(data.message || 'Invalid username or password.');
      }
      return;
    }
    setMfaChallenge(false);
    setLoginPassword('');
    setLoginTotp('');
    await refreshMe();
  }

  async function handleRegister(e: FormEvent) {
    e.preventDefault();
    setError('');
    setNotice('');
    const res = await fetch('/api/auth/register', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username: regUsername,
        displayName: regDisplayName,
        password: regPassword,
        termsAccepted: regTerms,
        joinCode: regJoinCode || null,
        honeypot: regHoneypot || null
      })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Registration failed.');
      return;
    }
    setRegPassword('');
    await refreshMe();
  }

  async function handleLogout() {
    const res = await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
    if (res.ok || res.status === 401) window.location.replace('/');
    else setError('Unable to sign out. Try again.');
  }

  async function openDm(targetUserId: string) {
    const res = await fetch('/api/conversations/dm', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ targetUserId })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      if (res.status === 404) throw new Error(organizationId ? 'This person is no longer available in this workspace.' : 'This contact is no longer available.');
      throw new Error(data.message || 'Unable to open chat.');
    }
    const dm = await res.json();
    setActiveConvId(dm.id);
    setWorkspaceTab('chat');
  }

  async function handleOpenDm(targetUserId: string) {
    setError('');
    try { await openDm(targetUserId); } catch (failure) { setError((failure as Error).message); }
  }

  async function handleUpdateCustomStatus(e: FormEvent) {
    e.preventDefault();
    setError('');
    setNotice('');
    const res = await fetch('/api/people/me/status', {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ customStatus: customStatusInput })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Unable to update custom status.');
      return;
    }
    setNotice('Custom status updated.');
    await refreshMe();
  }

  async function handleAvatarChange(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setError('');
    setNotice('');
    const res = await fetch('/api/people/me/avatar', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': file.type || 'application/octet-stream' },
      body: file
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Unable to upload avatar.');
      return;
    }
    setAvatarBust(v => v + 1);
    setNotice('Avatar updated.');
    await refreshMe();
  }

  async function handleChangePassword(e: FormEvent) {
    e.preventDefault();
    setError('');
    setNotice('');
    const res = await fetch('/api/auth/change-password', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ currentPassword, newPassword })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Unable to change password.');
      return;
    }
    setCurrentPassword('');
    setNewPassword('');
    setNotice('Password updated. Other devices have been signed out.');
    await loadSessions();
  }

  async function handleRevokeSession(sessionId: string) {
    const res = await fetch(`/api/auth/sessions/${encodeURIComponent(sessionId)}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (res.ok) await loadSessions();
  }

  async function handleStartMfa() {
    setError('');
    const res = await fetch('/api/auth/mfa/setup', { method: 'POST', credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setMfaSecret(data.sharedKey);
    }
  }

  async function handleEnableMfa(e: FormEvent) {
    e.preventDefault();
    setError('');
    const res = await fetch('/api/auth/mfa/enable', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code: mfaCode })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Invalid authenticator code.');
      return;
    }
    setMfaCode('');
    setMfaSecret(null);
    setRecoveryCodes(data.recoveryCodes ?? []);
    setNotice('Multi-factor authentication enabled.');
    await refreshMe();
  }

  async function handleHrImport(dryRun: boolean) {
    setError('');
    setNotice('');
    const res = await fetch(`/api/org/employees/import?dryRun=${dryRun ? 'true' : 'false'}`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ csvContent: hrCsv })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || (data.errors ? data.errors.join(' ') : 'Import failed.'));
      return;
    }
    setHrReport(data);
    setNotice(dryRun ? 'Dry-run diff generated.' : 'HR directory import applied.');
    if (!dryRun) await loadDepartments();
  }

  function openContactsFromChat() {
    setNewChatOpen(false);
    setWorkspaceTab('directory');
    void loadDirectory(dirQuery, dirDeptId);
    requestAnimationFrame(() => contactUsernameRef.current?.focus());
  }

  const handleVersionStale = useCallback(() => {
    setVersionStale(true);
  }, []);

  if (authLoading) {
    return (
      <div className="nx-empty-stage" style={{ minHeight: '100dvh' }}>
        <div className="nx-brand-mark">N</div>
        <p style={{ marginTop: 14 }}>Connecting to Nexus…</p>
      </div>
    );
  }

  if (!user) {
    const switchAuthTab = (next: 'login' | 'register', moveFocus = false) => {
      setAuthTab(next);
      setError('');
      if (moveFocus) {
        (next === 'login' ? loginTabRef : registerTabRef).current?.focus();
      }
    };

    const handleAuthTabKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
      if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
        e.preventDefault();
        switchAuthTab(authTab === 'login' ? 'register' : 'login', true);
      } else if (e.key === 'Home') {
        e.preventDefault();
        switchAuthTab('login', true);
      } else if (e.key === 'End') {
        e.preventDefault();
        switchAuthTab('register', true);
      }
    };

    return (
      <main className="nx-auth-page">
        <section className="nx-auth-hero">
          <div className="nx-auth-brand">
            <span className="nx-brand-mark">N</span>
            <strong className="nx-auth-brand-name">Nexus</strong>
          </div>

          <div>
            <h1>Workplace communication, simplified.</h1>
            <p>
              Message people, start group chats, and stay connected in your team’s workspace.
            </p>
            <ul className="nx-auth-features">
              <li>● Instant messaging with offline queue &amp; reliable sync</li>
              <li>● Private conversations protected by strict membership controls</li>
              <li>● Vietnamese-aware directory search &amp; TOTP account security</li>
            </ul>
          </div>

        </section>

        <section className="nx-auth-panel">
          <div className="nx-auth-card">
            {workspaceInvitation && <p className="nx-muted">Sign in or create an account to accept your workspace invitation.</p>}
            <div
              className="nx-auth-switcher"
              role="tablist"
              aria-label="Authentication mode"
              onKeyDown={handleAuthTabKeyDown}
            >
              <button
                ref={loginTabRef}
                id="auth-tab-login"
                type="button"
                role="tab"
                aria-selected={authTab === 'login'}
                aria-controls="auth-panel-login"
                tabIndex={authTab === 'login' ? 0 : -1}
                className={authTab === 'login' ? 'active' : ''}
                onClick={() => switchAuthTab('login')}
              >
                Sign in
              </button>
              <button
                ref={registerTabRef}
                id="auth-tab-register"
                type="button"
                role="tab"
                aria-selected={authTab === 'register'}
                aria-controls="auth-panel-register"
                tabIndex={authTab === 'register' ? 0 : -1}
                className={authTab === 'register' ? 'active' : ''}
                onClick={() => switchAuthTab('register')}
              >
                Create account
              </button>
            </div>

            {error && <p className="nx-error" role="alert">{error}</p>}
            {notice && <p className="nx-ok" role="status">{notice}</p>}

            <div
              role="tabpanel"
              id={authTab === 'login' ? 'auth-panel-login' : 'auth-panel-register'}
              aria-labelledby={authTab === 'login' ? 'auth-tab-login' : 'auth-tab-register'}
            >
            {authTab === 'login' ? (
              <form onSubmit={handleLogin}>
                <Field label="Username" htmlFor="login-username">
                  <input id="login-username" type="text" autoComplete="username" required value={loginUsername} onChange={e => setLoginUsername(e.target.value)} />
                </Field>
                <Field label="Password" htmlFor="login-password">
                  <input id="login-password" type="password" autoComplete="current-password" required value={loginPassword} onChange={e => setLoginPassword(e.target.value)} />
                </Field>
                {mfaChallenge && (
                  <Field label="Authenticator code" htmlFor="login-totp">
                    <input id="login-totp" type="text" inputMode="numeric" required value={loginTotp} onChange={e => setLoginTotp(e.target.value)} />
                  </Field>
                )}
                <Button type="submit" variant="primary" fullWidth>
                  Sign in to Nexus
                </Button>
              </form>
            ) : (
              <form onSubmit={handleRegister}>
                <div className="nx-hp" aria-hidden="true">
                  <label htmlFor="reg-website">Website</label>
                  <input id="reg-website" tabIndex={-1} autoComplete="off" value={regHoneypot} onChange={e => setRegHoneypot(e.target.value)} />
                </div>
                <Field label="Username (3–32 characters)" htmlFor="reg-username">
                  <input id="reg-username" type="text" autoComplete="username" required minLength={3} maxLength={32} value={regUsername} onChange={e => setRegUsername(e.target.value)} />
                </Field>
                <Field label="Display name" htmlFor="reg-display-name">
                  <input id="reg-display-name" type="text" autoComplete="name" required maxLength={80} value={regDisplayName} onChange={e => setRegDisplayName(e.target.value)} />
                </Field>
                <Field label="Password (at least 12 characters)" htmlFor="reg-password">
                  <input id="reg-password" type="password" autoComplete="new-password" required minLength={12} value={regPassword} onChange={e => setRegPassword(e.target.value)} />
                </Field>
                {policy?.mode === 'OpenWithJoinCode' && (
                  <Field label="Join code" htmlFor="reg-join-code">
                    <input id="reg-join-code" type="text" required value={regJoinCode} onChange={e => setRegJoinCode(e.target.value)} />
                  </Field>
                )}
                <div className="nx-field">
                  <label className="nx-terms-label">
                    <input type="checkbox" checked={regTerms} onChange={e => setRegTerms(e.target.checked)} required />
                    <span>I accept the terms of use.</span>
                  </label>
                </div>
                <Button type="submit" variant="primary" fullWidth>
                  Create Nexus account
                </Button>
              </form>
            )}
            </div>
          </div>
        </section>
      </main>
    );
  }

  if (isWorkspaceHome) {
    return <>
      <OrganizationControls home userId={user.id} mfaEnabled={user.mfaEnabled} onSignOut={() => void handleLogout()}
        onSecurity={() => window.location.assign('/personal?view=security')} />
      {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
    </>;
  }

  const pageTitle =
    workspaceTab === 'chat' ? 'Chat' :
    workspaceTab === 'communities' ? 'Communities' :
    workspaceTab === 'directory' ? (organizationId ? 'Directory' : 'Contacts') :
    workspaceTab === 'security' ? 'Profile & security' :
    'HR import';

  return (
    <div className="nx-app">
      {/* Left App Rail */}
      <nav className="nx-rail" aria-label="Main navigation">
        <div className="nx-rail-logo" title="Nexus">
          <span className="nx-brand-mark sm">N</span>
        </div>

        <div className="nx-rail-nav">
          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'chat' ? 'active' : ''}`}
            aria-current={workspaceTab === 'chat' ? 'page' : undefined}
            onClick={() => setWorkspaceTab('chat')}
          >
            <MessageSquare size={20} aria-hidden="true" />
            <span>Chat</span>
            {unreadTotal > 0 && (
              <span className="nx-rail-badge" aria-label={`${unreadTotal} unread messages`}>
                {unreadTotal > 99 ? '99+' : unreadTotal}
              </span>
            )}
          </button>

          {organizationId && <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'communities' ? 'active' : ''}`}
            aria-current={workspaceTab === 'communities' ? 'page' : undefined}
            onClick={() => setWorkspaceTab('communities')}
          >
            <LayoutGrid size={20} aria-hidden="true" />
            <span>Communities</span>
          </button>}

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'directory' ? 'active' : ''}`}
            aria-current={workspaceTab === 'directory' ? 'page' : undefined}
            onClick={() => {
              setWorkspaceTab('directory');
              void loadDirectory(dirQuery, dirDeptId);
            }}
          >
            <Users size={20} aria-hidden="true" />
            <span>{organizationId ? "Directory" : "Contacts"}</span>
          </button>

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'security' ? 'active' : ''}`}
            aria-current={workspaceTab === 'security' ? 'page' : undefined}
            onClick={() => setWorkspaceTab('security')}
          >
            <Shield size={20} aria-hidden="true" />
            <span>Profile &amp; security</span>
          </button>

          {user.orgRole === 'HRAdmin' && (
            <button
              type="button"
              className={`nx-rail-btn ${workspaceTab === 'hr' ? 'active' : ''}`}
              aria-current={workspaceTab === 'hr' ? 'page' : undefined}
              onClick={() => setWorkspaceTab('hr')}
            >
              <Briefcase size={20} aria-hidden="true" />
              <span>HR import</span>
            </button>
          )}
        </div>

        <div className="nx-rail-footer">
          <button
            type="button"
            className="nx-rail-btn compact"
            title={darkMode ? 'Switch to light theme' : 'Switch to dark theme'}
            aria-label={darkMode ? 'Switch to light theme' : 'Switch to dark theme'}
            onClick={() => setDarkMode(v => !v)}
          >
            {darkMode ? <Sun size={18} aria-hidden="true" /> : <Moon size={18} aria-hidden="true" />}
          </button>

          <button
            type="button"
            className="nx-rail-avatar-btn"
            title={`${user.displayName} (@${user.username})`}
            aria-label="Open profile & security"
            onClick={() => setWorkspaceTab('security')}
          >
            <Avatar
              displayName={user.displayName}
              avatarUrl={user.avatarUrl}
              cacheKey={avatarBust}
              presence="online"
              onRail
            />
          </button>

          <button
            type="button"
            className="nx-rail-btn compact"
            title="Sign out"
            aria-label="Sign out"
            onClick={handleLogout}
          >
            <LogOut size={18} aria-hidden="true" />
          </button>
        </div>
      </nav>

      {/* Main Workspace */}
      <div className="nx-workspace">
        <header className="nx-topbar">
          <div className="nx-topbar-left">
            <OrganizationControls userId={user.id} mfaEnabled={user.mfaEnabled} onSignOut={() => void handleLogout()} onSecurity={() => setWorkspaceTab('security')} />
            <h1 className="nx-topbar-title">{pageTitle}</h1>
          </div>

          <div className="nx-topbar-right">
            {versionStale && (
              <Button variant="primary" size="sm" onClick={() => window.location.reload()}>
                Update available · Reload
              </Button>
            )}
            <span className="nx-conn-pill" role="status" title={apiVersion ? `API v${apiVersion}` : undefined}>
              <span className={`nx-dot ${connState}`} />
              <span>{connState === 'connected' ? 'Connected' : connState === 'reconnecting' ? 'Reconnecting…' : 'Offline'}</span>
            </span>
            <strong className="nx-user-name-wrap">{user.displayName}</strong>
          </div>
        </header>

        {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        {notice && <ToastBar variant="ok" message={notice} onDismiss={() => setNotice('')} />}

        <main className="nx-main-area">
          <div className={`nx-tab-keepalive ${workspaceTab === 'chat' ? '' : 'hidden'}`}>
            <ChatWorkspace
              currentUserId={user.id}
              currentDisplayName={user.displayName}
              initialVersion={apiVersion}
              selectedConversationId={activeConvId}
              directoryUsers={directory}
              onSelectConversation={setActiveConvId}
              onNewChat={() => setNewChatOpen(true)}
              onConnectionStateChange={setConnState}
              onUnreadTotalChange={setUnreadTotal}
              onCommunityFeedChanged={conversationId => communityFeedListenerRef.current?.(conversationId)}
              onVersionStale={handleVersionStale}
            />
          </div>
          {newChatOpen && <NewChatDialog
            currentUserId={user.id}
            onClose={() => setNewChatOpen(false)}
            onOpenChat={openDm}
            onOpenContacts={openContactsFromChat}
          />}

          {organizationId && workspaceTab === 'communities' && (
            <CommunitiesWorkspace
              currentUserId={user.id}
              currentOrgRole={user.orgRole}
              currentIsUnverified={user.isUnverified}
              directoryUsers={directory}
              departments={departments}
              feedListenerRef={communityFeedListenerRef}
            />
          )}

          {workspaceTab === 'directory' && (
            <div className="nx-page-scroll">
              <div className="nx-page-container">
                <div className="nx-page-head">
                  <div>
                    <h2>{organizationId ? "Directory" : "Contacts"}</h2>
                    <p>{organizationId ? "Search by name or username." : "Search the people you have connected with."}</p>
                  {!organizationId && <ContactControls usernameRef={contactUsernameRef} onChange={() => void loadDirectory(dirQuery, '')} />}
                  </div>
                </div>

                <form
                  className="nx-filter-bar"
                  onSubmit={e => {
                    e.preventDefault();
                    void loadDirectory(dirQuery, dirDeptId);
                  }}
                >
                  <div className="nx-search-input-wrap" style={{ flex: 2, minWidth: 220 }}>
                    <Search size={15} aria-hidden="true" />
                    <input
                      id="dir-search"
                      type="text"
                      aria-label={organizationId ? 'Search directory' : 'Search contacts'}
                      placeholder="Search by name or @username…"
                      value={dirQuery}
                      onChange={e => setDirQuery(e.target.value)}
                    />
                  </div>
                  {organizationId && <select
                    id="dir-dept"
                    className="nx-select"
                    aria-label="Filter by department"
                    value={dirDeptId}
                    onChange={e => {
                      setDirDeptId(e.target.value);
                      void loadDirectory(dirQuery, e.target.value);
                    }}
                    style={{ flex: 1, minWidth: 180 }}
                  >
                    <option value="">All departments</option>
                    {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
                  </select>}
                  <Button type="submit" variant="primary">Search</Button>
                </form>

                {directory.length === 0 ? (
                  <Card>
                    <p className="nx-muted" style={{ margin: 0, textAlign: 'center' }}>No matching active accounts.</p>
                  </Card>
                ) : (
                  <div className="nx-dir-grid">
                    {directory.map(p => (
                      <article className="nx-person-card" key={p.id}>
                        <div className="nx-person-top">
                          <Avatar displayName={p.displayName} avatarUrl={p.avatarUrl} cacheKey={avatarBust} size="lg" />
                          <div style={{ minWidth: 0, flex: 1 }}>
                            <strong className="nx-user-name-wrap">{p.displayName}</strong>
                            <div className="nx-muted" style={{ marginTop: 2 }}>
                              @{p.username}
                              {p.departmentName ? ` · ${p.departmentName}` : ''}
                              {p.branchName ? ` · ${p.branchName}` : ''}
                            </div>
                            {p.customStatus && <div className="nx-person-quote">“{p.customStatus}”</div>}
                          </div>
                        </div>

                        <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                          {p.id === user.id ? (
                            <Badge variant="role">You</Badge>
                          ) : (
                            <Button variant="primary" size="sm" onClick={() => void handleOpenDm(p.id)}>
                              <MessageSquare size={14} aria-hidden="true" /> Message
                            </Button>
                          )}
                        </div>
                      </article>
                    ))}
                  </div>
                )}
              </div>
            </div>
          )}

          {workspaceTab === 'security' && (
            <div className="nx-page-scroll">
              <div className="nx-page-container">
                <div className="nx-page-head">
                  <div>
                    <h2>Profile &amp; security</h2>
                    <p>Manage your avatar, custom status, password, authenticator MFA, and signed-in devices.</p>
                  </div>
                  <Button variant="danger" onClick={handleLogout}>
                    <LogOut size={15} aria-hidden="true" /> Sign out
                  </Button>
                </div>

                {user.mfaSetupRequired && (
                  <ToastBar
                    variant="error"
                    rounded
                    message="Administrative roles require TOTP multi-factor authentication. Set up MFA below."
                  />
                )}

                <div className="nx-settings-grid">
                  {organizationId && <Card title="Profile & Status">
                    <div className="nx-profile-hero">
                      <Avatar displayName={user.displayName} avatarUrl={user.avatarUrl} cacheKey={avatarBust} size="lg" />
                      <div>
                        <strong className="nx-user-name-wrap">{user.displayName}</strong>
                        <div className="nx-muted" style={{ marginTop: 3 }}>
                          @{user.username} · <Badge variant="role">{user.orgRole}</Badge>
                        </div>
                      </div>
                    </div>

                    <form onSubmit={handleUpdateCustomStatus} className="nx-status-form">
                      <div style={{ flex: 1 }}>
                        <Field label="Custom status" htmlFor="custom-status" compact>
                          <input
                            id="custom-status"
                            type="text"
                            maxLength={100}
                            placeholder="e.g. On site until 15:00"
                            value={customStatusInput}
                            onChange={e => setCustomStatusInput(e.target.value)}
                          />
                        </Field>
                      </div>
                      <Button type="submit" variant="primary">Save</Button>
                    </form>

                    <Field label="Avatar image (re-encoded to 256×256 PNG, max 2 MB)" htmlFor="avatar-upload" compact>
                      <input id="avatar-upload" type="file" accept="image/png,image/jpeg,image/webp,image/gif" onChange={handleAvatarChange} />
                    </Field>
                  </Card>}

                  <Card title="Change password">
                    <form onSubmit={handleChangePassword}>
                      <Field label="Current password" htmlFor="cur-pw">
                        <input id="cur-pw" type="password" autoComplete="current-password" required value={currentPassword} onChange={e => setCurrentPassword(e.target.value)} />
                      </Field>
                      <Field label="New password (at least 12 characters)" htmlFor="new-pw">
                        <input id="new-pw" type="password" autoComplete="new-password" required minLength={12} value={newPassword} onChange={e => setNewPassword(e.target.value)} />
                      </Field>
                      <Button type="submit" variant="primary">Update password</Button>
                    </form>
                  </Card>

                  <Card title="Authenticator MFA (TOTP)">
                    {user.mfaEnabled ? (
                      <p className="nx-ok">TOTP multi-factor authentication is active on your account.</p>
                    ) : mfaSecret ? (
                      <form onSubmit={handleEnableMfa}>
                        <p className="nx-muted">Add this shared key to your authenticator app:</p>
                        <p><code>{mfaSecret}</code></p>
                        <Field label="6-digit code" htmlFor="mfa-code">
                          <input id="mfa-code" type="text" inputMode="numeric" required value={mfaCode} onChange={e => setMfaCode(e.target.value)} />
                        </Field>
                        <Button type="submit" variant="primary">Verify &amp; enable MFA</Button>
                      </form>
                    ) : (
                      <>
                        <p className="nx-muted" style={{ marginBottom: 14 }}>Protect your Nexus account with a 6-digit authenticator code.</p>
                        <Button onClick={handleStartMfa}>Set up authenticator app</Button>
                      </>
                    )}
                    {recoveryCodes.length > 0 && (
                      <div style={{ marginTop: 14 }}>
                        <p className="nx-muted">Save these one-time recovery codes:</p>
                        <code>{recoveryCodes.join('  ')}</code>
                      </div>
                    )}
                  </Card>

                  <Card title={`Signed-in devices (${sessions.length})`}>
                    {sessions.map(s => (
                      <div className="nx-row" key={s.id}>
                        <div style={{ minWidth: 0 }}>
                          <div>
                            <strong>{s.isCurrent ? 'Current session' : 'Other session'}</strong>
                            <span className="nx-muted"> · {s.ipAddress}</span>
                          </div>
                          <div className="nx-muted" style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{s.userAgent}</div>
                        </div>
                        {!s.isCurrent && (
                          <Button variant="danger" size="sm" onClick={() => void handleRevokeSession(s.id)}>
                            Sign out
                          </Button>
                        )}
                      </div>
                    ))}
                  </Card>
                </div>
              </div>
            </div>
          )}

          {workspaceTab === 'hr' && user.orgRole === 'HRAdmin' && (
            <div className="nx-page-scroll">
              <div className="nx-page-container">
                <div className="nx-page-head">
                  <div>
                    <h2>HR CSV Import</h2>
                    <p>Preview changes, update employee records, and review suggested account matches.</p>
                  </div>
                </div>
                <Card>
                  <Field label="CSV records" htmlFor="hr-csv">
                    <textarea id="hr-csv" rows={7} value={hrCsv} onChange={e => setHrCsv(e.target.value)} />
                  </Field>
                  <div className="nx-inline-actions" style={{ marginBottom: 14 }}>
                    <Button onClick={() => void handleHrImport(true)}>Dry run (diff)</Button>
                    <Button variant="primary" onClick={() => void handleHrImport(false)}>Apply import</Button>
                  </div>
                  {hrReport && (
                    <div>
                      <p className="nx-muted">
                        {hrReport.dryRun ? 'Dry run' : 'Applied'} · Total: {hrReport.totalRows} · Added: {hrReport.addedCount} · Updated: {hrReport.updatedCount} · Unchanged: {hrReport.unchangedCount} · Deactivated: {hrReport.deactivatedCount}
                      </p>
                      {hrReport.matchSuggestions.length > 0 && (
                        <div>
                          <strong>Suggested account matches ({hrReport.matchSuggestions.length})</strong>
                          {hrReport.matchSuggestions.map(m => (
                            <div className="nx-row" key={`${m.userId}-${m.employeeCode}`}>
                              <span>{m.displayName} (@{m.username}) → <strong>{m.employeeCode}</strong> ({m.employeeFullName})</span>
                              <Badge variant="role">{m.confidence}</Badge>
                            </div>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </Card>
              </div>
            </div>
          )}
        </main>
      </div>
    </div>
  );
}

createRoot(document.getElementById('root')!).render(<App />);
