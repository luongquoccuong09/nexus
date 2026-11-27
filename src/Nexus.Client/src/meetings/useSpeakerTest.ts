import { useCallback, useEffect, useRef, useState } from 'react';

type AudioDeviceContext = AudioContext & { setSinkId?: (sinkId: string) => Promise<void> };

export function useSpeakerTest() {
  const current = useRef<AbortController | null>(null);
  const [status, setStatus] = useState<'idle' | 'playing' | 'complete'>('idle');
  const [error, setError] = useState('');
  const supported = typeof AudioContext === 'function';
  const canSelect = supported && typeof (AudioContext.prototype as AudioDeviceContext).setSinkId === 'function';

  const release = useCallback(() => {
    current.current?.abort();
    current.current = null;
  }, []);
  useEffect(() => release, [release]);

  function reset() {
    release();
    setStatus('idle');
    setError('');
  }

  async function play(deviceId: string) {
    reset();
    const controller = new AbortController();
    current.current = controller;
    const { signal } = controller;
    setStatus('playing');
    function fail(message: string) {
      if (signal.aborted) return;
      reset();
      setError(message);
    }
    try {
      const audio: AudioDeviceContext = new AudioContext();
      const timeout = window.setTimeout(() => fail('The speaker test could not start. Check your sound settings and try again.'), 5_000);
      signal.addEventListener('abort', () => {
        window.clearTimeout(timeout);
        if (audio.state !== 'closed') void audio.close().catch(() => {});
      }, { once: true });
      if (deviceId && !audio.setSinkId) {
        fail('This browser can only test the system default speaker.');
        return;
      }
      await Promise.all([audio.resume(), deviceId ? audio.setSinkId?.(deviceId) : undefined]);
      if (signal.aborted) return;
      const tone = audio.createOscillator();
      const volume = audio.createGain();
      const start = audio.currentTime;
      tone.frequency.setValueAtTime(440, start);
      tone.frequency.setValueAtTime(554, start + 0.23);
      tone.frequency.setValueAtTime(660, start + 0.46);
      volume.gain.setValueAtTime(0.001, start);
      volume.gain.exponentialRampToValueAtTime(0.06, start + 0.04);
      volume.gain.exponentialRampToValueAtTime(0.001, start + 0.69);
      tone.connect(volume).connect(audio.destination);
      signal.addEventListener('abort', () => {
        tone.disconnect();
        volume.disconnect();
      }, { once: true });
      tone.addEventListener('ended', () => {
        if (signal.aborted) return;
        release();
        setStatus('complete');
      }, { once: true });
      tone.start(start);
      tone.stop(start + 0.7);
    } catch (cause) {
      const permissionDenied = cause instanceof Error && cause.name === 'NotAllowedError';
      fail(permissionDenied
        ? 'Speaker permission was denied. Choose the system default or allow access and try again.'
        : 'Unable to test this speaker. Check your sound settings and try again.');
    }
  }

  return { supported, canSelect, status, error, play, release, reset };
}
