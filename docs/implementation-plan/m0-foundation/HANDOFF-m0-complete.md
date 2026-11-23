# M0 Foundation Complete Handoff (`M0.1`–`M0.9` + `P0.2`/`P0.3`)

- Branch: `develop`
- Date: 2026-11-23
- Status: **M0 complete** (all M0.1–M0.9 acceptance criteria verified locally including Testcontainers PostgreSQL 17 and multi-stage Docker build).

## Delivered across M0

1. **Contracts (`P0.2`, `P0.3`):** Complete authorization matrix ([`SECURITY_AND_AUTHZ.md`](../../SECURITY_AND_AUTHZ.md)) and sync/idempotency protocol ([`SYNC_PROTOCOL.md`](../../SYNC_PROTOCOL.md)).
2. **PostgreSQL & EF Core (`M0.2`):** `NexusDbContext`, `InitialFoundation` migration (`data_protection_keys`, `app_sessions`, `idempotency_keys`), separate `nexus_migrator` (DDL) vs `nexus_runtime` (DML only) roles, `DatabaseOptions` enforcing TLS and rejecting Supabase port `6543`, and Testcontainers PostgreSQL 17 integration tests proving `nexus_runtime` is denied DDL (`42501`).
3. **Secure Defaults (`M0.3`):** Authenticated `FallbackPolicy`, `ActiveUserMiddleware`, `ActiveUserHubFilter`, `WebSocketOriginMiddleware`, strict CORS rejecting `*`, antiforgery (`X-CSRF-TOKEN`), CSP & security headers, 10 MB body limit, `PaginationGuard` (`<= 50`), IP rate limiting.
4. **Authorization Engine (`M0.4`):** Deny-by-default `IAuthorizationService.Can(actor, action, resource)` verified by 66 matrix-derived `[Theory]` cases.
5. **Idempotency & Conditional Transitions (`M0.5`):** `idempotency_keys` table, `IdempotencyEndpointFilter` (SHA-256 body hash, 24h TTL, `Idempotency-Replayed: true`, `409 Conflict` on payload mismatch — `SEC-19`), and `ConditionalTransition` helper.
6. **Observability (`M0.6`):** Serilog Compact JSON logging with `SensitiveDataRedactingEnricher`, OpenTelemetry tracing/metrics, `/healthz`, `/readyz` (with PostgreSQL check), `/api/version`.
7. **CI & Security Gates (`M0.7`):** Roslyn security analyzers (`Directory.Build.props`), `scripts/audit/Test-SecurityGuardrails.ps1`, `.github/workflows/ci.yml` (build/test, `dotnet list package --vulnerable`, `npm audit`, Gitleaks, Trivy, `Build-KnowledgeIndex.ps1 -Check`).
8. **Container & Persistence (`M0.8`):** Multi-stage non-root `deploy/Dockerfile`, `deploy/docker-compose.dev.yml`, `deploy/postgres/init-roles.sql`, PostgreSQL-persisted Data Protection keys (`PersistKeysToDbContext`) and server-side BFF sessions (`PostgreSqlSessionTicketStore`).
9. **Storage & Scanner Seams (`M0.9`):** `IFileStorage` (`LocalFileStorage` outside `wwwroot` + server-side `SupabaseFileStorage` with `<= 60s` signed URL cap, random keys, SHA-256, path-traversal protection) and `IFileScanner` (`PrototypeFileScannerStub` returning `skipped_prototype`).

## Verification commands run

```powershell
dotnet build Nexus.slnx --no-restore -c Release
dotnet test Nexus.slnx --no-build -c Release --verbosity normal
docker build -f deploy/Dockerfile -t nexus-web:m0-verify .
dotnet list Nexus.slnx package --vulnerable --include-transitive
npm.cmd --prefix src/Nexus.Client run build
npm.cmd --prefix src/Nexus.Client audit --audit-level=high
pwsh -File ./scripts/audit/Test-SecurityGuardrails.ps1
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1 -Check
```

## Next milestone: M1 — Identity & Directory (`M1.1`–`M1.16`)

Start `M1a` (`M1.1`–`M1.8`): single `Organization`, `Department`/`Branch`, `employees`, ASP.NET Core Identity on PostgreSQL with BFF session cookies, open trial registration (`Unverified`), password policy & lockout (`SEC-01`, `SEC-20`, `SEC-20b`, `SEC-21`), security stamp & session revocation (`SEC-08`, `SEC-16`, `SEC-24`), signed-in devices, and admin TOTP MFA.
