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

## 3. Publish, Desktop MSI installer, and Supabase + Render deployment

Use the standardized PowerShell scripts under [`scripts/`](../scripts/README.md) (modeled after `RevitR4Converter`):

```powershell
# Full verification (guardrails, knowledge index, Release build, 113 PostgreSQL tests, client XSS tests + Vite build, Desktop build)
pwsh -File .\scripts\Run-Tests.ps1

# Build single-origin server package (artifacts\server-package\)
pwsh -File .\scripts\build\Build-ServerPackage.ps1

# Build per-user Windows Desktop MSI (WebView2, no admin UAC required) pointing at your server
pwsh -File .\scripts\build\Build-DesktopInstaller.ps1 -ProductVersion 0.2.0 -ServerUrl "https://<your-service>.onrender.com" -NoPause

# Or run everything in one command:
pwsh -File .\scripts\build\Build-All.ps1 -ProductVersion 0.2.0 -ServerUrl "https://<your-service>.onrender.com"
```

For step-by-step cloud deployment on **Supabase (PostgreSQL Session Pooler `:5432` + Private Storage) + Render**, see [`RUNBOOK-supabase-render.md`](implementation-plan/m2-messaging-sync/RUNBOOK-supabase-render.md).

Manual integrated smoke verification:

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

### Operator-issued password reset

Run the recovery command from a trusted operator terminal using the deployment's runtime configuration. The command prints one reset code that expires in 30 minutes.

```powershell
dotnet Nexus.Web.dll --issue-account-reset --AccountRecovery:Username <username>
```

Give the code to the account holder through your approved private channel. From the signed-out Nexus screen, they choose **Reset password**, paste the code, and confirm a new password. Successful redemption revokes their other sessions and returns them to sign in.

### Web Push setup and iOS installation

Apply the Web Push subscription migration with the migration role. Set `NexusPush__VapidPrivateKeyPem` to a P-256 private key in PEM format and `NexusPush__VapidSubject` to an operator `mailto:` or HTTPS URL. To provision a key, run `openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:prime256v1 -out nexus-vapid-private.pem` and place it in the deployment's secret store. Use the same key across instances. After changing it, use **Disable push** and **Enable push** on existing devices to renew their subscriptions.

- Serve the packaged app over trusted HTTPS. Configure outbound HTTPS to the browser providers needed by the deployment. Nexus limits accepted endpoints to recognized FCM, Mozilla Push, Apple Push and Windows Push hosts and keeps redirects disabled. [Apple Web Push requirements](https://developer.apple.com/documentation/usernotifications/sending-web-push-notifications-in-web-apps-and-browsers)
- On desktop or Android, open **Activity → Enable push** and approve notifications in the browser. On iOS or iPadOS 16.4 or later, open Nexus in Safari, choose **Share → Add to Home Screen**, and launch Nexus from the installed icon. Open **Activity → Enable push** and approve the browser prompt. Verify delivery from a second test account while the recipient's conversation remains unread. [WebKit Home Screen Web Push](https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/)
- Push requests have empty bodies. The service worker displays a generic alert and opens Activity. The worker checks unread state, resource access, the current session, conversation preferences, and quiet hours before sending. A provider `404` or `410` invalidates the endpoint. **Disable push** removes the current browser registration. Sign-out and remote session revocation stop delivery for the affected session.

### Optional email digest

Set `NexusDigest__Enabled=true`, `NexusDigest__Host`, `NexusDigest__Port`, and `NexusDigest__From` to enable delivery through a trusted SMTP relay. The default port is 587 with STARTTLS. Configure `NexusDigest__Username` and `NexusDigest__Password` in the secret store when the relay requires credentials. The default `NexusDigest__DelayHours` is 4. Use an approved relay and a test recipient to validate the deployment.

Opt in under **Activity → Email digest**. Personal activity uses a confirmed account address. Organization activity uses the active employee address attached to verified membership in that Organization. The digest contains an unread item count, and the worker rechecks resource access, quiet hours, mode, and push outcome before delivery. SMTP retries use a stable message ID for the same outbox job. A relay may accept a retry after a connection loss, so SMTP delivery has an at-least-once boundary.
