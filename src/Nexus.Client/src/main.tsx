import { FormEvent, useCallback, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
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

function UserNameWithBadge({ user }: { user: Pick<UserSummary, 'displayName' | 'isUnverified' | 'trustLevel'> }) {
  return (
    <span>
      <strong>{user.displayName}</strong>
      {user.isUnverified ? (
        <span className="nx-badge unverified" aria-label="Unverified account">Unverified</span>
      ) : (
        <span className="nx-badge verified" aria-label="Verified account">Verified</span>
      )}
    </span>
  );
}

function App() {
  const [apiStatus, setApiStatus] = useState('Connecting…');
  const [policy, setPolicy] = useState<RegistrationPolicy | null>(null);
  const [user, setUser] = useState<UserSummary | null>(null);
  const [authTab, setAuthTab] = useState<'login' | 'register'>('login');
  const [sessions, setSessions] = useState<SessionItem[]>([]);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  // Login state
  const [loginUsername, setLoginUsername] = useState('');
  const [loginPassword, setLoginPassword] = useState('');
  const [loginTotp, setLoginTotp] = useState('');
  const [mfaChallenge, setMfaChallenge] = useState(false);

  // Register state
  const [regUsername, setRegUsername] = useState('');
  const [regDisplayName, setRegDisplayName] = useState('');
  const [regPassword, setRegPassword] = useState('');
  const [regJoinCode, setRegJoinCode] = useState('');
  const [regTerms, setRegTerms] = useState(false);
  const [regHoneypot, setRegHoneypot] = useState('');

  // Password change & MFA setup state
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [mfaSecret, setMfaSecret] = useState<string | null>(null);
  const [mfaCode, setMfaCode] = useState('');
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);

  const loadSessions = useCallback(async () => {
    const res = await fetch('/api/auth/sessions', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setSessions(data.items ?? []);
    }
  }, []);

  const refreshMe = useCallback(async () => {
    const res = await fetch('/api/auth/me', { credentials: 'same-origin' });
    if (res.ok) {
      const me: UserSummary = await res.json();
      setUser(me);
      await loadSessions();
    } else {
      setUser(null);
      setSessions([]);
    }
  }, [loadSessions]);

  useEffect(() => {
    const controller = new AbortController();
    async function bootstrap() {
      try {
        const verRes = await fetch('/api/version', { signal: controller.signal });
        if (!verRes.ok) throw new Error('API unavailable');
        const ver = await verRes.json();
        setApiStatus(`Connected · API v${ver.version}`);

        const polRes = await fetch('/api/auth/registration-policy', { signal: controller.signal });
        if (polRes.ok) setPolicy(await polRes.json());

        await refreshMe();
      } catch {
        if (!controller.signal.aborted) setApiStatus('Unable to connect to the API.');
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
    setUser(data);
    await loadSessions();
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
    setUser(data);
    setNotice('Account created as Unverified.');
    await loadSessions();
  }

  async function handleLogout() {
    await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
    setUser(null);
    setSessions([]);
    setNotice('Signed out.');
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
    setNotice('Password updated. Other sessions have been signed out.');
    await loadSessions();
  }

  async function handleRevokeSession(sessionId: string) {
    const res = await fetch(`/api/auth/sessions/${encodeURIComponent(sessionId)}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (res.ok) {
      await loadSessions();
    }
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

  return (
    <main className="nx-shell">
      <header className="nx-header">
        <div>
          <h1 style={{ margin: 0 }}>Project Nexus</h1>
          <p className="nx-muted" role="status" style={{ margin: 0 }}>{apiStatus}</p>
        </div>
        {user && (
          <button className="nx-btn" type="button" onClick={handleLogout}>Sign out</button>
        )}
      </header>

      {policy?.confidentialityNotice && (
        <div className="nx-banner" role="note">{policy.confidentialityNotice}</div>
      )}

      {error && <p className="nx-error" role="alert">{error}</p>}
      {notice && <p className="nx-ok" role="status">{notice}</p>}

      {!user ? (
        <section className="nx-card">
          <div className="nx-tabs">
            <button type="button" className={`nx-btn ${authTab === 'login' ? 'primary' : ''}`} onClick={() => { setAuthTab('login'); setError(''); }}>
              Sign in
            </button>
            <button type="button" className={`nx-btn ${authTab === 'register' ? 'primary' : ''}`} onClick={() => { setAuthTab('register'); setError(''); }}>
              Create account
            </button>
          </div>

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
              <button className="nx-btn primary" type="submit">Sign in</button>
            </form>
          ) : (
            <form onSubmit={handleRegister}>
              <div className="nx-hp" aria-hidden="true">
                <label htmlFor="reg-website">Website</label>
                <input id="reg-website" tabIndex={-1} autoComplete="off" value={regHoneypot} onChange={e => setRegHoneypot(e.target.value)} />
              </div>
              <div className="nx-field">
                <label htmlFor="reg-username">Username</label>
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
                <label style={{ display: 'flex', gap: 8, fontWeight: 400 }}>
                  <input type="checkbox" checked={regTerms} onChange={e => setRegTerms(e.target.checked)} required />
                  I accept the trial terms ({policy?.termsVersion ?? 'trial-v1'}). New accounts start as Unverified.
                </label>
              </div>
              <button className="nx-btn primary" type="submit">Create account</button>
            </form>
          )}
        </section>
      ) : (
        <>
          <section className="nx-card">
            <h2 style={{ marginTop: 0 }}>Account</h2>
            <p>
              <UserNameWithBadge user={user} />
              <span className="nx-badge role">{user.orgRole}</span>
            </p>
            <p className="nx-muted">Username: <code>@{user.username}</code> · Status: {user.status}</p>
            {user.mfaSetupRequired && (
              <div className="nx-banner">Administrative roles require TOTP multi-factor authentication. Set up MFA below.</div>
            )}
          </section>

          <section className="nx-card">
            <h2 style={{ marginTop: 0 }}>Change password</h2>
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
            <h2 style={{ marginTop: 0 }}>Authenticator MFA (TOTP)</h2>
            {user.mfaEnabled ? (
              <p className="nx-ok">TOTP multi-factor authentication is active on your account.</p>
            ) : mfaSecret ? (
              <form onSubmit={handleEnableMfa}>
                <p className="nx-muted">Add this shared key to your authenticator app: <code>{mfaSecret}</code></p>
                <div className="nx-field">
                  <label htmlFor="mfa-code">6-digit code</label>
                  <input id="mfa-code" type="text" inputMode="numeric" required value={mfaCode} onChange={e => setMfaCode(e.target.value)} />
                </div>
                <button className="nx-btn primary" type="submit">Verify &amp; enable MFA</button>
              </form>
            ) : (
              <button className="nx-btn" type="button" onClick={handleStartMfa}>Set up authenticator app</button>
            )}
            {recoveryCodes.length > 0 && (
              <div style={{ marginTop: 12 }}>
                <p className="nx-muted">Save these one-time recovery codes:</p>
                <code>{recoveryCodes.join('  ')}</code>
              </div>
            )}
          </section>

          <section className="nx-card">
            <h2 style={{ marginTop: 0 }}>Signed-in devices ({sessions.length})</h2>
            {sessions.map(s => (
              <div className="nx-row" key={s.id}>
                <div>
                  <div>
                    <strong>{s.isCurrent ? 'Current session' : 'Other session'}</strong>
                    <span className="nx-muted"> · {s.ipAddress}</span>
                  </div>
                  <div className="nx-muted">{s.userAgent}</div>
                </div>
                {!s.isCurrent && (
                  <button className="nx-btn danger" type="button" onClick={() => handleRevokeSession(s.id)}>
                    Sign out
                  </button>
                )}
              </div>
            ))}
          </section>
        </>
      )}
    </main>
  );
}

createRoot(document.getElementById('root')!).render(<App />);
