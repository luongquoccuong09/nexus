/**
 * Timed availability resolution, formatting, and expiry helpers (M6.28).
 * Pure, deterministic helpers — zero polling; clients schedule a single local
 * setTimeout at expiresAt using getExpiryDelayMs / getEarliestExpiryDelayMs.
 */

export type AvailabilityState = 'Available' | 'Busy' | 'Away' | 'DoNotDisturb';

export type AvailabilityDotTone = 'available' | 'busy' | 'away' | 'dnd' | 'offline';

export interface AvailabilityDto {
  state: AvailabilityState;
  expiresAt: string | null;
}

export interface LiveAvailabilityEntry {
  isOnline: boolean;
  availability: AvailabilityDto;
  updatedAt?: string;
}

export interface AvailabilityChangedEvent {
  userId: string;
  availability: AvailabilityDto;
  isOnline: boolean;
  updatedAt: string;
}

export interface AvailabilityDurationOption {
  label: string;
  minutes: number | null;
}

export interface ResolvedUserAvailability {
  isOnline: boolean;
  availability: AvailabilityDto;
  dotTone: AvailabilityDotTone;
  label: string;
  expiryText: string | null;
}

export const AVAILABILITY_STATES: readonly AvailabilityState[] = [
  'Available',
  'Busy',
  'Away',
  'DoNotDisturb'
];

export const AVAILABILITY_DURATIONS: readonly AvailabilityDurationOption[] = [
  { label: 'Until I change it', minutes: null },
  { label: '30 minutes', minutes: 30 },
  { label: '1 hour', minutes: 60 },
  { label: '2 hours', minutes: 120 },
  { label: '8 hours', minutes: 480 },
  { label: '24 hours', minutes: 1440 }
];

const MAX_TIMEOUT_MS = 2_147_483_647;

export function isAvailabilityState(value: unknown): value is AvailabilityState {
  return value === 'Available' || value === 'Busy' || value === 'Away' || value === 'DoNotDisturb';
}

export function normalizeAvailability(availability?: Partial<AvailabilityDto> | null): AvailabilityDto {
  const state: AvailabilityState = isAvailabilityState(availability?.state) ? availability.state : 'Available';
  if (state === 'Available') {
    return { state: 'Available', expiresAt: null };
  }
  const rawExpires = typeof availability?.expiresAt === 'string' && availability.expiresAt.trim().length > 0
    ? availability.expiresAt
    : null;
  if (!rawExpires || !Number.isFinite(Date.parse(rawExpires))) {
    return { state, expiresAt: null };
  }
  return { state, expiresAt: rawExpires };
}

/**
 * Resolves the effective availability at nowMs, automatically reverting to Available
 * once expiresAt <= nowMs without requiring server polling.
 */
export function resolveEffectiveAvailability(
  availability?: Partial<AvailabilityDto> | null,
  nowMs: number = Date.now()
): AvailabilityDto {
  const normalized = normalizeAvailability(availability);
  if (normalized.state === 'Available' || !normalized.expiresAt) {
    return normalized;
  }
  const expiresMs = Date.parse(normalized.expiresAt);
  if (!Number.isFinite(expiresMs) || expiresMs <= nowMs) {
    return { state: 'Available', expiresAt: null };
  }
  return normalized;
}

/**
 * Computes the delay (in milliseconds) until an active availability expires,
 * or null if no future expiry timer is needed.
 */
export function getExpiryDelayMs(
  availability?: Partial<AvailabilityDto> | null,
  nowMs: number = Date.now()
): number | null {
  const effective = resolveEffectiveAvailability(availability, nowMs);
  if (effective.state === 'Available' || !effective.expiresAt) {
    return null;
  }
  const expiresMs = Date.parse(effective.expiresAt);
  if (!Number.isFinite(expiresMs) || expiresMs <= nowMs) {
    return null;
  }
  return Math.min(MAX_TIMEOUT_MS, Math.max(50, expiresMs - nowMs + 50));
}

/**
 * Finds the shortest delay until the next expiry across a collection of availability records.
 */
export function getEarliestExpiryDelayMs(
  availabilities: Iterable<Partial<AvailabilityDto> | null | undefined>,
  nowMs: number = Date.now()
): number | null {
  let earliest: number | null = null;
  for (const item of availabilities) {
    const delay = getExpiryDelayMs(item, nowMs);
    if (delay !== null && (earliest === null || delay < earliest)) {
      earliest = delay;
    }
  }
  return earliest;
}

export function formatAvailabilityLabel(state: AvailabilityState, isOnline: boolean): string {
  switch (state) {
    case 'DoNotDisturb':
      return 'Do not disturb';
    case 'Busy':
      return 'Busy';
    case 'Away':
      return 'Away';
    case 'Available':
    default:
      return isOnline ? 'Available' : 'Offline';
  }
}

export function availabilityToDotTone(state: AvailabilityState, isOnline: boolean): AvailabilityDotTone {
  switch (state) {
    case 'DoNotDisturb':
      return 'dnd';
    case 'Busy':
      return 'busy';
    case 'Away':
      return 'away';
    case 'Available':
    default:
      return isOnline ? 'available' : 'offline';
  }
}

/**
 * Formats an active future expiry timestamp (e.g. "Until 14:30" or "Until Nov 25, 14:30").
 */
export function formatAvailabilityExpiry(
  expiresAt: string | null | undefined,
  nowMs: number = Date.now(),
  locale?: string
): string | null {
  if (!expiresAt) return null;
  const targetMs = Date.parse(expiresAt);
  if (!Number.isFinite(targetMs) || targetMs <= nowMs) return null;

  const target = new Date(targetMs);
  const now = new Date(nowMs);
  const sameDay =
    target.getFullYear() === now.getFullYear() &&
    target.getMonth() === now.getMonth() &&
    target.getDate() === now.getDate();

  const timePart = target.toLocaleTimeString(locale, {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false
  });

  if (sameDay) {
    return `Until ${timePart}`;
  }

  const datePart = target.toLocaleDateString(locale, {
    month: 'short',
    day: 'numeric'
  });
  return `Until ${datePart}, ${timePart}`;
}

export function isChimeSuppressed(
  availability?: Partial<AvailabilityDto> | null,
  nowMs: number = Date.now()
): boolean {
  return resolveEffectiveAvailability(availability, nowMs).state === 'DoNotDisturb';
}

export function resolveUserAvailability(
  userId: string | null | undefined,
  fallback: { isOnline?: boolean; availability?: Partial<AvailabilityDto> | null },
  liveMap?: Record<string, LiveAvailabilityEntry>,
  nowMs: number = Date.now()
): ResolvedUserAvailability {
  const live = userId && liveMap ? liveMap[userId] : undefined;
  const isOnline = live ? live.isOnline : Boolean(fallback.isOnline);
  const rawAvailability = live ? live.availability : fallback.availability;
  const effective = resolveEffectiveAvailability(rawAvailability, nowMs);
  return {
    isOnline,
    availability: effective,
    dotTone: availabilityToDotTone(effective.state, isOnline),
    label: formatAvailabilityLabel(effective.state, isOnline),
    expiryText: formatAvailabilityExpiry(effective.expiresAt, nowMs)
  };
}
