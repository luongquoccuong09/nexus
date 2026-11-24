import { useEffect, useState } from 'react';
import { Bell } from 'lucide-react';
import { protectedRequest } from './scope';
import { Button, Field, Modal } from './ui';

type ConversationMode = 'all' | 'mentions' | 'muted';

export function ConversationNotificationButton({ conversationId }: { conversationId: string }) {
  const [open, setOpen] = useState(false);
  const [mode, setMode] = useState<ConversationMode | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const path = `/api/notifications/conversations/${conversationId}/preference`;

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setMode(null);
    setError('');
    void fetch(path, { signal: controller.signal }).then(async response => {
      if (!response.ok) throw new Error();
      const result: { mode: ConversationMode } = await response.json();
      if (!controller.signal.aborted) setMode(result.mode);
    }).catch(() => {
      if (!controller.signal.aborted) setError('Unable to load notification preferences.');
    });
    return () => controller.abort();
  }, [open, path]);

  async function save() {
    if (!mode) return;
    setSaving(true);
    setError('');
    try {
      const response = await protectedRequest(path, 'PUT', { mode });
      if (!response.ok) throw new Error();
      setOpen(false);
    } catch {
      setError('Unable to save notification preferences. Try again.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <>
      <Button size="sm" iconOnly title="Conversation notifications" aria-label="Conversation notifications" onClick={() => setOpen(true)}>
        <Bell size={16} aria-hidden="true" />
      </Button>
      {open && (
        <Modal open={open} onOpenChange={value => !saving && setOpen(value)} title="Conversation notifications"
          description="Choose which activity can notify you in this conversation."
          footer={<Button variant="primary" onClick={() => void save()} disabled={mode === null || saving}>{saving ? 'Saving…' : 'Save'}</Button>}>
          {error && <p role="alert" className="nx-error">{error}</p>}
          {mode === null ? (!error && <p role="status">Loading…</p>) : (
            <Field label="Notify me about" htmlFor="conversation-notification-mode">
              <select id="conversation-notification-mode" className="nx-select" value={mode} disabled={saving}
                onChange={event => setMode(event.currentTarget.value as ConversationMode)}>
                <option value="all">All activity</option>
                <option value="mentions">Mentions only</option>
                <option value="muted">Muted</option>
              </select>
            </Field>
          )}
        </Modal>
      )}
    </>
  );
}

interface QuietHoursForm {
  enabled: boolean;
  start: string;
  end: string;
  timeZoneId: string;
}

export function QuietHoursButton() {
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState<QuietHoursForm | null>(null);
  const [quietNow, setQuietNow] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const path = '/api/notifications/quiet-hours';

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setError('');
    setForm(null);
    void fetch(path, { signal: controller.signal }).then(async response => {
      if (!response.ok) throw new Error();
      const saved: Partial<QuietHoursForm> & { isQuietNow: boolean } = await response.json();
      if (!controller.signal.aborted) {
        setForm({
          enabled: saved.enabled ?? false,
          start: saved.start ?? '22:00',
          end: saved.end ?? '07:00',
          timeZoneId: saved.timeZoneId ?? Intl.DateTimeFormat().resolvedOptions().timeZone ?? 'UTC'
        });
        setQuietNow(saved.isQuietNow);
      }
    }).catch(() => {
      if (!controller.signal.aborted) setError('Unable to load quiet hours.');
    });
    return () => controller.abort();
  }, [open]);

  async function save() {
    if (!form) return;
    setSaving(true);
    setError('');
    try {
      const response = await protectedRequest(path, 'PUT', form);
      if (!response.ok) {
        const result = await response.json().catch(() => null);
        throw new Error(result?.message ?? 'Unable to save quiet hours. Try again.');
      }
      setOpen(false);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Unable to save quiet hours.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)}>Quiet hours</Button>
      {open && (
        <Modal open={open} onOpenChange={value => !saving && setOpen(value)} title="Quiet hours"
          description="Schedule when push and email notifications pause each day."
          footer={<Button variant="primary" onClick={() => void save()} disabled={!form || saving}>{saving ? 'Saving…' : 'Save'}</Button>}>
          {error && <p role="alert" className="nx-error">{error}</p>}
          {form ? (
            <>
              <label><input type="checkbox" checked={form.enabled} disabled={saving} onChange={event => setForm({ ...form, enabled: event.currentTarget.checked })} /> Enable quiet hours</label>
              {quietNow && form.enabled && <p className="nx-muted" role="status">Quiet hours are active.</p>}
              {form.enabled && (
                <>
                  <Field label="Start time" htmlFor="quiet-hours-start">
                    <input id="quiet-hours-start" type="time" value={form.start} disabled={saving} onChange={event => setForm({ ...form, start: event.currentTarget.value })} />
                  </Field>
                  <Field label="End time" htmlFor="quiet-hours-end">
                    <input id="quiet-hours-end" type="time" value={form.end} disabled={saving} onChange={event => setForm({ ...form, end: event.currentTarget.value })} />
                  </Field>
                  <Field label="Time zone" htmlFor="quiet-hours-zone">
                    <input id="quiet-hours-zone" type="text" placeholder="Asia/Bangkok" value={form.timeZoneId} disabled={saving}
                      onChange={event => setForm({ ...form, timeZoneId: event.currentTarget.value })} />
                  </Field>
                </>
              )}
            </>
          ) : (!error && <p role="status">Loading…</p>)}
        </Modal>
      )}
    </>
  );
}

interface EmailDigestState {
  configured: boolean;
  eligible: boolean;
  enabled: boolean;
  delayHours: number;
}

export function EmailDigestButton() {
  const [open, setOpen] = useState(false);
  const [state, setState] = useState<EmailDigestState | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const path = '/api/notifications/email-digest';

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setError('');
    setState(null);
    void fetch(path, { signal: controller.signal }).then(async response => {
      if (!response.ok) throw new Error();
      const data: EmailDigestState = await response.json();
      if (!controller.signal.aborted) {
        setState(data);
        setEnabled(data.enabled);
      }
    }).catch(() => {
      if (!controller.signal.aborted) setError('Unable to load email digest settings.');
    });
    return () => controller.abort();
  }, [open]);

  async function save() {
    if (!state) return;
    setSaving(true);
    setError('');
    try {
      const response = await protectedRequest(path, 'PUT', { enabled });
      if (!response.ok) throw new Error();
      setOpen(false);
    } catch {
      setError('Unable to save email digest settings.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)}>Email digest</Button>
      {open && (
        <Modal open={open} onOpenChange={value => !saving && setOpen(value)} title="Email digest"
          description="Send a count of unread activity to your verified email when push is unavailable."
          footer={state && (state.enabled || (state.configured && state.eligible)) && (
            <Button variant="primary" onClick={() => void save()} disabled={saving || (enabled && (!state.configured || !state.eligible))}>
              {saving ? 'Saving…' : 'Save'}
            </Button>
          )}>
          {error && <p role="alert" className="nx-error">{error}</p>}
          {state ? (
            <>
              {!state.configured && <p className="nx-muted">Email digest is unavailable on this server.</p>}
              {state.configured && !state.eligible && <p className="nx-muted">A verified email address is required.</p>}
              {(state.enabled || (state.configured && state.eligible)) && (
                <label>
                  <input type="checkbox" checked={enabled} disabled={saving || (!enabled && (!state.configured || !state.eligible))}
                    onChange={event => setEnabled(event.currentTarget.checked)} />
                  {' '}Email me after {state.delayHours} {state.delayHours === 1 ? 'hour' : 'hours'} of unread activity
                </label>
              )}
            </>
          ) : (!error && <p role="status">Loading…</p>)}
        </Modal>
      )}
    </>
  );
}
