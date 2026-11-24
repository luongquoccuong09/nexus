import { FormEvent, useEffect, useRef, useState } from 'react';
import { Button, Field, ToastBar } from './ui';

export function PasswordResetForm({ onBack, onReset }: { onBack: () => void; onReset: () => void }) {
  const [token, setToken] = useState('');
  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [error, setError] = useState('');
  const [sending, setSending] = useState(false);
  const submitting = useRef(false);
  const tokenInput = useRef<HTMLInputElement | null>(null);
  const confirmationInput = useRef<HTMLInputElement | null>(null);

  useEffect(() => tokenInput.current?.focus(), []);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (submitting.current) return;
    if (!token.trim()) {
      setError('Enter the reset code you received.');
      tokenInput.current?.focus();
      return;
    }
    if (password !== confirmation) {
      setError('The new passwords do not match.');
      confirmationInput.current?.focus();
      return;
    }

    submitting.current = true;
    setSending(true);
    setError('');
    try {
      const response = await fetch('/api/auth/reset-password', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: token.trim(), newPassword: password })
      });
      if (response.ok) {
        onReset();
        return;
      }
      const failure = await response.json().catch(() => ({}));
      switch (failure.code) {
        case 'invalid_reset_token':
          setError('This reset code is invalid, expired, or has already been used. Ask your administrator for a new code.');
          tokenInput.current?.focus();
          break;
        case 'password_policy_failed':
          setError(failure.message || 'Choose a password that meets the password requirements.');
          break;
        default:
          setError(response.status === 429 ? 'Too many attempts. Wait a moment and try again.' : 'Unable to reset your password. Try again.');
      }
    } catch {
      setError('Unable to reach Nexus. Check your connection and try again.');
    } finally {
      submitting.current = false;
      setSending(false);
    }
  }

  return (
    <form aria-label="Reset password" onSubmit={submit}>
      <h2>Reset password</h2>
      <p className="nx-muted">Enter the reset code provided by your administrator.</p>
      {error && <ToastBar variant="error" message={error} />}
      <Field label="Reset code" htmlFor="password-reset-code">
        <input ref={tokenInput} id="password-reset-code" type="text" autoComplete="off" autoCapitalize="none" spellCheck={false}
          required value={token} onChange={event => setToken(event.target.value)} />
      </Field>
      <Field label="New password (at least 12 characters)" htmlFor="password-reset-new">
        <input id="password-reset-new" type="password" autoComplete="new-password" minLength={12}
          required value={password} onChange={event => setPassword(event.target.value)} />
      </Field>
      <Field label="Confirm new password" htmlFor="password-reset-confirm">
        <input ref={confirmationInput} id="password-reset-confirm" type="password" autoComplete="new-password"
          required value={confirmation} onChange={event => setConfirmation(event.target.value)} />
      </Field>
      <Button type="submit" variant="primary" fullWidth disabled={sending}>
        {sending ? 'Resetting…' : 'Reset password'}
      </Button>
      <Button className="nx-auth-secondary" variant="ghost" fullWidth onClick={onBack} disabled={sending}>Back to sign in</Button>
    </form>
  );
}
