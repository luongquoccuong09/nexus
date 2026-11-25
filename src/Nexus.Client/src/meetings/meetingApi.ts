import { orgApiPath, personalApiPath, protectedRequest } from '../scope';
import type { DirectCall, DirectCallKind } from '../calls/callState';

interface MeetingScope { organizationId: string | null }
export interface MeetingSummary extends MeetingScope { meetingId: string; title: string }
export interface MeetingSession extends MeetingSummary { grantId: string; canManage: boolean }
interface MeetingCredentials extends MeetingSession { signalPath: string; token: string }
export interface MeetingConversation extends MeetingScope { conversationId: string }

export type MeetingIntent =
  | { kind: 'calendar'; organizationId: string | null; eventId: string; title: string }
  | ({ kind: 'conversation'; title: string } & MeetingConversation)
  | ({ kind: 'meeting' } & MeetingSummary)
  | ({ kind: 'directCall'; callId: string; callKind: DirectCallKind; onEnded: (call: DirectCall) => void } & MeetingSummary);

export class MeetingRequestError extends Error {}

function scoped({ organizationId }: MeetingScope, path: string) {
  return organizationId ? orgApiPath(organizationId, path) : personalApiPath(path);
}

function meetingPath(meeting: MeetingSummary) {
  return scoped(meeting, `/meetings/${meeting.meetingId}`);
}

async function meetingRequest(path: string, method: string, body?: unknown, signal?: AbortSignal) {
  const response = await protectedRequest(path, method, body, signal);
  if (response.ok) return response;
  if (response.status === 503) throw new MeetingRequestError('Meetings are temporarily unavailable.');
  if (response.status === 403 || response.status === 404) throw new MeetingRequestError('This meeting is unavailable or you do not have access.');
  if (response.status === 409 || response.status === 410) throw new MeetingRequestError('This meeting has already ended.');
  if (response.status === 429) throw new MeetingRequestError('Too many requests. Try again in a moment.');
  throw new MeetingRequestError('Unable to update the meeting. Try again.');
}

export async function currentMeeting(conversation: MeetingConversation, signal: AbortSignal) {
  const path = scoped(conversation, `/conversations/${conversation.conversationId}/meetings/current`);
  const response = await fetch(path, { signal, cache: 'no-store' });
  if (response.status === 204 && response.headers.get('Nexus-Meetings-Available') === 'false') return null;
  if (response.status === 204) return { available: true, current: null };
  if (!response.ok) return { available: false, current: null };
  return { available: true, current: await response.json() as MeetingSummary };
}

export async function joinMeeting(intent: MeetingIntent, signal: AbortSignal): Promise<MeetingCredentials> {
  if (intent.kind === 'calendar') {
    const response = await meetingRequest(scoped(intent, `/calendar/events/${intent.eventId}/meeting/join`), 'POST', undefined, signal);
    return await response.json();
  }
  let meeting: MeetingSummary;
  if (intent.kind === 'conversation') {
    const response = await meetingRequest(scoped(intent, `/conversations/${intent.conversationId}/meetings`), 'POST', undefined, signal);
    meeting = await response.json();
  } else {
    meeting = intent;
  }
  const response = await meetingRequest(`${meetingPath(meeting)}/join`, 'POST', undefined, signal);
  return await response.json();
}

export async function leaveMeeting(meeting: MeetingSession) {
  await meetingRequest(`${meetingPath(meeting)}/leave`, 'POST', { grantId: meeting.grantId });
}

export async function endMeeting(meeting: MeetingSession, signal: AbortSignal) {
  await meetingRequest(`${meetingPath(meeting)}/end`, 'POST', undefined, signal);
}

export async function removeMeetingParticipant(meeting: MeetingSession, grantId: string, signal: AbortSignal) {
  await meetingRequest(`${meetingPath(meeting)}/participants/${grantId}`, 'DELETE', undefined, signal);
}
