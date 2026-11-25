import { useCallback, useEffect, useRef, useState } from 'react';
import type { CalendarEventTarget } from './calendar/calendarApi';
import { renderSafeMarkdown } from './markdown';
import { EmailDigestButton, QuietHoursButton } from './notificationPreferences';
import { protectedRequest } from './scope';
import { Badge, Button, Card, EmptyState, ToastBar } from './ui';
import { WebPushSettings } from './webPush';

interface ActivityItem {
  id: string;
  kind: string;
  createdAt: string;
  conversationId: string;
  organizationId: string | null;
  conversationTitle: string | null;
  isUnread: boolean;
}

interface ActivityCursor {
  createdAt: string;
  id: string;
}

interface ActivitySource {
  title: string | null;
  body: string;
  postId?: string | null;
  messageId?: string | null;
  seq?: number | null;
  targetLink?: string | null;
  calendarEventId?: string;
  occurrenceStartUtc?: string | null;
}

function activityLabel(kind: string): string {
  switch (kind) {
    case 'mention.message': return 'Mention in a chat message';
    case 'mention.post': return 'Mention in a post';
    case 'mention.comment': return 'Mention in a comment';
    case 'activity.message': return 'New chat message';
    case 'activity.post': return 'New post';
    case 'activity.comment': return 'New comment';
    case 'calendar.invitation': return 'Calendar invitation';
    case 'calendar.updated': return 'Calendar event updated';
    case 'calendar.canceled': return 'Calendar event canceled';
    case 'calendar.reminder': return 'Calendar reminder';
    default: return 'Announcement reminder';
  }
}

export function ActivityWorkspace({ onOpenChat, onOpenChannel, onOpenContentLink, onOpenCalendar, onRead }: {
  onOpenChat: (conversationId: string) => void;
  onOpenChannel: (conversationId: string) => void;
  onOpenContentLink: (token: string) => void;
  onOpenCalendar: (target: CalendarEventTarget) => void;
  onRead: () => void;
}) {
  const [items, setItems] = useState<ActivityItem[]>([]);
  const [next, setNext] = useState<ActivityCursor | null>(null);
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [openId, setOpenId] = useState<string | null>(null);
  const [source, setSource] = useState<ActivitySource | null>(null);
  const [readingId, setReadingId] = useState<string | null>(null);
  const request = useRef<AbortController | null>(null);
  const sourceRequest = useRef<AbortController | null>(null);

  const load = useCallback(async (cursor?: ActivityCursor, background = false) => {
    if (background && request.current) return;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    if (!background) {
      setLoading(true);
      setError('');
    }
    try {
      const params = new URLSearchParams({ limit: '30', unreadOnly: String(unreadOnly) });
      if (cursor) {
        params.set('beforeAt', cursor.createdAt);
        params.set('beforeId', cursor.id);
      }
      const response = await fetch(`/api/notifications?${params}`, { signal: controller.signal });
      if (!response.ok) throw new Error('Unable to load activity.');
      const data: { items: ActivityItem[]; nextBefore: ActivityCursor | null } = await response.json();
      if (controller.signal.aborted) return;
      setItems(current => {
        if (!cursor) return data.items;
        const byId = new Map(current.map(item => [item.id, item]));
        for (const item of data.items) byId.set(item.id, item);
        return [...byId.values()];
      });
      setNext(data.nextBefore);
    } catch {
      if (!controller.signal.aborted && !background) setError('Unable to load activity. Try refreshing.');
    } finally {
      if (request.current === controller) {
        request.current = null;
        setLoading(false);
      }
    }
  }, [unreadOnly]);

  useEffect(() => {
    setItems([]);
    setNext(null);
    setOpenId(null);
    setSource(null);
    void load();
    return () => {
      request.current?.abort();
      sourceRequest.current?.abort();
    };
  }, [load]);

  useEffect(() => {
    if (items.length > 30) return;
    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible') void load(undefined, true);
    }, 15000);
    return () => window.clearInterval(timer);
  }, [items.length, load]);

  useEffect(() => {
    if (!openId || !source) return;
    const controller = new AbortController();
    const timer = window.setInterval(async () => {
      if (document.visibilityState !== 'visible') return;
      try {
        const response = await fetch(`/api/notifications/${openId}/source`, { signal: controller.signal });
        if (!controller.signal.aborted && (response.status === 401 || response.status === 404)) {
          setSource(null);
          setOpenId(null);
          setItems(current => current.filter(item => item.id !== openId));
        }
      } catch {
        // Keep the source visible during a temporary connection failure.
      }
    }, 15000);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [openId, source]);

  async function markRead(item: ActivityItem) {
    setReadingId(item.id);
    setError('');
    try {
      const response = await protectedRequest(`/api/notifications/${item.id}/read`, 'PUT');
      if (!response.ok) throw new Error('Unable to mark activity as read.');
      setItems(current => unreadOnly
        ? current.filter(entry => entry.id !== item.id)
        : current.map(entry => entry.id === item.id ? { ...entry, isUnread: false } : entry));
      onRead();
    } catch {
      setError('Unable to mark activity as read. Try again.');
    } finally {
      setReadingId(null);
    }
  }

  async function showSource(item: ActivityItem) {
    sourceRequest.current?.abort();
    setSource(null);
    if (openId === item.id) {
      setOpenId(null);
      return;
    }
    setError('');
    setOpenId(item.id);
    const controller = new AbortController();
    sourceRequest.current = controller;
    try {
      const response = await fetch(`/api/notifications/${item.id}/source`, { signal: controller.signal });
      if (!response.ok) throw new Error('Activity source is unavailable.');
      const data = await response.json();
      if (!controller.signal.aborted) setSource(data);
    } catch {
      if (!controller.signal.aborted) {
        setOpenId(null);
        setError('Unable to open the activity source. It may no longer be available.');
      }
    }
  }

  return (
    <div className="nx-page-scroll">
      <section className="nx-page-container" aria-label="Notification center">
        <div className="nx-page-head">
          <label><input type="checkbox" checked={unreadOnly} onChange={event => setUnreadOnly(event.currentTarget.checked)} /> Unread only</label>
          <div className="nx-inline-actions">
            <WebPushSettings />
            <QuietHoursButton />
            <EmailDigestButton />
            <Button size="sm" onClick={() => void load()} disabled={loading}>Refresh</Button>
          </div>
        </div>
        {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        {loading && <p role="status">Loading activity…</p>}
        {!loading && !error && items.length === 0 && (
          <EmptyState
            title={unreadOnly ? 'No unread activity' : 'No activity yet'}
            description={unreadOnly ? 'Clear the filter to see earlier activity.' : 'Mentions, reminders, and calendar invitations will appear here.'}
          />
        )}
        {items.map(item => (
          <Card key={item.id} title={activityLabel(item.kind)}>
            <p className="nx-muted">
              {item.conversationTitle && <>{item.conversationTitle} · </>}
              <time dateTime={item.createdAt}>{new Date(item.createdAt).toLocaleString()}</time>
              {item.isUnread && <> · <Badge variant="role">Unread</Badge></>}
            </p>
            <Button size="sm" aria-expanded={openId === item.id} onClick={() => void showSource(item)}>
              {openId === item.id ? 'Hide source' : 'View source'}
            </Button>{' '}
            {item.isUnread && <Button size="sm" disabled={readingId !== null} onClick={() => void markRead(item)}>Mark as read</Button>}
            {openId === item.id && (source ? (
              <>
                {source.title && <h3>{source.title}</h3>}
                {source.calendarEventId ? <p className="nx-msg-body">{source.body}</p>
                  : <div className="nx-msg-body" dangerouslySetInnerHTML={{ __html: renderSafeMarkdown(source.body) }} />}
                {source.calendarEventId && (
                  <Button size="sm" onClick={() => onOpenCalendar({
                    eventId: source.calendarEventId!, organizationId: item.organizationId, occurrenceStartUtc: source.occurrenceStartUtc
                  })}>
                    Open calendar event
                  </Button>
                )}
                {(item.kind === 'mention.message' || item.kind === 'activity.message') && !item.organizationId && (
                  <Button
                    size="sm"
                    onClick={() => source.targetLink ? onOpenContentLink(source.targetLink) : onOpenChat(item.conversationId)}
                  >
                    Open chat
                  </Button>
                )}
                {!source.calendarEventId && item.kind !== 'mention.message' && item.kind !== 'activity.message' && (
                  <Button
                    size="sm"
                    onClick={() => source.targetLink ? onOpenContentLink(source.targetLink) : onOpenChannel(item.conversationId)}
                  >
                    Open feed
                  </Button>
                )}
              </>
            ) : <p role="status">Loading source…</p>)}
          </Card>
        ))}
        {next && <Button onClick={() => void load(next)} disabled={loading}>Load more</Button>}
      </section>
    </div>
  );
}
