import { Download, Mic } from 'lucide-react';
import { formatBytes } from '../attachments/attachmentUtils';
import { formatVoiceDuration } from './voiceNoteHelpers';

export interface VoiceNotePlayerProps {
  fileName: string;
  audioUrl: string;
  downloadUrl?: string | null;
  durationMs?: number | null;
  sizeBytes?: number | null;
  compact?: boolean;
}

export function VoiceNotePlayer({
  fileName,
  audioUrl,
  downloadUrl,
  durationMs,
  sizeBytes,
  compact = false
}: VoiceNotePlayerProps) {
  const durationLabel = typeof durationMs === 'number' && durationMs > 0 ? formatVoiceDuration(durationMs) : null;
  const sizeLabel = typeof sizeBytes === 'number' && sizeBytes > 0 ? formatBytes(sizeBytes) : null;

  return (
    <div className={`nx-voice-player ${compact ? 'compact' : ''}`}>
      <div className="nx-voice-player-head">
        <span className="nx-voice-player-icon" aria-hidden="true">
          <Mic size={14} />
        </span>
        <div className="nx-voice-player-meta">
          <strong className="nx-voice-player-title" title={fileName}>
            Voice note
          </strong>
          <span className="nx-voice-player-sub nx-muted">
            {durationLabel && <span>{durationLabel}</span>}
            {durationLabel && sizeLabel && <span>·</span>}
            {sizeLabel && <span>{sizeLabel}</span>}
          </span>
        </div>
        {downloadUrl && !compact && (
          <a
            href={downloadUrl}
            download={fileName}
            className="nx-btn sm icon-only nx-voice-dl-btn"
            aria-label={`Download ${fileName}`}
            title={`Download ${fileName}`}
          >
            <Download size={14} aria-hidden="true" />
          </a>
        )}
      </div>
      <audio
        controls
        preload="metadata"
        src={audioUrl}
        className="nx-voice-audio"
        aria-label={`Play voice note ${fileName}${durationLabel ? ` (${durationLabel})` : ''}`}
      />
    </div>
  );
}
