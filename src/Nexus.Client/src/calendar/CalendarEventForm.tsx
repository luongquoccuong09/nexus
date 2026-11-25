import { type FormEvent, useEffect, useMemo, useRef, useState } from 'react';
import { ListPaging, useCursorList } from '../cursorList';
import { personKey } from '../peopleDirectory';
import { Button, Field } from '../ui';
import {
  type CalendarAttendee, type CalendarCommunity, type CalendarEvent, type CalendarEventInput, type CalendarPerson,
  listCalendarCandidates, saveCalendarEvent
} from './calendarApi';
import { calendarZones, defaultEventTimes, toEventInstant, toEventLocalTime } from './calendarTime';

const reminderOptions = [
  [0, 'At the start'], [5, '5 minutes before'], [15, '15 minutes before'], [30, '30 minutes before'],
  [60, '1 hour before'], [1440, '1 day before'], [10080, '1 week before']
] as const;

export default function CalendarEventForm({ event, invited, organizationId, initialCommunityId, communities, userId,
  localZone, onSaved, onBack }: {
  event?: CalendarEvent;
  invited: CalendarAttendee[];
  organizationId: string | null;
  initialCommunityId: string;
  communities: CalendarCommunity[];
  userId: string;
  localZone: string;
  onSaved: (event: CalendarEvent) => void;
  onBack: () => void;
}) {
  const [title, setTitle] = useState(event?.title ?? '');
  const [description, setDescription] = useState(event?.description ?? '');
  const [timeZone, setTimeZone] = useState(event?.timeZone ?? localZone);
  const [times, setTimes] = useState(() => event ? {
    startLocal: toEventLocalTime(event.startUtc, event.timeZone),
    endLocal: toEventLocalTime(event.endUtc, event.timeZone)
  } : defaultEventTimes(localZone));
  const [communityId, setCommunityId] = useState(event?.communityId ?? initialCommunityId);
  const [reminder, setReminder] = useState(event?.reminderMinutes?.toString() ?? '');
  const [isOnlineMeeting, setIsOnlineMeeting] = useState(event?.isOnlineMeeting ?? false);
  const [selectedPeople, setSelectedPeople] = useState<CalendarPerson[]>(() =>
    invited.map(person => ({ id: person.userId, displayName: person.displayName })));
  const [query, setQuery] = useState('');
  const [error, setError] = useState('');
  const [timeError, setTimeError] = useState<'start' | 'end' | ''>('');
  const [busy, setBusy] = useState(false);
  const request = useRef<AbortController | null>(null);
  const createAttempt = useRef<{ payload: string; key: string } | null>(null);
  useEffect(() => () => request.current?.abort(), []);

  const candidateQuery = query.trim().replace(/^@/, '');
  const searchKey = JSON.stringify([organizationId, communityId, communityId ? '' : candidateQuery]);
  const candidates = useCursorList(searchKey,
    (cursor, signal) => listCalendarCandidates(organizationId, communityId, candidateQuery, cursor, signal),
    personKey, !communityId && candidateQuery ? 180 : 0);
  const candidateNeedle = candidateQuery.toLocaleLowerCase();
  const displayedCandidates = candidates.items.filter(person =>
    person.id !== userId && (!communityId ||
      person.displayName.toLocaleLowerCase().includes(candidateNeedle) ||
      person.username?.toLocaleLowerCase().includes(candidateNeedle)));
  const zones = useMemo(() => calendarZones(localZone), [localZone]);

  function togglePerson(person: CalendarPerson, checked: boolean) {
    setSelectedPeople(current => {
      if (!checked) return current.filter(entry => entry.id !== person.id);
      if (current.some(entry => entry.id === person.id)) return current;
      return [...current, person];
    });
  }

  async function submit(formEvent: FormEvent<HTMLFormElement>) {
    formEvent.preventDefault();
    if (request.current) return;
    setError('');
    setTimeError('');
    let startUtc: string;
    let endUtc: string;
    try {
      startUtc = toEventInstant(times.startLocal, timeZone, event && { instant: event.startUtc, timeZone: event.timeZone });
    } catch {
      setTimeError('start');
      return;
    }
    try {
      endUtc = toEventInstant(times.endLocal, timeZone, event && { instant: event.endUtc, timeZone: event.timeZone });
    } catch {
      setTimeError('end');
      return;
    }
    if (Date.parse(endUtc) <= Date.parse(startUtc)) {
      setError('The event must end after it starts.');
      return;
    }
    if (!title.trim()) {
      setError('Enter an event title.');
      return;
    }
    const controller = new AbortController();
    request.current = controller;
    setBusy(true);
    try {
      const input: CalendarEventInput = {
        title: title.trim(), description: description.trim(), startUtc, endUtc, timeZone,
        communityId: communityId || null, attendeeIds: selectedPeople.map(person => person.id).sort(),
        reminderMinutes: reminder === '' ? null : Number(reminder), isOnlineMeeting, version: event?.version
      };
      if (!event) {
        const payload = JSON.stringify(input);
        if (createAttempt.current?.payload !== payload) createAttempt.current = { payload, key: crypto.randomUUID() };
      }
      const saved = await saveCalendarEvent(organizationId, event?.id ?? null, input, controller.signal, createAttempt.current?.key);
      if (!controller.signal.aborted) onSaved(saved);
    } catch (cause) {
      if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Unable to save the event.');
    } finally {
      if (request.current === controller) request.current = null;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  return (
    <form onSubmit={submit}>
      <Field label="Title" htmlFor="calendar-event-title">
        <input id="calendar-event-title" required maxLength={160} value={title} onChange={e => setTitle(e.target.value)} />
      </Field>
      <Field label="Description" htmlFor="calendar-event-description">
        <textarea id="calendar-event-description" rows={3} maxLength={4000} value={description} onChange={e => setDescription(e.target.value)} />
      </Field>
      <div className="nx-calendar-form-row">
        <Field label="Starts" htmlFor="calendar-event-start">
          <input id="calendar-event-start" type="datetime-local" step="1" required value={times.startLocal}
            aria-invalid={timeError === 'start'} aria-describedby={timeError === 'start' ? 'calendar-time-error' : undefined}
            onChange={e => { setTimeError(''); setTimes({ ...times, startLocal: e.target.value }); }} />
        </Field>
        <Field label="Ends" htmlFor="calendar-event-end">
          <input id="calendar-event-end" type="datetime-local" step="1" required value={times.endLocal}
            aria-invalid={timeError === 'end'} aria-describedby={timeError === 'end' ? 'calendar-time-error' : undefined}
            onChange={e => { setTimeError(''); setTimes({ ...times, endLocal: e.target.value }); }} />
        </Field>
      </div>
      {timeError && <p className="nx-error" id="calendar-time-error" role="alert">
        Choose an unambiguous {timeError === 'start' ? 'start' : 'end'} time outside the daylight-saving transition.
      </p>}
      <div className="nx-calendar-form-row">
        <Field label="Time zone" htmlFor="calendar-event-time-zone">
          <select id="calendar-event-time-zone" value={timeZone} onChange={e => { setTimeZone(e.target.value); setTimeError(''); }}>
            {!zones.includes(timeZone) && <option value={timeZone}>{timeZone}</option>}
            {zones.map(zone => <option key={zone}>{zone}</option>)}
          </select>
        </Field>
        <Field label="Reminder" htmlFor="calendar-event-reminder">
          <select id="calendar-event-reminder" value={reminder} onChange={e => setReminder(e.target.value)}>
            <option value="">None</option>
            {reminder !== '' && !reminderOptions.some(([minutes]) => minutes.toString() === reminder) &&
              <option value={reminder}>{reminder} minutes before</option>}
            {reminderOptions.map(([minutes, label]) => <option key={minutes} value={minutes}>{label}</option>)}
          </select>
        </Field>
      </div>
      {!event && organizationId && (
        <Field label="Event community" htmlFor="calendar-event-community">
          <select id="calendar-event-community" value={communityId} onChange={e => {
            setCommunityId(e.target.value);
            setSelectedPeople([]);
            setQuery('');
          }}>
            <option value="">No community</option>
            {communities.map(community => <option key={community.id} value={community.id}>{community.name}</option>)}
          </select>
        </Field>
      )}
      <div className="nx-field">
        <label className="nx-terms-label">
          <input type="checkbox" checked={isOnlineMeeting} onChange={e => setIsOnlineMeeting(e.currentTarget.checked)} />
          <span>Online meeting</span>
        </label>
      </div>
      <fieldset className="nx-calendar-invite-field">
        <legend>Invite attendees</legend>
        <Field label="Search people" htmlFor="calendar-event-people">
          <input id="calendar-event-people" type="search" value={query} onChange={e => setQuery(e.target.value)}
            autoComplete="off" maxLength={120} />
        </Field>
        {selectedPeople.length > 0 && (
          <ul className="nx-calendar-invited" aria-label="Selected attendees">
            {selectedPeople.map(person => (
              <li key={person.id}>
                <Button variant="ghost" size="sm" aria-label={`Remove ${person.displayName} from attendees`}
                  onClick={() => togglePerson(person, false)}>{person.displayName} ×</Button>
              </li>
            ))}
          </ul>
        )}
        {displayedCandidates.length > 0 && (
          <ul className="nx-calendar-candidates" aria-label="Matching people">
            {displayedCandidates.map(person => {
              const checked = selectedPeople.some(entry => entry.id === person.id);
              return (
                <li key={person.id}>
                  <label>
                    <input type="checkbox" checked={checked} onChange={e => togglePerson(person, e.target.checked)} />
                    <span>{person.displayName}{person.username && <small>@{person.username}</small>}</span>
                  </label>
                </li>
              );
            })}
          </ul>
        )}
        {candidates.loading && <p className="nx-muted" role="status">Loading people…</p>}
        {!candidates.loading && !candidates.error && displayedCandidates.length === 0 &&
          <p className="nx-muted" role="status">{candidates.next ? 'No matches on the loaded pages.' : 'No matching people.'}</p>}
        <ListPaging loading={candidates.loading} error={candidates.error} hasMore={candidates.next !== null}
          onMore={candidates.loadMore} onRetry={candidates.retry} moreLabel="Load more people" />
      </fieldset>
      {error && <p className="nx-error" role="alert">{error}</p>}
      <div className="nx-calendar-modal-actions">
        <Button onClick={onBack}>Back</Button>
        <Button type="submit" variant="primary" disabled={busy}>
          {busy ? 'Saving…' : event ? 'Save changes' : 'Create event'}
        </Button>
      </div>
    </form>
  );
}
