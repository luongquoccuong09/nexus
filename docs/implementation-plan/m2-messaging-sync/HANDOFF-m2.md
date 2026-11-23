# M2 Session Handoff — Core Messaging & Synchronization (Release 1)

- Date: 2026-11-23
- Branch: `develop`
- Scope: `M2.1`–`M2.11` + backend/SignalR & client XSS verification for `M2.12` (`SEC-03`, `SEC-05`, `SEC-07`, `SEC-10`, `SEC-15`, `SEC-17`, `SEC-18`, `SEC-19`, `SEC-20b`)

## 1. What shipped

- **PostgreSQL Schema & Migration (`M2.1`):**
  - Added EF Core migration `M2CoreMessaging` (`src/Nexus.Web/Infrastructure/Data/Migrations/`) creating `conversations`, `conversation_members`, `messages`, `conversation_events`, `user_blocks`, and `user_reports`.
  - Key invariants: unique partial `(organization_id, dm_key) WHERE dm_key IS NOT NULL`, unique `(conversation_id, seq)`, unique `(sender_id, client_msg_id)` with `payload_sha256`, unique `(conversation_id, change_seq)`, `visible_from_seq`, `last_read_seq`, and `left_at`.
- **Direct Messaging & Group Lifecycle (`M2.2`, `M2.8`, `M2.11`):**
  - `src/Nexus.Web/Modules/Conversations/ConversationEndpoints.cs`: `GET /api/conversations`, atomic `ON CONFLICT` DM upsert (`POST /api/conversations/dm`) enforcing `Unverified` daily new-DM quota (`SEC-20b`), `Idempotency-Key` group creation (`POST /api/conversations/groups`), member management (`GET/POST/DELETE /members`), rejoin history hiding (`visible_from_seq = next_seq`), `ConditionalTransition` ownership transfer (`POST /transfer-ownership`), owner leave guard, and admin promotion (`PUT /admins/{userId}`).
  - `src/Nexus.Web/Modules/People/SafetyEndpoints.cs`: `GET/PUT/DELETE /api/people/blocks`, `POST /api/people/reports` (persisted + `USER_REPORTED` audit log); blocks immediately deny DM open and DM sends.
- **Transactional Messaging, Delta Sync & Read State (`M2.3`, `M2.5`, `M2.7`):**
  - `src/Nexus.Web/Modules/Messaging/MessagingEndpoints.cs`: single-transaction `POST /api/conversations/{id}/messages` with atomic `UPDATE conversations SET next_seq = next_seq + 1, change_seq = change_seq + 1 RETURNING next_seq, change_seq`, `client_msg_id` idempotency replay and `409 Conflict` (`SEC-19`), cursor-paged `GET /messages` (`<= 50`), `GET /changes?after=N` respecting `visible_from_seq` and returning `410 Gone` (`resync_required`) when behind `min_available_change_seq - 1`, `GET /api/sync/reconcile`, and atomic `GREATEST(last_read_seq, LEAST(@seq, @maxSeq))` on `PUT /read`.
- **SignalR Fan-out, Presence & Typing (`M2.4`, `M2.10`):**
  - `src/Nexus.Web/Modules/Messaging/MessagingHub.cs`, `MessagingFanoutService.cs`, `PresenceAndTypingTracker.cs`: `/hubs/messaging` strictly uses `user:{id}` groups (no client-joined conversation groups), resolves recipients from PostgreSQL, pushes `ConversationChanged` / `ReadStateUpdated`, and keeps online presence + 2-second throttled `TypingIndicator` in memory.
- **React Client (`M2.6`, `M2.9`):**
  - Added `@microsoft/signalr` (`npm audit --audit-level=high` = 0 vulnerabilities), `src/Nexus.Client/vite.config.ts` `/hubs` WebSocket proxy (`ws: true`), `src/Nexus.Client/src/markdown.ts` (HTML-escaping safe Markdown subset blocking `javascript:`/`data:`/`vbscript:` links), `src/Nexus.Client/src/offlineQueue.ts` (IndexedDB outbox + sync cursors + reconnect jitter), and `src/Nexus.Client/src/chat.tsx` (optimistic offline queue + replay, SignalR reconnect/reconcile, `/api/version` update banner, `New messages` divider `UX-09`, typing indicator, read-state sync, group creation with `Idempotency-Key`, and Directory `"Message"` DM launch).
- **Tests (`M2.12`):**
  - `tests/Nexus.IntegrationTests/MessagingM2Tests.cs`: 6 Testcontainers PostgreSQL 17 integration tests verifying concurrent DM race, `Unverified` daily quota, multi-client SignalR fan-out & outsider/SystemAdmin denial (`SEC-03`, `SEC-05`, `SEC-07`, `SEC-15`), `client_msg_id` concurrent retry & `409` (`SEC-19`), offline delta sync + `410 Gone` + monotonic read state (`SEC-17`), group `Idempotency-Key` + member removal revoking access + rejoin hiding history + ownership transfer (`SEC-18`), and DM block/report (`M2.11`).
  - `src/Nexus.Client/tests/markdown.test.mjs`: 3 Node unit tests verifying `SEC-10` XSS/URL sanitization and safe Markdown subset.

## 2. Verification commands run

```powershell
dotnet build Nexus.slnx --no-restore -c Release
# Result: Build succeeded. 0 Warning(s), 0 Error(s)

$env:Path = "C:\Users\AI\AppData\Local\Programs\DockerDesktop\resources\bin;$env:Path"
dotnet test Nexus.slnx --no-build -c Release
# Result: Passed! - Failed: 0, Passed: 113, Skipped: 0, Total: 113, Duration: 16 s

npm.cmd --prefix src/Nexus.Client run test
# Result: 3/3 passed (SEC-10 Markdown XSS & URL safety)

npm.cmd --prefix src/Nexus.Client run build
# Result: tsc --noEmit && vite build succeeded (dist/ built in 299ms)

pwsh -File ./scripts/audit/Test-SecurityGuardrails.ps1
# Result: Security guardrails OK: scanned 91 files (0 violations)

npm.cmd --prefix src/Nexus.Client audit --audit-level=high
# Result: found 0 vulnerabilities

dotnet list Nexus.slnx package --vulnerable --include-transitive
# Result: Nexus.Web and Nexus.IntegrationTests have no vulnerable packages
```

## 3. Deferred / Next steps

- **Deferred from `M2.12`:** Headless Playwright browser E2E suite under `tests/e2e/` (backend HTTP + live multi-client SignalR integration tests and Node `SEC-10` tests are green; add browser Playwright tests when wiring `tests/e2e/`).
- **Next milestone options on `develop`:**
  - **M3 — Files (`M3.1`–`M3.6`):** Two-stage uploads (`client_upload_id`), magic-byte allowlist, authorized downloads (`<= 60s` signed URLs, `SEC-04`/`SEC-11`), image paste/thumbnails (`UX-08`), conversation Files tab & quotas, ClamAV quarantine/scan.
  - **M1c — Google OIDC Linking (`M1.12`–`M1.14`):** Optional Google linking with password reauth (`SEC-22`, `SEC-23`).
  - **M4 — Notifications & Outbox (`M4.1`–`M4.6`):** Transactional outbox worker, `@mentions`, notification center, mute/quiet hours, Web Push (VAPID).
