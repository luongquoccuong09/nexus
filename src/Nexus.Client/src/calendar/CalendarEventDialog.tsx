import { useEffect, useRef, useState } from 'react';
import { ListPaging, useCursorList } from '../cursorList';
import { Badge, Button, Modal } from '../ui';
import { useMeeting } from '../meetings/MeetingProvider';
import CalendarEventForm from './CalendarEventForm';
import {
  type CalendarAttendee, type CalendarCommunity, type CalendarEvent, type CalendarEventTarget,
  calendarEventId, cancelCalendarEvent, cancelCalendarOccurrence, downloadCalendarEvent, getCalendarEvent,
  listCalendarAttendees, respondToCalendarEvent, type RsvpStatus, rsvpStatuses
} from './calendarApi';
import { describeRecurrence } from './calendarRecurrence';
import { formatCalendarTimes } from './calendarTime';

function attendeeKey(attendee: CalendarAttendee) { return attendee.userId; }
type EventScope = 'event' | 'series' | 'occurrence';

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
  const [editor, setEditor] = useState<{ event: CalendarEvent; scope: EventScope; invited: CalendarAttendee[] } | null>(null);
  const [confirming, setConfirming] = useState<EventScope | null>(null);
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

  async function runAction(run: (signal: AbortSignal) => Promise<void>) {
    if (actionRequest.current) return;
    const controller = new AbortController();
    actionRequest.current = controller;
    setBusy(true);
    setError('');
    try {
      await run(controller.signal);
    } catch (cause) {
      if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Unable to update this event.');
    } finally {
      if (actionRequest.current === controller) actionRequest.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  async function openEditor(scope: EventScope) {
    if (!target || !event || actionRequest.current) return;
    if (scope === 'occurrence') {
      setEditor({ event, scope, invited: [] });
      return;
    }
    await runAction(async signal => {
      async function readInvitations() {
        const invited = new Map(attendees.items.map(item => [item.userId, item]));
        let next = attendees.next;
        while (next) {
          const page = await listCalendarAttendees(target!, next, signal);
          for (const item of page.items) invited.set(item.userId, item);
          next = page.next;
        }
        return [...invited.values()];
      }
      const master = target.occurrenceStartUtc
        ? getCalendarEvent({ organizationId: target.organizationId, eventId: target.eventId }, signal)
        : event;
      const [item, invited] = await Promise.all([master, readInvitations()]);
      if (signal.aborted) return;
      if (!item.canEdit || item.canceledAt) throw new Error('This event can no longer be edited.');
      setEditor({ event: item, scope, invited });
    });
  }

  async function cancelEvent() {
    if (!target || !event || !confirming) return;
    await runAction(async signal => {
      const updated = confirming === 'occurrence'
        ? await cancelCalendarOccurrence(target, event.version, signal)
        : await cancelCalendarEvent(target, event.version, signal);
      if (signal.aborted) return;
      setConfirming(null);
      if (target.occurrenceStartUtc && confirming === 'series') onClose();
      else setEvent(updated);
      onChange(updated, confirming === 'event' ? 'Event canceled.' : confirming === 'series' ? 'Series canceled.' : 'Occurrence canceled.');
    });
  }

  async function respond(status: RsvpStatus) {
    if (!target) return;
    await runAction(async signal => {
      const updated = await respondToCalendarEvent(target, status, signal);
      if (signal.aborted) return;
      setEvent(current => current && { ...current, myRsvp: updated.myRsvp, version: updated.version });
      void attendees.refresh();
      onChange(updated, 'Response saved.');
    });
  }

  function onSaved(saved: CalendarEvent) {
    if (!target) {
      onChange(saved, 'Event created.');
      onClose();
      return;
    }
    if (editor?.scope === 'series' && target.occurrenceStartUtc) onClose();
    else setEvent(saved);
    setEditor(null);
    void attendees.refresh();
    const updated = editor?.scope === 'occurrence' ? 'Occurrence updated.' : editor?.scope === 'series' ? 'Series updated.' : 'Event updated.';
    onChange(saved, updated);
  }

  const editable = Boolean(event?.canEdit && !event.canceledAt);
  const occurrence = Boolean(event?.occurrenceStartUtc);
  const series = event?.recurrence ? 'series' : 'event';
  const showForm = !target || (editor && editable);
  const matchingCommunity = event?.communityId ? communities.find(community => community.id === event.communityId) : null;

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} size="xl"
      title={showForm ? editor ? `Edit ${editor.scope}` : 'New event' : event?.title ?? 'Calendar event'}
      description={matchingCommunity ? `${scopeName} · ${matchingCommunity.name}` : scopeName}>
      {showForm ? (
        <CalendarEventForm event={editor?.event} editingOccurrence={editor?.scope === 'occurrence'}
          invited={editor?.invited ?? []} organizationId={organizationId}
          initialCommunityId={communityId} communities={communities} userId={userId} localZone={localZone}
          onSaved={onSaved} onBack={() => target ? setEditor(null) : onClose()} />
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
                {event.recurrence && <div><dt>Repeat</dt><dd>{describeRecurrence(event.recurrence)}</dd></div>}
              </dl>
              {event.description && <p className="nx-calendar-description">{event.description}</p>}
              {event.isOnlineMeeting && !event.canceledAt && target && (!event.recurrence || occurrence) && (
                <Button variant="primary" disabled={meeting.active || loading} onClick={() => {
                  meeting.open({ kind: 'calendar', ...target, title: event.title });
                  onClose();
                }}>Join meeting</Button>
              )}
              {event.canRsvp && !event.canceledAt && target && (
                <div className="nx-calendar-rsvp" role="group" aria-label="RSVP">
                  {rsvpStatuses.map(status => (
                    <Button key={status} aria-pressed={event.myRsvp === status} disabled={busy || loading}
                      onClick={() => void respond(status)}>
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
              {target && (
                <div className="nx-calendar-modal-actions">
                  {confirming ? (
                    <>
                      <p>{confirming === 'event' ? 'Cancel this event and notify its attendees?' : `Cancel this ${confirming}?`}</p>
                      <Button onClick={() => setConfirming(null)} disabled={busy}>Keep {confirming}</Button>
                      <Button variant="danger" disabled={busy || loading} onClick={() => void cancelEvent()}>
                        {confirming === 'event' ? 'Confirm cancellation' : `Confirm ${confirming} cancellation`}
                      </Button>
                    </>
                  ) : (
                    <>
                      <Button disabled={busy || loading} onClick={() => void runAction(signal => downloadCalendarEvent(target, signal))}>
                        Download .ics
                      </Button>
                      {editable && (
                        <>
                          {occurrence && <Button disabled={busy || loading} onClick={() => void openEditor('occurrence')}>Edit occurrence</Button>}
                          <Button onClick={() => void openEditor(series)}
                            disabled={busy || loading || attendees.loading || Boolean(attendees.error)}>Edit {series}</Button>
                          {occurrence && <Button variant="danger" disabled={busy || loading} onClick={() => setConfirming('occurrence')}>Cancel occurrence</Button>}
                          <Button variant="danger" disabled={busy || loading} onClick={() => setConfirming(series)}>Cancel {series}</Button>
                        </>
                      )}
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
