# M1a Identity & Auth Handoff (`M1.1`–`M1.8`)

- Branch: `develop`
- Date: 2026-11-23
- Completed items: `M1.1`, `M1.2`, `M1.3`, `M1.4`, `M1.4b`, `M1.5`, `M1.6`, `M1.7`, `M1.8`

## What landed

- **Org & Admin schema (`M1.1`):** `organizations`, `departments`, `branches`, `employees` (unique `(organization_id, employee_code)`), and `audit_logs` migrated via `M1aIdentityAndOrg`; CLI `--bootstrap-admin` creates the default organization and initial Verified `SystemAdmin`.
- **ASP.NET Core Identity + PostgreSQL BFF sessions (`M1.2`–`M1.4b`):** `NexusUser` (`users`), partial unique `employee_id`, `UsernamePolicy` + reserved blocklist, `CommonPasswordValidator` (`>= 12` chars, common password blocklist), `RegistrationMode` (`Open`/`OpenWithJoinCode`/`InviteOnly`/`Approval`), honeypot, switchable CAPTCHA hook, per-IP registration/login rate limits, `X-Robots-Tag: noindex, nofollow`, generic `401` login errors, lockout, admin verification/rename endpoints, and `Unverified` badge in API DTOs & React UI.
- **Session/SignalR revocation & devices (`M1.5`–`M1.7`):** `DatabaseAccountStatusLookup` verifies `Status == Active` AND `SecurityStamp` on every request/hub call; `IUserConnectionRegistry` immediately aborts active SignalR sockets on password change or disablement; `app_sessions` stores `user_agent` & `ip_address` for device listing and remote sign-out; `admin_reset_links` provides audited hashed single-use reset tokens.
- **Admin TOTP MFA (`M1.8`):** RFC 6238 TOTP enrollment + recovery codes; `SystemAdmin`, `HRAdmin`, and `CommsAdmin` are blocked from `/api/admin/*` until TOTP MFA is enabled and verified on login.
- **Minimal Client (`src/Nexus.Client/src/`):** Sign in (with TOTP challenge), open trial registration (with confidentiality notice & terms), `Unverified` badge wherever names render, change password, MFA setup, and signed-in devices list.

## Verification

- `dotnet build Nexus.slnx --no-restore -c Release` → **0 Warnings, 0 Errors**
- `dotnet test Nexus.slnx --no-build -c Release` → **All tests passed** (including `IdentityM1aTests` on Testcontainers PostgreSQL 17 covering `SEC-01`, `SEC-08`, `SEC-16`, `SEC-20`, `SEC-20b`, `SEC-21`, `SEC-24`)
- `npm.cmd --prefix src/Nexus.Client run build` → **Passed**
- `pwsh -File ./scripts/audit/Test-SecurityGuardrails.ps1` → **0 violations**
- `pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1 -Check` → **OK**

## Next step: M1b — Directory (`M1.9`–`M1.11`)

HR CSV import with dry run & diff report, Vietnamese directory search using `unaccent` + `pg_trgm`, custom status (`UX-06`), and re-encoded avatars.
