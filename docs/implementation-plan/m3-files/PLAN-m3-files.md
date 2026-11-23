# Plan — Milestone M3: Files (`M3.1`–`M3.6`)

- Status: in progress
- Branch: `develop`
- Scope: Two-stage uploads (`client_upload_id`), magic-byte allowlist & configurable D08 limits, authorized downloads & `<= 60s` signed URLs (`SEC-04`, `SEC-11`), image paste/drag-drop with server-side SkiaSharp re-encoding & thumbnails (`UX-08`), conversation Files tab & storage quotas, and ClamAV quarantine/fail-closed scanning.

## Deliverables

1. **Schema & Migration (`M3.1`):** `attachments` table (`AttachmentEntity`) with `UNIQUE(uploader_user_id, client_upload_id)`, `state` (`Pending`, `Attached`, `Quarantined`, `Rejected`), `scan_status`, random `object_key`, optional `thumbnail_object_key`, SHA-256 checksum, and orphan cleanup worker (`OrphanAttachmentCleanupService`).
2. **Magic-byte validation, image re-encoding & thumbnails (`M3.2`, `M3.4`):**
   - `FileContentInspector`: inspects leading magic bytes (never trusts client `Content-Type`), enforces allowlist (`image/png`, `image/jpeg`, `image/webp`, `image/gif`, `application/pdf`, `text/plain`, `text/csv`, Office OpenXML `.docx`/`.xlsx`/`.pptx`, `.zip`), rejects executables/HTML/SVG/scripts and mismatched extensions, enforces `FilePolicyOptions.MaxFileSizeBytes` (default 10 MB, configurable for D08).
   - `AttachmentImageProcessor` (MIT `SkiaSharp`): re-encodes uploaded images cleanly (stripping EXIF/COM metadata) and generates `<= 400px` PNG thumbnails (`UX-08`).
3. **Quarantine & ClamAV fail-closed scanning (`M3.6`):**
   - `ClamAvFileScanner` (`INSTREAM` TCP protocol against `clamd`) + configurable `FilePolicyOptions.FailClosedScanning` (when `true`, only `FileScanVerdict.Clean` is released; `Infected`, `FailedClosed`, or `SkippedPrototype` are rejected/quarantined and deleted from storage).
   - Prototype mode (`FailClosedScanning = false` in dev/trial) permits `PrototypeFileScannerStub` (`skipped_prototype`) while still rejecting `Infected` and `FailedClosed`.
4. **Authorized downloads, signed URLs & safe headers (`M3.3`, `SEC-04`, `SEC-11`):**
   - `GET /api/files/{attachmentId}/download` & `GET /api/files/{attachmentId}/thumbnail` & `POST /api/files/{attachmentId}/signed-url`: verify `Active` user + conversation membership via `IAuthorizationService.Can(actor, ResourceAction.FileDownload, ...)` + `visible_from_seq` + non-quarantined state.
   - Always send `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff` on downloads (and `nosniff` on thumbnails), and enforce signed URL TTL `<= 60s`.
5. **Conversation Files tab, quotas & UI (`M3.4`, `M3.5`):**
   - `POST /api/conversations/{id}/attachments` (Stage 1), `POST /api/conversations/{id}/messages` accepts optional `attachmentIds` (Stage 2 in the same transaction; allows attachment-only messages with optional caption), `GET /api/conversations/{id}/files` (cursor-paged `<= 50`) + quota summary (`usedBytes`, `quotaBytes`, `warning`).
   - React chat composer supports file picker, `Ctrl+V` image paste, drag-and-drop onto chat stage, pending attachment pills, inline image thumbnails (`UX-08`) + download cards, and a per-conversation **Files** drawer with storage quota warning banner — all built with Nexus Design System `src/ui/` primitives.
6. **Tests (`SEC-04`, `SEC-11`, `M3.1`–`M3.6`):** Testcontainers PostgreSQL 17 integration suite in `tests/Nexus.IntegrationTests/FilesM3Tests.cs`.
