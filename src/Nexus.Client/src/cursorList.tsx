import { useCallback, useEffect, useRef, useState } from 'react';
import { Button } from './ui';

export interface CursorPage<T, Cursor = string> {
  items: T[];
  next: Cursor | null;
}

class PageError extends Error {
  constructor(message: string, readonly status: number) { super(message); }
}

export async function readCursorPage<T>(response: Response, message: string): Promise<CursorPage<T>> {
  if (!response.ok) throw new PageError(message, response.status);
  const page: { items: T[]; nextCursor?: string | null; nextAfter?: string | null } = await response.json();
  return { items: page.items, next: page.nextCursor ?? page.nextAfter ?? null };
}

interface ListState<T, Cursor> extends CursorPage<T, Cursor> {
  key: string | null;
  pages: number;
  loading: boolean;
  error: { message: string; append: boolean } | null;
}

export function useCursorList<T, Cursor = string>(
  key: string | null,
  fetchPage: (cursor: Cursor | null, signal: AbortSignal) => Promise<CursorPage<T, Cursor>>,
  itemKey: (item: T) => string,
  delay = 0
) {
  const [state, setState] = useState<ListState<T, Cursor>>({ key: null, items: [], next: null, pages: 0, loading: false, error: null });
  const current = useRef(state);
  const liveKey = useRef(key);
  const request = useRef<AbortController | null>(null);
  const fetchRef = useRef(fetchPage);
  const itemKeyRef = useRef(itemKey);
  liveKey.current = key;
  fetchRef.current = fetchPage;
  itemKeyRef.current = itemKey;

  const load = useCallback(async (append = false) => {
    if (key === null || liveKey.current !== key) return;
    const previous = current.current.key === key ? current.current : { key, items: [], next: null, pages: 0, loading: false, error: null };
    if (append && (previous.next === null || previous.loading)) return;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    current.current = { ...previous, loading: true, error: null };
    setState(current.current);

    const read = fetchRef.current;
    const id = itemKeyRef.current;
    const items = new Map((append ? previous.items : []).map(item => [id(item), item]));
    const pagesToRead = append ? 1 : Math.max(1, previous.pages);
    let next = append ? previous.next : null;
    let pages = 0;
    try {
      do {
        const page = await read(next, controller.signal);
        for (const item of page.items) items.set(id(item), item);
        next = page.next;
        pages++;
      } while (next !== null && pages < pagesToRead);
      if (controller.signal.aborted || liveKey.current !== key) return;
      current.current = { key, items: [...items.values()], next, pages: append ? previous.pages + pages : pages, loading: false, error: null };
      setState(current.current);
      return current.current.items;
    } catch (error) {
      if (controller.signal.aborted || liveKey.current !== key) return;
      const lostAccess = error instanceof PageError && [401, 403, 404].includes(error.status);
      current.current = {
        ...previous,
        ...(lostAccess ? { items: [], next: null, pages: 0 } : {}),
        loading: false,
        error: { message: error instanceof Error ? error.message : 'Unable to load the list.', append: !lostAccess && append }
      };
      setState(current.current);
    } finally {
      if (request.current === controller) request.current = null;
    }
  }, [key]);

  useEffect(() => {
    if (key === null) return;
    const timer = window.setTimeout(() => void load(), delay);
    return () => {
      window.clearTimeout(timer);
      request.current?.abort();
    };
  }, [delay, key, load]);

  const view = key !== null && state.key === key ? state : { items: [], next: null, loading: key !== null, error: null };
  const refresh = useCallback(() => load(), [load]);
  const loadMore = useCallback(() => load(true), [load]);
  const retry = useCallback(() => load(current.current.error?.append ?? false), [load]);
  const updateItems = useCallback((updater: (items: T[]) => T[]) => {
    const nextItems = updater(current.current.items);
    current.current = { ...current.current, items: nextItems };
    setState(current.current);
  }, []);
  return { ...view, refresh, loadMore, retry, updateItems };
}

export function ListPaging({ loading, error, hasMore, onRetry, onMore, moreLabel = 'Load more' }: {
  loading: boolean;
  error: { message: string } | null;
  hasMore: boolean;
  onRetry: () => void;
  onMore: () => void;
  moreLabel?: string;
}) {
  if (!error && !hasMore) return null;
  return (
    <div className="nx-list-paging">
      {error && <p className="nx-error" role="alert">{error.message}</p>}
      <Button size="sm" disabled={loading} onClick={error ? onRetry : onMore}>
        {loading ? 'Loading…' : error ? 'Retry' : moreLabel}
      </Button>
    </div>
  );
}
