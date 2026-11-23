---
id: P0-D14
title: "Personal Chat and multiple Organizations on one server"
summary: "A Nexus account can use consent-based Personal Chat with zero Organizations and can join multiple isolated Organizations by invitation."
date: 2026-11-23
decided-by: owner
status: accepted
---

# Personal Chat and multiple Organizations on one server

One Nexus account can use Personal Chat immediately after sign-in. Organization membership is optional. Each server can host multiple Organizations, and an account can be a member of more than one.

## Personal Chat

- Personal Chat provides one unified account-level chat list (direct messages and group chats) across all of a user's active Workspaces and external contacts.
- Members who share at least one active Workspace can open a unified DM or group chat directly without sending a connection request (`GET /api/personal/people/chat-candidates`). People outside shared Workspaces connect via 2-way consent using the exact Nexus username or a private contact link (`GET /api/personal/people/directory` lists accepted external contacts). Exact-username requests return a generic confirmation so the request screen cannot be used as a general account directory.
- Direct messages and personal group chats use an explicit Personal scope (`organization_id = NULL`) and their own conversation memberships. Existing 1-on-1 DM and personal group chat history remains accessible to participants even if one later leaves a shared Workspace. Blocking, reporting, account status and request throttling apply. Organization roles and employee verification have no authority over Personal Chat.

## Organizations

- Creating a Nexus account grants zero Organization memberships. Any signed-in user can create a new Organization without enabling TOTP first and immediately becomes its Verified Owner. Workspace settings (invitations and member management) work out of the box and support optional TOTP enforcement via `NexusOrganizations:RequireAdminMfa=true` for strict enterprise deployments. TOTP remains required for specialized enterprise admin endpoints (HR import, account verification/disablement, and Official Community governance).
- Owner or Admin can issue and revoke an Organization invitation link. A link is valid for seven days and redeemable once by a signed-in account that confirms the invitation. Anyone holding an unused link can redeem it. Issuance, revocation and redemption are audited.
- Each Organization owns its Directory, HR imports, Communities, Channels, posts and Workspace files, strictly isolated by `organization_id` with immediate revocation on member removal. Membership holds the Organization role, employee verification and directory profile. Credentials, sessions and TOTP remain account-level. Organization administration cannot grant access to personal conversations or data in another Organization.
- The single-window SPA displays all joined Workspaces simultaneously without a workspace switcher combobox or full-page reloads: **Communities** groups communities under collapsible Workspace headers with inline settings and create actions, and **People** provides instant filter pills across Contacts and each Workspace. Within Communities, use **Shared Community** and **Private Community** and display the member's current Community role. Channels continue to inherit Community membership under [P0-D04](2026-11-23-p0-d04-community-channel-hierarchy.md).

## Scope enforcement and initialization

- Authorize the explicit Personal or Organization scope on HTTP, SignalR, file and sync entry points. Partition SignalR delivery, client state and offline data by account and scope. A shared sign-in supports simultaneous tabs in different scopes. Revoking one Organization membership leaves Personal Chat and other Organizations available.
- Because Project Nexus has not been released to production yet, the schema starts from a clean greenfield baseline migration (`InitialSchema`) with no legacy single-Organization columns on `users` and no in-place cutover pipeline. Organization creation is enabled by default (`NexusOrganizations:AllowCreation=true`).
- Update the contracts and tests in implementation order under the [roadmap](../implementation-plan/ROADMAP.md) and verify scope isolation on a fresh PostgreSQL database.

This decision supersedes [P0-D02](2026-11-23-p0-d02-single-organization.md) and the automatic Organization enrollment in [P0-D13](2026-11-23-p0-d13-open-trial-registration.md). [P0-D01](2026-11-23-p0-d01-direct-dms.md) applies to DMs within an Organization.
