import { createContext, lazy, type ReactNode, Suspense, useCallback, useContext, useMemo, useState } from 'react';
import { Modal } from '../ui';
import type { MeetingIntent } from './meetingApi';

const MeetingRoom = lazy(() => import('./MeetingRoom'));

interface MeetingContextValue {
  open: (intent: MeetingIntent) => void;
  active: boolean;
}

const MeetingContext = createContext<MeetingContextValue | null>(null);

export function useMeeting() {
  const controller = useContext(MeetingContext);
  if (!controller) throw new Error('Meeting controls must be inside MeetingProvider.');
  return controller;
}

export default function MeetingProvider({ children }: { children: ReactNode }) {
  const [intent, setIntent] = useState<MeetingIntent | null>(null);
  const open = useCallback((next: MeetingIntent) => setIntent(current => current ?? next), []);
  const close = useCallback(() => setIntent(null), []);
  const active = intent !== null;
  const value = useMemo(() => ({ open, active }), [open, active]);

  return (
    <MeetingContext.Provider value={value}>
      {children}
      {intent && (
        <Suspense fallback={
          <Modal open title={intent.title} description="Meeting" onOpenChange={next => { if (!next) close(); }}>
            <p role="status">Loading meeting…</p>
          </Modal>
        }>
          <MeetingRoom intent={intent} onClose={close} />
        </Suspense>
      )}
    </MeetingContext.Provider>
  );
}
