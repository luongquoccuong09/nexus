import { useEffect, useRef, useState } from 'react';
import { ListPaging, useCursorList } from '../cursorList';
import { Badge, Button, Modal } from '../ui';
import { useMeeting } from '../meetings/MeetingProvider';
import CalendarEventForm from './CalendarEventForm';
import {
  type CalendarAttendee, type CalendarCommunity, type CalendarEvent, type CalendarEventTarget,
  calendarEventId, cancelCalendarEvent, getCalendarEvent, listCalendarAttendees, respondToCalendarEvent,
  rsvpStatuses
} from './calendarApi';
import { formatCalendarTimes } from './calendarTime';

function attendeeKey(attendee: CalendarAttendee) { return attendee.userId; }

export default function CalendarEventDialog({ target, organizationId, scopeName, communityId, communities,
  userId, localZone, onClose, onChange, onEventLoaded, accessRevision }: {
  target: CalendarEventTarget | null;
  organizationId: string | null;
  scopeName: string;
  communityId: string;
  communities: CalendarCommunity[];
  userId: string;
  localZone: string;
  onClose: () => void;
  onChange: (event: CalendarEvent, message: string) => void;
  onEventLoaded?: (event: CalendarEvent) => void;
  accessRevision: number;
}) {
  const [event, setEvent] = useState<CalendarEvent | null>(null);
  const meeting = useMeeting();
  const [editing, setEditing] = useState(false);
  const [editAttendees, setEditAttendees] = useState<CalendarAttendee[]>([]);
  const [confirming, setConfirming] = useState(false);
  const [loading, setLoading] = useState(target !== null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);
  const eventRequest = useRef<AbortController | null>(null);
  const actionRequest = useRef<AbortController | null>(null);
  const loaded = useRef(onEventLoaded);
  loaded.current = onEventLoaded;

  const attendees = useCursorList(event && target ? calendarEventId(target) : null,
    (cursor, signal) => listCalendarAttendees(target!, cursor, signal), attendeeKey);

  useEffect(() => {
    if (!target) return;
    const controller = new AbortController();
    eventRequest.current = controller;
    setLoading(true);
    setError('');
    void getCalendarEvent(target, controller.signal).then(result => {
      if (controller.signal.aborted) return;
      setEvent(result);
      loaded.current?.(result);
    }).catch(cause => {
      if (!controller.signal.aborted) {
        setEvent(null);
        setError(cause instanceof Error ? cause.message : 'This event is unavailable.');
      }
    }).finally(() => {
      if (eventRequest.current === controller) {
        eventRequest.current = null;
        setLoading(false);
      }
    });
    return () => {
      controller.abort();
      if (eventRequest.current === controller) eventRequest.current = null;
    };
  }, [accessRevision, refresh, target]);
  useEffect(() => () => actionRequest.current?.abort(), []);

  async function openEditor() {
    if (!target || actionRequest.current) return;
    const controller = new AbortController();
    actionRequest.current = controller;
    setBusy(true);
    setError('');
    try {
      const invited = new Map(attendees.items.map(item => [item.userId, item]));
      let next = attendees.next;
      while (next) {
        const page = await listCalendarAttendees(target, next, controller.signal);
        for (const item of page.items) invited.set(item.userId, item);
        next = page.next;
      }
      if (controller.signal.aborted) return;
      setEditAttendees([...invited.values()]);
      setEditing(true);
    } catch (cause) {
      if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Unable to load attendees.');
    } finally {
      if (actionRequest.current === controller) actionRequest.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  async function update(
    run: (event: CalendarEvent, signal: AbortSignal) => Promise<CalendarEvent>,
    message: string
  ) {
    if (!event || actionRequest.current) return;
    const controller = new AbortController();
    actionRequest.current = controller;
    setBusy(true);
    setError('');
    try {
      const updated = await run(event, controller.signal);
      if (controller.signal.aborted) return;
      setEvent(updated);
      setConfirming(false);
      void attendees.refresh();
      onChange(updated, message);
    } catch (cause) {
      if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Unable to update this event.');
    } finally {
      if (actionRequest.current === controller) actionRequest.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  const editable = Boolean(event?.canEdit && !event.canceledAt);
  const showForm = !target || (editing && editable);
  const matchingCommunity = event?.communityId ? communities.find(community => community.id === event.communityId) : null;

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} size="xl"
      title={showForm ? event ? 'Edit event' : 'New event' : event?.title ?? 'Calendar event'}
      description={matchingCommunity ? `${scopeName} · ${matchingCommunity.name}` : scopeName}>
      {showForm ? (
        <CalendarEventForm event={event ?? undefined} invited={editAttendees} organizationId={organizationId}
          initialCommunityId={communityId} communities={communities} userId={userId} localZone={localZone}
          onSaved={saved => {
            if (!event) {
              onChange(saved, 'Event created.');
              onClose();
              return;
            }
            setEvent(saved);
            setEditing(false);
            void attendees.refresh();
            onChange(saved, 'Event updated.');
          }}
          onBack={() => event ? setEditing(false) : onClose()} />
      ) : (
        <>
          {loading && !event && <p role="status">Loading event…</p>}
          {error && <p className="nx-error" role="alert">{error}</p>}
          {!event && !loading && <Button onClick={() => setRefresh(current => current + 1)}>Retry</Button>}
          {event && (
            <>
              {event.canceledAt && <Badge variant="role">Canceled</Badge>}
              <dl className="nx-calendar-event-facts">
                <div>
                  <dt>When</dt>
                  <dd>
                    {formatCalendarTimes(event.startUtc, event.endUtc, localZone)} <span className="nx-muted">({localZone})</span>
                    {localZone !== event.timeZone && <span className="nx-calendar-event-zone nx-muted">
                      {formatCalendarTimes(event.startUtc, event.endUtc, event.timeZone)} ({event.timeZone})
                    </span>}
                  </dd>
                </div>
                <div><dt>Organizer</dt><dd>{event.organizerName}</dd></div>
              </dl>
              {event.description && <p className="nx-calendar-description">{event.description}</p>}
              {event.isOnlineMeeting && !event.canceledAt && target && (
                <Button variant="primary" disabled={meeting.active || loading} onClick={() => {
                  meeting.open({ kind: 'calendar', ...target, title: event.title });
                  onClose();
                }}>Join meeting</Button>
              )}
              {event.canRsvp && !event.canceledAt && target && (
                <div className="nx-calendar-rsvp" role="group" aria-label="RSVP">
                  {rsvpStatuses.map(status => (
                    <Button key={status} aria-pressed={event.myRsvp === status} disabled={busy || loading}
                      onClick={() => void update((_, signal) => respondToCalendarEvent(target, status, signal), 'Response saved.')}>
                      {status}
                    </Button>
                  ))}
                </div>
              )}
              <section aria-label="Attendees" className="nx-calendar-attendees">
                <h4>Attendees</h4>
                {attendees.loading && <p className="nx-muted" role="status">Loading attendees…</p>}
                {attendees.items.length > 0 && (
                  <ul>
                    {attendees.items.map(attendee => <li key={attendee.userId}>
                      <span>{attendee.displayName}</span><span className="nx-muted">{attendee.status ?? 'Awaiting response'}</span>
                    </li>)}
                  </ul>
                )}
                {!attendees.loading && !attendees.error && attendees.items.length === 0 &&
                  <p className="nx-muted">No attendees invited.</p>}
                <ListPaging loading={attendees.loading} error={attendees.error} hasMore={attendees.next !== null}
                  onMore={attendees.loadMore} onRetry={attendees.retry} moreLabel="Load more attendees" />
              </section>
              {editable && target && (
                <div className="nx-calendar-modal-actions">
                  {confirming ? (
                    <>
                      <p>Cancel this event and notify its attendees?</p>
                      <Button onClick={() => setConfirming(false)} disabled={busy}>Keep event</Button>
                      <Button variant="danger" disabled={busy || loading}
                        onClick={() => void update((current, signal) => cancelCalendarEvent(target, current.version, signal), 'Event canceled.')}>
                        Confirm cancellation
                      </Button>
                    </>
                  ) : (
                    <>
                      <Button onClick={() => void openEditor()}
                        disabled={busy || loading || attendees.loading || Boolean(attendees.error)}>Edit event</Button>
                      <Button variant="danger" disabled={busy || loading} onClick={() => setConfirming(true)}>Cancel event</Button>
                    </>
                  )}
                </div>
              )}
            </>
          )}
        </>
      )}
    </Modal>
  );
}
