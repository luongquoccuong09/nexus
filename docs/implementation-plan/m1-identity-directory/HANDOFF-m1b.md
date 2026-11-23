# M1b Directory & Profiles Handoff (`M1.9`–`M1.11`)

- Branch: `develop`
- Date: 2026-11-23
- Completed items: `M1.9`, `M1.10`, `M1.11`

## What landed

- **`M1.9` HR CSV Import (`Modules/Org/HrCsvImportService.cs`, `OrgEndpoints.cs`):**
  - RFC-4180 CSV parser upserting `employees`, `departments`, and `branches` by code.
  - Supports `?dryRun=true` (full diff report + `Unverified` account match suggestions without DB mutation) and `?dryRun=false` (persists + audits `HR_DIRECTORY_IMPORT`); re-importing identical CSV is idempotent (`0` added, `0` updated).
  - Strictly authorized for Verified `HRAdmin` with TOTP MFA (`SystemAdmin` and ordinary members receive `403`).
- **`M1.10` Vietnamese Directory Search (`Modules/People/PeopleEndpoints.cs`, migration `M1bDirectoryAndProfiles`):**
  - PostgreSQL `unaccent` + `pg_trgm` extensions (`deploy/postgres/init-roles.sql`), `public.immutable_unaccent(text)` handling Vietnamese `đ`/`Đ`, and GIN trigram indexes (`ix_users_display_name_trgm`, `ix_users_username_trgm`).
  - `GET /api/people/directory` supports diacritic-insensitive + fuzzy trigram search, department filter, pagination cap `<= 50`, minimal profiles only, and strictly hides `Disabled`/`Former` accounts.
- **`M1.11` Custom Status & Re-encoded Avatars (`Modules/People/AvatarProcessor.cs`, `PeopleEndpoints.cs`):**
  - `PUT /api/people/me/status` (`UX-06`, max 100 chars, control chars stripped).
  - `POST/DELETE /api/people/me/avatar` and `GET /api/people/{id}/avatar`: MIT-licensed `SkiaSharp` validates real image bytes, blocks spoofed files and decompression bombs, center-crops to `256x256`, strips EXIF/COM/XMP/IPTC metadata, re-encodes to clean PNG, stores via `IFileStorage`, and serves with `X-Content-Type-Options: nosniff` (authenticated Active users only).
- **Minimal Client (`src/Nexus.Client/src/main.tsx`):** Directory search tab with department dropdown, Profile custom status + avatar upload, and HR CSV dry-run/apply tab for `HRAdmin`.

## Verification

- `dotnet build Nexus.slnx --no-restore -c Release` → **0 Warnings, 0 Errors**
- `dotnet test Nexus.slnx --no-build -c Release` → **Passed 107/107** (including `DirectoryM1bTests` on Testcontainers PostgreSQL 17)
- `npm.cmd --prefix src/Nexus.Client run build` → **Passed**
- `dotnet list Nexus.slnx package --vulnerable --include-transitive` → **0 vulnerable packages**
- `pwsh -File ./scripts/audit/Test-SecurityGuardrails.ps1` → **0 violations**
- `pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1 -Check` → **OK**

## Next options

- **M2 — Core Messaging & Synchronization (`M2.1`–`M2.12`)** to deliver usable realtime DM & group chat (Release 1), OR
- **M1c — Google OIDC account linking (`M1.12`–`M1.14`, `SEC-22`/`SEC-23`)**.
