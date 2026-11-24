import { useCallback, useEffect, useRef, useState } from 'react';
import { Mic, RotateCcw, Square, Trash2, Upload, X } from 'lucide-react';
import { formatBytes } from '../attachments/attachmentUtils';
import { Button } from '../ui';
import {
  MAX_VOICE_NOTE_BYTES,
  MAX_VOICE_NOTE_DURATION_MS,
  buildVoiceNoteFilename,
  formatVoiceDuration,
  mapMicrophoneError,
  normalizeAudioMimeType,
  selectSupportedAudioMimeType
} from './voiceNoteHelpers';

export type VoiceUploadResult = { ok: true } | { ok: false; error: string };

export interface VoiceNoteRecorderProps {
  onUpload: (file: File, durationMs: number) => Promise<VoiceUploadResult>;
  onClose: () => void;
  disabled?: boolean;
}

type RecorderPhase = 'idle' | 'recording' | 'preview' | 'uploading';

export default function VoiceNoteRecorder({
  onUpload,
  onClose,
  disabled = false
}: VoiceNoteRecorderProps) {
  const [phase, setPhase] = useState<RecorderPhase>('idle');
  const [elapsedMs, setElapsedMs] = useState(0);
  const [recordedBlob, setRecordedBlob] = useState<Blob | null>(null);
  const [recordedDurationMs, setRecordedDurationMs] = useState(0);
  const [recordedMime, setRecordedMime] = useState('audio/webm');
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [error, setError] = useState('');

  const mediaRecorderRef = useRef<MediaRecorder | null>(null);
  const mediaStreamRef = useRef<MediaStream | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const timerRef = useRef<number | null>(null);
  const startedAtRef = useRef<number>(0);
  const discardOnStopRef = useRef(false);
  const inFlightRef = useRef(false);
  const previewUrlRef = useRef<string | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const startBtnRef = useRef<HTMLSpanElement | null>(null);

  const isSupported =
    typeof window !== 'undefined' &&
    typeof MediaRecorder !== 'undefined' &&
    Boolean(navigator.mediaDevices?.getUserMedia);

  const stopTracks = useCallback(() => {
    if (timerRef.current !== null) {
      window.clearInterval(timerRef.current);
      timerRef.current = null;
    }
    const stream = mediaStreamRef.current;
    mediaStreamRef.current = null;
    if (stream) {
      for (const track of stream.getTracks()) {
        try {
          track.stop();
        } catch {
          // Ignore track stop errors during teardown.
        }
      }
    }
  }, []);

  const revokePreview = useCallback(() => {
    if (previewUrlRef.current) {
      URL.revokeObjectURL(previewUrlRef.current);
      previewUrlRef.current = null;
    }
    setPreviewUrl(null);
  }, []);

  const cancelActiveRecording = useCallback(() => {
    discardOnStopRef.current = true;
    const rec = mediaRecorderRef.current;
    mediaRecorderRef.current = null;
    if (rec && rec.state !== 'inactive') {
      try {
        rec.stop();
      } catch {
        // Ignore stop errors.
      }
    }
    stopTracks();
    chunksRef.current = [];
  }, [stopTracks]);

  // Focus Start button on open
  useEffect(() => {
    const raf = window.requestAnimationFrame(() => {
      startBtnRef.current?.querySelector('button')?.focus();
    });
    return () => window.cancelAnimationFrame(raf);
  }, []);

  // Always clean up active recording, MediaStream tracks, and preview URL on unmount
  useEffect(() => {
    return () => {
      cancelActiveRecording();
      revokePreview();
    };
  }, [cancelActiveRecording, revokePreview]);

  // Escape closes recorder cleanly and stops any active microphone tracks
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        if (inFlightRef.current) return;
        cancelActiveRecording();
        revokePreview();
        onClose();
      }
    };
    document.addEventListener('keydown', onKeyDown, true);
    return () => document.removeEventListener('keydown', onKeyDown, true);
  }, [cancelActiveRecording, revokePreview, onClose]);

  const handleStopRecording = useCallback(() => {
    discardOnStopRef.current = false;
    const rec = mediaRecorderRef.current;
    if (rec && rec.state !== 'inactive') {
      try {
        rec.stop();
      } catch {
        stopTracks();
        setPhase('idle');
      }
    } else {
      stopTracks();
      setPhase('idle');
    }
  }, [stopTracks]);

  const handleStartRecording = useCallback(async () => {
    if (disabled || inFlightRef.current) return;
    if (!isSupported) {
      setError(mapMicrophoneError({ name: 'UnsupportedBrowser' }));
      return;
    }
    setError('');
    revokePreview();
    setRecordedBlob(null);
    setRecordedDurationMs(0);

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      mediaStreamRef.current = stream;

      const selectedMime = selectSupportedAudioMimeType(mime =>
        typeof MediaRecorder.isTypeSupported === 'function' ? MediaRecorder.isTypeSupported(mime) : false
      );
      const recorder = selectedMime
        ? new MediaRecorder(stream, { mimeType: selectedMime })
        : new MediaRecorder(stream);

      mediaRecorderRef.current = recorder;
      chunksRef.current = [];
      discardOnStopRef.current = false;

      recorder.ondataavailable = (ev: BlobEvent) => {
        if (ev.data && ev.data.size > 0) {
          chunksRef.current.push(ev.data);
        }
      };

      recorder.onerror = () => {
        stopTracks();
        mediaRecorderRef.current = null;
        chunksRef.current = [];
        setError('Recording stopped unexpectedly. Try again.');
        setPhase('idle');
      };

      recorder.onstop = () => {
        stopTracks();
        mediaRecorderRef.current = null;
        const duration = Math.min(
          MAX_VOICE_NOTE_DURATION_MS,
          Math.max(500, Date.now() - startedAtRef.current)
        );
        const chunks = chunksRef.current;
        chunksRef.current = [];

        if (discardOnStopRef.current || chunks.length === 0) {
          setPhase('idle');
          setElapsedMs(0);
          return;
        }

        const effectiveMime = recorder.mimeType || selectedMime || 'audio/webm';
        const blob = new Blob(chunks, { type: effectiveMime });
        const url = URL.createObjectURL(blob);
        previewUrlRef.current = url;
        setRecordedBlob(blob);
        setRecordedDurationMs(duration);
        setRecordedMime(effectiveMime);
        setPreviewUrl(url);
        setElapsedMs(duration);
        setPhase('preview');
        if (blob.size > MAX_VOICE_NOTE_BYTES) {
          setError('Voice note exceeds the 5 MB size limit. Record a shorter clip.');
        }
      };

      startedAtRef.current = Date.now();
      setElapsedMs(0);
      setPhase('recording');
      recorder.start(250);

      timerRef.current = window.setInterval(() => {
        const nextElapsed = Date.now() - startedAtRef.current;
        if (nextElapsed >= MAX_VOICE_NOTE_DURATION_MS) {
          setElapsedMs(MAX_VOICE_NOTE_DURATION_MS);
          handleStopRecording();
        } else {
          setElapsedMs(nextElapsed);
        }
      }, 200);
    } catch (err) {
      stopTracks();
      setError(mapMicrophoneError(err));
      setPhase('idle');
    }
  }, [disabled, isSupported, revokePreview, stopTracks, handleStopRecording]);

  const handleCancelRecording = () => {
    cancelActiveRecording();
    setElapsedMs(0);
    setError('');
    setPhase('idle');
  };

  const handleDiscardPreview = () => {
    if (inFlightRef.current) return;
    revokePreview();
    setRecordedBlob(null);
    setRecordedDurationMs(0);
    setElapsedMs(0);
    setError('');
    setPhase('idle');
  };

  const handleAttachVoiceNote = async () => {
    if (!recordedBlob || inFlightRef.current || disabled) return;
    if (recordedBlob.size > MAX_VOICE_NOTE_BYTES) {
      setError('Voice note exceeds the 5 MB size limit. Record a shorter clip.');
      return;
    }

    inFlightRef.current = true;
    setPhase('uploading');
    setError('');

    const baseMime = normalizeAudioMimeType(recordedMime) || 'audio/webm';
    const fileName = buildVoiceNoteFilename(baseMime);
    const file = new File([recordedBlob], fileName, { type: baseMime });
    const clampedDuration = Math.max(1, Math.min(MAX_VOICE_NOTE_DURATION_MS, Math.round(recordedDurationMs)));

    try {
      const result = await onUpload(file, clampedDuration);
      if (result.ok) {
        revokePreview();
        setRecordedBlob(null);
        setRecordedDurationMs(0);
        onClose();
      } else {
        // Preserve recorded blob + preview URL on recoverable upload failure
        setError(result.error || 'Unable to upload voice note. Try again.');
        setPhase('preview');
      }
    } catch {
      setError('Upload failed. Your recording is still ready to retry.');
      setPhase('preview');
    } finally {
      inFlightRef.current = false;
    }
  };

  const handleClose = () => {
    if (inFlightRef.current) return;
    cancelActiveRecording();
    revokePreview();
    onClose();
  };

  const tooLarge = Boolean(recordedBlob && recordedBlob.size > MAX_VOICE_NOTE_BYTES);

  return (
    <div
      ref={panelRef}
      className="nx-voice-recorder"
      role="region"
      aria-label="Voice note recorder"
    >
      <div className="nx-voice-recorder-head">
        <div className="nx-voice-recorder-title">
          <Mic size={15} aria-hidden="true" />
          <strong>Voice note</strong>
          <span className="nx-muted">Up to 2:00 · 5 MB max</span>
        </div>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          iconOnly
          aria-label="Close voice recorder"
          title="Close"
          disabled={phase === 'uploading'}
          onClick={handleClose}
        >
          <X size={14} aria-hidden="true" />
        </Button>
      </div>

      {error && (
        <p className="nx-error nx-voice-recorder-error" role="alert">
          {error}
        </p>
      )}

      {phase === 'idle' && (
        <div className="nx-voice-recorder-row">
          <span ref={startBtnRef}>
            <Button
              type="button"
              variant="primary"
              size="sm"
              disabled={disabled || !isSupported}
              onClick={() => void handleStartRecording()}
            >
              <Mic size={14} aria-hidden="true" /> Start recording
            </Button>
          </span>
          {!isSupported && (
            <span className="nx-muted">Audio recording is not supported in this browser.</span>
          )}
        </div>
      )}

      {phase === 'recording' && (
        <div className="nx-voice-recorder-row">
          <div className="nx-voice-live-indicator" role="status" aria-live="polite">
            <span className="nx-voice-pulse-dot" aria-hidden="true" />
            <span>Recording</span>
            <strong className="nx-voice-timer">
              {formatVoiceDuration(elapsedMs)} / {formatVoiceDuration(MAX_VOICE_NOTE_DURATION_MS)}
            </strong>
          </div>
          <div className="nx-voice-recorder-actions">
            <Button type="button" variant="primary" size="sm" onClick={handleStopRecording}>
              <Square size={13} aria-hidden="true" /> Stop
            </Button>
            <Button type="button" variant="ghost" size="sm" onClick={handleCancelRecording}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      {(phase === 'preview' || phase === 'uploading') && previewUrl && recordedBlob && (
        <div className="nx-voice-preview-stack">
          <div className="nx-voice-preview-meta">
            <span>{formatVoiceDuration(recordedDurationMs)}</span>
            <span>·</span>
            <span>{formatBytes(recordedBlob.size)}</span>
          </div>
          <audio
            controls
            src={previewUrl}
            className="nx-voice-audio"
            aria-label="Recorded voice note preview"
          />
          <div className="nx-voice-recorder-actions">
            <Button
              type="button"
              variant="primary"
              size="sm"
              disabled={phase === 'uploading' || tooLarge || disabled}
              onClick={() => void handleAttachVoiceNote()}
            >
              <Upload size={13} aria-hidden="true" />
              {phase === 'uploading' ? 'Uploading…' : 'Attach voice note'}
            </Button>
            <Button
              type="button"
              variant="default"
              size="sm"
              disabled={phase === 'uploading'}
              onClick={() => void handleStartRecording()}
            >
              <RotateCcw size={13} aria-hidden="true" /> Record again
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              disabled={phase === 'uploading'}
              onClick={handleDiscardPreview}
            >
              <Trash2 size={13} aria-hidden="true" /> Discard
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
