# API and Realtime Contracts

- Status: active (living contract — M0.1 baseline)
- Last updated: 2026-11-23
- Scope: HTTP API conventions, pagination limits, SignalR hub contracts, and client/server version coordination.

## 1. Global HTTP conventions

- **Same-origin BFF:** All API routes are rooted under `/api/*` on the same origin as the React SPA.
- **Unknown API routes:** `/api/{**path}` returns HTTP `404` (never falls through to `index.html`).
- **Pagination cap:** Every list endpoint is cursor-paginated and capped at **at most 50 entries per page**.
- **Idempotency:**
  - Message sends use `client_msg_id` (see [`SYNC_PROTOCOL.md`](SYNC_PROTOCOL.md)).
  - Non-message mutating commands (e.g., create group, create community) use the `Idempotency-Key` header (M0.5).
  - Conflicting payloads under an existing key return HTTP `409 Conflict`.
- **Authentication & status errors:**
  - Unauthenticated requests to protected routes return `401`.
  - Authenticated requests failing `IAuthorizationService.Can` or `ActiveUserMiddleware` return `403` (or `401` on revoked session).
  - Sign-in failures return generic error messages that do not disclose username existence.

## 2. Route scopes

- Account, invitation preview and redemption, and Organization creation and listing start at `/api/auth` or `/api/organizations`. A newly registered account has zero memberships.
- Prefix the Organization resource paths in the endpoint table with `/api/organizations/{organizationId}` in place of `/api`. This covers Directory, HR, Communities, channels, posts, chat, files and sync. The server requires active membership and authorizes access to the named resource. `GET /api/organizations/{organizationId}/me` returns the membership profile.
- Prefix Personal Chat, files, sync and safety paths with `/api/personal` in place of `/api`. `GET /api/personal/people/directory` exposes accepted, active, unblocked contacts. Use `/api/personal/contacts/requests` to list incoming requests or request a connection by exact username, `PUT /api/personal/contacts/requests/{id}` to respond, and `POST /api/personal/contacts/links` or `POST /api/personal/contacts/links/redeem` to share or request a connection using a private link.
- Use `GET|POST /api/organizations/{organizationId}/invitations` to list or issue invitations, `DELETE /api/organizations/{organizationId}/invitations/{id}` to revoke, and `POST /api/organizations/invitations/{preview|redeem}` to open or confirm an invitation. The seven-day token is stored as a hash and redeemed once. TOTP enforcement on workspace settings is configurable via `NexusOrganizations:RequireAdminMfa` (default `false`).
- Use `GET /api/organizations/{organizationId}/members`, `DELETE /api/organizations/{organizationId}/members/{memberId}` and `PUT /api/organizations/{organizationId}/members/{memberId}/administrator` for scoped membership administration. Only the Owner can change a member's general administrator role.
- Organization, invitation, membership, contact and contact-request lists accept `limit` and the `after` cursor from `nextAfter`. New account and scope-management mutations that use browser cookie authentication require a CSRF token from `GET /api/auth/csrf`.

### Endpoint reference

| Method & path | Auth | Response | Owner |
|---|---|---|---|
| `GET /api/version` | Public (`AllowAnonymous`) | `{ "version": "<major.minor.build>" }` — client reloads when version changes across deploys (M2.6) | `src/Nexus.Web/Infrastructure/Observability/HealthEndpoints.cs` |
| `GET /healthz` | Public (`AllowAnonymous`) | `{ "status": "Healthy", "checks": [...] }` — process liveness probe (excluded from rate limiting) | `src/Nexus.Web/Infrastructure/Observability/HealthEndpoints.cs` |
| `GET /readyz` | Public (`AllowAnonymous`) | `{ "status": "Healthy", "checks": [...] }` — dependency readiness probe (includes PostgreSQL check) | `src/Nexus.Web/Infrastructure/Observability/HealthEndpoints.cs` |
| `GET /api/auth/csrf`, `GET /api/auth/registration-policy` | Public (`AllowAnonymous`) | Antiforgery token & registration mode / trial confidentiality notice | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `POST /api/auth/register`, `POST /api/auth/login`, `POST /api/auth/reset-password` | Public (rate-limited) | Open trial registration with zero memberships, sign-in (generic `401` + TOTP challenge), one-time operator-issued reset link redemption | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `GET /api/auth/me`, `POST /api/auth/logout`, `POST /api/auth/change-password` | Authenticated (`Active`) | Profile with `isUnverified` flag, sign-out, password change revoking other sessions & aborting SignalR | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `GET /api/auth/sessions`, `DELETE /api/auth/sessions/{id}`, `POST /api/auth/sessions/revoke-others` | Authenticated (`Active`) | Signed-in device list (`<= 50`) and remote sign-out | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `POST /api/auth/mfa/setup`, `POST /api/auth/mfa/enable` | Authenticated (`Active`) | RFC 6238 TOTP authenticator enrollment + recovery codes | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `GET /api/admin/accounts`, `POST /api/admin/accounts/{id}/{verify,rename,status}` | Verified Admin + TOTP MFA | Scoped member list (`<= 50`), employee verification, Directory rename and suspension within the Organization | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `GET /api/org/departments`, `POST /api/org/employees/import?dryRun=true\|false`, `GET /api/org/employees/match-suggestions` | Active (`departments`); Verified `HRAdmin` + MFA (`import`); Verified `HRAdmin`/`SystemAdmin` + MFA (`suggestions`) | Department list (`<= 50`), idempotent HR CSV upsert with dry-run diff report, and `Unverified` → employee match suggestions (`M1.9`) | `src/Nexus.Web/Modules/Org/OrgEndpoints.cs` |
| `GET /api/people/directory?q=&departmentId=&limit=50` | Authenticated (`Active`) | Vietnamese diacritic-insensitive (`unaccent` + `pg_trgm`) directory search returning minimal profiles; hides `Disabled`/`Former` (`M1.10`) | `src/Nexus.Web/Modules/People/PeopleEndpoints.cs` |
| `PUT /api/people/me/status`, `POST/DELETE /api/people/me/avatar`, `GET /api/people/{id}/avatar` | Authenticated (`Active`) | Custom status (`UX-06`, max 100 chars) and server-side SkiaSharp re-encoded 256×256 PNG avatars stripping EXIF/metadata (`M1.11`) | `src/Nexus.Web/Modules/People/PeopleEndpoints.cs` |
| `GET /api/people/blocks`, `PUT/DELETE /api/people/blocks/{targetUserId}`, `POST /api/people/reports` | Authenticated (`Active`) | DM blocking (blocks DM open & sends immediately) and audited user reporting (`USER_REPORTED`) (`M2.11`) | `src/Nexus.Web/Modules/People/SafetyEndpoints.cs` |
| `GET /api/conversations`, `POST /api/conversations/dm`, `POST /api/conversations/groups` | Authenticated (`Active`) | Conversation list (`<= 50`) with unread count & online status, atomic `dm_key` upsert (enforcing `Unverified` daily new-DM limit), and `Idempotency-Key` group creation (`M2.2`, `M2.8`) | `src/Nexus.Web/Modules/Conversations/ConversationEndpoints.cs` |
| `GET/POST/DELETE /api/conversations/{id}/members`, `POST /leave`, `POST /transfer-ownership`, `PUT /admins/{userId}` | Active member / Group Admin / Owner | Group member list (`<= 50`), add/rejoin (`visible_from_seq = next_seq`), removal revoking access immediately (`SEC-18`), `ConditionalTransition` ownership transfer, and admin promotion (`M2.8`) | `src/Nexus.Web/Modules/Conversations/ConversationEndpoints.cs` |
| `POST/GET /api/conversations/{id}/messages`, `GET /changes?after=N`, `PUT /read`, `GET /api/sync/reconcile` | Active member (`IAuthorizationService.Can`) | Single-tx `seq`/`change_seq` send with `client_msg_id` idempotency & `409` (`SEC-19`) + optional `attachmentIds` Stage-2 attach (`M3.1`), cursor history (`<= 50`), delta events respecting `visible_from_seq` + `410 Gone` stale-cursor trigger (`SEC-17`), atomic `GREATEST(last_read_seq)` read state, and reconnect reconcile (`M2.3`, `M2.5`, `M2.7`) | `src/Nexus.Web/Modules/Messaging/MessagingEndpoints.cs` |
| `POST /api/conversations/{id}/attachments`, `GET /api/conversations/{id}/files` | Active member (`FileUpload` / `ConversationRead`) | Stage-1 upload (`clientUploadId` idempotent, magic-byte allowlist, SkiaSharp EXIF strip + `<= 400px` PNG thumbnail, ClamAV `zINSTREAM` / fail-closed scan, per-user & per-conversation quotas with 80% warning) and cursor-paged (`<= 50`) Files tab respecting `visible_from_seq` (`M3.1`, `M3.2`, `M3.4`, `M3.5`, `M3.6`) | `src/Nexus.Web/Modules/Files/FilesEndpoints.cs` |
| `GET /api/files/{id}/download`, `GET /api/files/{id}/thumbnail`, `GET /api/files/{id}/preview`, `POST /api/files/{id}/signed-url` | Active member (`FileDownload` + `visible_from_seq` / community membership) | Authorized download with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`, private PNG thumbnail, and full-resolution re-encoded image preview. The link endpoint returns a scoped application download URL that rechecks current authorization. | `src/Nexus.Web/Modules/Files/FilesEndpoints.cs` |
| `GET/POST /api/communities`, `POST /{id}/join`, `GET/POST/DELETE /{id}/members`, `PUT /{id}/admins/{uid}`, `POST /{id}/leave`, `POST /{id}/transfer-ownership`, `POST /{id}/official`, `POST /{id}/emergency-transfer-ownership`, `POST /{id}/channels`, `PUT /{id}/department-mapping`, `POST /{id}/sync-department` | Active member / Community Owner & Admin / Verified `CommsAdmin`+MFA (`official`) / Verified `SystemAdmin`+MFA (`emergency-transfer-ownership`) | Teams-style Community CRUD (`Idempotency-Key`, atomic main feed with 0 default sub-channels per `P0-D04`), optional sub-channels (`Members` \| `AdminsOnly`), Official designation (`SEC-20b`), audited emergency ownership transfer, and idempotent HR department sync (`M5.0`–`M5.4`) | `src/Nexus.Web/Modules/Conversations/CommunityEndpoints.cs` |
| `GET/POST /api/channels/{conversationId}/posts`, `PUT /api/posts/{id}/pin`, `POST /api/posts/{id}/comments`, `PUT/DELETE /api/posts/{id}/reactions/{emoji}`, `PUT /api/posts/{id}/acknowledge`, `GET /api/posts/{id}/acknowledgments?format=json\|csv`, `POST /api/posts/{id}/remind`, `PUT /api/polls/{id}/vote` | Active community member (`ConversationRead` / `MessageSend` respecting `AdminsOnly`) | Pinned-first channel feed (`<= 50`), announcements with idempotent read acknowledgments + formula-safe CSV export + audited reminders (`CM-03`), comments, idempotent reactions, and transactional poll voting (`CM-01`, `M5.3`, `M5.5`, `M5.6`) | `src/Nexus.Web/Modules/Messaging/CommunityFeedEndpoints.cs` |

## 3. SignalR hub contract

- **Origin enforcement:** Reject WebSocket connections whose `Origin` header does not match the configured same-origin host (`M0.3`).
- **Active user enforcement:** `ActiveUserHubFilter` checks account status and security stamp on connect and every hub invocation; password change or disabling an account aborts open connections (`M1.5`, `M1.6`).
- **Scope and group naming.** Connect to `/hubs/personal/messaging` or `/hubs/organizations/{organizationId}/messaging`. The server adds the connection to `scope:{scopeId}:user:{userId}`, using the empty UUID as the Personal scope key. It reads active recipients from PostgreSQL before sending `ConversationChanged(conversationId, changeSeq)` and `ReadStateUpdated(conversationId, lastReadSeq)`. Revoking an Organization membership closes that Organization's connections for the member.
- **Presence & typing (`M2.10`):** In-memory `PresenceAndTypingTracker`; `SendTyping(conversationId)` verifies DB membership via `IAuthorizationService.Can` and throttles `TypingIndicator` to 1 event per 2 seconds per `(user, conversation)`.
