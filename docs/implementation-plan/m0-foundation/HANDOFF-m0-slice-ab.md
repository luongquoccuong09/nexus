# M0 Non-DB Foundation Handoff (`P0.2`, `P0.3`, `M0.3`, `M0.4`, `M0.6`, `M0.7`, `M0.9`)

- Branch: `develop`
- Date: 2026-11-23
- Completed items: `P0.2`, `P0.3`, `M0.3`, `M0.4`, `M0.6`, `M0.7`, `M0.9`

## What landed

1. **`P0.2` ([`docs/SECURITY_AND_AUTHZ.md`](../../SECURITY_AND_AUTHZ.md)):** Complete capability matrix covering DMs (`P0-D01`), blocking, trial directory & `Unverified` restrictions (`SEC-20b`), group chats, Teams-style Communities + optional sub-channels (`P0-D04`), official designation, ownership & emergency transfers, `@channel`/`@all`, and organization administration.
2. **`P0.3` ([`docs/SYNC_PROTOCOL.md`](../../SYNC_PROTOCOL.md)):** Complete protocol for `client_msg_id`, dual `seq`/`change_seq`, SignalR crash-between-commit-and-broadcast recovery, 30-day delta retention + `410 Gone` full-resync trigger, rejoin history boundaries, `Idempotency-Key` (24h TTL, SHA-256 request hash, `409 Conflict`), and conditional state transitions.
3. **`M0.3` (`src/Nexus.Web/Infrastructure/Security/`):** Authenticated `FallbackPolicy`, `ActiveUserMiddleware`, `ActiveUserHubFilter`, `WebSocketOriginMiddleware`, strict CORS rejecting `*` at startup, antiforgery (`X-CSRF-TOKEN`), CSP & security headers, Kestrel 10 MB body limit, `PaginationGuard` (`<= 50`), and IP rate limiter.
4. **`M0.4` (`NexusAuthorizationService`):** Centralized deny-by-default `IAuthorizationService.Can(actor, action, resource)` verified by 66 matrix-derived `[Theory]` cases in `tests/Nexus.IntegrationTests/AuthorizationMatrixTests.cs`.
5. **`M0.6` (`src/Nexus.Web/Infrastructure/Observability/`):** Serilog Compact JSON console logging with `SensitiveDataRedactingEnricher`, OpenTelemetry ASP.NET Core & HttpClient tracing/metrics, and `/healthz`, `/readyz`, `/api/version`.
6. **`M0.7` (`Directory.Build.props`, `scripts/audit/Test-SecurityGuardrails.ps1`, `.github/workflows/ci.yml`):** Roslyn security analyzers (`AnalysisModeSecurity=All`, security rules promoted to errors), static guardrails rejecting `AllowAnyOrigin`, disabled TLS cert validation, Supabase `:6543`, and client storage credentials, plus CI pipeline with `dotnet list package --vulnerable`, `npm audit`, Gitleaks, Trivy, and `Build-KnowledgeIndex.ps1 -Check`.
7. **`M0.9` (`src/Nexus.Web/Infrastructure/Storage/`):** `IFileStorage` (`LocalFileStorage` outside `wwwroot` + server-side `SupabaseFileStorage` adapter with `<= 60s` signed URL cap, random object keys, SHA-256 checksums, path-traversal guards, safe `attachment` + `nosniff` download headers) and `IFileScanner` (`PrototypeFileScannerStub` returning `skipped_prototype`).

## Commands run and verified

```powershell
dotnet build Nexus.slnx --no-restore -c Release          # 0 Warnings, 0 Errors
dotnet test Nexus.slnx --no-build -c Release             # Passed: 95, Failed: 0
dotnet list Nexus.slnx package --vulnerable --include-transitive   # 0 vulnerable
npm.cmd --prefix src/Nexus.Client run build
npm.cmd --prefix src/Nexus.Client audit --audit-level=high         # 0 vulnerabilities
pwsh -File ./scripts/audit/Test-SecurityGuardrails.ps1             # 0 violations
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1 -Check         # OK
```

## Next step (Docker/PostgreSQL slices: `M0.2`, `M0.5`, `M0.8`)

- Install/enable Docker Desktop (or WSL2 Docker) on this workstation for Testcontainers PostgreSQL.
- Implement `M0.2` (`NexusDbContext`, EF Core migrations, separate `nexus_migrator` vs `nexus_runtime` roles, TLS verification, Testcontainers PostgreSQL integration tests).
- Implement `M0.5` (`idempotency_keys` table + `IdempotencyEndpointFilter` + `ConditionalTransition` helper) and `M0.8` (`deploy/Dockerfile`, Data Protection keys + sessions in PostgreSQL, end-to-end M0 acceptance test).
