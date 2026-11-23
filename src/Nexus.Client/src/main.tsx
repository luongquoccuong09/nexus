import { ChangeEvent, FormEvent, useCallback, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { ChatWorkspace } from './chat';
import {
  IconBriefcase,
  IconChat,
  IconLogOut,
  IconMoon,
  IconSearch,
  IconShield,
  IconSun,
  IconUsers
} from './icons';
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

function getInitials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

function UserNameWithBadge({ user }: { user: { displayName: string; isUnverified: boolean } }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
      <strong>{user.displayName}</strong>
      {user.isUnverified ? (
        <span className="nx-badge unverified" aria-label="Unverified account">Unverified</span>
      ) : (
        <span className="nx-badge verified" aria-label="Verified account">Verified</span>
      )}
    </span>
  );
}

function AvatarThumb({
  displayName,
  avatarUrl,
  cacheKey,
  size = 'md'
}: {
  displayName: string;
  avatarUrl?: string | null;
  cacheKey?: number;
  size?: 'sm' | 'md' | 'lg';
}) {
  const cls = size === 'sm' ? 'nx-avatar sm' : size === 'lg' ? 'nx-avatar lg' : 'nx-avatar';
  if (avatarUrl) {
    const src = cacheKey ? `${avatarUrl}?v=${cacheKey}` : avatarUrl;
    return <img className={cls} src={src} alt={`${displayName} avatar`} />;
  }
  return <span className={cls} aria-hidden="true">{getInitials(displayName)}</span>;
}

function App() {
  const [apiVersion, setApiVersion] = useState<string | null>(null);
  const [versionStale, setVersionStale] = useState(false);
  const [policy, setPolicy] = useState<RegistrationPolicy | null>(null);
  const [trialDismissed, setTrialDismissed] = useState(false);
  const [user, setUser] = useState<UserSummary | null>(null);
  const [authLoading, setAuthLoading] = useState(true);
  const [authTab, setAuthTab] = useState<'login' | 'register'>('login');
  const [workspaceTab, setWorkspaceTab] = useState<'chat' | 'directory' | 'security' | 'hr'>('chat');
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
            <strong style={{ fontSize: 20, letterSpacing: '-0.01em' }}>Nexus</strong>
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

          <div style={{ fontSize: 12.5, opacity: 0.72 }}>
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
                <div className="nx-field">
                  <label htmlFor="login-username">Username</label>
                  <input id="login-username" type="text" autoComplete="username" required value={loginUsername} onChange={e => setLoginUsername(e.target.value)} />
                </div>
                <div className="nx-field">
                  <label htmlFor="login-password">Password</label>
                  <input id="login-password" type="password" autoComplete="current-password" required value={loginPassword} onChange={e => setLoginPassword(e.target.value)} />
                </div>
                {mfaChallenge && (
                  <div className="nx-field">
                    <label htmlFor="login-totp">Authenticator code</label>
                    <input id="login-totp" type="text" inputMode="numeric" required value={loginTotp} onChange={e => setLoginTotp(e.target.value)} />
                  </div>
                )}
                <button className="nx-btn primary" type="submit" style={{ width: '100%', padding: '10px 14px', marginTop: 4 }}>
                  Sign in to Nexus
                </button>
              </form>
            ) : (
              <form onSubmit={handleRegister}>
                <div className="nx-hp" aria-hidden="true">
                  <label htmlFor="reg-website">Website</label>
                  <input id="reg-website" tabIndex={-1} autoComplete="off" value={regHoneypot} onChange={e => setRegHoneypot(e.target.value)} />
                </div>
                <div className="nx-field">
                  <label htmlFor="reg-username">Username (3–32 characters)</label>
                  <input id="reg-username" type="text" autoComplete="username" required minLength={3} maxLength={32} value={regUsername} onChange={e => setRegUsername(e.target.value)} />
                </div>
                <div className="nx-field">
                  <label htmlFor="reg-display-name">Display name</label>
                  <input id="reg-display-name" type="text" autoComplete="name" required maxLength={80} value={regDisplayName} onChange={e => setRegDisplayName(e.target.value)} />
                </div>
                <div className="nx-field">
                  <label htmlFor="reg-password">Password (at least 12 characters)</label>
                  <input id="reg-password" type="password" autoComplete="new-password" required minLength={12} value={regPassword} onChange={e => setRegPassword(e.target.value)} />
                </div>
                {policy?.mode === 'OpenWithJoinCode' && (
                  <div className="nx-field">
                    <label htmlFor="reg-join-code">Join code</label>
                    <input id="reg-join-code" type="text" required value={regJoinCode} onChange={e => setRegJoinCode(e.target.value)} />
                  </div>
                )}
                <div className="nx-field">
                  <label style={{ display: 'flex', gap: 8, fontWeight: 400, alignItems: 'flex-start', cursor: 'pointer' }}>
                    <input type="checkbox" checked={regTerms} onChange={e => setRegTerms(e.target.checked)} required style={{ marginTop: 3 }} />
                    <span>I accept the trial terms ({policy?.termsVersion ?? 'trial-v1'}). New accounts start as Unverified.</span>
                  </label>
                </div>
                <button className="nx-btn primary" type="submit" style={{ width: '100%', padding: '10px 14px' }}>
                  Create Nexus account
                </button>
              </form>
            )}
          </div>
        </section>
      </main>
    );
  }

  const pageTitle =
    workspaceTab === 'chat' ? 'Nexus' :
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
            <IconChat />
            <span>Chat</span>
            {unreadTotal > 0 && (
              <span className="nx-rail-badge" aria-label={`${unreadTotal} unread messages`}>
                {unreadTotal > 99 ? '99+' : unreadTotal}
              </span>
            )}
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
            <IconUsers />
            <span>People</span>
          </button>

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'security' ? 'active' : ''}`}
            onClick={() => setWorkspaceTab('security')}
            aria-label="Profile and Security"
          >
            <IconShield />
            <span>Security</span>
          </button>

          {user.orgRole === 'HRAdmin' && (
            <button
              type="button"
              className={`nx-rail-btn ${workspaceTab === 'hr' ? 'active' : ''}`}
              onClick={() => setWorkspaceTab('hr')}
              aria-label="HR Import"
            >
              <IconBriefcase />
              <span>HR</span>
            </button>
          )}
        </div>

        <div className="nx-rail-footer">
          <button
            type="button"
            className="nx-rail-btn"
            style={{ minHeight: 38 }}
            title={darkMode ? 'Switch to light theme' : 'Switch to dark theme'}
            aria-label={darkMode ? 'Switch to light theme' : 'Switch to dark theme'}
            onClick={() => setDarkMode(v => !v)}
          >
            {darkMode ? <IconSun /> : <IconMoon />}
          </button>

          <button
            type="button"
            className="nx-rail-avatar-btn"
            title={`${user.displayName} (@${user.username})`}
            aria-label="Open profile"
            onClick={() => setWorkspaceTab('security')}
          >
            <AvatarThumb displayName={user.displayName} avatarUrl={user.avatarUrl} cacheKey={avatarBust} />
            <span className="nx-avatar-dot nx-dot online" style={{ borderColor: 'var(--brand-950)' }} />
          </button>

          <button
            type="button"
            className="nx-rail-btn"
            style={{ minHeight: 38 }}
            title="Sign out"
            aria-label="Sign out"
            onClick={handleLogout}
          >
            <IconLogOut />
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
              <button type="button" className="nx-btn primary sm" onClick={() => window.location.reload()}>
                Update available · Reload
              </button>
            )}
            <span className="nx-conn-pill" role="status" title={apiVersion ? `API v${apiVersion}` : undefined}>
              <span className={`nx-dot ${connState}`} />
              <span>{connState === 'connected' ? 'Connected' : connState === 'reconnecting' ? 'Reconnecting…' : 'Offline'}</span>
            </span>
            <UserNameWithBadge user={user} />
          </div>
        </header>

        {error && (
          <div className="nx-toast-bar error" role="alert">
            <span>{error}</span>
            <button type="button" onClick={() => setError('')} aria-label="Dismiss error">×</button>
          </div>
        )}
        {notice && (
          <div className="nx-toast-bar ok" role="status">
            <span>{notice}</span>
            <button type="button" onClick={() => setNotice('')} aria-label="Dismiss notice">×</button>
          </div>
        )}

        <main className="nx-main-area">
          {/* Keep ChatWorkspace mounted so SignalR stays connected while browsing People/Security */}
          <div style={{ display: workspaceTab === 'chat' ? 'flex' : 'none', flex: 1, minWidth: 0, minHeight: 0 }}>
            <ChatWorkspace
              currentUserId={user.id}
              currentDisplayName={user.displayName}
              currentIsUnverified={user.isUnverified}
              initialVersion={apiVersion}
              selectedConversationId={activeConvId}
              directoryUsers={directory}
              onSelectConversation={setActiveConvId}
              onOpenDirectory={() => {
                setWorkspaceTab('directory');
                void loadDirectory(dirQuery, dirDeptId);
              }}
              onConnectionStateChange={setConnState}
              onUnreadTotalChange={setUnreadTotal}
              onVersionStale={() => setVersionStale(true)}
            />
          </div>

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
                    <IconSearch />
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
                    aria-label="Filter by department"
                    value={dirDeptId}
                    onChange={e => {
                      setDirDeptId(e.target.value);
                      void loadDirectory(dirQuery, e.target.value);
                    }}
                    style={{
                      flex: 1,
                      minWidth: 180,
                      padding: '7px 10px',
                      borderRadius: 8,
                      border: '1px solid var(--border)',
                      background: 'var(--bg-surface)',
                      color: 'var(--text-primary)'
                    }}
                  >
                    <option value="">All departments</option>
                    {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
                  </select>
                  <button className="nx-btn primary" type="submit">Search</button>
                </form>

                {directory.length === 0 ? (
                  <div className="nx-card" style={{ textAlign: 'center', padding: 36 }}>
                    <p className="nx-muted" style={{ margin: 0 }}>No matching active accounts.</p>
                  </div>
                ) : (
                  <div className="nx-dir-grid">
                    {directory.map(p => (
                      <article className="nx-person-card" key={p.id}>
                        <div className="nx-person-top">
                          <AvatarThumb displayName={p.displayName} avatarUrl={p.avatarUrl} cacheKey={avatarBust} size="lg" />
                          <div style={{ minWidth: 0, flex: 1 }}>
                            <UserNameWithBadge user={p} />
                            <div className="nx-muted" style={{ marginTop: 2 }}>
                              @{p.username}
                              {p.departmentName ? ` · ${p.departmentName}` : ''}
                              {p.branchName ? ` · ${p.branchName}` : ''}
                            </div>
                            {p.customStatus && (
                              <div className="nx-muted" style={{ marginTop: 6, color: 'var(--text-secondary)' }}>
                                “{p.customStatus}”
                              </div>
                            )}
                          </div>
                        </div>

                        <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                          {p.id === user.id ? (
                            <span className="nx-badge role">You</span>
                          ) : (
                            <button
                              type="button"
                              className="nx-btn primary sm"
                              onClick={() => void handleOpenDm(p.id)}
                            >
                              <IconChat size={14} /> Message
                            </button>
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
                  <button className="nx-btn danger" type="button" onClick={handleLogout}>
                    <IconLogOut size={15} /> Sign out
                  </button>
                </div>

                {user.mfaSetupRequired && (
                  <div className="nx-toast-bar error" style={{ borderRadius: 10, marginBottom: 16 }}>
                    <span>Administrative roles require TOTP multi-factor authentication. Set up MFA below.</span>
                  </div>
                )}

                <div className="nx-settings-grid">
                  <section className="nx-card">
                    <h3>Profile &amp; Status</h3>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 18 }}>
                      <AvatarThumb displayName={user.displayName} avatarUrl={user.avatarUrl} cacheKey={avatarBust} size="lg" />
                      <div>
                        <div><UserNameWithBadge user={user} /></div>
                        <div className="nx-muted" style={{ marginTop: 3 }}>
                          @{user.username} · <span className="nx-badge role">{user.orgRole}</span>
                        </div>
                      </div>
                    </div>

                    <form onSubmit={handleUpdateCustomStatus} style={{ display: 'flex', gap: 8, marginBottom: 16 }}>
                      <div className="nx-field" style={{ flex: 1, marginBottom: 0 }}>
                        <label htmlFor="custom-status">Custom status</label>
                        <input
                          id="custom-status"
                          type="text"
                          maxLength={100}
                          placeholder="e.g. On site until 15:00"
                          value={customStatusInput}
                          onChange={e => setCustomStatusInput(e.target.value)}
                        />
                      </div>
                      <button className="nx-btn primary" type="submit" style={{ alignSelf: 'flex-end' }}>Save</button>
                    </form>

                    <div className="nx-field" style={{ marginBottom: 0 }}>
                      <label htmlFor="avatar-upload">Avatar image (re-encoded to 256×256 PNG, max 2 MB)</label>
                      <input id="avatar-upload" type="file" accept="image/png,image/jpeg,image/webp,image/gif" onChange={handleAvatarChange} />
                    </div>
                  </section>

                  <section className="nx-card">
                    <h3>Change password</h3>
                    <form onSubmit={handleChangePassword}>
                      <div className="nx-field">
                        <label htmlFor="cur-pw">Current password</label>
                        <input id="cur-pw" type="password" autoComplete="current-password" required value={currentPassword} onChange={e => setCurrentPassword(e.target.value)} />
                      </div>
                      <div className="nx-field">
                        <label htmlFor="new-pw">New password (at least 12 characters)</label>
                        <input id="new-pw" type="password" autoComplete="new-password" required minLength={12} value={newPassword} onChange={e => setNewPassword(e.target.value)} />
                      </div>
                      <button className="nx-btn primary" type="submit">Update password</button>
                    </form>
                  </section>

                  <section className="nx-card">
                    <h3>Authenticator MFA (TOTP)</h3>
                    {user.mfaEnabled ? (
                      <p className="nx-ok">TOTP multi-factor authentication is active on your account.</p>
                    ) : mfaSecret ? (
                      <form onSubmit={handleEnableMfa}>
                        <p className="nx-muted">Add this shared key to your authenticator app:</p>
                        <p><code style={{ fontSize: 13 }}>{mfaSecret}</code></p>
                        <div className="nx-field">
                          <label htmlFor="mfa-code">6-digit code</label>
                          <input id="mfa-code" type="text" inputMode="numeric" required value={mfaCode} onChange={e => setMfaCode(e.target.value)} />
                        </div>
                        <button className="nx-btn primary" type="submit">Verify &amp; enable MFA</button>
                      </form>
                    ) : (
                      <>
                        <p className="nx-muted" style={{ marginBottom: 14 }}>Protect your Nexus account with a 6-digit authenticator code.</p>
                        <button className="nx-btn" type="button" onClick={handleStartMfa}>Set up authenticator app</button>
                      </>
                    )}
                    {recoveryCodes.length > 0 && (
                      <div style={{ marginTop: 14 }}>
                        <p className="nx-muted">Save these one-time recovery codes:</p>
                        <code>{recoveryCodes.join('  ')}</code>
                      </div>
                    )}
                  </section>

                  <section className="nx-card">
                    <h3>Signed-in devices ({sessions.length})</h3>
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
                          <button className="nx-btn danger sm" type="button" onClick={() => void handleRevokeSession(s.id)}>
                            Sign out
                          </button>
                        )}
                      </div>
                    ))}
                  </section>
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
                <section className="nx-card">
                  <div className="nx-field">
                    <label htmlFor="hr-csv">CSV records</label>
                    <textarea id="hr-csv" rows={7} value={hrCsv} onChange={e => setHrCsv(e.target.value)} />
                  </div>
                  <div style={{ display: 'flex', gap: 8, marginBottom: 14 }}>
                    <button className="nx-btn" type="button" onClick={() => void handleHrImport(true)}>Dry run (diff)</button>
                    <button className="nx-btn primary" type="button" onClick={() => void handleHrImport(false)}>Apply import</button>
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
                              <span className="nx-badge role">{m.confidence}</span>
                            </div>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </section>
              </div>
            </div>
          )}
        </main>
      </div>
    </div>
  );
}

createRoot(document.getElementById('root')!).render(<App />);
