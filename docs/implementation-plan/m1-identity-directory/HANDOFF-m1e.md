# M1e and M2 Personal Chat and Organizations handoff

[P0-D14](../../decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) is implemented in the workspace. The SPA lets any signed-in user create a workspace, join via invitation, or start Personal Chat directly from the home screen without requiring TOTP first. A single account can accept contacts, start private DMs and create group chats independently of Organization membership. Each tab uses the scope in its own URL. Organization owners and admins manage invitations and membership within their Organization, with optional TOTP enforcement configurable via `NexusOrganizations:RequireAdminMfa`.

## Verification commands

```powershell
pwsh -File ./scripts/Run-Tests.ps1
dotnet test tests/Nexus.IntegrationTests --filter 'FullyQualifiedName~ChatScopeTests'
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1
git diff --check
```

- The integration suite uses disposable PostgreSQL 17 Testcontainers initialized from the consolidated `20261123132240_InitialSchema` baseline migration. `ChatScopeTests` covers zero-Organization registration, consent by username and private link, Personal DMs and groups, competing invitation redemption, revoked and expired links, independent roles and hubs, and access checks before and after Organization revocation.
- `FilesM3Tests` verifies that delta events produce working, authorized scoped links.
- The local browser check exercised sign-in with zero memberships, a contact request and acceptance, a delivered Personal message using two accounts, and the authenticator requirement shown by the Organization creation dialog.
- Two-scope SignalR, unread count and reconciliation tests use concurrent connections for a single account.
- Two browser tabs used the same account as Owner in Organization A and Member in Organization B. A local proxy dropped message sends for A while B delivered normally. Reopening A after the proxy restored access flushed its queued message. PostgreSQL contained exactly one message in each expected conversation.

## Fresh database initialization (greenfield)

Because there has been no production release yet, all pre-release migrations and legacy cutover scaffolding (`LegacyOrganizationReview`, `legacy_review_pending`, legacy Organization columns on `users`, and `ScopeLegacyAttachmentUrls`) have been removed in favor of a single clean baseline migration (`20261123132240_InitialSchema`). `NexusOrganizations:AllowCreation` now defaults to `true` in all environments (still configurable to `false` if an operator chooses to restrict creation).

- **Local Docker PostgreSQL reset:**
  ```powershell
  docker compose -f deploy/docker-compose.dev.yml down -v
  docker compose -f deploy/docker-compose.dev.yml up --build -d
  ```
- **Fresh Supabase database:** run [`deploy/supabase/init-supabase.sql`](../../../deploy/supabase/init-supabase.sql) in SQL Editor, then run `pwsh -File .\scripts\deploy\Invoke-SupabaseMigrate.ps1` (or `dotnet run --project src/Nexus.Web -- --migrate`).

M2.15 is complete for the greenfield deployment model.

## Account recovery and specialized roles

The Nexus operator can issue a single-use, 30-minute account reset token using `dotnet Nexus.Web.dll --issue-account-reset` with `AccountRecovery__Username` set. Run this in an operator-controlled terminal because the command writes the token to standard output for private delivery. The account ID and operation are audited. Organization admins manage membership and Directory access in their scope.

Existing HR and Communications requirements still apply. A newly created Organization has an Owner and provisioned membership management. A setup flow for assigning specialized HR or Communications roles in a new Organization is outside the current checklist and remains open.
