import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import FullCalendar, { type CalendarRef, type DatesSetInfo, type EventInput } from '@fullcalendar/react';
import dayGrid from '@fullcalendar/react/daygrid';
import timeGrid from '@fullcalendar/react/timegrid';
import list from '@fullcalendar/react/list';
import monarch from '@fullcalendar/react/themes/monarch';
import { ChevronLeft, ChevronRight, Plus } from 'lucide-react';
import type { OrganizationItem } from '../organizationControls';
import { ListPaging, useCursorList } from '../cursorList';
import { Button, Field, ToastBar } from '../ui';
import CalendarEventDialog from './CalendarEventDialog';
import {
  type CalendarCommunity, type CalendarDateRange, type CalendarEvent, type CalendarEventTarget,
  calendarEventId, listCalendarCommunities, listCalendarEvents
} from './calendarApi';
import { localCalendarZone } from './calendarTime';
import '@fullcalendar/react/skeleton.css';
import '@fullcalendar/react/themes/monarch/theme.css';
import './calendar.css';

const calendarPlugins = [monarch, dayGrid, timeGrid, list];
const views = [['Month', 'dayGridMonth'], ['Week', 'timeGridWeek'], ['Agenda', 'listWeek']] as const;
function eventKey(event: CalendarEvent) { return event.id; }
function communityKey(community: CalendarCommunity) { return community.id; }

export default function CalendarWorkspace({ organizations, userId, initialEvent, accessRevision }: {
  organizations: OrganizationItem[];
  userId: string;
  initialEvent: CalendarEventTarget | null;
  accessRevision: number;
}) {
  const calendarRef = useRef<CalendarRef>(null);
  const [viewType, setViewType] = useState('dayGridMonth');
  const [viewTitle, setViewTitle] = useState('');
  const [range, setRange] = useState<CalendarDateRange | null>(null);
  const [organizationChoice, setOrganizationChoice] = useState(initialEvent?.organizationId ?? '');
  const [communityChoice, setCommunityChoice] = useState('');
  const [dialog, setDialog] = useState<'create' | CalendarEventTarget | null>(initialEvent);
  const [windowFocusRevision, setWindowFocusRevision] = useState(0);
  const [notice, setNotice] = useState('');
  const jumpPending = useRef(initialEvent);
  const localZone = localCalendarZone();
  const organization = organizations.find(item => item.id === organizationChoice);
  const organizationId = organization?.id ?? null;

  const communities = useCursorList(organizationId,
    (cursor, signal) => listCalendarCommunities(organizationId!, cursor, signal), communityKey);
  const communityId = communities.items.some(item => item.id === communityChoice) ? communityChoice : '';
  const queryKey = range && JSON.stringify([organizationId, communityId, range.fromUtc, range.toUtc]);
  const events = useCursorList(queryKey,
    (cursor, signal) => listCalendarEvents(organizationId, communityId, range!, cursor, signal), eventKey);
  const calendarEvents = useMemo<EventInput[]>(() => events.items.map(event => ({
    id: event.id,
    title: event.canceledAt ? `Canceled · ${event.title}` : event.title,
    start: event.startUtc,
    end: event.endUtc,
    allDay: false,
    extendedProps: { canceled: event.canceledAt !== null }
  })), [events.items]);

  useEffect(() => {
    function refreshOnFocus() { setWindowFocusRevision(current => current + 1); }
    window.addEventListener('focus', refreshOnFocus);
    return () => window.removeEventListener('focus', refreshOnFocus);
  }, []);

  const access = accessRevision + windowFocusRevision;
  const lastAccess = useRef(access);
  const refreshCalendar = events.refresh;
  const refreshCommunities = communities.refresh;
  useEffect(() => {
    if (lastAccess.current === access) return;
    lastAccess.current = access;
    void refreshCommunities();
    void refreshCalendar();
  }, [access, refreshCalendar, refreshCommunities]);

  const syncView = useCallback(({ start, end, view }: DatesSetInfo) => {
    const fromUtc = start.toISOString();
    const toUtc = end.toISOString();
    setRange(current => current?.fromUtc === fromUtc && current.toUtc === toUtc ? current : { fromUtc, toUtc });
    setViewType(view.type);
    setViewTitle(view.title);
  }, []);

  const showEventDate = useCallback((event: CalendarEvent) => {
    const pending = jumpPending.current;
    if (pending?.eventId === event.id && pending.organizationId === event.organizationId) {
      jumpPending.current = null;
      calendarRef.current?.getApi().gotoDate(new Date(event.startUtc));
    }
  }, []);

  function changeScope(organizationId: string) {
    setOrganizationChoice(organizationId);
    setCommunityChoice('');
    setDialog(null);
  }

  function onEventChange(event: CalendarEvent, message: string) {
    if (dialog === 'create') {
      setCommunityChoice(event.communityId ?? '');
      calendarRef.current?.getApi().gotoDate(new Date(event.startUtc));
    }
    void refreshCalendar();
    setNotice(message);
  }

  const dialogTarget = dialog === 'create' ? null : dialog;
  const showDialog = dialog === 'create' || dialogTarget !== null && dialogTarget.organizationId === organizationId;

  return (
    <div className="nx-page-scroll nx-calendar-page">
      <section className="nx-page-container nx-calendar" aria-label="Calendar">
        <div className="nx-calendar-controls">
          <Field label="Calendar scope" htmlFor="calendar-scope" compact>
            <select id="calendar-scope" value={organizationId ?? ''} onChange={e => changeScope(e.target.value)}>
              <option value="">Personal</option>
              {organizations.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
            </select>
          </Field>
          {organizationId && (
            <Field label="Community" htmlFor="calendar-community" compact>
              <select id="calendar-community" value={communityId} onChange={e => setCommunityChoice(e.target.value)}>
                <option value="">All communities</option>
                {communities.items.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
              </select>
            </Field>
          )}
          <Button variant="primary" className="nx-calendar-new" onClick={() => setDialog('create')}>
            <Plus size={17} aria-hidden="true" /> New event
          </Button>
        </div>
        {organizationId && (
          <ListPaging loading={communities.loading} error={communities.error} hasMore={communities.next !== null}
            onMore={communities.loadMore} onRetry={communities.retry} moreLabel="Load more communities" />
        )}
        {notice && <ToastBar variant="ok" message={notice} onDismiss={() => setNotice('')} />}
        <div className="nx-calendar-frame">
          <header className="nx-calendar-toolbar">
            <h2 aria-live="polite">{viewTitle || 'Calendar'}</h2>
            <div className="nx-calendar-period" role="group" aria-label="Calendar dates">
              <Button size="sm" iconOnly aria-label="Previous period" onClick={() => calendarRef.current?.getApi().prev()}>
                <ChevronLeft size={18} aria-hidden="true" />
              </Button>
              <Button size="sm" onClick={() => calendarRef.current?.getApi().today()}>Today</Button>
              <Button size="sm" iconOnly aria-label="Next period" onClick={() => calendarRef.current?.getApi().next()}>
                <ChevronRight size={18} aria-hidden="true" />
              </Button>
            </div>
            <div className="nx-calendar-views" role="group" aria-label="Calendar view">
              {views.map(([label, view]) => (
                <Button key={view} size="sm" aria-pressed={viewType === view}
                  onClick={() => calendarRef.current?.getApi().changeView(view)}>{label}</Button>
              ))}
            </div>
          </header>
          <div className="nx-calendar-grid">
            <FullCalendar ref={calendarRef} plugins={calendarPlugins}
              initialView="dayGridMonth" timeZone="local" headerToolbar={false} datesSet={syncView}
              dayHeaderFormat={{ weekday: 'short' }} dayMaxEvents={3}
              height={viewType === 'timeGridWeek' ? 560 : 'auto'}
              eventInteractive eventClass={({ event }) => event.extendedProps.canceled ? 'nx-calendar-canceled' : ''}
              eventClick={({ event }) => setDialog({ organizationId, eventId: event.id })}
              eventTimeFormat={{ hour: 'numeric', minute: '2-digit' }}
              noEventsContent={events.loading ? 'Loading events…' : 'No events in this period.'}
              events={calendarEvents} />
          </div>
          <footer className="nx-calendar-footer">
            <p className="nx-muted">Times shown in {localZone}.</p>
            {events.loading && viewType !== 'listWeek' && <p className="nx-muted" role="status">Loading events…</p>}
            <ListPaging loading={events.loading} error={events.error} hasMore={events.next !== null}
              onMore={events.loadMore} onRetry={events.retry} moreLabel="Load more events" />
            <Button variant="ghost" size="sm" disabled={events.loading} onClick={() => void events.refresh()}>Refresh calendar</Button>
          </footer>
        </div>
      </section>
      {showDialog && (
        <CalendarEventDialog key={dialogTarget ? calendarEventId(dialogTarget) : 'create'} target={dialogTarget}
          organizationId={organizationId} scopeName={organization?.name ?? 'Personal'} communityId={communityId}
          communities={communities.items} userId={userId} localZone={localZone} accessRevision={access}
          onClose={() => setDialog(null)} onChange={onEventChange} onEventLoaded={showEventDate} />
      )}
    </div>
  );
}
