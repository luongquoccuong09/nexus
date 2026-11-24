import { RefObject } from 'react';
import { Download, ExternalLink, FileText } from 'lucide-react';
import { AttachmentItem, StorageQuotaSummary, formatBytes } from '../attachments/attachmentUtils';
import { Button } from '../ui';
import { ChannelFileItem } from './types';

export interface ChannelFilesDrawerProps {
  headingRef: RefObject<HTMLHeadingElement | null>;
  files: ChannelFileItem[];
  loading: boolean;
  error: 'refresh' | 'older' | null;
  hasMore: boolean;
  quota: StorageQuotaSummary | null;
  onClose: () => void;
  onPreviewImage: (att: AttachmentItem) => void;
  onGoToPost?: (sourceLink: string) => void;
  onLoadMore: (older: boolean) => void;
}

export function ChannelFilesDrawer({
  headingRef,
  files,
  loading,
  error,
  hasMore,
  quota,
  onClose,
  onPreviewImage,
  onGoToPost,
  onLoadMore
}: ChannelFilesDrawerProps) {
  return (
    <aside className="nx-drawer" aria-label="Channel files">
      <div className="nx-drawer-head">
        <h4 ref={headingRef} tabIndex={-1}>
          {loading && files.length === 0 ? 'Files' : `Files (${files.length})`}
        </h4>
        <Button variant="ghost" size="sm" onClick={onClose}>
          Close
        </Button>
      </div>

      <div className="nx-drawer-body">
        {quota && (
          <div className="nx-quota-box">
            <div style={{ display: 'flex', justifyContent: 'space-between', gap: 6 }}>
              <strong>Channel storage</strong>
              <span className="nx-muted">
                {formatBytes(quota.conversationUsedBytes)} / {formatBytes(quota.conversationQuotaBytes)}
              </span>
            </div>
            <div className="nx-quota-bar">
              <div
                className="nx-quota-fill"
                style={{
                  width: `${Math.min(
                    100,
                    Math.round((quota.conversationUsedBytes / Math.max(1, quota.conversationQuotaBytes)) * 100)
                  )}%`
                }}
              />
            </div>
          </div>
        )}

        {loading && files.length === 0 ? (
          <p className="nx-muted" role="status" style={{ textAlign: 'center', marginTop: 18 }}>
            Loading files…
          </p>
        ) : files.length === 0 && !error ? (
          <p className="nx-muted" style={{ textAlign: 'center', marginTop: 18 }}>
            No files shared in this channel yet.
          </p>
        ) : (
          <div>
            {files.map(f => (
              <div className="nx-row nx-channel-file-row" key={f.id}>
                {f.thumbnailUrl ? (
                  <button
                    type="button"
                    className="nx-file-preview-trigger"
                    onClick={() => onPreviewImage(f)}
                    title={`Preview ${f.fileName}`}
                  >
                    <img
                      src={f.thumbnailUrl}
                      alt=""
                      style={{ width: 32, height: 32, borderRadius: 6, objectFit: 'cover', flexShrink: 0 }}
                    />
                    <span style={{ minWidth: 0, flex: 1 }}>
                      <div className="nx-att-file-title">{f.fileName}</div>
                      <div className="nx-muted nx-channel-file-sub">
                        {formatBytes(f.sizeBytes)}
                        {f.uploaderDisplayName ? ` · ${f.uploaderDisplayName}` : ''}
                      </div>
                      {f.postTitle && (
                        <div className="nx-muted nx-channel-file-sub" title={f.postTitle}>
                          {f.postTitle}
                        </div>
                      )}
                    </span>
                  </button>
                ) : (
                  <div className="nx-att-file-meta" style={{ flex: 1 }}>
                    <FileText size={18} aria-hidden="true" />
                    <div style={{ minWidth: 0, flex: 1 }}>
                      <div className="nx-att-file-title">{f.fileName}</div>
                      <div className="nx-muted nx-channel-file-sub">
                        {formatBytes(f.sizeBytes)}
                        {f.uploaderDisplayName ? ` · ${f.uploaderDisplayName}` : ''}
                      </div>
                      {f.postTitle && (
                        <div className="nx-muted nx-channel-file-sub" title={f.postTitle}>
                          {f.postTitle}
                        </div>
                      )}
                    </div>
                  </div>
                )}
                <div className="nx-channel-file-actions">
                  {f.sourceLink && onGoToPost && (
                    <Button
                      variant="ghost"
                      size="sm"
                      iconOnly
                      aria-label={`Go to post for ${f.fileName}`}
                      title="Go to post"
                      onClick={() => onGoToPost(f.sourceLink!)}
                    >
                      <ExternalLink size={13} aria-hidden="true" />
                    </Button>
                  )}
                  <a
                    href={f.downloadUrl}
                    download={f.fileName}
                    className="nx-btn sm"
                    aria-label={`Download ${f.fileName}`}
                    title={`Download ${f.fileName}`}
                  >
                    <Download size={13} aria-hidden="true" />
                  </a>
                </div>
              </div>
            ))}
          </div>
        )}

        {(hasMore || error || (loading && files.length > 0)) && (
          <div className="nx-empty-side">
            {error && <p className="nx-error" role="alert">Unable to load files.</p>}
            {loading && files.length > 0 && <span role="status">Loading files…</span>}
            {!loading && (
              <Button size="sm" onClick={() => onLoadMore(error !== 'refresh')}>
                {error ? 'Retry' : 'Load more files'}
              </Button>
            )}
          </div>
        )}
      </div>
    </aside>
  );
}
