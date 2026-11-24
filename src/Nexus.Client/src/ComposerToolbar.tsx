import {
  KeyboardEvent as ReactKeyboardEvent,
  RefObject,
  Suspense,
  lazy,
  useCallback,
  useEffect,
  useRef,
  useState
} from 'react';
import { Bold, Code, FileCode2, Italic, Link2, List, ListOrdered, Smile } from 'lucide-react';
import {
  FormatAction,
  TextRange,
  applyFormatAction,
  insertEmoji,
  insertLink,
  resolveComposerShortcut
} from './composerFormatting';
import { isSafeHttpUrl } from './markdown';
import { Button } from './ui';

const LazyEmojiPicker = lazy(() => import('./EmojiPicker'));

export interface ComposerToolbarProps {
  textareaRef: RefObject<HTMLTextAreaElement | null>;
  value: string;
  onChange: (nextValue: string) => void;
  disabled?: boolean;
  placement?: 'top' | 'bottom';
  openLinkSignal?: number;
  ariaLabel?: string;
}

interface SavedRange {
  start: number;
  end: number;
}

function restoreTextareaRange(textarea: HTMLTextAreaElement | null, start: number, end: number): void {
  if (!textarea) return;
  requestAnimationFrame(() => {
    textarea.focus();
    try {
      textarea.setSelectionRange(start, end);
    } catch {
      // Ignore selection restoration errors if textarea is no longer mounted.
    }
  });
}

export function handleComposerFormattingKeyDown(
  event: ReactKeyboardEvent<HTMLTextAreaElement>,
  compositionActive: boolean,
  value: string,
  onChange: (nextValue: string) => void,
  onOpenLink: () => void
): boolean {
  const action = resolveComposerShortcut(event.nativeEvent, compositionActive);
  if (!action) return false;
  event.preventDefault();

  if (action === 'link') {
    onOpenLink();
    return true;
  }

  const textarea = event.currentTarget;
  const next = applyFormatAction(
    {
      value,
      selectionStart: textarea.selectionStart ?? value.length,
      selectionEnd: textarea.selectionEnd ?? value.length
    },
    action
  );
  onChange(next.value);
  restoreTextareaRange(textarea, next.selectionStart, next.selectionEnd);
  return true;
}

export function ComposerToolbar({
  textareaRef,
  value,
  onChange,
  disabled = false,
  placement = 'top',
  openLinkSignal = 0,
  ariaLabel = 'Message formatting'
}: ComposerToolbarProps) {
  const [openPopover, setOpenPopover] = useState<'none' | 'link' | 'emoji'>('none');
  const [linkUrl, setLinkUrl] = useState('');
  const [linkLabel, setLinkLabel] = useState('');
  const [linkError, setLinkError] = useState('');

  const toolbarRef = useRef<HTMLDivElement | null>(null);
  const linkBtnWrapRef = useRef<HTMLSpanElement | null>(null);
  const emojiBtnWrapRef = useRef<HTMLSpanElement | null>(null);
  const linkUrlInputRef = useRef<HTMLInputElement | null>(null);
  const savedRangeRef = useRef<SavedRange>({ start: value.length, end: value.length });
  const lastLinkSignalRef = useRef(openLinkSignal);

  const captureCurrentRange = useCallback((): SavedRange => {
    const el = textareaRef.current;
    const range: SavedRange = el
      ? {
          start: el.selectionStart ?? value.length,
          end: el.selectionEnd ?? value.length
        }
      : { start: value.length, end: value.length };
    savedRangeRef.current = range;
    return range;
  }, [textareaRef, value.length]);

  const openLinkPopover = useCallback(() => {
    if (disabled) return;
    const range = captureCurrentRange();
    const selected = value.slice(range.start, range.end).trim();
    setLinkUrl('');
    setLinkLabel(selected);
    setLinkError('');
    setOpenPopover('link');
  }, [captureCurrentRange, disabled, value]);

  useEffect(() => {
    if (openLinkSignal !== lastLinkSignalRef.current) {
      lastLinkSignalRef.current = openLinkSignal;
      if (openLinkSignal > 0 && !disabled) {
        openLinkPopover();
      }
    }
  }, [disabled, openLinkPopover, openLinkSignal]);

  useEffect(() => {
    if (disabled && openPopover !== 'none') {
      setOpenPopover('none');
    }
  }, [disabled, openPopover]);

  useEffect(() => {
    if (openPopover === 'link') {
      requestAnimationFrame(() => linkUrlInputRef.current?.focus());
    }
  }, [openPopover]);

  useEffect(() => {
    if (openPopover === 'none') return;
    const handleOutside = (ev: MouseEvent) => {
      if (toolbarRef.current && !toolbarRef.current.contains(ev.target as Node)) {
        setOpenPopover('none');
      }
    };
    document.addEventListener('mousedown', handleOutside);
    return () => {
      document.removeEventListener('mousedown', handleOutside);
    };
  }, [openPopover]);

  function focusTriggerButton(kind: 'link' | 'emoji') {
    const wrap = kind === 'link' ? linkBtnWrapRef.current : emojiBtnWrapRef.current;
    const btn = wrap?.querySelector('button');
    requestAnimationFrame(() => {
      if (btn) btn.focus();
      else textareaRef.current?.focus();
    });
  }

  function applyToolbarFormat(action: FormatAction) {
    if (disabled) return;
    const el = textareaRef.current;
    const state: TextRange = {
      value,
      selectionStart: el?.selectionStart ?? value.length,
      selectionEnd: el?.selectionEnd ?? value.length
    };
    const next = applyFormatAction(state, action);
    setOpenPopover('none');
    onChange(next.value);
    restoreTextareaRange(el, next.selectionStart, next.selectionEnd);
  }

  function submitLink() {
    const trimmedUrl = linkUrl.trim();
    if (!isSafeHttpUrl(trimmedUrl)) {
      setLinkError('Enter a valid http:// or https:// URL.');
      linkUrlInputRef.current?.focus();
      return;
    }

    const range = savedRangeRef.current;
    const result = insertLink(
      {
        value,
        selectionStart: range.start,
        selectionEnd: range.end
      },
      trimmedUrl,
      linkLabel
    );

    if (!result.ok) {
      setLinkError(result.error);
      linkUrlInputRef.current?.focus();
      return;
    }

    setOpenPopover('none');
    setLinkUrl('');
    setLinkLabel('');
    setLinkError('');
    onChange(result.next.value);
    restoreTextareaRange(textareaRef.current, result.next.selectionStart, result.next.selectionEnd);
  }

  function handleLinkKeyDown(e: ReactKeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      setOpenPopover('none');
      focusTriggerButton('link');
      return;
    }
    if (
      e.key === 'Enter' &&
      (e.target as HTMLElement).tagName === 'INPUT' &&
      !e.nativeEvent.isComposing &&
      e.nativeEvent.keyCode !== 229
    ) {
      e.preventDefault();
      e.stopPropagation();
      submitLink();
    }
  }

  function handleEmojiToggle() {
    if (disabled) return;
    if (openPopover === 'emoji') {
      setOpenPopover('none');
      return;
    }
    captureCurrentRange();
    setOpenPopover('emoji');
  }

  function handleEmojiSelect(emoji: string) {
    const range = savedRangeRef.current;
    const next = insertEmoji(
      {
        value,
        selectionStart: range.start,
        selectionEnd: range.end
      },
      emoji
    );
    savedRangeRef.current = { start: next.selectionStart, end: next.selectionEnd };
    setOpenPopover('none');
    onChange(next.value);
    restoreTextareaRange(textareaRef.current, next.selectionStart, next.selectionEnd);
  }

  return (
    <div className="nx-composer-toolbar" ref={toolbarRef} role="toolbar" aria-label={ariaLabel}>
      <div className="nx-composer-toolbar-group">
        <Button
          variant="ghost"
          size="sm"
          iconOnly
          aria-label="Bold (Ctrl+B)"
          title="Bold (Ctrl+B)"
          disabled={disabled}
          onMouseDown={e => e.preventDefault()}
          onClick={() => applyToolbarFormat('bold')}
        >
          <Bold size={15} aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="sm"
          iconOnly
          aria-label="Italic (Ctrl+I)"
          title="Italic (Ctrl+I)"
          disabled={disabled}
          onMouseDown={e => e.preventDefault()}
          onClick={() => applyToolbarFormat('italic')}
        >
          <Italic size={15} aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="sm"
          iconOnly
          aria-label="Inline code (Ctrl+E)"
          title="Inline code (Ctrl+E)"
          disabled={disabled}
          onMouseDown={e => e.preventDefault()}
          onClick={() => applyToolbarFormat('inlineCode')}
        >
          <Code size={15} aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="sm"
          iconOnly
          aria-label="Code block"
          title="Code block"
          disabled={disabled}
          onMouseDown={e => e.preventDefault()}
          onClick={() => applyToolbarFormat('codeBlock')}
        >
          <FileCode2 size={15} aria-hidden="true" />
        </Button>
        <span className="nx-toolbar-sep" aria-hidden="true" />
        <Button
          variant="ghost"
          size="sm"
          iconOnly
          aria-label="Bullet list"
          title="Bullet list"
          disabled={disabled}
          onMouseDown={e => e.preventDefault()}
          onClick={() => applyToolbarFormat('bulletList')}
        >
          <List size={15} aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="sm"
          iconOnly
          aria-label="Numbered list"
          title="Numbered list"
          disabled={disabled}
          onMouseDown={e => e.preventDefault()}
          onClick={() => applyToolbarFormat('numberedList')}
        >
          <ListOrdered size={15} aria-hidden="true" />
        </Button>
        <span className="nx-toolbar-sep" aria-hidden="true" />
        <span ref={linkBtnWrapRef}>
          <Button
            variant={openPopover === 'link' ? 'primary' : 'ghost'}
            size="sm"
            iconOnly
            aria-label="Insert link (Ctrl+K)"
            title="Insert link (Ctrl+K)"
            aria-haspopup="dialog"
            aria-expanded={openPopover === 'link'}
            disabled={disabled}
            onMouseDown={e => e.preventDefault()}
            onClick={() => {
              if (openPopover === 'link') {
                setOpenPopover('none');
              } else {
                openLinkPopover();
              }
            }}
          >
            <Link2 size={15} aria-hidden="true" />
          </Button>
        </span>
        <span ref={emojiBtnWrapRef}>
          <Button
            variant={openPopover === 'emoji' ? 'primary' : 'ghost'}
            size="sm"
            iconOnly
            aria-label="Insert emoji"
            title="Insert emoji"
            aria-haspopup="dialog"
            aria-expanded={openPopover === 'emoji'}
            disabled={disabled}
            onMouseDown={e => e.preventDefault()}
            onClick={handleEmojiToggle}
          >
            <Smile size={15} aria-hidden="true" />
          </Button>
        </span>
      </div>

      {openPopover === 'link' && (
        <div
          className={`nx-composer-popover nx-link-popover placement-${placement}`}
          role="dialog"
          aria-label="Insert link"
          onKeyDown={handleLinkKeyDown}
        >
          <div className="nx-link-popover-title">Insert link</div>
          <label className="nx-link-popover-field">
            <span>URL</span>
            <input
              ref={linkUrlInputRef}
              type="url"
              placeholder="https://example.com"
              value={linkUrl}
              onChange={e => {
                setLinkUrl(e.target.value);
                if (linkError) setLinkError('');
              }}
            />
          </label>
          <label className="nx-link-popover-field">
            <span>Link text (optional)</span>
            <input
              type="text"
              placeholder="Display text"
              value={linkLabel}
              onChange={e => setLinkLabel(e.target.value)}
            />
          </label>
          {linkError && (
            <div className="nx-link-popover-error" role="alert">
              {linkError}
            </div>
          )}
          <div className="nx-link-popover-actions">
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                setOpenPopover('none');
                focusTriggerButton('link');
              }}
            >
              Cancel
            </Button>
            <Button size="sm" variant="primary" onClick={submitLink}>
              Insert link
            </Button>
          </div>
        </div>
      )}

      {openPopover === 'emoji' && (
        <div className={`nx-composer-popover nx-emoji-popover placement-${placement}`}>
          <Suspense
            fallback={
              <div className="nx-emoji-loading" role="status">
                Loading emoji…
              </div>
            }
          >
            <LazyEmojiPicker
              onSelect={handleEmojiSelect}
              onClose={() => {
                setOpenPopover('none');
                focusTriggerButton('emoji');
              }}
            />
          </Suspense>
        </div>
      )}
    </div>
  );
}
