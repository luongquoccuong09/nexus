import { useCallback, useEffect, useRef, useState } from 'react';
import {
  acquirePreviewStream, microphoneLevel, permissionFromCapture, permissionFromQuery, previewConstraints, type PreviewDevice,
  previewError, type PreviewPermission
} from './preJoinCapture';

function attachMicrophoneMeter(stream: MediaStream, audio: AudioContext, signal: AbortSignal, update: (value: number) => void) {
  const source = audio.createMediaStreamSource(stream);
  const analyser = audio.createAnalyser();
  analyser.fftSize = 256;
  source.connect(analyser);
  const samples = new Uint8Array(analyser.fftSize);
  const timer = window.setInterval(() => {
    analyser.getByteTimeDomainData(samples);
    update(microphoneLevel(samples));
  }, 90);
  signal.addEventListener('abort', () => {
    window.clearInterval(timer);
    source.disconnect();
    analyser.disconnect();
  }, { once: true });
}

export function usePreJoinCapture(device: PreviewDevice, onDevicesChanged: () => void) {
  const capture = useRef<AbortController | null>(null);
  const [stream, setStream] = useState<MediaStream | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [level, setLevel] = useState(0);
  const [meterUnavailable, setMeterUnavailable] = useState(false);
  const [permission, setPermission] = useState<PreviewPermission>('unknown');

  useEffect(() => {
    let disposed = false;
    let status: PermissionStatus | null = null;
    const update = () => { if (status && !disposed) setPermission(permissionFromQuery(status.state)); };
    // Firefox and older WebView2 builds can reject camera or microphone queries; capture results fill the state instead.
    let query: Promise<PermissionStatus> | undefined;
    try {
      query = navigator.permissions?.query({ name: device as PermissionName });
    } catch {
      query = undefined;
    }
    query?.then(result => {
      if (disposed) return;
      status = result;
      update();
      result.addEventListener('change', update);
    }).catch(() => {});
    return () => {
      disposed = true;
      status?.removeEventListener('change', update);
    };
  }, [device]);

  function recordCapture(cause: unknown) {
    const observed = permissionFromCapture(cause);
    if (observed) setPermission(observed);
  }

  const release = useCallback(() => {
    capture.current?.abort();
    capture.current = null;
  }, []);
  useEffect(() => release, [release]);

  function stop(message = '') {
    release();
    setStream(null);
    setLoading(false);
    setError(message);
    setLevel(0);
    setMeterUnavailable(false);
  }

  async function start(deviceId: string) {
    stop();
    const media = navigator.mediaDevices;
    if (!media?.getUserMedia) {
      setError('Device previews require a secure connection in a supported browser.');
      return;
    }
    const controller = new AbortController();
    capture.current = controller;
    const { signal } = controller;
    setLoading(true);
    let audio: AudioContext | null = null;
    if (device === 'microphone') {
      try {
        audio = new AudioContext();
        const context = audio;
        signal.addEventListener('abort', () => { void context.close().catch(() => {}); }, { once: true });
        void audio.resume().catch(() => { if (!signal.aborted) setMeterUnavailable(true); });
      } catch {
        setMeterUnavailable(true);
      }
    }
    try {
      const next = await acquirePreviewStream(() => media.getUserMedia(previewConstraints(device, deviceId)), signal);
      if (!next || signal.aborted) return;
      const tracks = device === 'camera' ? next.getVideoTracks() : next.getAudioTracks();
      if (!tracks.some(track => track.readyState === 'live')) {
        stop(`No ${device} was detected. Connect a ${device} and try again.`);
        return;
      }
      for (const track of tracks) {
        track.addEventListener('ended', () => {
          stop(`The ${device} disconnected. Select an available device and try again.`);
          onDevicesChanged();
        }, { once: true, signal });
      }
      if (audio) {
        try {
          attachMicrophoneMeter(next, audio, signal, setLevel);
        } catch {
          setMeterUnavailable(true);
        }
      }
      recordCapture(null);
      setStream(next);
      onDevicesChanged();
    } catch (cause) {
      if (signal.aborted) return;
      recordCapture(cause);
      stop(previewError(device, cause));
    } finally {
      if (!signal.aborted) setLoading(false);
    }
  }

  return { stream, loading, error, level, meterUnavailable, permission, start, stop, release };
}
