import { useRef, useState } from 'react';
import { Button, Modal, ToastBar } from './ui';

export interface OwnershipTransfer {
  path: string;
  resourceLabel: 'Group chat' | 'Community';
  resourceName: string;
  userId: string;
  displayName: string;
  username: string;
}

export function TransferOwnershipDialog({
  target,
  onClose,
  onRefresh,
  onTransferred
}: {
  target: OwnershipTransfer;
  onClose: () => void;
  onRefresh: () => void;
  onTransferred: () => void;
}) {
  const submitting = useRef(false);
  const [busy, setBusy] = useState(false);
  const [stale, setStale] = useState(false);
  const [error, setError] = useState('');

  async function transfer() {
    if (submitting.current || stale) return;
    submitting.current = true;
    setBusy(true);
    setError('');
    try {
      const response = await fetch(target.path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ newOwnerUserId: target.userId })
      });
      if (response.ok) {
        onTransferred();
        return;
      }
      if ([401, 403, 404, 409].includes(response.status)) {
        setStale(true);
        setError('The owner or member may have changed. Close this dialog and check the current member list.');
        onRefresh();
        return;
      }
      setError('Unable to transfer ownership. Try again.');
    } catch {
      setError('Unable to confirm the transfer. Check the current member list and try again.');
      onRefresh();
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  }

  return (
    <Modal open onOpenChange={open => { if (!open && !submitting.current) onClose(); }}
      title="Transfer ownership"
      description="The selected member will become Owner and your role will change to Admin."
      footer={(
        <>
          <Button disabled={busy} onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={busy || stale} onClick={() => void transfer()}>
            {busy ? 'Transferring…' : 'Transfer ownership'}
          </Button>
        </>
      )}
    >
      <dl className="nx-transfer-details">
        <div><dt>{target.resourceLabel}</dt><dd>{target.resourceName}</dd></div>
        <div><dt>New Owner</dt><dd>{target.displayName} (@{target.username})</dd></div>
      </dl>
      {error && <ToastBar variant="error" message={error} />}
    </Modal>
  );
}
