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

## 2. Implemented endpoints (M0.1 / M0.6)

| Method & path | Auth | Response | Owner |
|---|---|---|---|
| `GET /api/version` | Public (`AllowAnonymous`) | `{ "version": "<major.minor.build>" }` — client reloads when version changes across deploys (M2.6) | `src/Nexus.Web/Infrastructure/Observability/HealthEndpoints.cs` |
| `GET /healthz` | Public (`AllowAnonymous`) | `{ "status": "Healthy", "checks": [...] }` — process liveness probe (excluded from rate limiting) | `src/Nexus.Web/Infrastructure/Observability/HealthEndpoints.cs` |
| `GET /readyz` | Public (`AllowAnonymous`) | `{ "status": "Healthy", "checks": [...] }` — dependency readiness probe (includes PostgreSQL check) | `src/Nexus.Web/Infrastructure/Observability/HealthEndpoints.cs` |
| `GET /api/auth/csrf`, `GET /api/auth/registration-policy` | Public (`AllowAnonymous`) | Antiforgery token & registration mode / trial confidentiality notice | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `POST /api/auth/register`, `POST /api/auth/login`, `POST /api/auth/reset-password` | Public (rate-limited) | Open trial registration (`Unverified`), sign-in (generic `401` + TOTP challenge), one-time admin reset link redemption | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `GET /api/auth/me`, `POST /api/auth/logout`, `POST /api/auth/change-password` | Authenticated (`Active`) | Profile with `isUnverified` flag, sign-out, password change revoking other sessions & aborting SignalR | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `GET /api/auth/sessions`, `DELETE /api/auth/sessions/{id}`, `POST /api/auth/sessions/revoke-others` | Authenticated (`Active`) | Signed-in device list (`<= 50`) and remote sign-out | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `POST /api/auth/mfa/setup`, `POST /api/auth/mfa/enable` | Authenticated (`Active`) | RFC 6238 TOTP authenticator enrollment + recovery codes | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `GET /api/admin/accounts`, `POST /api/admin/accounts/{id}/{verify,rename,status,reset-link}` | Verified Admin + TOTP MFA | Account listing (`<= 50`), HR employee verification, impersonator rename, disablement with immediate session/SignalR revocation, audited one-time reset links | `src/Nexus.Web/Modules/Identity/IdentityEndpoints.cs` |
| `GET /api/org/departments`, `POST /api/org/employees/import?dryRun=true\|false`, `GET /api/org/employees/match-suggestions` | Active (`departments`); Verified `HRAdmin` + MFA (`import`); Verified `HRAdmin`/`SystemAdmin` + MFA (`suggestions`) | Department list (`<= 50`), idempotent HR CSV upsert with dry-run diff report, and `Unverified` → employee match suggestions (`M1.9`) | `src/Nexus.Web/Modules/Org/OrgEndpoints.cs` |
| `GET /api/people/directory?q=&departmentId=&limit=50` | Authenticated (`Active`) | Vietnamese diacritic-insensitive (`unaccent` + `pg_trgm`) directory search returning minimal profiles; hides `Disabled`/`Former` (`M1.10`) | `src/Nexus.Web/Modules/People/PeopleEndpoints.cs` |
| `PUT /api/people/me/status`, `POST/DELETE /api/people/me/avatar`, `GET /api/people/{id}/avatar` | Authenticated (`Active`) | Custom status (`UX-06`, max 100 chars) and server-side SkiaSharp re-encoded 256×256 PNG avatars stripping EXIF/metadata (`M1.11`) | `src/Nexus.Web/Modules/People/PeopleEndpoints.cs` |

## 3. SignalR hub contract (planned M0 / M2.4)

- **Origin enforcement:** Reject WebSocket connections whose `Origin` header does not match the configured same-origin host (M0.3).
- **Active user enforcement:** `ActiveUserHubFilter` checks account status on connect and hub invocations; disabling an account aborts open connections (M1.6).
- **Group naming:** `user:{id}` only. Server reads conversation recipients from PostgreSQL and pushes `(conversation_id, change_seq)` notifications.
- **Presence & typing (M2.10):** Throttled and kept in memory only (no Redis).
