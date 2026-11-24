import { Download, FileText } from 'lucide-react';
import { AttachmentItem, formatBytes } from './attachmentUtils';

export interface AttachmentGalleryProps {
  attachments: AttachmentItem[];
  onPreviewImage: (att: AttachmentItem) => void;
}

export function AttachmentGallery({ attachments, onPreviewImage }: AttachmentGalleryProps) {
  if (attachments.length === 0) return null;

  return (
    <div className="nx-msg-attachments">
      {attachments.map(att =>
        att.thumbnailUrl ? (
          <div key={att.id} className="nx-att-image-wrap">
            <button
              type="button"
              className="nx-att-thumb-btn"
              aria-label={`Preview ${att.fileName}`}
              onClick={() => onPreviewImage(att)}
            >
              <img className="nx-att-thumb-img" src={att.thumbnailUrl} alt={att.fileName} />
            </button>
            <a
              href={att.downloadUrl}
              download={att.fileName}
              className="nx-att-img-dl"
              aria-label={`Download ${att.fileName}`}
              title={`Download ${att.fileName} (${formatBytes(att.sizeBytes)})`}
            >
              <Download size={14} aria-hidden="true" />
            </a>
          </div>
        ) : (
          <a key={att.id} href={att.downloadUrl} download={att.fileName} className="nx-att-file-card">
            <span className="nx-att-file-meta">
              <FileText size={16} aria-hidden="true" />
              <span style={{ minWidth: 0 }}>
                <div className="nx-att-file-title">{att.fileName}</div>
                <div className="nx-muted">{formatBytes(att.sizeBytes)}</div>
              </span>
            </span>
            <Download size={15} aria-hidden="true" />
          </a>
        )
      )}
    </div>
  );
}
