import { readCursorPage } from '../cursorList';
import { searchChatCandidates, searchPeopleScope } from '../peopleDirectory';
import { orgApiPath, personalApiPath, protectedRequest } from '../scope';

export type RsvpStatus = 'Going' | 'Tentative' | 'Declined';
export const rsvpStatuses: readonly RsvpStatus[] = ['Going', 'Tentative', 'Declined'];

export interface CalendarEvent {
  id: string;
  organizationId: string | null;
  communityId: string | null;
  organizerId: string;
  organizerName: string;
  title: string;
  description: string | null;
  startUtc: string;
  endUtc: string;
  timeZone: string;
  reminderMinutes: number | null;
  isOnlineMeeting: boolean;
  canceledAt: string | null;
  version: number;
  myRsvp: RsvpStatus | null;
  canEdit: boolean;
  canRsvp: boolean;
}

export interface CalendarEventInput {
  title: string;
  description: string;
  startUtc: string;
  endUtc: string;
  timeZone: string;
  communityId: string | null;
  attendeeIds: string[];
  reminderMinutes: number | null;
  isOnlineMeeting?: boolean;
  version?: number;
}

export interface CalendarCommunity { id: string; name: string }
export interface CalendarPerson { id: string; displayName: string; username?: string }
export interface CalendarAttendee { userId: string; displayName: string; status: RsvpStatus | null }
export interface CalendarDateRange { fromUtc: string; toUtc: string }
export interface CalendarEventTarget { organizationId: string | null; eventId: string }

export function calendarEventId(target: CalendarEventTarget) {
  return `${target.organizationId ?? 'personal'}:${target.eventId}`;
}

function eventsPath(organizationId: string | null) {
  return organizationId
    ? orgApiPath(organizationId, '/calendar/events')
    : personalApiPath('/calendar/events');
}

function paging(cursor: string | null) {
  const params = new URLSearchParams({ limit: '50' });
  if (cursor) params.set('cursor', cursor);
  return params;
}

export async function listCalendarEvents(organizationId: string | null, communityId: string,
  range: CalendarDateRange, cursor: string | null, signal: AbortSignal) {
  const params = paging(cursor);
  params.set('fromUtc', range.fromUtc);
  params.set('toUtc', range.toUtc);
  if (communityId) params.set('communityId', communityId);
  return readCursorPage<CalendarEvent>(await fetch(`${eventsPath(organizationId)}?${params}`, { signal }),
    'Unable to load this calendar.');
}

export async function listCalendarCommunities(organizationId: string, cursor: string | null, signal: AbortSignal) {
  return readCursorPage<CalendarCommunity>(
    await fetch(`${orgApiPath(organizationId, '/communities')}?${paging(cursor)}`, { signal }),
    'Unable to load communities.'
  );
}

export async function listCalendarCandidates(organizationId: string | null, communityId: string,
  query: string, cursor: string | null, signal: AbortSignal) {
  if (!organizationId) return searchChatCandidates(query, cursor, signal);
  if (!communityId) return searchPeopleScope<CalendarPerson>(organizationId, query, '', cursor, signal);
  const page = await readCursorPage<{ userId: string; displayName: string; username: string; isActive: boolean }>(
    await fetch(`${orgApiPath(organizationId, `/communities/${communityId}/members`)}?${paging(cursor)}`, { signal }),
    'Unable to load community members.'
  );
  return {
    items: page.items.filter(member => member.isActive).map(member => ({
      id: member.userId, displayName: member.displayName, username: member.username
    })),
    next: page.next
  };
}

export async function getCalendarEvent({ organizationId, eventId }: CalendarEventTarget, signal: AbortSignal) {
  const response = await fetch(`${eventsPath(organizationId)}/${eventId}`, { signal });
  if (!response.ok) throw new Error('This event is unavailable.');
  return await response.json() as CalendarEvent;
}

export async function listCalendarAttendees({ organizationId, eventId }: CalendarEventTarget, cursor: string | null, signal: AbortSignal) {
  return readCursorPage<CalendarAttendee>(
    await fetch(`${eventsPath(organizationId)}/${eventId}/attendees?${paging(cursor)}`, { signal }),
    'Unable to load attendees.'
  );
}

async function writeEvent(path: string, method: string, body: unknown, signal: AbortSignal, key?: string) {
  const response = await protectedRequest(path, method, body, signal, key ? { 'Idempotency-Key': key } : undefined);
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { message?: string } | null;
    throw new Error(problem?.message || 'Unable to update this calendar event.');
  }
  return await response.json() as CalendarEvent;
}

export function saveCalendarEvent(organizationId: string | null, eventId: string | null, input: CalendarEventInput,
  signal: AbortSignal, key?: string) {
  const path = eventsPath(organizationId);
  return eventId ? writeEvent(`${path}/${eventId}`, 'PUT', input, signal) : writeEvent(path, 'POST', input, signal, key);
}

export function cancelCalendarEvent({ organizationId, eventId }: CalendarEventTarget, version: number, signal: AbortSignal) {
  return writeEvent(`${eventsPath(organizationId)}/${eventId}/cancel`, 'POST', { version }, signal);
}

export function respondToCalendarEvent({ organizationId, eventId }: CalendarEventTarget, status: RsvpStatus, signal: AbortSignal) {
  return writeEvent(`${eventsPath(organizationId)}/${eventId}/rsvp`, 'PUT', { status }, signal);
}
