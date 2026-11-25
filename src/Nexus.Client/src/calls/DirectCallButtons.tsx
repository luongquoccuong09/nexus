import { Phone, Video } from 'lucide-react';
import { Button } from '../ui';
import type { CallConversation } from './callApi';
import { useDirectCall } from './DirectCallProvider';

export default function DirectCallButtons({ organizationId, conversationId, peerName, onStart }: CallConversation & {
  peerName: string;
  onStart?: () => void;
}) {
  const calls = useDirectCall();
  if (!calls.available) return null;
  const target = { organizationId, conversationId };
  return (
    <span className="nx-direct-call-buttons" role="group" aria-label="Direct calling">
      <Button size="sm" iconOnly aria-label="Voice call" title="Voice call" disabled={!calls.canStart}
        onClick={() => { if (calls.start(target, peerName, 'Voice')) onStart?.(); }}>
        <Phone size={16} aria-hidden="true" />
      </Button>
      <Button size="sm" iconOnly aria-label="Video call" title="Video call" disabled={!calls.canStart}
        onClick={() => { if (calls.start(target, peerName, 'Video')) onStart?.(); }}>
        <Video size={16} aria-hidden="true" />
      </Button>
    </span>
  );
}
