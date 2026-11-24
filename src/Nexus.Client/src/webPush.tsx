import { useEffect, useState } from 'react';
import { protectedRequest } from './scope';
import { Button } from './ui';

interface PushSettings {
  publicKey: string | null;
  subscribed: boolean;
}

export function WebPushSettings() {
  const [registration, setRegistration] = useState<ServiceWorkerRegistration | null>(null);
  const [settings, setSettings] = useState<PushSettings | null>(null);
  const [subscription, setSubscription] = useState<PushSubscription | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const supported = window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window;

  useEffect(() => {
    if (!supported) return;
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch('/api/notifications/push', { signal: controller.signal });
        if (!response.ok) throw new Error();
        const data: PushSettings = await response.json();
        if (controller.signal.aborted) return;
        setSettings(data);
        if (!data.publicKey && !data.subscribed) return;

        await navigator.serviceWorker.register('/nexus-sw.js');
        const ready = await navigator.serviceWorker.ready;
        const active = await ready.pushManager.getSubscription();
        if (!controller.signal.aborted) {
          setRegistration(ready);
          setSubscription(active);
        }
      } catch {
        if (!controller.signal.aborted) setError('Unable to load push notification settings. Refresh to try again.');
      }
    })();
    return () => controller.abort();
  }, [supported]);

  async function enable() {
    if (!registration || !settings?.publicKey) return;
    setBusy(true);
    setError('');
    try {
      const active = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: settings.publicKey });
      setSubscription(active);
      const response = await protectedRequest('/api/notifications/push', 'PUT', { endpoint: active.endpoint });
      if (!response.ok) throw new Error('Unable to register this browser for push.');
      setSettings({ ...settings, subscribed: true });
    } catch (failure) {
      if (failure instanceof DOMException && failure.name === 'NotAllowedError') {
        setError('Notifications are blocked. You can allow them in your browser settings.');
      } else {
        setError('Unable to enable push. If this browser was registered before, disable that subscription and try again.');
      }
    } finally {
      setBusy(false);
    }
  }

  async function disable() {
    if (!settings) return;
    setBusy(true);
    setError('');
    try {
      const response = await protectedRequest('/api/notifications/push', 'DELETE');
      if (!response.ok) throw new Error();
      setSettings({ ...settings, subscribed: false });
      await subscription?.unsubscribe();
      setSubscription(null);
    } catch {
      setError('Unable to finish disabling push on this browser. Try again.');
    } finally {
      setBusy(false);
    }
  }

  if (!supported) return <p className="nx-muted">Push is unavailable in this browser. On iPhone or iPad, install Nexus from Safari using Add to Home Screen.</p>;
  if (settings && !settings.publicKey && !settings.subscribed) return <p className="nx-muted">Web push is unavailable on this server.</p>;

  const active = Boolean(settings?.subscribed && subscription);
  return (
    <>
      {error && <p className="nx-error" role="alert">{error}</p>}
      <div className="nx-inline-actions">
        {!active && <Button size="sm" disabled={!registration || busy || !settings?.publicKey} onClick={() => void enable()}>Enable push</Button>}
        {(settings?.subscribed || subscription) && <Button size="sm" disabled={busy} onClick={() => void disable()}>Disable push</Button>}
        {active && <span role="status" className="nx-muted">Push is enabled on this browser.</span>}
      </div>
    </>
  );
}
