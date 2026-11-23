# Plan: M1a — Username/Password Identity, Trial Registration, Revocation & Admin MFA

- Status: active (`develop`)
- Date: 2026-11-23
- Scope: `M1.1`, `M1.2`, `M1.3`, `M1.4`, `M1.4b`, `M1.5`, `M1.6`, `M1.7`, `M1.8`
- Security tests covered: `SEC-01`, `SEC-08`, `SEC-16`, `SEC-20`, `SEC-20b`, `SEC-21`, `SEC-24`

## Architecture Summary

- **Org module (`Modules/Org/`):** Single `Organization` (`P0-D02`), optional `Department` and `Branch` metadata, and `Employee` records (`employee_code` unique) for verification.
- **Identity module (`Modules/Identity/`):**
  - `NexusUser : IdentityUser<Guid>` backed by `IdentityDbContext<NexusUser, IdentityRole<Guid>, Guid>` in `NexusDbContext` with snake_case PostgreSQL tables.
  - `DatabaseAccountStatusLookup` validates `Status == Active` and current `SecurityStamp` against PostgreSQL on every authenticated HTTP request and SignalR invocation.
  - `IUserConnectionRegistry` tracks live SignalR connections per `userId` and immediately calls `Abort()` on password change (`M1.5`) or account disablement (`M1.6`).
  - `UsernamePolicy` (`3–32` `[a-z0-9._-]`, reserved blocklist) + `CommonPasswordValidator` (`>= 12` chars, common password blocklist).
  - `RegistrationMode` (`Open`, `OpenWithJoinCode`, `InviteOnly`, `Approval`), honeypot field, switchable `IRegistrationCaptchaVerifier`, per-IP registration & login rate limiting, `X-Robots-Tag: noindex, nofollow`.
  - Server-side BFF sessions in `app_sessions` extended with `user_agent`, `ip_address`, `security_stamp` for `M1.7` device listing & remote sign-out.
  - RFC 6238 TOTP MFA (`M1.8`) required for `SystemAdmin`, `HRAdmin`, and `CommsAdmin`.
- **Admin audit (`Modules/Admin/`):** Append-only `audit_logs` table recording account verification, renaming, disablement, role changes, and admin password reset links.
- **Minimal Client (`src/Nexus.Client/`):** Sign-in, open trial registration (with confidentiality notice and terms checkbox), authenticated dashboard displaying the `Unverified` badge wherever a name appears, password change, device sessions manager, and TOTP MFA setup.
