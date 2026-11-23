import { ChangeEvent, FormEvent, useCallback, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Briefcase, LayoutGrid, LogOut, MessageSquare, Moon, Search, Shield, Sun, Users } from 'lucide-react';
import { ChatWorkspace } from './chat';
import { CommunitiesWorkspace } from './communities';
import { Avatar, Badge, Button, Card, Field, ToastBar, UserNameWithBadge } from './ui';
import './style.css';

interface UserSummary {
  id: string;
  username: string;
  displayName: string;
  status: string;
  trustLevel: string;
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
  trustLevel: string;
  isUnverified: boolean;
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
  captchaEnabled: boolean;
  termsVersion: string;
  confidentialityNotice: string;
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

function App() {
  const [apiVersion, setApiVersion] = useState<string | null>(null);
  const [versionStale, setVersionStale] = useState(false);
  const [policy, setPolicy] = useState<RegistrationPolicy | null>(null);
  const [trialDismissed, setTrialDismissed] = useState(false);
  const [user, setUser] = useState<UserSummary | null>(null);
  const [authLoading, setAuthLoading] = useState(true);
  const [authTab, setAuthTab] = useState<'login' | 'register'>('login');
  const [workspaceTab, setWorkspaceTab] = useState<'chat' | 'communities' | 'directory' | 'security' | 'hr'>('chat');
  const [activeConvId, setActiveConvId] = useState<string | null>(null);
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

  // HR CSV Import state
  const [hrCsv, setHrCsv] = useState('employee_code,full_name,email,department_code,department_name,branch_code,branch_name,is_active\n');
  const [hrReport, setHrReport] = useState<HrImportReport | null>(null);

  useEffect(() => {
    document.body.classList.toggle('dark', darkMode);
    localStorage.setItem('nexus-theme', darkMode ? 'dark' : 'light');
  }, [darkMode]);

  const loadSessions = useCallback(async () => {
    const res = await fetch('/api/auth/sessions', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setSessions(data.items ?? []);
    }
  }, []);

  const loadDirectory = useCallback(async (q: string, deptId: string) => {
    const params = new URLSearchParams();
    if (q.trim()) params.set('q', q.trim());
    if (deptId) params.set('departmentId', deptId);
    params.set('limit', '50');
    const res = await fetch(`/api/people/directory?${params.toString()}`, { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setDirectory(data.items ?? []);
    }
  }, []);

  const loadDepartments = useCallback(async () => {
    const res = await fetch('/api/org/departments?limit=50', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setDepartments(data.items ?? []);
    }
  }, []);

  const refreshMe = useCallback(async () => {
    const res = await fetch('/api/auth/me', { credentials: 'same-origin' });
    if (res.ok) {
      const me: UserSummary = await res.json();
      setUser(me);
      setCustomStatusInput(me.customStatus ?? '');
      await Promise.all([loadSessions(), loadDepartments(), loadDirectory('', '')]);
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
    setNotice('Welcome to Nexus! Your account starts as Unverified.');
    await refreshMe();
  }

  async function handleLogout() {
    await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
    setUser(null);
    setSessions([]);
    setDirectory([]);
    setActiveConvId(null);
    setUnreadTotal(0);
    setNotice('Signed out.');
  }

  async function handleOpenDm(targetUserId: string) {
    setError('');
    const res = await fetch('/api/conversations/dm', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ targetUserId })
    });
    if (res.ok) {
      const dm = await res.json();
      setActiveConvId(dm.id);
      setWorkspaceTab('chat');
    } else {
      const data = await res.json().catch(() => ({}));
      setError(data.message || 'Unable to open direct message.');
    }
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

  const handleOpenDirectoryFromChat = useCallback(() => {
    setWorkspaceTab('directory');
    void loadDirectory(dirQuery, dirDeptId);
  }, [dirDeptId, dirQuery, loadDirectory]);

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
              Real-time direct messages, team group chats, and a fast organization directory built for everyday work.
            </p>
            <ul className="nx-auth-features">
              <li>● Instant messaging with offline queue &amp; reliable sync</li>
              <li>● Private conversations protected by strict membership controls</li>
              <li>● Vietnamese-aware colleague search &amp; TOTP account security</li>
            </ul>
          </div>

          <div className="nx-auth-footer">
            {policy?.confidentialityNotice ?? 'Nexus Workplace'}
            {apiVersion ? ` · v${apiVersion}` : ''}
          </div>
        </section>

        <section className="nx-auth-panel">
          <div className="nx-auth-card">
            <div className="nx-auth-switcher" role="tablist">
              <button
                type="button"
                role="tab"
                aria-selected={authTab === 'login'}
                className={authTab === 'login' ? 'active' : ''}
                onClick={() => { setAuthTab('login'); setError(''); }}
              >
                Sign in
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={authTab === 'register'}
                className={authTab === 'register' ? 'active' : ''}
                onClick={() => { setAuthTab('register'); setError(''); }}
              >
                Create account
              </button>
            </div>

            {error && <p className="nx-error" role="alert">{error}</p>}
            {notice && <p className="nx-ok" role="status">{notice}</p>}

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
                    <span>I accept the trial terms ({policy?.termsVersion ?? 'trial-v1'}). New accounts start as Unverified.</span>
                  </label>
                </div>
                <Button type="submit" variant="primary" fullWidth>
                  Create Nexus account
                </Button>
              </form>
            )}
          </div>
        </section>
      </main>
    );
  }

  const pageTitle =
    workspaceTab === 'chat' ? 'Nexus' :
    workspaceTab === 'communities' ? 'Communities' :
    workspaceTab === 'directory' ? 'Directory' :
    workspaceTab === 'security' ? 'Profile & Security' :
    'HR CSV Import';

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
            onClick={() => setWorkspaceTab('chat')}
            aria-label="Chat"
          >
            <MessageSquare size={20} aria-hidden="true" />
            <span>Chat</span>
            {unreadTotal > 0 && (
              <span className="nx-rail-badge" aria-label={`${unreadTotal} unread messages`}>
                {unreadTotal > 99 ? '99+' : unreadTotal}
              </span>
            )}
          </button>

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'communities' ? 'active' : ''}`}
            onClick={() => setWorkspaceTab('communities')}
            aria-label="Communities"
          >
            <LayoutGrid size={20} aria-hidden="true" />
            <span>Teams</span>
          </button>

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'directory' ? 'active' : ''}`}
            onClick={() => {
              setWorkspaceTab('directory');
              void loadDirectory(dirQuery, dirDeptId);
            }}
            aria-label="Directory"
          >
            <Users size={20} aria-hidden="true" />
            <span>People</span>
          </button>

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'security' ? 'active' : ''}`}
            onClick={() => setWorkspaceTab('security')}
            aria-label="Profile and Security"
          >
            <Shield size={20} aria-hidden="true" />
            <span>Security</span>
          </button>

          {user.orgRole === 'HRAdmin' && (
            <button
              type="button"
              className={`nx-rail-btn ${workspaceTab === 'hr' ? 'active' : ''}`}
              onClick={() => setWorkspaceTab('hr')}
              aria-label="HR Import"
            >
              <Briefcase size={20} aria-hidden="true" />
              <span>HR</span>
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
            aria-label="Open profile"
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
            <h1 className="nx-topbar-title">{pageTitle}</h1>
            {policy?.confidentialityNotice && !trialDismissed && (
              <div className="nx-trial-pill" role="note" title={policy.confidentialityNotice}>
                <span>{policy.confidentialityNotice}</span>
                <button type="button" onClick={() => setTrialDismissed(true)} aria-label="Dismiss notice">×</button>
              </div>
            )}
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
            <UserNameWithBadge user={user} />
          </div>
        </header>

        {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        {notice && <ToastBar variant="ok" message={notice} onDismiss={() => setNotice('')} />}

        <main className="nx-main-area">
          <div className={`nx-tab-keepalive ${workspaceTab === 'chat' ? '' : 'hidden'}`}>
            <ChatWorkspace
              currentUserId={user.id}
              currentDisplayName={user.displayName}
              currentIsUnverified={user.isUnverified}
              initialVersion={apiVersion}
              selectedConversationId={activeConvId}
              directoryUsers={directory}
              onSelectConversation={setActiveConvId}
              onOpenDirectory={handleOpenDirectoryFromChat}
              onConnectionStateChange={setConnState}
              onUnreadTotalChange={setUnreadTotal}
              onVersionStale={handleVersionStale}
            />
          </div>

          {workspaceTab === 'communities' && (
            <CommunitiesWorkspace
              currentUserId={user.id}
              currentOrgRole={user.orgRole}
              currentIsUnverified={user.isUnverified}
              directoryUsers={directory}
              departments={departments}
            />
          )}

          {workspaceTab === 'directory' && (
            <div className="nx-page-scroll">
              <div className="nx-page-container">
                <div className="nx-page-head">
                  <div>
                    <h2>People Directory</h2>
                    <p>Search colleagues by display name (Vietnamese diacritic-insensitive) or username.</p>
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
                      aria-label="Search colleagues"
                      placeholder="Search name or @username…"
                      value={dirQuery}
                      onChange={e => setDirQuery(e.target.value)}
                    />
                  </div>
                  <select
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
                  </select>
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
                            <UserNameWithBadge user={p} />
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
                    <h2>Profile &amp; Security</h2>
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
                  <Card title="Profile & Status">
                    <div className="nx-profile-hero">
                      <Avatar displayName={user.displayName} avatarUrl={user.avatarUrl} cacheKey={avatarBust} size="lg" />
                      <div>
                        <UserNameWithBadge user={user} />
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
                  </Card>

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
                    <p>Preview diffs with dry run, upsert employee records, and review Unverified account match suggestions.</p>
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
                          <strong>Suggested Unverified matches ({hrReport.matchSuggestions.length})</strong>
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
