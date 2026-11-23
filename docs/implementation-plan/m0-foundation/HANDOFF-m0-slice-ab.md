# M0 Slice A & B Handoff (`P0.2`, `P0.3`, `M0.3`, `M0.4`, `M0.6`)

- Branch: `develop`
- Date: 2026-11-23
- Completed items: `P0.2`, `P0.3`, `M0.3`, `M0.4`, `M0.6`

## What landed

1. **`P0.2` ([`docs/SECURITY_AND_AUTHZ.md`](../../SECURITY_AND_AUTHZ.md)):** Complete capability matrix covering DMs (`P0-D01`), blocking, trial directory & `Unverified` restrictions (`SEC-20b`), group chats, Teams-style Communities + optional sub-channels (`P0-D04`), official designation, ownership & emergency transfers, `@channel`/`@all`, and organization administration.
2. **`P0.3` ([`docs/SYNC_PROTOCOL.md`](../../SYNC_PROTOCOL.md)):** Complete protocol for `client_msg_id`, dual `seq`/`change_seq`, SignalR crash-between-commit-and-broadcast recovery, 30-day delta retention + `410 Gone` full-resync trigger, rejoin history boundaries, `Idempotency-Key` (24h TTL, SHA-256 request hash, `409 Conflict`), and conditional state transitions.
3. **`M0.3` (`src/Nexus.Web/Infrastructure/Security/`):** Authenticated `FallbackPolicy`, `ActiveUserMiddleware`, `ActiveUserHubFilter`, `WebSocketOriginMiddleware`, strict CORS rejecting `*` at startup, antiforgery (`X-CSRF-TOKEN`), CSP & security headers, Kestrel 10 MB body limit, `PaginationGuard` (`<= 50`), and IP rate limiter.
4. **`M0.4` (`NexusAuthorizationService`):** Centralized deny-by-default `IAuthorizationService.Can(actor, action, resource)` verified by 66 matrix-derived `[Theory]` cases in `tests/Nexus.IntegrationTests/AuthorizationMatrixTests.cs`.
5. **`M0.6` (`src/Nexus.Web/Infrastructure/Observability/`):** Serilog Compact JSON console logging with `SensitiveDataRedactingEnricher`, OpenTelemetry ASP.NET Core & HttpClient tracing/metrics, and `/healthz`, `/readyz`, `/api/version`.

## Commands run and verified

```powershell
dotnet build Nexus.slnx --no-restore
dotnet test Nexus.slnx --no-build --verbosity normal    # Passed: 80, Failed: 0
npm.cmd --prefix src/Nexus.Client run build
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1 -Check
```

## Next step (Slice C: `M0.2`, `M0.5`, `M0.9`)

- Install/enable Docker Desktop (or WSL2 Docker) on this workstation for Testcontainers PostgreSQL.
- Implement `M0.2` (`NexusDbContext`, EF Core migrations, separate `nexus_migrator` vs `nexus_runtime` roles, TLS verification, Testcontainers PostgreSQL integration tests).
- Implement `M0.5` (`idempotency_keys` table + `IdempotencyEndpointFilter` + `ConditionalTransition` helper) and `M0.9` (`IFileStorage` + `IFileScanner` stub).
