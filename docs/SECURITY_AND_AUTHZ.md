# Security and Resource Authorization

- Status: active (canonical — supersedes `docs/authorization-matrix.md`)
- Last updated: 2026-11-23
- Scope: authorization matrix, security invariants, forbidden patterns, and `SEC-01`–`SEC-24` test traceability.

Every protected action must go through `IAuthorizationService.Can(actor, action, resource)` (M0.4). **Deny by default:** any action without an explicit approved policy is denied.

## 1. Resource authorization matrix (initial M0.4 contract)

Each integration test identifies the **actor**, **account status** (`Active`/`Disabled`, `Verified`/`Unverified`), **resource**, **action**, and **expected result**.

| Scenario | Expected result |
|---|---|
| Anonymous user reads private data, or a `Disabled` account calls HTTP, a SignalR hub, or a download endpoint using an existing session | **Deny.** |
| An `Active` member reads a DM or group chat | **Allow** within the authorized history range. **Deny** nonmembers, including `SystemAdmin`. |
| Community member reads the `General` channel or another channel in that community | **Allow** according to current community membership and community verification requirements. |
| Removed member opens a post, searches messages, downloads a file, or syncs a former channel | **Deny** further delivery. |
| Community `Owner`/`Admin` adds a channel to that community | **Allow.** **Deny** ordinary members and admins of another community. |
| A member posts to an `admins_only` channel | **Deny** ordinary members. **Allow** the community `Owner`/`Admin` if verification requirements are met. |
| A `Verified` `CommsAdmin` creates or designates an official community | **Allow.** **Deny** `Unverified` accounts. |
| `HRAdmin` imports the directory or `SystemAdmin` disables an account | **Allow** within administrative scope and **audit** the action. |

### Pending P0.2 expansions (complete before M1/M2/M5)

Before implementing the relevant milestones, expand this matrix to cover:
- Trial directory access and `Unverified` daily new-DM limits (`M1.4b`)
- Blocking and reporting in DMs (`M2.11`)
- History visibility after rejoining a group or community (`M2.8`, `M5.0`)
- Creating and joining public vs private communities (`M5.0`)
- Ownership transfer and emergency transfer (`M2.8`, `M5.2`, `M7.2`)
- `@channel` and `@all` mention authorization (`M4.2`)

Always test **both allowed and denied cases** through the real HTTP endpoint or SignalR hub.

## 2. Non-negotiable security invariants

1. **Account status check everywhere:** `ActiveUserMiddleware` (HTTP) and `ActiveUserHubFilter` (SignalR) must reject `Disabled` accounts immediately, including on active sessions and open WebSockets.
2. **Revocation on password change or disablement:** Updating password (`M1.5`) or disabling an account (`M1.6`) rotates the security stamp, revokes sessions, and aborts active SignalR connections.
3. **No automatic Google account linking by email:** Linking Google OIDC (`M1.12`) requires an active login plus password reauthentication and stores unique `(issuer, sub)`.
4. **Generic authentication errors:** Sign-in errors must never reveal whether a username exists (`M1.4`).
5. **Private files fail closed:** Validate magic bytes and allowlists, quarantine and scan with ClamAV before releasing real data, authorize downloads before streaming or issuing `<= 60s` signed URLs, and always set `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`.
6. **Zero sensitive data in logs:** Never log message bodies, passwords, cookies, or tokens.
7. **Forbidden code patterns (enforced in CI at M0.7):**
   - `AllowAnyOrigin()` in CORS configuration
   - Disabling TLS certificate validation
   - Exposing privileged storage credentials in `src/Nexus.Client/`

## 3. Security test traceability (`SEC-01`–`SEC-24`)

Preserve the intent of every security test; fix the implementation when a test fails. Populate the **Test** column as each milestone lands.

| Code | Focus | Target milestone | Test reference |
|---|---|---|---|
| `SEC-01` | Sign-in lockout, rate limiting, generic auth errors | M1.4 | *Pending M1.4* |
| `SEC-03` | Private DM/group isolation (nonmember C cannot read A/B) | M2.12 | *Pending M2.12* |
| `SEC-04` | Authorized file downloads & short-lived signed URLs | M3.3 | *Pending M3.3* |
| `SEC-05` | Membership enforcement on message sends and reads | M2.12 | *Pending M2.12* |
| `SEC-07` | SignalR unauthorized subscription / fanout isolation | M2.12 | *Pending M2.12* |
| `SEC-08` | Account disablement revokes HTTP + SignalR immediately | M1.6 | *Pending M1.6* |
| `SEC-10` | Sanitized Markdown / XSS prevention | M2.9 | *Pending M2.9* |
| `SEC-11` | File content-type / `attachment` + `nosniff` headers | M3.3 | *Pending M3.3* |
| `SEC-14` | Offsite encrypted backup & independent restore rehearsal | M8.3 | *Pending M8.3* |
| `SEC-15` | Search/delta authorization enforcement | M2.12 | *Pending M2.12* |
| `SEC-16` | Disabled account session & hub abort | M1.6 | *Pending M1.6* |
| `SEC-17` | Complete sync across disconnects/retries without loss or duplicates | M2.12 | *Pending M2.12* |
| `SEC-18` | Member removal revokes further delivery and offline replay | M2.12 | *Pending M2.12* |
| `SEC-19` | `409 Conflict` on mismatched payload under same `client_msg_id` | M2.12 | *Pending M2.12* |
| `SEC-20` | Open registration rate limiting / anti-abuse | M1.3 | *Pending M1.3* |
| `SEC-20b` | `Unverified` badge and capability restrictions | M1.4b | *Pending M1.4b* |
| `SEC-21` | Password policy (>=12 chars, common password block) | M1.4 | *Pending M1.4* |
| `SEC-22` | Google OIDC PKCE/nonce/state & no auto-link by email | M1.12 | *Pending M1.12* |
| `SEC-23` | Google linking requires active login + password reauth | M1.12 | *Pending M1.12* |
| `SEC-24` | Password change rotates security stamp & revokes other sessions | M1.5 | *Pending M1.5* |
