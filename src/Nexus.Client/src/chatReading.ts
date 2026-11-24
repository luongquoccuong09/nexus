import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';

interface ScrollPosition {
  followEnd: boolean;
  top: number;
  anchor?: { seq: string; offset: number };
}

const bottomThreshold = 32;
const readDelay = 120;

export function useChatReading(
  conversationId: string | null,
  visible: boolean,
  content: unknown,
  outgoingCount: number,
  lastReadSeq: number,
  markRead: (id: string, seq: number, manualUnreadVersion?: number) => Promise<number>,
  reconnectCount: number,
  manualUnread?: { fromSeq: number; version: number }
) {
  const feedRef = useRef<HTMLDivElement | null>(null);
  const positions = useRef(new Map<string, ScrollPosition>());
  const acknowledging = useRef(new Map<string, number>());
  const acknowledged = useRef(new Map<string, number>());
  const timer = useRef<number>(0);
  const requestFrame = useRef<number>(0);
  const [showNewMessages, setShowNewMessages] = useState(false);

  const measure = useCallback(() => {
    window.clearTimeout(timer.current);
    const feed = feedRef.current;
    if (!conversationId || !feed || !visible || document.visibilityState !== 'visible' || feed.clientHeight === 0) return;
    const bounds = feed.getBoundingClientRect();
    const children = feed.querySelectorAll<HTMLElement>('[data-message-seq]');
    const readThrough = Math.max(lastReadSeq, acknowledged.current.get(conversationId) ?? 0);
    let highestVisible = readThrough;
    let highestRendered = readThrough;
    let visibleSeq = 0;
    let anchor: ScrollPosition['anchor'];
    for (const element of children) {
      const rect = element.getBoundingClientRect();
      const seq = Number(element.dataset.messageSeq);
      if (!anchor && rect.bottom > bounds.top) anchor = { seq: String(seq), offset: rect.top - bounds.top };
      highestRendered = Math.max(highestRendered, seq);
      const overlap = Math.min(bounds.bottom, rect.bottom) - Math.max(bounds.top, rect.top);
      if (overlap >= Math.min(16, rect.height) && overlap > 0) {
        highestVisible = Math.max(highestVisible, seq);
        visibleSeq = Math.max(visibleSeq, seq);
      }
    }
    positions.current.set(conversationId, {
      followEnd: feed.scrollHeight - feed.clientHeight - feed.scrollTop <= bottomThreshold,
      top: feed.scrollTop,
      anchor
    });
    setShowNewMessages(highestRendered > highestVisible);
    const markerVersion = manualUnread && visibleSeq >= manualUnread.fromSeq ? manualUnread.version : undefined;
    if ((highestVisible <= readThrough && markerVersion === undefined) || acknowledging.current.has(conversationId)) return;

    timer.current = window.setTimeout(() => {
      if (document.visibilityState !== 'visible' || feedRef.current !== feed || feed.clientHeight === 0) return;
      acknowledging.current.set(conversationId, highestVisible);
      void markRead(conversationId, highestVisible, markerVersion)
        .then(confirmed => {
          acknowledged.current.set(conversationId, Math.max(confirmed, acknowledged.current.get(conversationId) ?? 0));
        })
        .catch(() => { /* Scroll, visibility, or reconnect checks will retry visible messages. */ })
        .finally(() => acknowledging.current.delete(conversationId));
    }, readDelay);
  }, [conversationId, lastReadSeq, manualUnread?.fromSeq, manualUnread?.version, markRead, visible]);

  const scheduleMeasure = useCallback(() => {
    window.cancelAnimationFrame(requestFrame.current);
    requestFrame.current = window.requestAnimationFrame(measure);
  }, [measure]);

  useLayoutEffect(() => {
    const feed = feedRef.current;
    if (!feed || !conversationId || !visible || document.visibilityState !== 'visible') return;
    const position = positions.current.get(conversationId);
    if (!position || position.followEnd) {
      feed.scrollTop = feed.scrollHeight;
    } else {
      const anchor = position.anchor && feed.querySelector(`[data-message-seq="${position.anchor.seq}"]`);
      if (anchor && position.anchor) {
        feed.scrollTop += anchor.getBoundingClientRect().top - feed.getBoundingClientRect().top - position.anchor.offset;
      } else {
        feed.scrollTop = position.top;
      }
    }
    scheduleMeasure();
    return () => {
      window.clearTimeout(timer.current);
      window.cancelAnimationFrame(requestFrame.current);
    };
  }, [content, conversationId, outgoingCount, reconnectCount, scheduleMeasure, visible]);

  useEffect(() => {
    const feed = feedRef.current;
    if (!visible || !feed || !conversationId) return;
    const onVisibility = () => {
      if (document.visibilityState === 'visible' && positions.current.get(conversationId)?.followEnd) {
        feed.scrollTop = feed.scrollHeight;
      }
      scheduleMeasure();
    };
    document.addEventListener('visibilitychange', onVisibility);
    const resize = new ResizeObserver(scheduleMeasure);
    resize.observe(feed);
    return () => {
      document.removeEventListener('visibilitychange', onVisibility);
      resize.disconnect();
    };
  }, [conversationId, scheduleMeasure, visible]);

  const showNewest = useCallback(() => {
    const feed = feedRef.current;
    const newest = Array.from(feed?.querySelectorAll<HTMLElement>('[data-message-seq]') ?? []).at(-1);
    if (!feed || !newest) return;
    feed.scrollTop += newest.getBoundingClientRect().bottom - feed.getBoundingClientRect().bottom;
    newest.focus({ preventScroll: true });
    scheduleMeasure();
  }, [scheduleMeasure]);

  return { feedRef, onScroll: scheduleMeasure, showNewMessages, showNewest };
}
