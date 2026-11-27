import { createContext, lazy, type ReactNode, Suspense, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { Button, Modal } from '../ui';
import { endDirectCall } from '../calls/callApi';
import type { MeetingIntent } from './meetingApi';
import type { MeetingDevices } from './meetingDevices';

type MeetingRoomModule = typeof import('./MeetingRoom');
let pendingRoom: Promise<MeetingRoomModule> | null = null;
let loadedRoom: MeetingRoomModule['default'] | null = null;

function loadMeetingRoom() {
  pendingRoom ??= import('./MeetingRoom').then(module => {
    loadedRoom = module.default;
    return module;
  }).catch(cause => {
    pendingRoom = null;
    throw cause;
  });
  return pendingRoom;
}
const MeetingRoom = lazy(loadMeetingRoom);
const PreJoinMeeting = lazy(() => import('./PreJoinMeeting'));

export function preloadMeetingRoom() {
  void loadMeetingRoom().catch(() => {});
}

interface MeetingContextValue {
  open: (intent: MeetingIntent) => void;
  closeCall: (callId: string) => void;
  active: boolean;
}

const MeetingContext = createContext<MeetingContextValue | null>(null);

export function useMeeting() {
  const controller = useContext(MeetingContext);
  if (!controller) throw new Error('Meeting controls must be inside MeetingProvider.');
  return controller;
}

function LoadingMeeting({ intent, onClose, failed = false }: { intent: MeetingIntent; onClose: () => void; failed?: boolean }) {
  const ending = useRef(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const isDirect = intent.kind === 'directCall';
  async function dismiss() {
    if (!isDirect) {
      onClose();
      return;
    }
    if (ending.current) return;
    ending.current = true;
    setBusy(true);
    setError('');
    try {
      const result = await endDirectCall({ id: intent.callId, organizationId: intent.organizationId }, new AbortController().signal);
      if (result) intent.onEnded(result);
      onClose();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to end the call. Try again.');
    } finally {
      ending.current = false;
      setBusy(false);
    }
  }
  return (
    <Modal open title={intent.title} description={isDirect ? 'Direct call' : 'Meeting'}
      onOpenChange={open => { if (!open) void dismiss(); }}
      footer={isDirect ? <Button variant="danger" disabled={busy} onClick={() => void dismiss()}>End call</Button> : undefined}>
      {failed ? <p className="nx-error" role="alert">Unable to load call media. Reload Nexus and try again.</p> :
        <p role="status">{isDirect ? 'Loading call…' : 'Loading meeting…'}</p>}
      {error && <p className="nx-error" role="alert">{error}</p>}
    </Modal>
  );
}

export default function MeetingProvider({ children }: { children: ReactNode }) {
  const [meeting, setMeeting] = useState<{ intent: MeetingIntent; devices?: MeetingDevices } | null>(null);
  const [readyRoom, setReadyRoom] = useState<MeetingRoomModule['default'] | null>(() => loadedRoom);
  const [failedCall, setFailedCall] = useState<string | null>(null);
  const open = useCallback((intent: MeetingIntent) => setMeeting(current => current ?? { intent }), []);
  const close = useCallback(() => setMeeting(null), []);
  const closeCall = useCallback((callId: string) =>
    setMeeting(current => current?.intent.kind === 'directCall' && current.intent.callId === callId ? null : current), []);
  const join = useCallback((devices: MeetingDevices) =>
    setMeeting(current => current && !current.devices && current.intent.kind !== 'directCall' ? { ...current, devices } : current), []);
  const intent = meeting?.intent;
  const active = meeting !== null;
  const value = useMemo(() => ({ open, closeCall, active }), [open, closeCall, active]);
  const dismiss = intent?.kind === 'directCall' ? () => closeCall(intent.callId) : close;
  const ReadyMeetingRoom = readyRoom ?? loadedRoom;

  useEffect(() => {
    if (intent?.kind !== 'directCall' || ReadyMeetingRoom) return;
    let closed = false;
    // Keep the accepted call's loading prompt independent of Suspense reveal timing.
    void loadMeetingRoom().then(module => {
      if (!closed) setReadyRoom(() => module.default);
    }).catch(() => {
      if (!closed) setFailedCall(intent.callId);
    });
    return () => { closed = true; };
  }, [intent, ReadyMeetingRoom]);

  let content: ReactNode = null;
  if (intent?.kind === 'directCall') {
    content = ReadyMeetingRoom
      ? <ReadyMeetingRoom intent={intent} onClose={dismiss} />
      : <LoadingMeeting intent={intent} onClose={dismiss} failed={failedCall === intent.callId} />;
  } else if (intent) {
    content = (
      <Suspense fallback={<LoadingMeeting intent={intent} onClose={close} />}>
        {meeting?.devices ? <MeetingRoom intent={intent} selectedDevices={meeting.devices} onClose={close} /> :
          <PreJoinMeeting title={intent.title} onJoin={join} onClose={close} />}
      </Suspense>
    );
  }

  return (
    <MeetingContext.Provider value={value}>
      {children}
      {content}
    </MeetingContext.Provider>
  );
}
