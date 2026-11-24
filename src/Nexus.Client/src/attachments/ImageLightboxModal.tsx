import { Download } from 'lucide-react';
import { Button, Modal } from '../ui';
import { AttachmentItem, formatBytes } from './attachmentUtils';

export interface ImageLightboxModalProps {
  image: AttachmentItem | null;
  onClose: () => void;
}

export function ImageLightboxModal({ image, onClose }: ImageLightboxModalProps) {
  return (
    <Modal
      open={Boolean(image)}
      onOpenChange={open => {
        if (!open) onClose();
      }}
      title={image?.fileName ?? 'Image preview'}
      size="xl"
      footer={
        image ? (
          <>
            <span className="nx-muted" style={{ marginRight: 'auto', alignSelf: 'center' }}>
              {image.imageWidth && image.imageHeight
                ? `${image.imageWidth}×${image.imageHeight} · `
                : ''}
              {formatBytes(image.sizeBytes)}
            </span>
            <a href={image.downloadUrl} download={image.fileName} className="nx-btn primary sm">
              <Download size={14} aria-hidden="true" /> Download
            </a>
            <Button size="sm" onClick={onClose}>
              Close
            </Button>
          </>
        ) : undefined
      }
    >
      {image && (
        <div className="nx-lightbox-body">
          <img
            className="nx-lightbox-img"
            src={image.previewUrl ?? image.thumbnailUrl ?? image.downloadUrl}
            alt={image.fileName}
          />
        </div>
      )}
    </Modal>
  );
}
