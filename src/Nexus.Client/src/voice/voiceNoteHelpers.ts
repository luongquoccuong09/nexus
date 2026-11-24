export const MAX_VOICE_NOTE_DURATION_MS = 120_000;
export const MAX_VOICE_NOTE_BYTES = 5 * 1024 * 1024;

export const PREFERRED_AUDIO_MIME_TYPES = [
  'audio/webm;codecs=opus',
  'audio/webm',
  'audio/ogg;codecs=opus',
  'audio/ogg',
  'audio/mp4'
] as const;

export function normalizeAudioMimeType(mimeType: string | null | undefined): string {
  if (!mimeType) return '';
  return mimeType.split(';')[0].trim().toLowerCase();
}

export function selectSupportedAudioMimeType(
  isTypeSupported?: (mime: string) => boolean
): string | null {
  if (typeof isTypeSupported !== 'function') return null;
  for (const candidate of PREFERRED_AUDIO_MIME_TYPES) {
    try {
      if (isTypeSupported(candidate)) return candidate;
    } catch {
      // Ignore probe errors and try next candidate.
    }
  }
  return null;
}

export function voiceNoteFileExtension(mimeType: string | null | undefined): '.webm' | '.ogg' | '.m4a' {
  const base = normalizeAudioMimeType(mimeType);
  if (base === 'audio/ogg') return '.ogg';
  if (base === 'audio/mp4' || base === 'audio/x-m4a' || base === 'audio/aac') return '.m4a';
  return '.webm';
}

export function buildVoiceNoteFilename(mimeType: string | null | undefined, now: Date = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  const stamp =
    `${now.getUTCFullYear()}${pad(now.getUTCMonth() + 1)}${pad(now.getUTCDate())}-` +
    `${pad(now.getUTCHours())}${pad(now.getUTCMinutes())}${pad(now.getUTCSeconds())}`;
  return `voice-note-${stamp}${voiceNoteFileExtension(mimeType)}`;
}

export function formatVoiceDuration(durationMs: number | null | undefined): string {
  if (typeof durationMs !== 'number' || !Number.isFinite(durationMs) || durationMs <= 0) {
    return '0:00';
  }
  const totalSeconds = Math.max(0, Math.round(durationMs / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

export function mapMicrophoneError(err: unknown): string {
  const name =
    err && typeof err === 'object' && 'name' in err && typeof (err as { name?: unknown }).name === 'string'
      ? (err as { name: string }).name
      : '';

  switch (name) {
    case 'NotAllowedError':
    case 'PermissionDeniedError':
      return 'Microphone permission was denied. Allow microphone access in your browser settings and try again.';
    case 'NotFoundError':
    case 'DevicesNotFoundError':
      return 'No microphone was detected. Connect a microphone and try again.';
    case 'NotReadableError':
    case 'TrackStartError':
      return 'Your microphone is currently in use by another app. Close it and try again.';
    case 'OverconstrainedError':
      return 'No compatible microphone was found for audio recording.';
    case 'SecurityError':
      return 'Voice recording requires a secure (HTTPS or localhost) connection.';
    case 'UnsupportedBrowser':
      return 'Voice recording is not supported in this browser.';
    default:
      return 'Unable to access your microphone. Check your device permissions and try again.';
  }
}
