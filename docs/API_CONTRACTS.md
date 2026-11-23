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

## 2. Implemented endpoints (M0.1)

| Method & path | Auth | Response | Owner |
|---|---|---|---|
| `GET /api/version` | Public | `{ "version": "<major.minor.build>" }` — client reloads when version changes across deploys (M2.6) | `src/Nexus.Web/NexusHost.cs` |

Planned health endpoints (M0.6): `GET /healthz` (liveness), `GET /readyz` (readiness).

## 3. SignalR hub contract (planned M0 / M2.4)

- **Origin enforcement:** Reject WebSocket connections whose `Origin` header does not match the configured same-origin host (M0.3).
- **Active user enforcement:** `ActiveUserHubFilter` checks account status on connect and hub invocations; disabling an account aborts open connections (M1.6).
- **Group naming:** `user:{id}` only. Server reads conversation recipients from PostgreSQL and pushes `(conversation_id, change_seq)` notifications.
- **Presence & typing (M2.10):** Throttled and kept in memory only (no Redis).
