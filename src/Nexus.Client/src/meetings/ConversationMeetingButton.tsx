import { useEffect, useState } from 'react';
import { Video } from 'lucide-react';
import { Button } from '../ui';
import { currentMeeting, type MeetingSummary } from './meetingApi';
import { useMeeting } from './MeetingProvider';

export default function ConversationMeetingButton({ organizationId, conversationId, title, visible }: {
  organizationId: string | null;
  conversationId: string;
  title: string;
  visible: boolean;
}) {
  const { open, active } = useMeeting();
  const [status, setStatus] = useState<{ available: boolean; current: MeetingSummary | null } | null>(null);
  useEffect(() => {
    if (!visible || active) return;
    const controller = new AbortController();
    let querying = false;
    async function load() {
      if (document.visibilityState === 'hidden' || querying) return;
      querying = true;
      try {
        const state = await currentMeeting({ organizationId, conversationId }, controller.signal);
        if (state === null) window.clearInterval(timer);
        if (!controller.signal.aborted) setStatus(state);
      } catch {
        if (!controller.signal.aborted) setStatus(null);
      } finally {
        querying = false;
      }
    }
    const timer = window.setInterval(() => void load(), 5000);
    void load();
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [organizationId, conversationId, visible, active]);

  if (!status?.available) return null;
  const label = status.current ? 'Join meeting' : 'Meet now';
  return (
    <Button size="sm" aria-label={label} title={label} disabled={active}
      onClick={() => open(status.current
        ? { kind: 'meeting', ...status.current }
        : { kind: 'conversation', organizationId, conversationId, title })}>
      <Video size={17} aria-hidden="true" />
      <span className="nx-stage-action-label">{label}</span>
    </Button>
  );
}
