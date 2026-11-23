# M3 Session Handoff — Files (`M3.1`–`M3.6`)

- Date: 2026-11-23
- Branch: `develop`
- Scope: Two-stage file uploads, magic-byte validation, authorized downloads & `<= 60s` signed URLs (`SEC-04`, `SEC-11`), image paste/drag-drop + SkiaSharp EXIF strip & thumbnails (`UX-08`), conversation Files tab & quotas, and ClamAV `zINSTREAM` fail-closed scanning.

## 1. What shipped

- **Schema & Migration (`M3.1`):**
  - Added [`AttachmentEntity.cs`](../../../src/Nexus.Web/Modules/Files/AttachmentEntity.cs) and EF Core migration `M3FilesAndAttachments` creating `attachments` (`UNIQUE(uploader_user_id, client_upload_id)`, `state`, `scan_status`, random `object_key`, optional `thumbnail_object_key`, SHA-256 checksum).
  - Added [`OrphanAttachmentCleanupService.cs`](../../../src/Nexus.Web/Modules/Files/OrphanAttachmentCleanupService.cs) (`BackgroundService` + `CleanupOrphansAsync`) sweeping abandoned `Pending`/`Rejected`/`Quarantined` objects from both `IFileStorage` and PostgreSQL.
- **Magic Bytes, Re-encoding & Thumbnails (`M3.2`, `M3.4`):**
  - [`FileContentInspector.cs`](../../../src/Nexus.Web/Modules/Files/FileContentInspector.cs): verifies actual binary magic bytes (PNG, JPEG, WebP, GIF, PDF, OpenXML `.docx`/`.xlsx`/`.pptx`/`.zip`, safe UTF-8 `.txt`/`.csv`), rejects executables (`MZ`/`ELF`/Mach-O), HTML/SVG/scripts, and extension mismatches; enforces configurable `NexusFiles:MaxFileSizeBytes` (`P0-D08`).
  - Uses MIT `SkiaSharp` to re-encode images (stripping EXIF/GPS/COM metadata) and generate `<= 400px` PNG thumbnails (`UX-08`).
- **Authorized Downloads, Signed URLs & Safe Headers (`M3.3`, `SEC-04`, `SEC-11`):**
  - [`FilesEndpoints.cs`](../../../src/Nexus.Web/Modules/Files/FilesEndpoints.cs): `POST /api/conversations/{id}/attachments`, `GET /api/conversations/{id}/files`, `GET /api/files/{id}/download`, `GET /api/files/{id}/thumbnail`, `POST /api/files/{id}/signed-url`.
  - Every download/thumbnail/signed-URL checks `Active` status, `IAuthorizationService.Can(..., FileDownload, ...)`, `visible_from_seq`, and non-quarantined state; always sets `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`, and caps signed URL TTL at `<= 60` seconds.
- **Quotas & ClamAV Fail-Closed Scanning (`M3.5`, `M3.6`):**
  - [`FilePolicyOptions.cs`](../../../src/Nexus.Web/Modules/Files/FilePolicyOptions.cs) & [`ClamAvFileScanner.cs`](../../../src/Nexus.Web/Modules/Files/ClamAvFileScanner.cs): native ClamAV TCP `zINSTREAM` client, fail-closed rejection (`503` on outage/stub when `NexusFiles:FailClosedScanning=true`, `422` + `FILE_SCAN_REJECTED` audit log on infected payload), and per-user/per-conversation storage quotas with 80% warning flags.
- **Messaging & Nexus Design System UI (`M3.1`, `M3.4`, `M3.5`):**
  - Updated [`MessagingEndpoints.cs`](../../../src/Nexus.Web/Modules/Messaging/MessagingEndpoints.cs) so `POST /messages` binds pending `attachmentIds` atomically in the same transaction (supporting attachment-only messages) and hydrates attachments in `GET /messages` and `conversation_events`.
  - Updated [`chat.tsx`](../../../src/Nexus.Client/src/chat.tsx) and [`style.css`](../../../src/Nexus.Client/src/style.css) with paperclip picker, `Ctrl+V` clipboard image paste, drag-and-drop onto chat stage, pending chips, inline thumbnails & download cards, and per-conversation **Files** drawer with storage quota progress bar and 80% warning — passing `Test-UiDesignCompliance.ps1` with 0 violations.
- **Tests (`SEC-04`, `SEC-11`, `M3.1`–`M3.6`):**
  - [`FilesM3Tests.cs`](../../../tests/Nexus.IntegrationTests/FilesM3Tests.cs): 4 Testcontainers PostgreSQL 17 integration tests including an in-process TCP mock `clamd` speaking `zINSTREAM`.

## 2. Verification commands run

```powershell
pwsh -File .\scripts\Run-Tests.ps1
# Security guardrails: 116 files, 0 violations
# UI design compliance: 19 files, 0 violations
# Knowledge index: OK (4 facts, 8 decisions)
# Nexus.slnx Release build: 0 Warning(s), 0 Error(s)
# dotnet test (PostgreSQL 17 Testcontainers): Passed 117, Failed 0, Skipped 0
# Client SEC-10 unit tests: 3/3 passed
# Client tsc --noEmit && vite build: OK
# Nexus.Desktop Release build: 0 Warning(s), 0 Error(s)
```

## 3. Deployment note for Supabase + Render

When promoting `develop` → `main` to deploy M3 live on Render:
1. Run `pwsh -File .\scripts\deploy\Invoke-SupabaseMigrate.ps1 -MigrateOnly` to apply the `M3FilesAndAttachments` migration (`attachments` table) to Supabase before/during deploy.
2. Ensure `NexusStorage__Provider=Supabase`, `NexusStorage__SupabaseUrl`, `NexusStorage__SupabaseBucket=nexus-files`, and `NexusStorage__SupabaseServiceRoleKey` are configured in Render Environment (already defined in `render.yaml`).
3. Before onboarding real confidential company data (M8 pilot gate), run ClamAV alongside the app and set `NexusFiles__FailClosedScanning=true` + `NexusFiles__ClamAvHost=<clamd-host>`.
