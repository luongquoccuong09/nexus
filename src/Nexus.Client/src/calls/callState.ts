export type DirectCallKind = 'Voice' | 'Video';
export type DirectCallState = 'ringing' | 'accepted' | 'declined' | 'canceled' | 'missed' | 'ended';

export interface DirectCallChange {
  id: string;
  organizationId: string | null;
  conversationId: string;
  state: DirectCallState;
  version: number;
}

export interface DirectCall extends DirectCallChange {
  callerId: string;
  callerName: string;
  calleeId: string;
  calleeName: string;
  kind: DirectCallKind;
  meetingId: string | null;
  createdAt: string;
  expiresAt: string;
  acceptedAt: string | null;
  endedAt: string | null;
  canJoinMedia: boolean;
}

export interface CallRecord {
  change: DirectCallChange;
  detail: DirectCall | null;
  unavailable?: boolean;
}

export function isCallActive(state: DirectCallState) {
  return state === 'ringing' || state === 'accepted';
}

export function mergeCallRecord(previous: CallRecord | undefined, change: DirectCallChange, detail: DirectCall | null = null) {
  if (previous && (previous.change.version > change.version ||
      previous.change.version === change.version && (previous.unavailable || !detail || previous.change.state !== change.state))) {
    return previous;
  }
  return { change, detail };
}

export function activeRingingCall(record: CallRecord, now: number) {
  if (record.change.state !== 'ringing' || !record.detail || !(Date.parse(record.detail.expiresAt) > now)) return null;
  return record.detail;
}

export function shouldJoinCallMedia(record: CallRecord, locallyRequested: boolean) {
  return locallyRequested && record.change.state === 'accepted' &&
    record.detail?.canJoinMedia === true && Boolean(record.detail.meetingId);
}

export function callResultMessage(state: DirectCallState) {
  switch (state) {
    case 'declined': return 'Call declined.';
    case 'canceled': return 'Call canceled.';
    case 'missed': return 'Call missed.';
    case 'ended': return 'Call ended.';
    default: return '';
  }
}
