export type PreviewDevice = 'camera' | 'microphone';

export function previewConstraints(device: PreviewDevice, deviceId: string): MediaStreamConstraints {
  const selection = deviceId ? { deviceId: { exact: deviceId } } : true;
  return device === 'camera' ? { video: selection, audio: false } : { video: false, audio: selection };
}

export async function acquirePreviewStream(capture: () => Promise<MediaStream>, signal: AbortSignal) {
  if (signal.aborted) return null;
  const stream = await capture();
  const stop = () => { for (const track of stream.getTracks()) track.stop(); };
  if (signal.aborted) {
    stop();
    return null;
  }
  signal.addEventListener('abort', stop, { once: true });
  return stream;
}

export function previewError(device: PreviewDevice, cause: unknown) {
  const name = cause && typeof cause === 'object' && 'name' in cause ? cause.name : '';
  const label = device === 'camera' ? 'Camera' : 'Microphone';
  switch (name) {
    case 'NotAllowedError':
    case 'PermissionDeniedError':
      return `${label} permission was denied. Allow ${device} access in Nexus or your browser settings and try again.`;
    case 'NotFoundError':
    case 'DevicesNotFoundError':
      return `No ${device} was detected. Connect a ${device} and try again.`;
    case 'NotReadableError':
    case 'TrackStartError':
      return `${label} is in use or unavailable. Close other apps using it and try again.`;
    case 'OverconstrainedError':
      return `The selected ${device} is unavailable. Choose another device and try again.`;
    default:
      return `Unable to access your ${device}. Check your device and try again.`;
  }
}

export type PreviewPermission = 'granted' | 'denied' | 'prompt' | 'unknown';

export function permissionFromQuery(state: unknown): PreviewPermission {
  return state === 'granted' || state === 'denied' || state === 'prompt' ? state : 'unknown';
}

// Browsers without a camera or microphone permission query still report the outcome of a user-started capture.
export function permissionFromCapture(cause: unknown): PreviewPermission | null {
  if (cause === null) return 'granted';
  const name = cause && typeof cause === 'object' && 'name' in cause ? cause.name : '';
  return name === 'NotAllowedError' || name === 'PermissionDeniedError' ? 'denied' : null;
}

export function permissionStatus(device: PreviewDevice, state: PreviewPermission) {
  const label = device === 'camera' ? 'Camera' : 'Microphone';
  switch (state) {
    case 'granted': return `${label} access: Allowed`;
    case 'denied': return `${label} access: Blocked. Allow ${device} access in Nexus or your browser settings, then try again.`;
    case 'prompt': return `${label} access: Nexus will ask when you start the ${device === 'camera' ? 'preview' : 'test'}.`;
    default: return `${label} access: Checked when you start the ${device === 'camera' ? 'preview' : 'test'}.`;
  }
}

export function microphoneLevel(samples: Uint8Array) {
  let total = 0;
  for (const sample of samples) {
    const amplitude = (sample - 128) / 128;
    total += amplitude * amplitude;
  }
  return samples.length ? Math.min(100, Math.round(300 * Math.sqrt(total / samples.length))) : 0;
}

const labels: Record<MediaDeviceKind, string> = {
  videoinput: 'Camera', audioinput: 'Microphone', audiooutput: 'Speaker'
};

export function previewDeviceOptions(devices: readonly MediaDeviceInfo[], kind: MediaDeviceKind) {
  const options = new Map<string, { id: string; label: string }>();
  for (const device of devices) {
    if (device.kind !== kind || !device.deviceId || device.deviceId === 'default' || options.has(device.deviceId)) continue;
    options.set(device.deviceId, { id: device.deviceId, label: device.label || `${labels[kind]} ${options.size + 1}` });
  }
  return [...options.values()];
}
