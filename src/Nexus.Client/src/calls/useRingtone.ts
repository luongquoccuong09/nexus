import { useEffect, useRef, useState } from 'react';

export function useRingtone(conversationId: string | null, suppressed: boolean) {
  const audioContext = useRef<AudioContext | null>(null);
  const [soundReady, setSoundReady] = useState(false);

  useEffect(() => {
    if (typeof AudioContext !== 'function') return;
    let disposed = false;
    function unlock() {
      if (audioContext.current?.state === 'running') return;
      try {
        const audio = audioContext.current ?? new AudioContext();
        audioContext.current = audio;
        void audio.resume().then(() => {
          if (!disposed) setSoundReady(audio.state === 'running');
        }).catch(() => {});
      } catch {
        // The visible incoming prompt remains available when audio cannot start.
      }
    }
    window.addEventListener('pointerdown', unlock);
    window.addEventListener('keydown', unlock);
    if (navigator.userActivation?.hasBeenActive) unlock();
    return () => {
      disposed = true;
      window.removeEventListener('pointerdown', unlock);
      window.removeEventListener('keydown', unlock);
      void audioContext.current?.close().catch(() => {});
      audioContext.current = null;
    };
  }, []);

  useEffect(() => {
    const audio = audioContext.current;
    if (!conversationId || suppressed || !soundReady || !audio) return;
    const request = new AbortController();
    let timer: number | undefined;
    const oscillators = new Set<OscillatorNode>();
    function chime() {
      if (!audio || audio.state !== 'running') return;
      for (const [delay, frequency] of [[0, 440], [0.18, 587]]) {
        const start = audio.currentTime + delay;
        const tone = audio.createOscillator();
        const volume = audio.createGain();
        tone.type = 'sine';
        tone.frequency.value = frequency;
        volume.gain.setValueAtTime(0.001, start);
        volume.gain.exponentialRampToValueAtTime(0.05, start + 0.03);
        volume.gain.exponentialRampToValueAtTime(0.001, start + 0.14);
        tone.connect(volume).connect(audio.destination);
        oscillators.add(tone);
        tone.onended = () => {
          oscillators.delete(tone);
          tone.disconnect();
          volume.disconnect();
        };
        tone.start(start);
        tone.stop(start + 0.15);
      }
    }
    const preference = `/api/notifications/conversations/${encodeURIComponent(conversationId)}/preference`;
    void fetch(preference, { signal: request.signal, cache: 'no-store' }).then(async response => {
      if (!response.ok) return;
      const state: { mode: string } = await response.json();
      if (request.signal.aborted || state.mode === 'muted') return;
      chime();
      timer = window.setInterval(chime, 2_700);
    }).catch(() => {});
    return () => {
      request.abort();
      window.clearInterval(timer);
      for (const tone of oscillators) tone.stop();
    };
  }, [conversationId, soundReady, suppressed]);
}
