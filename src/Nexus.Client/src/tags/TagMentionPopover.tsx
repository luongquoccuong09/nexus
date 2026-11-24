import { RefObject, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AtSign, Search, Users } from 'lucide-react';
import { orgApiPath } from '../scope';
import { Badge, Button } from '../ui';
import { PeopleTagDto, PeopleTagPageResponse, insertTagMention } from './tagHelpers';

interface TagMentionPopoverProps {
  organizationId: string;
  communityId: string;
  disabled?: boolean;
  textareaRef: RefObject<HTMLTextAreaElement | null>;
  value: string;
  onChange: (nextValue: string) => void;
}

export function TagMentionPopover({
  organizationId,
  communityId,
  disabled = false,
  textareaRef,
  value,
  onChange
}: TagMentionPopoverProps) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [tags, setTags] = useState<PeopleTagDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const wrapRef = useRef<HTMLDivElement | null>(null);
  const triggerWrapRef = useRef<HTMLSpanElement | null>(null);
  const searchRef = useRef<HTMLInputElement | null>(null);
  const selectionRef = useRef<{ start: number; end: number }>({ start: value.length, end: value.length });

  const loadTags = useCallback(
    async (signal?: AbortSignal) => {
      setLoading(true);
      setError('');
      try {
        const url = orgApiPath(
          organizationId,
          `/api/communities/${communityId}/tags?includeOrgTags=true&limit=50`
        );
        const res = await window.fetch(url, { credentials: 'same-origin', signal });
        const data = (await res.json().catch(() => ({}))) as Partial<PeopleTagPageResponse> & {
          message?: string;
        };
        if (!res.ok) {
          if (!signal?.aborted) setError(data.message || 'Unable to load tags.');
          return;
        }
        if (!signal?.aborted) {
          setTags(Array.isArray(data.items) ? data.items : []);
        }
      } catch (err) {
        if ((err as Error).name !== 'AbortError') {
          setError('Unable to load tags.');
        }
      } finally {
        if (!signal?.aborted) setLoading(false);
      }
    },
    [organizationId, communityId]
  );

  // Close when switching community
  useEffect(() => {
    setOpen(false);
    setQuery('');
    setTags([]);
    setError('');
  }, [organizationId, communityId]);

  // Fetch on open only (zero eager polling)
  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    void loadTags(controller.signal);
    const raf = window.requestAnimationFrame(() => {
      searchRef.current?.focus();
    });
    return () => {
      window.cancelAnimationFrame(raf);
      controller.abort();
    };
  }, [open, loadTags]);

  // Outside click & Escape handling
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (e: PointerEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        setOpen(false);
        window.requestAnimationFrame(() => {
          triggerWrapRef.current?.querySelector('button')?.focus();
        });
      }
    };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown, true);
    };
  }, [open]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase().replace(/^@/, '');
    if (!q) return tags;
    return tags.filter(
      t => t.name.toLowerCase().includes(q) || t.slug.toLowerCase().includes(q)
    );
  }, [tags, query]);

  const handleSelectTag = (tag: PeopleTagDto) => {
    const result = insertTagMention(value, selectionRef.current.start, selectionRef.current.end, tag.slug);
    onChange(result.value);
    setOpen(false);
    setQuery('');
    window.requestAnimationFrame(() => {
      const el = textareaRef.current;
      if (el) {
        el.focus();
        el.setSelectionRange(result.selectionStart, result.selectionEnd);
      }
    });
  };

  const handleToggle = () => {
    if (!open) {
      const el = textareaRef.current;
      if (el) {
        selectionRef.current = {
          start: el.selectionStart ?? value.length,
          end: el.selectionEnd ?? value.length
        };
      } else {
        selectionRef.current = { start: value.length, end: value.length };
      }
      setQuery('');
    }
    setOpen(v => !v);
  };

  return (
    <div className="nx-tag-mention-wrap" ref={wrapRef}>
      <span ref={triggerWrapRef}>
        <Button
          type="button"
          variant={open ? 'primary' : 'ghost'}
          size="sm"
          disabled={disabled}
          aria-haspopup="dialog"
          aria-expanded={open}
          aria-label="Mention a tag"
          title="Insert @tag mention"
          onClick={handleToggle}
        >
          <AtSign size={14} aria-hidden="true" /> Tag
        </Button>
      </span>

      {open && (
        <div className="nx-tag-mention-popover" role="dialog" aria-label="Mention a tag">
          <div className="nx-search-input-wrap nx-tag-mention-search">
            <Search size={13} aria-hidden="true" />
            <input
              ref={searchRef}
              type="search"
              aria-label="Filter tags to mention"
              placeholder="Filter tags…"
              value={query}
              onChange={e => setQuery(e.target.value)}
              onKeyDown={e => {
                if (e.nativeEvent.isComposing || e.nativeEvent.keyCode === 229) return;
                if (e.key === 'Enter') {
                  e.preventDefault();
                  e.stopPropagation();
                  if (filtered.length > 0) {
                    handleSelectTag(filtered[0]);
                  }
                }
              }}
            />
          </div>

          <div className="nx-tag-mention-list" role="listbox" aria-label="Available tags">
            {loading && tags.length === 0 ? (
              <div className="nx-tag-mention-empty" role="status">Loading tags…</div>
            ) : error ? (
              <div className="nx-tag-mention-empty">
                <p className="nx-error" role="alert">{error}</p>
                <Button type="button" variant="ghost" size="sm" onClick={() => void loadTags()}>
                  Retry
                </Button>
              </div>
            ) : filtered.length === 0 ? (
              <div className="nx-tag-mention-empty">
                {tags.length === 0 ? 'No tags available yet.' : 'No matching tags.'}
              </div>
            ) : (
              filtered.map(tag => (
                <button
                  key={tag.id}
                  type="button"
                  role="option"
                  aria-selected={false}
                  className="nx-tag-mention-item"
                  onClick={() => handleSelectTag(tag)}
                >
                  <div className="nx-tag-mention-item-main">
                    <strong title={tag.name}>{tag.name}</strong>
                    <span className="nx-tag-mention-slug">@{tag.slug}</span>
                  </div>
                  <div className="nx-tag-mention-item-meta">
                    <Badge variant="role">
                      {tag.scope === 'organization' ? 'Org' : 'Community'}
                    </Badge>
                    <span className="nx-muted">
                      <Users size={11} aria-hidden="true" /> {tag.memberCount}
                    </span>
                  </div>
                </button>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}
