import type { DirectCallKind, DirectCallState } from './callState';

export type CallHistoryFilter = 'all' | 'incoming' | 'outgoing' | 'missed';
export const callHistoryFilters = [
  ['all', 'All'], ['incoming', 'Incoming'], ['outgoing', 'Outgoing'], ['missed', 'Missed']
] as const satisfies ReadonlyArray<readonly [CallHistoryFilter, string]>;

export interface CallHistoryRow {
  id: string;
  organizationId: string | null;
  conversationId: string;
  kind: 'voice' | 'video';
  state: Exclude<DirectCallState, 'ringing' | 'accepted'>;
  direction: 'incoming' | 'outgoing';
  counterpartId: string;
  counterpartName: string;
  createdAt: string;
  acceptedAt: string | null;
  endedAt: string;
  durationSeconds: number | null;
  canCallBack: boolean;
}

export const callOutcome: Record<CallHistoryRow['state'], string> = {
  declined: 'Declined',
  canceled: 'Canceled',
  missed: 'Missed',
  ended: 'Ended'
};

export function callHistoryPath(filter: CallHistoryFilter, cursor: string | null) {
  const query = new URLSearchParams({ filter, limit: '50' });
  if (cursor) query.set('cursor', cursor);
  return `/api/calls/history?${query}`;
}

export function describeCallDuration(durationSeconds: number | null) {
  if (durationSeconds === null || !Number.isFinite(durationSeconds) || durationSeconds < 0) return null;
  const seconds = Math.floor(durationSeconds);
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor(seconds / 60) % 60;
  const remainder = seconds % 60;
  return [
    hours ? `${hours} hr` : '',
    minutes ? `${minutes} min` : '',
    remainder || seconds === 0 ? `${remainder} sec` : ''
  ].filter(Boolean).join(' ');
}

export function callBackDetails(row: CallHistoryRow, canStart: boolean) {
  if (!row.canCallBack || !canStart) return null;
  const kind: DirectCallKind = row.kind === 'video' ? 'Video' : 'Voice';
  return {
    target: { conversationId: row.conversationId, organizationId: row.organizationId },
    peerName: row.counterpartName,
    kind
  };
}
