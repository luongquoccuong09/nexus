import { createContext, type ReactNode, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Phone, Video } from 'lucide-react';
import { preloadMeetingRoom, useMeeting } from '../meetings/MeetingProvider';
import { Button, ToastBar } from '../ui';
import { type CallAction, type CallConversation, CallRequestError, type CallTarget,
  getDirectCall, listActiveCalls, startDirectCall, updateDirectCall } from './callApi';
import { activeRingingCall, type CallRecord, callResultMessage, type DirectCall, type DirectCallChange,
  type DirectCallKind, isCallActive, mergeCallRecord, shouldJoinCallMedia } from './callState';
import { useRingtone } from './useRingtone';
import './calls.css';

interface DirectCallContextValue {
  start: (target: CallConversation, peerName: string, kind: DirectCallKind) => boolean;
  receive: (change: DirectCallChange) => void;
  available: boolean;
  canStart: boolean;
  historyRevision: number;
}

const DirectCallContext = createContext<DirectCallContextValue | null>(null);

export function useDirectCall() {
  const calls = useContext(DirectCallContext);
  if (!calls) throw new Error('Direct call controls must be inside DirectCallProvider.');
  return calls;
}

export default function DirectCallProvider({ children, userId, connectionState, suppressChime, accessRevision }: {
  children: ReactNode;
  userId: string;
  connectionState: 'connected' | 'reconnecting' | 'offline';
  suppressChime: boolean;
  accessRevision: number;
}) {
  const { open: openMedia, active: mediaActive, closeCall } = useMeeting();
  const records = useRef(new Map<string, CallRecord>());
  const wantedHere = useRef(new Set<string>());
  const openedHere = useRef(new Set<string>());
  const lookups = useRef(new Map<string, AbortController>());
  const actions = useRef(new Map<string, AbortController>());
  const snapshot = useRef<AbortController | null>(null);
  const starting = useRef<AbortController | null>(null);
  const retry = useRef<{ key: string; id: string } | null>(null);
  const [rows, setRows] = useState<CallRecord[]>([]);
  const [available, setAvailable] = useState(false);
  const [historyRevision, setHistoryRevision] = useState(0);
  const [sending, setSending] = useState<string | null>(null);
  const [busyCalls, setBusyCalls] = useState<string[]>([]);
  const [notice, setNotice] = useState('');
  const [error, setError] = useState('');
  const [clock, setClock] = useState(0);

  const publish = useCallback(() => setRows([...records.current.values()]), []);

  const removeAccess = useCallback((id: string, expected?: CallRecord) => {
    const current = records.current.get(id);
    if (!current || expected && current !== expected || !isCallActive(current.change.state)) return;
    records.current.set(id, { change: current.change, detail: null, unavailable: true });
    wantedHere.current.delete(id);
    openedHere.current.delete(id);
    closeCall(id);
    publish();
  }, [closeCall, publish]);

  const remember = useCallback((change: DirectCallChange, detail: DirectCall | null = null) => {
    const previous = records.current.get(change.id);
    const next = mergeCallRecord(previous, change, detail);
    if (next === previous) return false;
    records.current.set(change.id, next);
    if (!isCallActive(change.state)) {
      if (!previous || previous.change.version < change.version) setHistoryRevision(current => current + 1);
      wantedHere.current.delete(change.id);
      openedHere.current.delete(change.id);
      closeCall(change.id);
      if (previous && isCallActive(previous.change.state)) {
        setNotice(callResultMessage(change.state));
      }
    }
    // Keep recent terminal versions briefly to discard out-of-order events after reconnecting.
    if (records.current.size > 100) {
      for (const [id, row] of records.current) {
        if (row.unavailable || !isCallActive(row.change.state)) records.current.delete(id);
        if (records.current.size <= 100) break;
      }
    }
    publish();
    return true;
  }, [closeCall, publish]);

  const loadCall = useCallback((target: CallTarget) => {
    lookups.current.get(target.id)?.abort();
    const controller = new AbortController();
    lookups.current.set(target.id, controller);
    const expected = records.current.get(target.id);
    void getDirectCall(target, controller.signal).then(detail => {
      if (!controller.signal.aborted) remember(detail, detail);
    }).catch(cause => {
      if (!controller.signal.aborted && cause instanceof CallRequestError && [403, 404].includes(cause.status)) {
        removeAccess(target.id, expected);
      }
    }).finally(() => {
      if (lookups.current.get(target.id) === controller) lookups.current.delete(target.id);
    });
  }, [remember, removeAccess]);

  const receive = useCallback((change: DirectCallChange) => {
    if (!change?.id || !remember(change)) return;
    if (isCallActive(change.state)) {
      loadCall(change);
    } else {
      lookups.current.get(change.id)?.abort();
    }
  }, [loadCall, remember]);

  const refresh = useCallback(async () => {
    snapshot.current?.abort();
    const controller = new AbortController();
    snapshot.current = controller;
    const baseline = new Map(records.current);
    const found = new Set<string>();
    let cursor: string | null = null;
    try {
      do {
        const page = await listActiveCalls(cursor, controller.signal);
        if (controller.signal.aborted) return;
        if (cursor === null) setAvailable(page.available ?? true);
        for (const detail of page.items) {
          found.add(detail.id);
          remember(detail, detail);
        }
        cursor = page.nextCursor;
      } while (cursor);
      for (const [id, record] of baseline) {
        if (!found.has(id)) removeAccess(id, record);
      }
    } catch {
      // SignalR and the next successful refresh can restore the authoritative call state.
    } finally {
      if (snapshot.current === controller) snapshot.current = null;
    }
  }, [remember, removeAccess]);

  useEffect(() => {
    void refresh();
    const onVisible = () => { if (document.visibilityState === 'visible') void refresh(); };
    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('online', onVisible);
    const interval = connectionState === 'connected' ? null : window.setInterval(onVisible, 5_000);
    return () => {
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('online', onVisible);
      if (interval !== null) window.clearInterval(interval);
      snapshot.current?.abort();
    };
  }, [refresh, connectionState, accessRevision]);

  useEffect(() => () => {
    starting.current?.abort();
    for (const controller of lookups.current.values()) controller.abort();
    for (const controller of actions.current.values()) controller.abort();
  }, []);

  useEffect(() => {
    const now = Date.now();
    const expires = rows.flatMap(row => {
      if (row.change.state !== 'ringing' || !row.detail) return [];
      const time = Date.parse(row.detail.expiresAt);
      return time > now ? [time] : [];
    });
    if (!expires.length) return;
    const timer = window.setTimeout(() => {
      setClock(current => current + 1);
      const at = Date.now();
      for (const row of rows) {
        if (row.change.state === 'ringing' && row.detail && Date.parse(row.detail.expiresAt) <= at) loadCall(row.change);
      }
    }, Math.min(...expires) - now + 50);
    return () => window.clearTimeout(timer);
  }, [clock, loadCall, rows]);

  const ringing = rows.map(row => activeRingingCall(row, Date.now())).filter((call): call is DirectCall => call !== null);
  const incoming = ringing.find(call => call.calleeId === userId);
  const outgoing = ringing.find(call => call.callerId === userId && wantedHere.current.has(call.id)) ??
    ringing.find(call => call.callerId === userId);
  const ring = incoming ?? outgoing;
  const hasIncoming = incoming !== undefined;
  useEffect(() => {
    if (hasIncoming) preloadMeetingRoom();
  }, [hasIncoming]);
  useRingtone(incoming?.conversationId ?? null, suppressChime || Boolean(incoming && busyCalls.includes(incoming.id)));

  const canStart = available && !sending && !ring && !mediaActive;
  const start = useCallback((target: CallConversation, peerName: string, kind: DirectCallKind) => {
    if (!canStart || starting.current) return false;
    const key = JSON.stringify([target.organizationId, target.conversationId, kind]);
    const dedupe = retry.current?.key === key ? retry.current : { key, id: crypto.randomUUID() };
    retry.current = dedupe;
    const controller = new AbortController();
    starting.current = controller;
    setSending(peerName);
    setNotice('');
    setError('');
    preloadMeetingRoom();
    void startDirectCall(target, kind, dedupe.id, controller.signal).then(detail => {
      if (controller.signal.aborted) return;
      retry.current = null;
      wantedHere.current.add(detail.id);
      remember(detail, detail);
      if (records.current.get(detail.id)?.change.state === 'accepted') loadCall(detail);
    }).catch(cause => {
      if (!controller.signal.aborted) {
        if (cause instanceof CallRequestError && cause.status < 500) retry.current = null;
        setError(cause instanceof Error ? cause.message : 'Unable to start the call.');
      }
    }).finally(() => {
      if (starting.current === controller) {
        starting.current = null;
        if (!controller.signal.aborted) setSending(null);
      }
    });
    return true;
  }, [canStart, loadCall, remember]);

  async function act(call: DirectCall, action: CallAction) {
    if (actions.current.has(call.id)) return;
    const controller = new AbortController();
    actions.current.set(call.id, controller);
    setBusyCalls([...actions.current.keys()]);
    setError('');
    if (action === 'accept') preloadMeetingRoom();
    try {
      const detail = await updateDirectCall(call, action, controller.signal);
      if (controller.signal.aborted) return;
      if (action === 'accept' && detail.canJoinMedia) wantedHere.current.add(call.id);
      remember(detail, detail);
    } catch (cause) {
      if (controller.signal.aborted) return;
      if (cause instanceof CallRequestError && cause.status === 409) loadCall(call);
      else if (cause instanceof CallRequestError && [403, 404].includes(cause.status)) removeAccess(call.id);
      else setError(cause instanceof Error ? cause.message : 'Unable to update the call.');
    } finally {
      if (actions.current.get(call.id) === controller) {
        actions.current.delete(call.id);
        if (!controller.signal.aborted) setBusyCalls([...actions.current.keys()]);
      }
    }
  }

  const joinMedia = useCallback((call: DirectCall) => {
    if (!call.meetingId || openedHere.current.has(call.id)) return;
    openedHere.current.add(call.id);
    openMedia({
      kind: 'directCall', callId: call.id, callKind: call.kind, meetingId: call.meetingId,
      organizationId: call.organizationId, title: call.callerId === userId ? call.calleeName : call.callerName,
      onEnded: detail => remember(detail, detail)
    });
  }, [openMedia, remember, userId]);

  useEffect(() => {
    if (mediaActive) return;
    const row = rows.find(record => !openedHere.current.has(record.change.id) &&
      shouldJoinCallMedia(record, wantedHere.current.has(record.change.id)));
    if (row?.detail) joinMedia(row.detail);
  }, [joinMedia, mediaActive, rows]);

  const resume = !mediaActive && !ring && rows.find(record =>
    !openedHere.current.has(record.change.id) && shouldJoinCallMedia(record, true)) || null;
  const value = useMemo(() => ({ start, receive, available, canStart, historyRevision }),
    [start, receive, available, canStart, historyRevision]);
  const title = incoming ? `Incoming ${incoming.kind.toLowerCase()} call` : outgoing || sending
    ? `Calling ${outgoing?.calleeName ?? sending}` : 'Call in progress';

  return (
    <DirectCallContext.Provider value={value}>
      {children}
      {(ring || sending || resume || error || notice) && createPortal(
        <div className="nx-call-alerts">
          {(ring || sending || resume) && (
            <section className="nx-call-prompt" aria-label={title}>
              <div className="nx-call-heading" role="status">
                {(ring?.kind ?? resume?.detail?.kind) === 'Video' ? <Video size={22} aria-hidden="true" /> : <Phone size={22} aria-hidden="true" />}
                <div><h2>{title}</h2>
                  {incoming && <p>{incoming.callerName}</p>}
                  {outgoing && !incoming && <p>{outgoing.kind} call</p>}
                  {sending && !ring && <p>Starting call…</p>}
                </div>
              </div>
              <div className="nx-call-actions">
                {incoming && (
                  <>
                    <Button variant="primary" disabled={mediaActive || busyCalls.includes(incoming.id)}
                      onPointerEnter={preloadMeetingRoom} onFocus={preloadMeetingRoom}
                      onClick={() => void act(incoming, 'accept')}>Accept call</Button>
                    <Button variant="danger" disabled={busyCalls.includes(incoming.id)}
                      onClick={() => void act(incoming, 'decline')}>Decline call</Button>
                  </>
                )}
                {!incoming && outgoing && (
                  <Button variant="danger" disabled={busyCalls.includes(outgoing.id)}
                    onClick={() => void act(outgoing, 'cancel')}>Cancel call</Button>
                )}
                {resume?.detail && (
                  <Button variant="primary" onClick={() => joinMedia(resume.detail!)}>Resume call</Button>
                )}
              </div>
            </section>
          )}
          {notice && <ToastBar variant="ok" message={notice} onDismiss={() => setNotice('')} />}
          {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        </div>,
        document.body
      )}
    </DirectCallContext.Provider>
  );
}
