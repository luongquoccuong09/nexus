# Plan: M0 Foundation & Pre-M1 Contracts (`develop`)

- Status: draft (initial design on `develop` from `main@ded8f0a`)
- Date: 2026-11-23
- Scope: Complete `P0.2`–`P0.3` contracts and `M0.2`–`M0.9` foundation so `M1` (Identity & Directory) and `M2` (Messaging & Sync) can build on verified security, authorization, persistence, and sync primitives.

## 1. Starting Baseline

- **Branch:** `develop` (branched from `main` at `ded8f0a`).
- **Completed:** `M0.1` same-origin ASP.NET Core (`net10.0`) + React/Vite/TS solution, verified locally (`dotnet test Nexus.slnx` passes 1/1).
- **Environment check (2026-11-23):**
  - `.NET SDK 10.0.401` and `Node.js 24` are ready.
  - `docker`, `podman`, and `psql` are **not yet installed** on this workstation (`wsl.exe` is available).
  - Because `M0.2` requires PostgreSQL (and Testcontainers requires a container runtime), we split M0 into **no-DB slices** (executable immediately) and **DB/container slices** (executed as soon as PostgreSQL/Docker is available).

## 2. Target `src/Nexus.Web` Modular Monolith Layout

Establish shared infrastructure seams once in M0 so all 8 feature modules follow identical patterns:

```text
src/Nexus.Web/
├── Program.cs                        # Minimal entry point -> NexusHost.Build(args).Run()
├── NexusHost.cs                      # Composition root: registers Infrastructure + Modules
├── Infrastructure/
│   ├── Security/
│   │   ├── SecurityDefaultsExtensions.cs   # FallbackPolicy, CORS, CSP, Antiforgery, RateLimiting, Origin checks (M0.3)
│   │   ├── ActiveUserMiddleware.cs         # Rejects Disabled accounts on HTTP (M0.3)
│   │   ├── ActiveUserHubFilter.cs          # Rejects Disabled accounts on SignalR (M0.3)
│   │   ├── IAuthorizationService.cs        # Can(ActorContext, ResourceAction, ResourceContext) (M0.4)
│   │   └── NexusAuthorizationService.cs    # Deny-by-default policy evaluation pipeline (M0.4)
│   ├── Data/
│   │   ├── NexusDbContext.cs               # EF Core DbContext + module IEntityTypeConfiguration discovery (M0.2)
│   │   ├── DbConnectionOptions.cs          # Separate runtime vs migration connection strings + TLS enforcement (M0.2)
│   │   └── Migrations/                     # EF Core migrations (M0.2)
│   ├── Idempotency/
│   │   ├── IdempotencyKeyEntity.cs         # idempotency_keys table (M0.5)
│   │   ├── IdempotencyEndpointFilter.cs    # Idempotency-Key header replay / 409 Conflict (M0.5)
│   │   └── ConditionalTransition.cs        # Conditional UPDATE helper (M0.5)
│   ├── Observability/
│   │   ├── LoggingExtensions.cs            # Serilog JSON + redaction of bodies/cookies/tokens (M0.6)
│   │   └── HealthEndpoints.cs              # /healthz, /readyz, /api/version (M0.6)
│   └── Storage/
│       ├── IFileStorage.cs                 # Private storage abstraction (M0.9)
│       ├── LocalFileStorage.cs             # Local volume implementation for dev/demo (M0.9)
│       ├── IFileScanner.cs                 # Malware scan seam (M0.9)
│       └── PrototypeFileScannerStub.cs     # Returns skipped_prototype until ClamAV in M3.6 (M0.9)
└── Modules/
    ├── Identity/
    ├── Org/
    ├── People/
    ├── Conversations/
    ├── Messaging/
    ├── Files/
    ├── Notifications/
    └── Admin/
```

Every feature module exposes two static registration methods (no cross-module direct DbContext mutation):
- `builder.Services.Add<Module>Module(builder.Configuration)`
- `app.Map<Module>Endpoints()`

## 3. Execution Slices

### Slice A — Complete Pre-Implementation Contracts (`P0.2` & `P0.3`)
*No Docker/PostgreSQL required. Unblocks `M0.4` and `M0.5`.*

1. **`P0.2` — Complete [`docs/SECURITY_AND_AUTHZ.md`](../../SECURITY_AND_AUTHZ.md):**
   - Formalize the full capability matrix across `(ActorRole, TrustLevel, AccountStatus, ResourceKind, Action)`:
     - Account & trial rules (`Active`/`Disabled`, `Verified`/`Unverified` badge & daily DM limit, trial directory access).
     - DM & Group chat rules (open DMs per `P0-D01`, blocking/reporting, group add/remove, history visibility on removal/rejoin, ownership transfer).
     - Teams-style Community & optional sub-channel rules per updated [`P0-D04`](../../decisions/2026-11-23-p0-d04-community-channel-hierarchy.md) (creating communities, joining public vs private, Owner/Admin `+ Add channel`, posting to `admins_only` community/channels, official designation by verified `CommsAdmin`, `@channel`/`@all`).
     - Administrative boundaries (`SystemAdmin`, `HRAdmin`, `CommsAdmin` never have implicit read access to private conversations).
   - Define the exact `ActorContext`, `ResourceAction`, and `ResourceContext` types used by `M0.4`.

2. **`P0.3` — Complete [`docs/SYNC_PROTOCOL.md`](../../SYNC_PROTOCOL.md):**
   - Lock exact semantics for:
     - `client_msg_id` (UUIDv7, unique per `(sender_id, client_msg_id)`, payload hash comparison for `409 Conflict`).
     - Dual sequence allocation (`seq` vs `change_seq`) inside one PostgreSQL transaction.
     - Crash-between-commit-and-broadcast recovery (client reconnect reconciliation + bounded polling fallback).
     - `conversation_events` retention window and `410 Gone` / `resync_required` full-resync trigger.
     - Rejoin history visibility boundary (`joined_at` / `visible_from_seq`).
     - Non-message `Idempotency-Key` contract for `M0.5` (scope `(user_id, method, route, key)`, 24h TTL, SHA-256 request body hash, cached response replay, `409` on mismatch, `409`/`425` while in-flight).

### Slice B — Security Defaults, Authorization Engine & Observability (`M0.3`, `M0.4`, `M0.6`)
*No Docker/PostgreSQL required. Fully verifiable via `tests/Nexus.IntegrationTests`.*

1. **`M0.3` — Secure defaults:**
   - Authenticated `FallbackPolicy` (`[AllowAnonymous]` explicitly limited to `/api/version`, `/healthz`, `/readyz`, SPA static shell, and auth entry points in M1).
   - `ActiveUserMiddleware` and `ActiveUserHubFilter` backed by `IAccountStatusLookup` (in-memory/stub in M0.3, EF Core/Identity-backed in M1).
   - Strict CORS policy from configuration (explicitly forbids `AllowAnyOrigin`), WebSocket `Origin` validation middleware, antiforgery, CSP + `X-Content-Type-Options: nosniff` + `X-Frame-Options: DENY`.
   - Global request body size limit, pagination helper capping every list query to `<= 50`, and IP/user rate limiting policies.
   - Integration tests verifying: unauthenticated `/api/*` → `401`; `Disabled` account HTTP & SignalR → denied; invalid WebSocket `Origin` → rejected; page size `> 50` → rejected/clamped.

2. **`M0.4` — `IAuthorizationService.Can(actor, action, resource)`:**
   - Single centralized evaluation pipeline:
     1. Reject if `actor` is null or `Status != Active`.
     2. Evaluate explicit policy table mapped from `docs/SECURITY_AND_AUTHZ.md`.
     3. Default return `Deny` for any unmapped `(action, resource)` combination.
   - xUnit `[Theory]` test suite derived directly from every row of the `P0.2` matrix (both allow and deny cases).

3. **`M0.6` — Observability & health endpoints:**
   - Serilog structured JSON console sink with redaction policy ensuring `Authorization`, `Cookie`, `Set-Cookie`, password fields, and message bodies are never logged.
   - OpenTelemetry tracing/metrics wiring (ASP.NET Core + HttpClient).
   - `GET /healthz` (process liveness), `GET /readyz` (dependency readiness — checks PostgreSQL once M0.2 lands), `GET /api/version`.

### Slice C — PostgreSQL, EF Core, Idempotency & Storage Seams (`M0.2`, `M0.5`, `M0.9`)
*Requires container runtime (Docker/WSL2) or local/dev PostgreSQL.*

1. **`M0.2` — PostgreSQL, EF Core & migration pipeline:**
   - Add `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`, and `Testcontainers.PostgreSQL`.
   - Separate `ConnectionStrings:Runtime` (`nexus_runtime` — DML only) and `ConnectionStrings:Migration` (`nexus_migrator` — DDL owner).
   - Enforce TLS in non-local environments (`SslMode=Require` or `VerifyFull`) and document Supabase Session Mode `:5432`.
   - Integration test proving: migration role applies migrations cleanly, runtime role can CRUD application tables, and runtime role **cannot** execute DDL (`CREATE`/`DROP TABLE`).

2. **`M0.5` — `idempotency_keys` & conditional transitions:**
   - EF Core entity + migration for `idempotency_keys` (`user_id`, `scope`, `key`, `request_hash`, `status_code`, `response_json`, `expires_at`).
   - `IdempotencyEndpointFilter` applying the `P0.3` contract.
   - Reusable `ConditionalTransition` helper (`ExecuteUpdateAsync` returning affected rows → inspect state → idempotent `200`/`204` or `409 Conflict`).
   - Integration tests covering exact replay, `409` on differing payload, and expiry.

3. **`M0.9` — `IFileStorage` & `IFileScanner`:**
   - Implement `LocalFileStorage` (random object keys, never exposing raw paths or credentials to client) and `PrototypeFileScannerStub` (`skipped_prototype`).
   - Integration test uploading and retrieving a private PDF only when authorized, with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`.

### Slice D — CI Security Gates & Demo Container (`M0.7`, `M0.8`)

1. **`M0.7` — CI & static security checks:**
   - Add Roslyn security analyzers (`Microsoft.CodeAnalysis.NetAnalyzers` + security rules as errors) and a PowerShell/CI guard script (`scripts/audit/Test-SecurityGuardrails.ps1`) verifying:
     - No `AllowAnyOrigin` in `src/`
     - No disabled TLS certificate validation (`ServerCertificateCustomValidationCallback`, `DangerousAcceptAnyServerCertificateValidator`)
     - No privileged storage keys in `src/Nexus.Client/`
     - `Build-KnowledgeIndex.ps1 -Check` passes
   - Add CI workflow (GitHub Actions / selected Git host) running `dotnet restore/build/test`, `npm ci && npm run build`, `dotnet list package --vulnerable`, `npm audit`, Gitleaks, and Trivy.

2. **`M0.8` — Container & M0 Acceptance:**
   - Multi-stage `deploy/Dockerfile` (non-root user) + `deploy/docker-compose.dev.yml` (PostgreSQL with init script creating `nexus_migrator` and `nexus_runtime`).
   - Persist ASP.NET Core Data Protection keys and sessions in PostgreSQL.
   - End-to-end M0 acceptance test: dev-seeded login → write DB record → receive SignalR notification → upload & authorized download of a private PDF.

## 4. Verification & Exit Criteria for M0

- All `M0.1`–`M0.9` items checked off in [`../ROADMAP.md`](../ROADMAP.md).
- `dotnet test Nexus.slnx` green (including matrix-derived authorization tests and PostgreSQL Testcontainers tests).
- `npm.cmd --prefix src/Nexus.Client run build` and `dotnet publish src/Nexus.Web -c Release -o artifacts/publish` green.
- `pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1 -Check` green.
- Canonical docs updated (`CODEMAP.md`, `DATA_MODEL.md`, `SECURITY_AND_AUTHZ.md`, `API_CONTRACTS.md`, `SYNC_PROTOCOL.md`) and `HANDOFF-m0.md` recorded.
