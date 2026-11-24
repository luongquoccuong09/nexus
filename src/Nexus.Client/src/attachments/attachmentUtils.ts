export interface AttachmentItem {
  id: string;
  conversationId: string;
  messageId?: string | null;
  postId?: string | null;
  uploaderUserId: string;
  clientUploadId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  sha256Hex: string;
  imageWidth?: number | null;
  imageHeight?: number | null;
  state: string;
  scanStatus: string;
  downloadUrl: string;
  thumbnailUrl?: string | null;
  previewUrl?: string | null;
  createdAt: string;
}

export interface StorageQuotaSummary {
  conversationUsedBytes: number;
  conversationQuotaBytes: number;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export const formatFileSize = formatBytes;

export function isImageAttachment(att: Pick<AttachmentItem, 'contentType' | 'thumbnailUrl'>): boolean {
  return Boolean(att.thumbnailUrl) || (att.contentType ?? '').toLowerCase().startsWith('image/');
}

export function isAudioAttachment(att: Pick<AttachmentItem, 'contentType'>): boolean {
  return (att.contentType ?? '').toLowerCase().startsWith('audio/');
}
