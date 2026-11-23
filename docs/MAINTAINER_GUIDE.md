# Maintainer Guide

- Status: active
- Last updated: 2026-11-23 (M0.1 baseline)
- Scope: day-to-day engineering workflow — setup, build, test, migrations, security checks, docs maintenance, publish, and deployment.

## 1. Prerequisites

- **.NET SDK:** pinned in [`global.json`](../global.json) (`10.0.401` on the preparation machine; verify CI matches).
- **Node.js:** Node.js 24 with npm.
- **Container runtime (M0.2+):** Docker or compatible runtime for Testcontainers PostgreSQL integration tests.
- **PowerShell:** `pwsh` (PowerShell 7+) or Windows PowerShell 5.1 for audit scripts under `scripts/audit/`.

On Windows, use `npm.cmd` wherever Linux/macOS instructions use `npm`.

## 2. Local development commands

Run from the repository root (`d:\develop\nexus-workplace`):

```powershell
dotnet restore Nexus.slnx
dotnet build Nexus.slnx --no-restore
dotnet test Nexus.slnx --no-build
npm.cmd --prefix src/Nexus.Client ci
```

Run the backend host (terminal 1):

```powershell
dotnet run --project src/Nexus.Web --launch-profile http
```

Run the Vite client dev server (terminal 2):

```powershell
npm.cmd --prefix src/Nexus.Client run dev
```

Open `http://127.0.0.1:5173`. Vite proxies `/api/*` to the ASP.NET Core host on `http://127.0.0.1:5080`.

## 3. Publish and integrated smoke verification

Stop the development host if it is bound to port 5080, then build and run the published single-origin artifact:

```powershell
dotnet publish src/Nexus.Web -c Release -o artifacts/publish
Push-Location artifacts/publish
dotnet Nexus.Web.dll --urls http://127.0.0.1:5080
```

Verify:
1. `GET http://127.0.0.1:5080/api/version` returns JSON with the assembly version.
2. `GET http://127.0.0.1:5080/` serves the built React app.
3. A direct browser request to a client-side route (e.g., `/communities/general`) falls back to `index.html`.
4. An unknown API route (e.g., `/api/does-not-exist`) returns HTTP `404`.

Stop with `Ctrl+C` and run `Pop-Location`.

## 4. Testing discipline

- **Integration tests (`tests/Nexus.IntegrationTests/`):** xUnit tests exercising real HTTP endpoints, SignalR hubs, authorization policies, and PostgreSQL (via Testcontainers from M0.2+).
- **End-to-end tests (`tests/e2e/`):** Playwright multi-user and multi-device browser scenarios (M2+).
- **Security test integrity:** Never weaken, skip, or rewrite a security test (`SEC-01`–`SEC-24`) to make a failing implementation pass. Fix the implementation.
- **Verify through normal entry points:** Record the exact commands run in the milestone handoff (`docs/implementation-plan/<milestone>/HANDOFF-*.md`) before marking a roadmap item complete.

## 5. Database migrations (M0.2+)

- Use separate **runtime** and **migration** PostgreSQL roles.
- Never run schema migrations using the runtime application role in production.
- Verify TLS on database connections. When connecting through Supabase's pooler, always use **Session Mode on port 5432**.
- Every schema change must update [`DATA_MODEL.md`](DATA_MODEL.md).

## 6. Security & quality gates (M0.7)

CI ([`.github/workflows/ci.yml`](../.github/workflows/ci.yml)) runs the same restore/build/test commands as local development plus:

- Secret scanning (`Gitleaks`)
- Dependency vulnerability checks (`dotnet list Nexus.slnx package --vulnerable --include-transitive`, `npm audit --audit-level=high`, `Trivy`)
- Roslyn security analyzers (`Directory.Build.props` promotes security CA rules to build errors)
- Static guardrails rejecting `AllowAnyOrigin`, disabled TLS certificate validation, Supabase port `6543`, and privileged storage credentials in `src/Nexus.Client/`:

```powershell
pwsh -File ./scripts/audit/Test-SecurityGuardrails.ps1
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1 -Check
```

## 7. Documentation maintenance

- When making an architectural or product decision: add a card in `docs/decisions/` (see [`decisions/README.md`](decisions/README.md)) and run `Build-KnowledgeIndex.ps1`.
- When discovering a non-obvious runtime/infra/library behavior: add a card in `docs/facts/<area>/` (see [`facts/README.md`](facts/README.md)) and run `Build-KnowledgeIndex.ps1`.
- When completing a milestone item: update [`implementation-plan/ROADMAP.md`](implementation-plan/ROADMAP.md), update the active `HANDOFF-*.md`, and fold permanent contracts into the canonical docs (`ARCHITECTURE.md`, `SECURITY_AND_AUTHZ.md`, `DATA_MODEL.md`, `API_CONTRACTS.md`, `SYNC_PROTOCOL.md`).

## 8. Observability & operations (M0.6 / M8)

- Structured JSON logging via Serilog + OpenTelemetry tracing (M0.6).
- **Never log** message bodies, passwords, cookies, or tokens.
- Health endpoints: `/healthz` (liveness), `/readyz` (readiness), `/api/version` (client reload trigger).
- Detailed deployment, backup, restore, key-rotation, and incident runbooks will be added under `docs/implementation-plan/m8-hardening/` before pilot launch (M8.6).
