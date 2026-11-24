# Security and Resource Authorization

- Status: active (canonical — completes `P0.2`)
- Last updated: 2026-11-23
- Scope: complete capability matrix, domain authorization contract (`IAuthorizationService.Can`), security invariants, forbidden patterns, and `SEC-01`–`SEC-24` traceability.

Every protected HTTP endpoint, SignalR hub method, file download, search query, and delta-sync read must evaluate `Nexus.Web.Infrastructure.Security.IAuthorizationService.Can(actor, action, resource)`. **Deny by default:** any `(actor, action, resource)` combination without an explicit approved policy returns `false` (deny).

## 1. Evaluation Order

`IAuthorizationService.Can(actor, action, resource)` evaluates rules in strict order:

1. **Authentication & Account Status:** Reject immediately if `actor` is null or `actor.Status != AccountStatus.Active` (`Disabled` or `Former` accounts are denied across HTTP, SignalR, downloads, and sync).
2. **Chat and Organization scope.** Validate the route scope and active Organization membership before evaluating the resource policy. Personal Chat uses its own scope and contact policy. Reject resources outside the route scope. [P0-D14](decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md)
3. **Explicit Action & Resource Policy:** Evaluate the matrix rules below according to `ResourceKind`, `actor.OrgRole`, `actor.TrustLevel`, `resource.MembershipRole`, `resource.IsOfficial`, `resource.Visibility`, `resource.PostingPolicy`, and relationship flags (`IsBlocked`, `DailyNewDmQuotaExceeded`).
4. **Default Deny:** Return `false` if no explicit rule allows the action.

> **Management vs Content Separation:** Organizational admin roles (`SystemAdmin`, `HRAdmin`, `CommsAdmin`) **never** grant implicit read or post access to DMs, group chats, or private communities where the admin is not a current member. Break-glass emergency export is governed separately by [`P0-D07`](decisions/2026-11-23-p0-d06-d07-offboarding-and-break-glass.md) (`M7.5`).

Personal Chat directory results include accepted contacts. Opening a Personal DM or adding a member to a Personal group requires an accepted contact. Organization membership revocation closes that Organization's connections for the account and blocks further reads, downloads and sync in its scope. The account remains available in other scopes. Global account recovery is an audited operator action.

## 2. Capability Matrix (`P0.2`)

### 2.1 Direct Messages & Group Chats (`ResourceKind.Dm`, `ResourceKind.GroupChat`)

| Action | Active `Verified` | Active `Unverified` | Non-member (incl. `SystemAdmin`/`HRAdmin`/`CommsAdmin`) | Removed member (`left_at != null`) |
|---|---|---|---|---|
| `DirectorySearch` (view active colleagues) | **Allow** (hides `Disabled`) | **Allow** (trial minimal profile, hides `Disabled`) | N/A | N/A |
| `DmOpen` (`P0-D01`) | **Allow** if target is `Active` & not blocked | **Allow** if target is `Active`, not blocked, & under daily new-DM limit (`SEC-20b`) | N/A | N/A |
| `DmBlockOrReport` | **Allow** against another active user | **Allow** against another active user | N/A | N/A |
| `ManageOwnBlockList` | **Allow** for the caller's own records in the requested scope | **Allow** for the caller's own records in the requested scope | N/A | N/A |
| `GroupCreate` | **Allow** | **Allow** | N/A | N/A |
| `ConversationRead` / `MessageSearch` / `FileDownload` / `DeltaSync` | **Allow** if current member (within authorized history range) | **Allow** if current member | **Deny** (`SEC-03`, `SEC-15`) | **Deny** further delivery (`SEC-18`) |
| `MessageSend` / `FileUpload` | **Allow** if current member (and DM not blocked) | **Allow** if current member (and DM not blocked) | **Deny** (`SEC-05`) | **Deny** (`SEC-18`) |
| `MessagePin` | **Allow** for an unblocked DM participant or a Group `Owner` or `Admin` | **Allow** under the same membership rules | **Deny** | **Deny** |
| `GroupAddMember` / `GroupRemoveMember` / `GroupManageAdmins` | **Allow** if Group `Owner` or `Admin` | **Allow** if Group `Owner` or `Admin` | **Deny** | **Deny** |
| `GroupTransferOwnership` | **Allow** if Group `Owner` | **Allow** if Group `Owner` | **Deny** | **Deny** |
| `GroupLeave` | **Allow** if member (Owner must transfer first if other members exist) | **Allow** if member (same Owner rule) | **Deny** | **Deny** |

### 2.2 Communities & Optional Sub-channels (`ResourceKind.Community`, `ResourceKind.CommunityChannel`)

Per updated [`P0-D04`](decisions/2026-11-23-p0-d04-community-channel-hierarchy.md), Communities follow the Microsoft Teams Communities model: each Community owns membership (`Owner`/`Admin`/`Member`) and has its own main feed directly, plus zero or more optional sub-channels that inherit its membership and roles.

| Action | Community `Owner` | Community `Admin` | Community `Member` | Active Non-member | `Unverified` Actor |
|---|---|---|---|---|---|
| `CommunityCreate` (standard / social) | — | — | — | **Allow** (creator becomes `Owner`) | **Allow** (social only; **Deny** Official) |
| `CommunityDesignateOfficial` | **Deny** unless Verified `CommsAdmin` | **Deny** unless Verified `CommsAdmin` | **Deny** | **Allow** only for Verified `CommsAdmin` | **Deny** (`SEC-20b`) |
| `CommunityDiscoverAndJoin` (Public, Social) | — | — | — | **Allow** | **Allow** |
| `CommunityDiscoverAndJoin` (Public, Official) | — | — | — | **Allow** if `Verified` | **Deny** (`SEC-20b`) |
| `CommunityDiscoverAndJoin` (Private) | — | — | — | **Deny** (invite only) | **Deny** |
| `ChannelCreate` (`+ Add channel`) | **Allow** (if Official, requires `Verified`) | **Allow** (if Official, requires `Verified`) | **Deny** | **Deny** (other community's admin is also **Deny**) | **Deny** on Official; **Allow** on own Social community |
| `ConversationRead` / `MessageSearch` / `FileDownload` / `DeltaSync` (main feed or any sub-channel) | **Allow** (if Official, requires `Verified`) | **Allow** (if Official, requires `Verified`) | **Allow** (if Official, requires `Verified`) | **Deny** (including `SystemAdmin`) | **Deny** on Official; **Allow** on joined Social |
| `MessageSend` / `FileUpload` (`PostingPolicy.Members`) | **Allow** | **Allow** | **Allow** (if Official, requires `Verified`) | **Deny** | **Deny** on Official; **Allow** on joined Social |
| `MessageSend` / `FileUpload` (`PostingPolicy.AdminsOnly`) | **Allow** (if Official, requires `Verified`) | **Allow** (if Official, requires `Verified`) | **Deny** | **Deny** | **Deny** on Official |
| `MentionChannelOrAll` (`@channel` / `@all`) | **Allow** if `Verified` | **Allow** if `Verified` | **Deny** | **Deny** | **Deny** (`SEC-20b`) |
| `CommunityAddOrRemoveMember` | **Allow** (cannot add `Unverified` to Official) | **Allow** (cannot remove `Owner` or add `Unverified` to Official) | **Deny** | **Deny** | **Deny** on Official |
| `CommunityManageAdmins` / `CommunityTransferOwnership` | **Allow** | **Deny** | **Deny** | **Deny** | **Deny** on Official |
| `CommunityEmergencyTransferOwnership` | **Deny** | **Deny** | **Deny** | **Allow** only for Verified `SystemAdmin` (audited, `M7.2`) | **Deny** |

### 2.3 Organization Administration (`ResourceKind.Organization`, `ResourceKind.UserAccount`, `ResourceKind.AuditLog`)

| Action | `SystemAdmin` (`Verified`) | `HRAdmin` (`Verified`) | `CommsAdmin` (`Verified`) | Ordinary `Member` / `Unverified` |
|---|---|---|---|---|
| `AdminImportDirectory` | **Deny** | **Allow** (audited) | **Deny** | **Deny** |
| `AdminVerifyAccount` (match `employee_id` / rename impersonator) | **Allow** (audited) | **Allow** (audited) | **Deny** | **Deny** |
| `AdminDisableOrEnableAccount` | **Allow** (audited, cannot disable self) | **Deny** | **Deny** | **Deny** |
| `AdminManageRoles` | **Allow** (audited) | **Deny** | **Deny** | **Deny** |
| `AdminViewAuditLog` | **Allow** (full) | **Allow** (HR scope only) | **Deny** | **Deny** |
| `AdminEmergencyOwnershipTransfer` | **Allow** (audited) | **Deny** | **Allow** (Official communities only, audited) | **Deny** |

### 2.4 History Visibility After Removal & Rejoining

- **On removal (`left_at` set):** Deny all subsequent reads, searches, delta syncs, and file downloads for that conversation/community immediately (`SEC-18`). Retain messages previously authored by the removed user for remaining members.
- **On rejoining:** A rejoining member receives access starting from their new `visible_from_seq` (the conversation's `next_seq` at rejoin time for Groups/Private Communities; full current channel history for Public Communities unless restricted).

### 2.5 Deployment Monitor (`ResourceKind.Monitor`)

| Actor | Read metrics, history, and reports | Run / Stop |
|---|---|---|
| Active account with deployment reader grant | Allow | Deny |
| Active account with deployment operator grant | Allow | Allow with verified MFA and antiforgery |
| Organization administrator without a deployment grant | Deny | Deny |
| Disabled or revoked session | Deny | Deny |

The user policy checks deployment grants independently of Organization membership. The Monitor DTOs contain aggregate measurements, configured profile values, and allowlisted lifecycle events. The machine endpoints use a dedicated runner credential over TLS, with a Development loopback exception. The machine principal receives one sandbox lease and may report only against its matching generation. The worker provisions isolated localhost Docker projects. `Nexus.Web` has no Docker access or shell execution endpoint.

## 3. Domain Authorization Contract (`M0.4`)

Implemented in `src/Nexus.Web/Infrastructure/Security/`:

```csharp
public interface IAuthorizationService
{
    bool Can(ActorContext actor, ResourceAction action, ResourceContext resource);
}
```

- `ActorContext`: `(Guid UserId, Guid OrganizationId, AccountStatus Status, TrustLevel TrustLevel, OrgRole OrgRole)`
- `ResourceContext`: `(ResourceKind Kind, Guid OrganizationId, ResourceRole? MembershipRole, bool IsOfficial, Visibility Visibility, PostingPolicy PostingPolicy, bool TargetIsActive, bool IsBlocked, bool DailyNewDmQuotaExceeded, bool TargetIsSelf, bool TargetIsOwner, bool TargetIsVerified, bool HasOtherMembers, bool IsHrAuditScope)`

All matrix cells above are verified by xUnit `[Theory]` tests in `tests/Nexus.IntegrationTests/AuthorizationMatrixTests.cs`.

## 4. Non-Negotiable Security Invariants

1. **Account status check everywhere:** `ActiveUserMiddleware` (HTTP) and `ActiveUserHubFilter` (SignalR) reject `Disabled` accounts immediately on active sessions and open WebSockets.
2. **Revocation on password change or disablement:** Updating password (`M1.5`) or disabling an account (`M1.6`) rotates the security stamp, revokes sessions, and aborts active SignalR connections.
3. **No automatic Google account linking by email:** Linking Google OIDC (`M1.12`) requires an active login plus password reauthentication and stores unique `(issuer, sub)`.
4. **Generic authentication errors:** Sign-in errors never disclose whether a username exists (`M1.4`).
5. **Private files fail closed:** Validate magic bytes and allowlists, quarantine and scan with ClamAV before releasing real data, authorize downloads before streaming or issuing `<= 60s` signed URLs, and always set `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`.
6. **Zero sensitive data in logs:** Never log message bodies, passwords, cookies, or tokens.
7. **Forbidden code patterns (enforced in CI at M0.7):**
   - `AllowAnyOrigin()` in CORS configuration (startup throws if `*` is configured)
   - Disabling TLS certificate validation
   - Exposing privileged storage credentials in `src/Nexus.Client/`

## 5. Security Test Traceability (`SEC-01`–`SEC-24`)

Preserve the intent of every security test; fix the implementation when a test fails.

| Code | Focus | Target milestone | Test reference |
|---|---|---|---|
| `SEC-01` | Sign-in lockout, rate limiting, generic auth errors | M1.4 | `IdentityM1aTests.SEC01_SEC21_PasswordPolicy_GenericLoginErrors_AndAccountLockout` |
| `SEC-03` | Private DM/group isolation (nonmember C cannot read A/B) | M0.4 / M2.12 | `AuthorizationMatrixTests` + `MessagingM2Tests.M2_3_M2_4_M2_10_SEC03_SEC05_SEC07_SEC15_RealtimeIsolation_AndSystemAdminDenied` |
| `SEC-04` | Authorized file downloads & short-lived signed URLs | M3.3 | `FilesM3Tests.M3_3_SEC04_SEC11_AuthorizedDownloads_SafeHeaders_SignedUrlCap_AndRemovalIsolation` |
| `SEC-05` | Membership enforcement on message sends and reads | M0.4 / M2.12 | `AuthorizationMatrixTests` + `MessagingM2Tests.M2_3_M2_4_M2_10_SEC03_SEC05_SEC07_SEC15_RealtimeIsolation_AndSystemAdminDenied` |
| `SEC-07` | SignalR unauthorized subscription / Origin / fanout isolation | M0.3 / M2.12 | `SecurityDefaultsTests` (Origin + ActiveUserHubFilter) + `MessagingM2Tests.M2_3_M2_4_M2_10_SEC03_SEC05_SEC07_SEC15_RealtimeIsolation_AndSystemAdminDenied` |
| `SEC-08` | Account disablement revokes HTTP + SignalR immediately | M0.3 / M1.6 | `IdentityM1aTests.SEC08_SEC16_SEC20b_M1_8_AdminMfa_VerifyEmployee_Rename_ResetLink_AndDisable` |
| `SEC-10` | Sanitized Markdown / XSS prevention | M2.9 | `src/Nexus.Client/tests/markdown.test.mjs` (`renderSafeMarkdown`, `isSafeHttpUrl`) |
| `SEC-11` | File content-type / `attachment` + `nosniff` headers | M3.3 | `FilesM3Tests.M3_3_SEC04_SEC11_AuthorizedDownloads_SafeHeaders_SignedUrlCap_AndRemovalIsolation` |
| `SEC-14` | Offsite encrypted backup & independent restore rehearsal | M8.3 | *Pending M8.3* |
| `SEC-15` | Search/delta authorization enforcement | M0.4 / M2.12 / M6.6 | `AuthorizationMatrixTests`, `MessagingM2Tests` (`/changes` delta), and `RichChatMessageSearchTests` (SQL message search) |
| `SEC-16` | Disabled account session & hub abort | M0.3 / M1.6 | `IdentityM1aTests.SEC08_SEC16_SEC20b_M1_8_AdminMfa_VerifyEmployee_Rename_ResetLink_AndDisable` |
| `SEC-17` | Complete sync across disconnects/retries without loss or duplicates | M2.12 | `MessagingM2Tests.M2_5_M2_7_SEC17_DeltaSync_Reconcile_410Gone_AndMonotonicReadState` |
| `SEC-18` | Member removal revokes further delivery and offline replay | M0.4 / M2.12 | `AuthorizationMatrixTests` + `MessagingM2Tests.M2_8_SEC18_GroupLifecycle_IdempotencyKey_RemovalRevokesAccess_RejoinHidesHistory_AndOwnershipTransfer` |
| `SEC-19` | `409 Conflict` on mismatched payload under same `client_msg_id` / `Idempotency-Key` | M0.5 / M2.12 | `PostgresFoundationTests` (`Idempotency-Key`) + `MessagingM2Tests.M2_3_SEC19_ClientMsgIdIdempotency_ConcurrentRetry_AndConflict` |
| `SEC-20` | Open registration rate limiting / anti-abuse | M1.3 | `IdentityM1aTests.SEC20_OpenRegistration_CreatesUnverifiedAccount_EnforcesUniqueReservedHoneypotAndRateLimit` |
| `SEC-20b` | `Unverified` badge and capability restrictions | M0.4 / M1.4b | `AuthorizationMatrixTests` + `IdentityM1aTests` + `MessagingM2Tests.M2_2_SEC20b_DmOpenRaceIsAtomic_AndUnverifiedDailyLimitEnforced` |
| `SEC-21` | Password policy (>=12 chars, common password block) | M1.4 | `IdentityM1aTests.SEC01_SEC21_PasswordPolicy_GenericLoginErrors_AndAccountLockout` |
| `SEC-22` | Google OIDC PKCE/nonce/state & no auto-link by email | M1.12 | *Pending M1.12* |
| `SEC-23` | Google linking requires active login + password reauth | M1.12 | *Pending M1.12* |
| `SEC-24` | Password change rotates security stamp & revokes other sessions | M1.5 | `IdentityM1aTests.SEC24_M1_5_M1_7_ChangePassword_RemoteSignOut_AndAdminResetLink` |
