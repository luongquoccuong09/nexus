# Code Map — Read Before Grepping

- Status: active
- Last updated: 2026-11-23 (M0.1 baseline)
- Audience: a new AI session or engineer picking up a feature, bug fix, or review.

Use this map before running broad searches across the repository. Recommended lookup order:

1. **§1–§2** — repository structure, module graph, and runtime entry points.
2. **§3** — "task → starting file → test file" table.
3. **`docs/codemap/`** — machine-generated symbol/context/edge indexes (populated as modules grow from M0.2+).
4. **§4** — known pitfalls by area.

This file is a **navigation map**; it does not replace [`ARCHITECTURE.md`](ARCHITECTURE.md), [`MAINTAINER_GUIDE.md`](MAINTAINER_GUIDE.md), [`SECURITY_AND_AUTHZ.md`](SECURITY_AND_AUTHZ.md), or [`SYNC_PROTOCOL.md`](SYNC_PROTOCOL.md).

## 0. Search scope

Only `src/`, `tests/`, `scripts/`, `deploy/`, and active `docs/` are source of truth. Exclude generated and historical paths when searching:

| Path | What it is | Exclude from code search? |
|---|---|---|
| `src/Nexus.Web/bin`, `obj` | .NET build outputs | Always |
| `src/Nexus.Client/node_modules`, `dist` | npm packages and built SPA bundle | Always |
| `artifacts/` | Published host output (`dotnet publish`) | Always |
| `docs/archive/` | Historical proposals and audits | Yes (reference only, never source of truth) |
| `docs/mockups/` | Static HTML interaction prototypes | Yes (UI illustration only; backend enforces access) |

```powershell
rg -n "<pattern>" src tests -g "!**/bin/**" -g "!**/obj/**" -g "!**/node_modules/**" -g "!**/dist/**"
```

## 1. System and module graph

```mermaid
flowchart LR
    CLIENT["Nexus.Client<br/>React + Vite + TypeScript"]
    HOST["Nexus.Web<br/>ASP.NET Core Host<br/>(API + SignalR + Static SPA)"]
    PG[("PostgreSQL<br/>(Session Mode :5432)")]
    STORE[("Private File Storage<br/>Supabase / Local Volume")]

    CLIENT -- "HTTP BFF (same-origin)<br/>+ SignalR WebSocket" --> HOST
    HOST --> PG
    HOST --> STORE
```

Feature modules inside `src/Nexus.Web/Modules/`:

| Module | Directory | Responsibility | Milestone |
|---|---|---|---|
| **Identity** | `src/Nexus.Web/Modules/Identity/` | Accounts, password auth, BFF sessions, TOTP MFA, Google OIDC linking, Active/Disabled & Verified/Unverified states | M1 |
| **Org** | `src/Nexus.Web/Modules/Org/` | Single Organization boundary, departments, branches, HR CSV import & employee matching | M1 |
| **People** | `src/Nexus.Web/Modules/People/` | Directory search (`unaccent`, `pg_trgm`), minimal profiles, custom status, avatars, blocking/reporting | M1 / M2 |
| **Conversations** | `src/Nexus.Web/Modules/Conversations/` | DMs (`dm_key`), group chats, communities, channels, membership, roles, official designation | M2 / M5 |
| **Messaging** | `src/Nexus.Web/Modules/Messaging/` | Messages, `seq`/`change_seq`, `conversation_events`, SignalR fanout (`user:{id}`), read state, reactions, polls, announcements | M2 / M5 / M6 |
| **Files** | `src/Nexus.Web/Modules/Files/` | Two-stage uploads, magic-byte checks, signed/streamed downloads, quotas, ClamAV quarantine/scan | M3 |
| **Notifications** | `src/Nexus.Web/Modules/Notifications/` | Transactional outbox worker, mentions, notification center, mute/quiet hours, Web Push (VAPID), email digest | M4 |
| **Admin** | `src/Nexus.Web/Modules/Admin/` | Account/role management, audit log, emergency ownership transfer, retention/break-glass, aggregate metrics | M7 |

## 2. Current runtime entry points (M0.1)

| File | Role |
|---|---|
| `src/Nexus.Web/Program.cs` | Minimal entry point calling `NexusHost.Build(args).Run()` |
| `src/Nexus.Web/NexusHost.cs` | Configures static files, `GET /api/version`, `/api/{**path}` 404 fallback, and SPA `index.html` fallback |
| `src/Nexus.Client/vite.config.ts` | Dev server on `127.0.0.1:5173`, proxies `/api` to `http://127.0.0.1:5080` |
| `src/Nexus.Client/src/main.tsx` | React client bootstrap |
| `tests/Nexus.IntegrationTests/HostTests.cs` | xUnit integration smoke tests against `NexusHost` |

## 3. Task → starting file → verification

| If you need to... | Start here | Also check / test |
|---|---|---|
| Add or change an API endpoint or host middleware | `src/Nexus.Web/NexusHost.cs` or `src/Nexus.Web/Modules/<Module>/` | `tests/Nexus.IntegrationTests/`, [`SECURITY_AND_AUTHZ.md`](SECURITY_AND_AUTHZ.md), [`API_CONTRACTS.md`](API_CONTRACTS.md) |
| Change authorization policy or role rules | `docs/SECURITY_AND_AUTHZ.md` → `IAuthorizationService` (M0.4+) | Matrix-derived integration tests (`tests/Nexus.IntegrationTests/`) |
| Change messaging, ordering, retries, or SignalR sync | `docs/SYNC_PROTOCOL.md` → `Modules/Messaging/` & `Modules/Conversations/` | `SEC-17`, `SEC-18`, `SEC-19`, Playwright multi-device tests (`tests/e2e/`) |
| Add a database table or migration | `docs/DATA_MODEL.md` → EF Core DbContext & migrations (M0.2+) | Testcontainers PostgreSQL integration tests (separate runtime vs migration roles) |
| Update client UI or API calls | `src/Nexus.Client/src/` | `npm.cmd --prefix src/Nexus.Client run build`, [`design-system/README.md`](design-system/README.md) |
| Record a product/architecture decision | `docs/decisions/README.md` | Run `pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1` |
| Record a measured runtime or infra fact | `docs/facts/README.md` | Run `pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1` |

## 4. Known pitfalls by area

- **Same-origin SPA vs API fallback:** `/api/{**path}` must return `404` rather than falling through to `index.html`; otherwise missing API routes look like HTTP 200 HTML pages to the client.
- **Supabase pooler mode:** Always use **Session Mode on port 5432** (not Transaction Mode on 6543). See [`facts/postgres/supabase-pooler-session-mode-5432.md`](facts/postgres/supabase-pooler-session-mode-5432.md).
- **Prototypes are not specs:** `docs/mockups/*.html` illustrate UI flows only; never infer backend authorization rules from client markup. Always check [`SECURITY_AND_AUTHZ.md`](SECURITY_AND_AUTHZ.md).
- **Windows npm commands:** Use `npm.cmd` in PowerShell scripts and Windows instructions to avoid execution-policy friction on `npm.ps1`.
