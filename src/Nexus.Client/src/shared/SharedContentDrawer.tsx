import { KeyboardEvent, useCallback, useEffect, useRef, useState } from 'react';
import { Download, ExternalLink, FileText, Globe, Image as ImageIcon, Link2, RefreshCw, Search, X } from 'lucide-react';
import { AttachmentItem, formatBytes, isAudioAttachment } from '../attachments/attachmentUtils';
import { VoiceNotePlayer } from '../voice/VoiceNotePlayer';
import { isSafeHttpUrl } from '../markdown';
import { Badge, Button } from '../ui';
import {
  SHARED_TABS,
  SharedContentCursor,
  SharedContentItem,
  SharedQuotaSummary,
  SharedTab,
  buildSharedContentUrl,
  formatSharedHost,
  mergeSharedItems,
  resolveSafeSharedHref
} from './sharedContentHelpers';

export interface SharedContentDrawerProps {
  scope: { kind: 'personal' } | { kind: 'organization'; organizationId: string };
  conversationId: string;
  originLabel: 'message' | 'post';
  lightboxOpen: boolean;
  onClose: () => void;
  onPreviewImage: (att: AttachmentItem) => void;
  onOpenContentLink?: (token: string) => void;
}

function toAttachmentItem(item: SharedContentItem): AttachmentItem {
  return {
    id: item.id,
    conversationId: item.conversationId,
    messageId: item.messageId ?? null,
    postId: item.postId ?? null,
    uploaderUserId: item.senderUserId,
    clientUploadId: '',
    fileName: item.fileName ?? 'Attachment',
    contentType: item.contentType ?? 'application/octet-stream',
    sizeBytes: typeof item.sizeBytes === 'number' ? item.sizeBytes : 0,
    sha256Hex: '',
    imageWidth: item.imageWidth ?? item.width ?? null,
    imageHeight: item.imageHeight ?? item.height ?? null,
    durationMs: item.durationMs ?? null,
    state: 'attached',
    scanStatus: 'clean',
    downloadUrl: item.downloadUrl ?? '',
    thumbnailUrl: item.thumbnailUrl ?? null,
    previewUrl: item.previewUrl ?? null,
    audioUrl: item.audioUrl ?? null,
    createdAt: item.createdAt
  };
}

function formatShortDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString([], { month: 'short', day: 'numeric' });
}

export default function SharedContentDrawer({
  scope,
  conversationId,
  originLabel,
  lightboxOpen,
  onClose,
  onPreviewImage,
  onOpenContentLink
}: SharedContentDrawerProps) {
  const [activeTab, setActiveTab] = useState<SharedTab>('files');
  const [queryInput, setQueryInput] = useState('');
  const [appliedQuery, setAppliedQuery] = useState('');
  const [items, setItems] = useState<SharedContentItem[]>([]);
  const [nextCursor, setNextCursor] = useState<SharedContentCursor | null>(null);
  const [quota, setQuota] = useState<SharedQuotaSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<'refresh' | 'older' | null>(null);

  const headingRef = useRef<HTMLHeadingElement | null>(null);
  const requestSeqRef = useRef(0);
  const abortRef = useRef<AbortController | null>(null);
  const scopeKey = scope.kind === 'organization' ? `org:${scope.organizationId}` : 'personal';

  useEffect(() => {
    requestAnimationFrame(() => headingRef.current?.focus());
  }, []);

  useEffect(() => {
    const handleEsc = (ev: globalThis.KeyboardEvent) => {
      if (ev.key !== 'Escape' || ev.defaultPrevented || lightboxOpen) return;
      if (document.querySelector('[role="dialog"]')) return;
      ev.preventDefault();
      onClose();
    };
    window.addEventListener('keydown', handleEsc);
    return () => window.removeEventListener('keydown', handleEsc);
  }, [lightboxOpen, onClose]);

  useEffect(() => {
    const trimmed = queryInput.trim();
    if (!trimmed) {
      setAppliedQuery('');
      return;
    }
    const timer = window.setTimeout(() => setAppliedQuery(trimmed), 220);
    return () => window.clearTimeout(timer);
  }, [queryInput]);

  const fetchTab = useCallback(
    async (tab: SharedTab, query: string, cursor: SharedContentCursor | null, append: boolean) => {
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;
      const reqId = ++requestSeqRef.current;

      setLoading(true);
      setError(null);
      if (!append) {
        setItems([]);
        setNextCursor(null);
      }

      try {
        const url = buildSharedContentUrl(scope, conversationId, tab, cursor, query);
        const res = await window.fetch(url, { credentials: 'same-origin', signal: controller.signal });
        if (controller.signal.aborted || reqId !== requestSeqRef.current) return;
        if (!res.ok) throw new Error('Unable to load shared content.');

        const data = await res.json();
        if (controller.signal.aborted || reqId !== requestSeqRef.current) return;

        const incoming: SharedContentItem[] = Array.isArray(data.items) ? data.items : [];
        setItems(prev => mergeSharedItems(prev, incoming, append));
        setNextCursor(
          data.hasMore && data.nextBefore && data.nextBeforeId
            ? { before: data.nextBefore, beforeId: data.nextBeforeId }
            : null
        );
        if (tab === 'files' && data.quota) {
          setQuota(data.quota as SharedQuotaSummary);
        }
      } catch {
        if (!controller.signal.aborted && reqId === requestSeqRef.current) {
          setError(append ? 'older' : 'refresh');
        }
      } finally {
        if (reqId === requestSeqRef.current) {
          setLoading(false);
        }
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [conversationId, scopeKey]
  );

  useEffect(() => {
    void fetchTab(activeTab, appliedQuery, null, false);
    return () => {
      abortRef.current?.abort();
    };
  }, [activeTab, appliedQuery, fetchTab]);

  function handleTabKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    const idx = SHARED_TABS.findIndex(t => t.id === activeTab);
    if (e.key === 'ArrowRight') {
      e.preventDefault();
      setActiveTab(SHARED_TABS[(idx + 1) % SHARED_TABS.length].id);
    } else if (e.key === 'ArrowLeft') {
      e.preventDefault();
      setActiveTab(SHARED_TABS[(idx - 1 + SHARED_TABS.length) % SHARED_TABS.length].id);
    }
  }

  function handleJumpToSource(sourceLink: string) {
    if (!onOpenContentLink) return;
    if (window.innerWidth <= 860) onClose();
    onOpenContentLink(sourceLink);
  }

  const goLabel = originLabel === 'post' ? 'Go to post' : 'Go to message';

  return (
    <aside className="nx-drawer nx-shared-drawer" aria-label="Shared content">
      <div className="nx-drawer-head">
        <h4 ref={headingRef} tabIndex={-1}>
          Shared
        </h4>
        <div className="nx-inline-actions">
          <Button
            variant="ghost"
            size="sm"
            iconOnly
            aria-label="Refresh shared content"
            title="Refresh"
            onClick={() => void fetchTab(activeTab, appliedQuery, null, false)}
          >
            <RefreshCw size={14} aria-hidden="true" />
          </Button>
          <Button variant="ghost" size="sm" onClick={onClose}>
            Close
          </Button>
        </div>
      </div>

      <div className="nx-shared-tabs" role="tablist" aria-label="Shared content tabs" onKeyDown={handleTabKeyDown}>
        {SHARED_TABS.map(tab => {
          const selected = activeTab === tab.id;
          return (
            <button
              key={tab.id}
              type="button"
              role="tab"
              aria-selected={selected}
              className={`nx-shared-tab ${selected ? 'active' : ''}`}
              onClick={() => setActiveTab(tab.id)}
            >
              {tab.id === 'files' && <FileText size={13} aria-hidden="true" />}
              {tab.id === 'links' && <Link2 size={13} aria-hidden="true" />}
              {tab.id === 'media' && <ImageIcon size={13} aria-hidden="true" />}
              <span>{tab.label}</span>
            </button>
          );
        })}
      </div>

      <div className="nx-shared-search-row">
        <Search size={14} className="nx-shared-search-icon" aria-hidden="true" />
        <input
          type="search"
          className="nx-shared-search-input"
          aria-label={`Filter shared ${activeTab}`}
          placeholder={`Filter ${activeTab}…`}
          maxLength={100}
          value={queryInput}
          onChange={e => setQueryInput(e.target.value)}
        />
        {queryInput && (
          <button
            type="button"
            className="nx-emoji-clear-btn"
            aria-label="Clear filter"
            onClick={() => {
              setQueryInput('');
              setAppliedQuery('');
            }}
          >
            <X size={13} aria-hidden="true" />
          </button>
        )}
      </div>

      <div className="nx-drawer-body" role="tabpanel" aria-label={`Shared ${activeTab}`}>
        {activeTab === 'files' && quota && (
          <div className="nx-quota-box">
            <div style={{ display: 'flex', justifyContent: 'space-between', gap: 6 }}>
              <strong>Storage</strong>
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

        {loading && items.length === 0 ? (
          <p className="nx-muted" role="status" style={{ textAlign: 'center', marginTop: 18 }}>
            Loading shared {activeTab}…
          </p>
        ) : items.length === 0 && !error ? (
          <p className="nx-muted" style={{ textAlign: 'center', marginTop: 18 }}>
            {appliedQuery
              ? `No matching ${activeTab} found.`
              : `No ${activeTab} shared here yet.`}
          </p>
        ) : activeTab === 'files' ? (
          <div>
            {items.map(item => {
              const fileName = item.fileName ?? 'Attachment';
              const sizeLabel = typeof item.sizeBytes === 'number' ? formatBytes(item.sizeBytes) : '';
              return (
                <div className="nx-row nx-channel-file-row" key={item.id}>
                  {item.thumbnailUrl ? (
                    <button
                      type="button"
                      className="nx-file-preview-trigger"
                      onClick={() => onPreviewImage(toAttachmentItem(item))}
                      title={`Preview ${fileName}`}
                    >
                      <img
                        src={item.thumbnailUrl}
                        alt=""
                        style={{ width: 32, height: 32, borderRadius: 6, objectFit: 'cover', flexShrink: 0 }}
                      />
                      <span style={{ minWidth: 0, flex: 1 }}>
                        <div className="nx-att-file-title">{fileName}</div>
                        <div className="nx-muted nx-channel-file-sub">
                          {sizeLabel}
                          {item.senderDisplayName ? ` · ${item.senderDisplayName}` : ''}
                        </div>
                        {item.postTitle && (
                          <div className="nx-muted nx-channel-file-sub" title={item.postTitle}>
                            {item.postTitle}
                          </div>
                        )}
                      </span>
                    </button>
                  ) : (
                    <div className="nx-att-file-meta" style={{ flex: 1 }}>
                      <FileText size={18} aria-hidden="true" />
                      <div style={{ minWidth: 0, flex: 1 }}>
                        <div className="nx-att-file-title">{fileName}</div>
                        <div className="nx-muted nx-channel-file-sub">
                          {sizeLabel}
                          {item.senderDisplayName ? ` · ${item.senderDisplayName}` : ''}
                        </div>
                        {item.postTitle && (
                          <div className="nx-muted nx-channel-file-sub" title={item.postTitle}>
                            {item.postTitle}
                          </div>
                        )}
                      </div>
                    </div>
                  )}
                  <div className="nx-channel-file-actions">
                    {item.sourceLink && onOpenContentLink && (
                      <Button
                        variant="ghost"
                        size="sm"
                        iconOnly
                        aria-label={`${goLabel} for ${fileName}`}
                        title={goLabel}
                        onClick={() => handleJumpToSource(item.sourceLink!)}
                      >
                        <ExternalLink size={13} aria-hidden="true" />
                      </Button>
                    )}
                    {item.downloadUrl && (
                      <a
                        href={item.downloadUrl}
                        download={fileName}
                        className="nx-btn sm"
                        aria-label={`Download ${fileName}`}
                        title={`Download ${fileName}`}
                      >
                        <Download size={13} aria-hidden="true" />
                      </a>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        ) : activeTab === 'links' ? (
          <div className="nx-shared-links-list">
            {items.map(item => {
              const safeHref = resolveSafeSharedHref(item.url);
              const isSafe = Boolean(safeHref && isSafeHttpUrl(safeHref));
              const host = formatSharedHost(item);
              const displayLabel = (item.label ?? '').trim() || safeHref || item.url || 'Link';
              return (
                <div className="nx-shared-link-card" key={item.id}>
                  <div className="nx-shared-link-main">
                    {isSafe && safeHref ? (
                      <a
                        href={safeHref}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="nx-shared-link-title"
                        title={safeHref}
                      >
                        {displayLabel}
                      </a>
                    ) : (
                      <span className="nx-shared-link-title">{displayLabel}</span>
                    )}
                    <div className="nx-shared-link-meta">
                      {host && (
                        <Badge variant="role">
                          <Globe size={10} aria-hidden="true" /> {host}
                        </Badge>
                      )}
                      <span className="nx-muted nx-channel-file-sub">
                        {item.senderDisplayName}
                        {item.createdAt ? ` · ${formatShortDate(item.createdAt)}` : ''}
                      </span>
                    </div>
                  </div>
                  {item.sourceLink && onOpenContentLink && (
                    <Button
                      variant="ghost"
                      size="sm"
                      iconOnly
                      aria-label={`${goLabel} for ${displayLabel}`}
                      title={goLabel}
                      onClick={() => handleJumpToSource(item.sourceLink!)}
                    >
                      <ExternalLink size={13} aria-hidden="true" />
                    </Button>
                  )}
                </div>
              );
            })}
          </div>
        ) : (
          <div className="nx-shared-media-grid">
            {items.map(item => {
              const fileName = item.fileName ?? 'Media';
              const isAudio = Boolean(item.audioUrl && isAudioAttachment({ contentType: item.contentType ?? '' }));
              if (isAudio && item.audioUrl) {
                return (
                  <div className="nx-shared-media-tile nx-shared-audio-tile" key={item.id}>
                    <VoiceNotePlayer
                      fileName={fileName}
                      audioUrl={item.audioUrl}
                      downloadUrl={item.downloadUrl}
                      durationMs={item.durationMs}
                      sizeBytes={item.sizeBytes}
                      compact
                    />
                    <div className="nx-shared-media-bar">
                      <span className="nx-shared-media-name" title={fileName}>{fileName}</span>
                      <div className="nx-shared-media-actions">
                        {item.sourceLink && onOpenContentLink && (
                          <Button
                            variant="ghost"
                            size="sm"
                            iconOnly
                            aria-label={`${goLabel} for ${fileName}`}
                            title={goLabel}
                            onClick={() => handleJumpToSource(item.sourceLink!)}
                          >
                            <ExternalLink size={12} aria-hidden="true" />
                          </Button>
                        )}
                        {item.downloadUrl && (
                          <a
                            href={item.downloadUrl}
                            download={fileName}
                            className="nx-btn sm icon-only"
                            aria-label={`Download ${fileName}`}
                            title={`Download ${fileName}`}
                          >
                            <Download size={12} aria-hidden="true" />
                          </a>
                        )}
                      </div>
                    </div>
                  </div>
                );
              }
              const thumbSrc = item.thumbnailUrl ?? item.previewUrl ?? item.downloadUrl ?? '';
              return (
                <div className="nx-shared-media-tile" key={item.id}>
                  <button
                    type="button"
                    className="nx-shared-media-btn"
                    aria-label={`Preview ${fileName}`}
                    title={fileName}
                    onClick={() => onPreviewImage(toAttachmentItem(item))}
                  >
                    {thumbSrc ? (
                      <img src={thumbSrc} alt={fileName} loading="lazy" />
                    ) : (
                      <ImageIcon size={24} aria-hidden="true" />
                    )}
                  </button>
                  <div className="nx-shared-media-bar">
                    <span className="nx-shared-media-name" title={fileName}>{fileName}</span>
                    <div className="nx-shared-media-actions">
                      {item.sourceLink && onOpenContentLink && (
                        <Button
                          variant="ghost"
                          size="sm"
                          iconOnly
                          aria-label={`${goLabel} for ${fileName}`}
                          title={goLabel}
                          onClick={() => handleJumpToSource(item.sourceLink!)}
                        >
                          <ExternalLink size={12} aria-hidden="true" />
                        </Button>
                      )}
                      {item.downloadUrl && (
                        <a
                          href={item.downloadUrl}
                          download={fileName}
                          className="nx-btn sm icon-only"
                          aria-label={`Download ${fileName}`}
                          title={`Download ${fileName}`}
                        >
                          <Download size={12} aria-hidden="true" />
                        </a>
                      )}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        )}

        {(nextCursor || error || (loading && items.length > 0)) && (
          <div className="nx-empty-side">
            {error && <p className="nx-error" role="alert">Unable to load shared {activeTab}.</p>}
            {loading && items.length > 0 && <span role="status">Loading…</span>}
            {!loading && (
              <Button
                size="sm"
                onClick={() =>
                  void fetchTab(
                    activeTab,
                    appliedQuery,
                    error === 'refresh' ? null : nextCursor,
                    error !== 'refresh'
                  )
                }
              >
                {error ? 'Retry' : 'Load more'}
              </Button>
            )}
          </div>
        )}
      </div>
    </aside>
  );
}
