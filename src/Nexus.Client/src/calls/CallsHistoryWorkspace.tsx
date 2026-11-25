import { useEffect, useMemo, useRef, useState } from 'react';
import { PhoneIncoming, PhoneMissed, PhoneOutgoing } from 'lucide-react';
import type { OrganizationItem } from '../organizationControls';
import { ListPaging, readCursorPage, useCursorList } from '../cursorList';
import { Button } from '../ui';
import { callBackDetails, callHistoryFilters, callHistoryPath, type CallHistoryFilter, type CallHistoryRow,
  callOutcome, describeCallDuration } from './callHistory';
import { useDirectCall } from './DirectCallProvider';
import './call-history.css';

function callKey(call: CallHistoryRow) { return call.id; }
const localCallTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });

export default function CallsHistoryWorkspace({ userId, organizations, accessRevision }: {
  userId: string;
  organizations: OrganizationItem[];
  accessRevision: number;
}) {
  const [filter, setFilter] = useState<CallHistoryFilter>('all');
  const calls = useDirectCall();
  const history = useCursorList(`${userId}:${filter}`, async (cursor, signal) =>
    readCursorPage<CallHistoryRow>(await fetch(callHistoryPath(filter, cursor), { signal, cache: 'no-store' }),
      'Unable to load call history.'), callKey);
  const organizationNames = useMemo(() => new Map(organizations.map(organization => [organization.id, organization.name])),
    [organizations]);
  const revision = accessRevision + calls.historyRevision;
  const lastRevision = useRef(revision);
  const refresh = history.refresh;

  useEffect(() => {
    if (revision === lastRevision.current) return;
    lastRevision.current = revision;
    void refresh();
  }, [refresh, revision]);

  useEffect(() => {
    const onFocus = () => void refresh();
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [refresh]);

  return (
    <div className="nx-page-scroll nx-call-history-page">
      <section className="nx-page-container nx-call-history" aria-labelledby="call-history-heading">
        <div className="nx-page-head">
          <h2 id="call-history-heading">Call history</h2>
          <Button size="sm" disabled={history.loading} onClick={() => void refresh()}>Refresh calls</Button>
        </div>
        <div className="nx-filter-pills" role="group" aria-label="Call history filters">
          {callHistoryFilters.map(([value, label]) => (
            <button key={value} type="button" className={`nx-filter-pill${filter === value ? ' active' : ''}`}
              aria-pressed={filter === value} aria-controls="call-history-items" onClick={() => setFilter(value)}>
              {label}
            </button>
          ))}
        </div>
        {history.loading && history.items.length === 0 && <p className="nx-muted" role="status">Loading calls…</p>}
        {!history.loading && !history.error && history.items.length === 0 && (
          <p className="nx-muted nx-call-history-empty" role="status">No calls in this view.</p>
        )}
        <ul id="call-history-items" className="nx-call-history-items" role="list" aria-label="Call history" aria-busy={history.loading}>
          {history.items.map(call => {
            const direction = call.direction === 'incoming' ? 'Incoming' : 'Outgoing';
            const kind = call.kind === 'voice' ? 'Voice call' : 'Video call';
            const Icon = call.state === 'missed' && call.direction === 'incoming' ? PhoneMissed :
              call.direction === 'incoming' ? PhoneIncoming : PhoneOutgoing;
            const titleId = `call-history-title-${call.id}`;
            const duration = describeCallDuration(call.durationSeconds);
            const scope = call.organizationId ? organizationNames.get(call.organizationId) ?? 'Organization' : 'Personal';
            const callback = callBackDetails(call, calls.canStart);
            return (
              <li key={call.id} className="nx-call-history-row" aria-labelledby={titleId}>
                <span className="nx-call-history-icon" data-missed={call.state === 'missed' || undefined}>
                  <Icon size={19} aria-hidden="true" />
                </span>
                <div className="nx-call-history-detail">
                  <h3 id={titleId}>{call.counterpartName}</h3>
                  <p>{direction} · {kind} · {scope}</p>
                </div>
                <div className="nx-call-history-time">
                  <span data-missed={call.state === 'missed' || undefined}>{callOutcome[call.state]}</span>
                  <time dateTime={call.createdAt}>{localCallTime.format(new Date(call.createdAt))}</time>
                  {duration && <span aria-label={`Duration ${duration}`}>{duration}</span>}
                </div>
                <Button size="sm" className="nx-call-history-callback" disabled={!callback} aria-describedby={titleId}
                  onClick={() => { if (callback) calls.start(callback.target, callback.peerName, callback.kind); }}>
                  Call back
                </Button>
              </li>
            );
          })}
        </ul>
        <ListPaging loading={history.loading} error={history.error} hasMore={history.next !== null}
          onMore={history.loadMore} onRetry={history.retry} moreLabel="Load more calls" />
      </section>
    </div>
  );
}
