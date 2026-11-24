import { KeyboardEvent, useEffect, useId, useRef, useState } from 'react';
import { BellOff, ChevronDown, Settings } from 'lucide-react';
import {
  AVAILABILITY_DURATIONS,
  AVAILABILITY_STATES,
  AvailabilityDto,
  AvailabilityState,
  availabilityToDotTone,
  formatAvailabilityExpiry,
  formatAvailabilityLabel,
  getExpiryDelayMs,
  isChimeSuppressed,
  normalizeAvailability,
  resolveEffectiveAvailability
} from './availability';
import { protectedRequest } from './scope';
import { Avatar, Button } from './ui';

export interface AvailabilityUpdateResult {
  availability: AvailabilityDto;
  isOnline: boolean;
  suppressChime: boolean;
}

export interface AvailabilityPickerProps {
  availability: AvailabilityDto;
  isOnline: boolean;
  nowMs?: number;
  onUpdated: (next: AvailabilityUpdateResult) => void;
  variant?: 'inline' | 'popover';
  popoverPlacement?: 'rail' | 'topbar';
  displayName?: string;
  username?: string;
  avatarUrl?: string | null;
  avatarCacheKey?: number;
  onOpenProfileSettings?: () => void;
}

const STATE_DESCRIPTIONS: Record<AvailabilityState, string> = {
  Available: 'Show when you are online and ready to respond.',
  Busy: 'Let others know you are focused or in a meeting.',
  Away: 'Show that you stepped away for a bit.',
  DoNotDisturb: 'Pause push notifications and chimes while keeping Activity updates.'
};

export function AvailabilityPicker({
  availability,
  isOnline,
  nowMs = Date.now(),
  onUpdated,
  variant = 'inline',
  popoverPlacement = 'topbar',
  displayName = '',
  username = '',
  avatarUrl = null,
  avatarCacheKey,
  onOpenProfileSettings
}: AvailabilityPickerProps) {
  const [open, setOpen] = useState(false);
  const [durationMinutes, setDurationMinutes] = useState<number | null>(null);
  const [inFlight, setInFlight] = useState(false);
  const [error, setError] = useState('');
  const [localNow, setLocalNow] = useState(nowMs);

  const containerRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const firstOptionRef = useRef<HTMLButtonElement | null>(null);
  const durationSelectId = useId();

  useEffect(() => {
    setLocalNow(nowMs);
  }, [nowMs]);

  // Single local timeout for automatic expiry reversion (zero polling)
  useEffect(() => {
    const delay = getExpiryDelayMs(availability, localNow);
    if (delay === null) return;
    const timer = window.setTimeout(() => {
      setLocalNow(Date.now());
    }, delay);
    return () => window.clearTimeout(timer);
  }, [availability, localNow]);

  useEffect(() => {
    if (!open || variant !== 'popover') return;
    requestAnimationFrame(() => firstOptionRef.current?.focus());

    const handleOutside = (ev: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(ev.target as Node)) {
        setOpen(false);
      }
    };
    const handleEsc = (ev: globalThis.KeyboardEvent) => {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        setOpen(false);
        triggerRef.current?.focus();
      }
    };
    document.addEventListener('mousedown', handleOutside);
    document.addEventListener('keydown', handleEsc);
    return () => {
      document.removeEventListener('mousedown', handleOutside);
      document.removeEventListener('keydown', handleEsc);
    };
  }, [open, variant]);

  const effective = resolveEffectiveAvailability(availability, localNow);
  const dotTone = availabilityToDotTone(effective.state, isOnline);
  const statusLabel = formatAvailabilityLabel(effective.state, isOnline);
  const expiryText = formatAvailabilityExpiry(effective.expiresAt, localNow);

  async function commitAvailability(nextState: AvailabilityState, nextDuration: number | null) {
    if (inFlight) return;
    setError('');
    setInFlight(true);

    const previous: AvailabilityUpdateResult = {
      availability: effective,
      isOnline,
      suppressChime: isChimeSuppressed(effective, localNow)
    };

    const optimisticExpires =
      nextState !== 'Available' && nextDuration !== null
        ? new Date(Date.now() + nextDuration * 60_000).toISOString()
        : null;
    const optimisticAvailability: AvailabilityDto = {
      state: nextState,
      expiresAt: optimisticExpires
    };

    onUpdated({
      availability: optimisticAvailability,
      isOnline,
      suppressChime: nextState === 'DoNotDisturb'
    });

    const payload: { state: AvailabilityState; durationMinutes?: number } =
      nextState === 'Available' || nextDuration === null
        ? { state: nextState }
        : { state: nextState, durationMinutes: nextDuration };

    try {
      const response = await protectedRequest('/api/people/me/availability', 'PUT', payload);
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        onUpdated(previous);
        setError(typeof data.message === 'string' && data.message ? data.message : 'Unable to update availability.');
        return;
      }

      const confirmed = normalizeAvailability({
        state: data.state ?? nextState,
        expiresAt: data.expiresAt ?? null
      });
      const confirmedOnline = typeof data.isOnline === 'boolean' ? data.isOnline : isOnline;
      const confirmedSuppress =
        typeof data.suppressChime === 'boolean'
          ? data.suppressChime
          : isChimeSuppressed(confirmed, Date.now());

      setLocalNow(Date.now());
      onUpdated({
        availability: confirmed,
        isOnline: confirmedOnline,
        suppressChime: confirmedSuppress
      });
    } catch {
      onUpdated(previous);
      setError('Unable to update availability. Check your connection and try again.');
    } finally {
      setInFlight(false);
    }
  }

  function handleStateClick(nextState: AvailabilityState) {
    if (nextState === 'Available') {
      setDurationMinutes(null);
      void commitAvailability('Available', null);
      return;
    }
    void commitAvailability(nextState, durationMinutes);
  }

  function handleDurationChange(rawValue: string) {
    const parsed = rawValue === '' ? null : Number(rawValue);
    const nextDuration = parsed !== null && Number.isFinite(parsed) ? parsed : null;
    setDurationMinutes(nextDuration);
    if (effective.state !== 'Available') {
      void commitAvailability(effective.state, nextDuration);
    }
  }

  function handlePopoverKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      setOpen(false);
      triggerRef.current?.focus();
    }
  }

  const controls = (
    <div className="nx-avail-body">
      <div className="nx-avail-current">
        <span className={`nx-dot ${dotTone}`} aria-hidden="true" />
        <strong className="nx-avail-current-label">{statusLabel}</strong>
        {expiryText && <span className="nx-muted nx-avail-expiry">· {expiryText}</span>}
      </div>

      <div className="nx-avail-options" role="radiogroup" aria-label="Availability status">
        {AVAILABILITY_STATES.map((state, index) => {
          const checked = effective.state === state;
          const optionTone = availabilityToDotTone(state, true);
          const optionLabel = formatAvailabilityLabel(state, true);
          return (
            <button
              key={state}
              ref={index === 0 ? firstOptionRef : undefined}
              type="button"
              role="radio"
              aria-checked={checked}
              disabled={inFlight}
              className={`nx-avail-option ${checked ? 'active' : ''}`}
              onClick={() => handleStateClick(state)}
            >
              <span className={`nx-dot ${optionTone}`} aria-hidden="true" />
              <span className="nx-avail-option-text">
                <span className="nx-avail-option-name">{optionLabel}</span>
                {variant === 'inline' && (
                  <span className="nx-avail-option-desc">{STATE_DESCRIPTIONS[state]}</span>
                )}
              </span>
            </button>
          );
        })}
      </div>

      {effective.state !== 'Available' && (
        <div className="nx-avail-duration-row">
          <label htmlFor={durationSelectId}>Clear status after</label>
          <select
            id={durationSelectId}
            className="nx-select"
            disabled={inFlight}
            value={durationMinutes === null ? '' : String(durationMinutes)}
            onChange={e => handleDurationChange(e.target.value)}
          >
            {AVAILABILITY_DURATIONS.map(opt => (
              <option key={opt.label} value={opt.minutes === null ? '' : String(opt.minutes)}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
      )}

      {effective.state === 'DoNotDisturb' && (
        <div className="nx-avail-dnd-note" role="status">
          <BellOff size={14} aria-hidden="true" />
          <span>Do not disturb pauses push notifications and chimes while keeping Activity updates.</span>
        </div>
      )}

      {error && (
        <div className="nx-error" role="alert" style={{ margin: 0 }}>
          {error}
        </div>
      )}
    </div>
  );

  if (variant === 'inline') {
    return controls;
  }

  return (
    <div className={`nx-avail-popover-wrap placement-${popoverPlacement}`} ref={containerRef}>
      {popoverPlacement === 'rail' ? (
        <button
          ref={triggerRef}
          type="button"
          className="nx-rail-avatar-btn"
          title={`${displayName} (@${username}) · ${statusLabel}${expiryText ? ` (${expiryText})` : ''}`}
          aria-label={`Account status: ${statusLabel}. Open availability selector`}
          aria-haspopup="dialog"
          aria-expanded={open}
          onClick={() => setOpen(v => !v)}
        >
          <Avatar
            displayName={displayName}
            avatarUrl={avatarUrl}
            cacheKey={avatarCacheKey}
            presence={dotTone}
            onRail
          />
        </button>
      ) : (
        <button
          ref={triggerRef}
          type="button"
          className="nx-avail-pill-btn"
          aria-label={`Availability: ${statusLabel}${expiryText ? `, ${expiryText}` : ''}. Change status`}
          aria-haspopup="dialog"
          aria-expanded={open}
          onClick={() => setOpen(v => !v)}
        >
          <span className={`nx-dot ${dotTone}`} aria-hidden="true" />
          <span className="nx-avail-pill-label">{statusLabel}</span>
          {expiryText && <span className="nx-avail-pill-expiry">{expiryText}</span>}
          <ChevronDown size={13} aria-hidden="true" />
        </button>
      )}

      {open && (
        <div
          className={`nx-avail-popover placement-${popoverPlacement}`}
          role="dialog"
          aria-label="Set availability"
          onKeyDown={handlePopoverKeyDown}
        >
          {displayName && (
            <div className="nx-avail-popover-head">
              <strong className="nx-avail-popover-name">{displayName}</strong>
              {username && <span className="nx-muted">@{username}</span>}
            </div>
          )}
          {controls}
          {onOpenProfileSettings && (
            <div className="nx-avail-popover-foot">
              <Button
                variant="ghost"
                size="sm"
                fullWidth
                onClick={() => {
                  setOpen(false);
                  onOpenProfileSettings();
                }}
              >
                <Settings size={14} aria-hidden="true" /> Profile &amp; security
              </Button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
