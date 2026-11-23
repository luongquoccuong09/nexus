# M1e and M2 Personal Chat and Organizations handoff

[P0-D14](../../decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) is implemented in the workspace. The SPA defaults to Personal Chat. A single account can accept contacts, start private DMs and create group chats independently of Organization membership. Each tab uses the scope in its own URL. Organization owners and admins use TOTP-authenticated sessions to manage invitations and membership within their Organization.

## Verification commands

```powershell
pwsh -File ./scripts/Run-Tests.ps1
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~LegacyOrganizationCutoverTests|FullyQualifiedName~ChatScopeTests'
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1
git diff --check
```

- The integration suite uses disposable PostgreSQL 17 Testcontainers. `ChatScopeTests` covers zero-Organization registration, consent by username and private link, Personal DMs and groups, competing invitation redemption, revoked and expired links, independent roles and hubs, and access checks before and after Organization revocation.
- `LegacyOrganizationCutoverTests` upgrades a disposable M5 database, exercises manifest validation and application, and verifies ownership transfer, access retention for the approved set, account retention, chat history and attachment references. `FilesM3Tests` verifies that retained delta events produce working, authorized scoped links.
- The local browser check exercised sign-in with zero memberships, a contact request and acceptance, a delivered Personal message using two accounts, and the authenticator requirement shown by the Organization creation dialog.
- Two-scope SignalR, unread count and reconciliation tests use concurrent connections for a single account.
- Two browser tabs used the same account as Owner in Organization A and Member in Organization B. A local proxy dropped message sends for A while B delivered normally. Reopening A after the proxy restored access flushed its queued message. PostgreSQL contained exactly one message in each expected conversation.

## Legacy Organization cutover

1. On a staging copy of the existing database, restore a fresh backup and use the same app build as the intended cutover. Preserve private storage and its existing object keys. Keep `NexusOrganizations__AllowCreation=false`.
2. Obtain the legacy Organization Owner's approved account IDs. Confirm the Owner's account has TOTP enabled. For each Community or group whose Owner is excluded, select an approved existing member as the new Owner. Official Communities require a Verified replacement.
3. Run the migration and validate the manifest against that staging copy using the configured migration and runtime connection strings. Set `LegacyOrganization__Manifest` to an operator-controlled file path. The first review command validates within a transaction and rolls back.

```powershell
dotnet Nexus.Web.dll --migrate
dotnet Nexus.Web.dll --review-legacy-organization
dotnet Nexus.Web.dll --review-legacy-organization --apply-legacy-review
```

```json
{
  "organizationId": "00000000-0000-0000-0000-000000000001",
  "ownerId": "00000000-0000-0000-0000-000000000002",
  "approvedAccountIds": ["00000000-0000-0000-0000-000000000002"],
  "communities": [],
  "groups": [
    {
      "resourceId": "00000000-0000-0000-0000-000000000003",
      "newOwnerId": "00000000-0000-0000-0000-000000000002"
    }
  ]
}
```

4. Verify both approved and excluded accounts, historical messages, attachment downloads, revocation, and independent contexts against staging. Notify clients to send or copy any queued messages before cutover. Server-side messages migrate and the previous unscoped local queue is left in the old browser storage namespace.
5. Pause production writes and take a verified backup before running the same migration and reviewed manifest on production. Upgrade the server and SPA together. The HTTP and SignalR paths changed and `/api/version` reports `2.0.0`. Enable `NexusOrganizations__AllowCreation=true` only after production scope and migration checks pass. Recovery uses the pre-cutover backup or a forward fix once new-scope data exists.

M2.15 stays open pending the Owner's manifest, deployment-specific staging rehearsal and production cutover.

## Account recovery and specialized roles

The Nexus operator can issue a single-use, 30-minute account reset token using `dotnet Nexus.Web.dll --issue-account-reset` with `AccountRecovery__Username` set. Run this in an operator-controlled terminal because the command writes the token to standard output for private delivery. The account ID and operation are audited. Organization admins manage membership and Directory access in their scope.

Existing HR and Communications requirements still apply. A newly created Organization has an Owner and provisioned membership management. A setup flow for assigning specialized HR or Communications roles in a new Organization is outside the current checklist and remains open.
