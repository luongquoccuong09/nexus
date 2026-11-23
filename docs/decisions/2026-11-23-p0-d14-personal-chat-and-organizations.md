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

- Personal Chat contains direct messages and group chats. People can send a connection request using the exact Nexus username or share a private contact link. Both users must consent before the connection can be used for a new personal DM. A recipient of a forwarded contact link still needs the issuer's approval.
- Personal Chat shows accepted contacts and conversations the account participates in. Exact-username requests return a generic confirmation so the request screen cannot be used as a general account directory.
- People can create personal group chats with accepted contacts. Conversation participants can see each other within that group. Group participation does not add people to each other's contacts.
- Personal conversations use an explicit Personal scope and their own conversation memberships. They have no Organization membership requirement. Blocking, reporting, account status and request throttling apply. Organization roles and employee verification have no authority over Personal Chat.

## Organizations

- Creating a Nexus account grants zero Organization memberships. Signed-in users can create a new Organization and become its Owner. TOTP is required for Organization administration.
- Owner or Admin can issue and revoke an Organization invitation link. A link is valid for seven days and redeemable once by a signed-in account that confirms the invitation. Anyone holding an unused link can redeem it. Issuance, revocation and redemption are audited.
- Each Organization owns its Directory, organization conversations, Communities and Channels. Membership holds the Organization role, employee verification and directory profile. Credentials, sessions and TOTP remain account-level. Organization administration cannot grant access to personal conversations or data in another Organization.
- The context picker exposes Personal Chat, the account's Organizations, and create and join actions. Organization settings are available to the corresponding administrators. Within Communities, use **Open to organization members** and **Invite only** and display the member's current Community role. Channels continue to inherit Community membership under [P0-D04](2026-11-23-p0-d04-community-channel-hierarchy.md).

## Scope enforcement and initialization

- Authorize the explicit Personal or Organization scope on HTTP, SignalR, file and sync entry points. Partition SignalR delivery, client state and offline data by account and scope. A shared sign-in supports simultaneous tabs in different scopes. Revoking one Organization membership leaves Personal Chat and other Organizations available.
- Because Project Nexus has not been released to production yet, the schema starts from a clean greenfield baseline migration (`InitialSchema`) with no legacy single-Organization columns on `users` and no in-place cutover pipeline. Organization creation is enabled by default (`NexusOrganizations:AllowCreation=true`).
- Update the contracts and tests in implementation order under the [roadmap](../implementation-plan/ROADMAP.md) and verify scope isolation on a fresh PostgreSQL database.

This decision supersedes [P0-D02](2026-11-23-p0-d02-single-organization.md) and the automatic Organization enrollment in [P0-D13](2026-11-23-p0-d13-open-trial-registration.md). [P0-D01](2026-11-23-p0-d01-direct-dms.md) applies to DMs within an Organization.
