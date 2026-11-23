# Domain Glossary

- Status: active (canonical)
- Last updated: 2026-11-24

Use these exact terms in product copy, code identifiers, database schemas, tests, and documentation.

| Term | Meaning in Nexus | Canonical reference |
|---|---|---|
| **Organization** | An isolated workspace with its own membership, Directory, HR records, and Communities (displayed together in a single window without switching routes). Department and branch are optional directory metadata. | [P0-D14](decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) |
| **Personal Chat** | Unified account-level messaging (DMs and group chats) across shared-workspace members and accepted external contacts. Available with zero Organization memberships; 1-on-1 DM history persists even after leaving a Workspace. | [P0-D14](decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) |
| **Contact** | A connection between two Nexus accounts outside a shared Workspace after both users consent (by exact username or private contact link). | [P0-D14](decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) |
| **Community** | A named group within an Organization with membership, `Owner`/`Admin`/`Member` roles, visibility (**Shared Community** or **Private Community**), and its own main feed directly (following Microsoft Teams Communities). | [P0-D04](decisions/2026-11-23-p0-d04-community-channel-hierarchy.md), [P0-D14](decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) |
| **Channel** | An optional sub-conversation belonging to exactly one community (created on demand via `+ Add channel`; no forced default `General` channel). In MVP, channels inherit community membership and roles. | [P0-D04](decisions/2026-11-23-p0-d04-community-channel-hierarchy.md) |
| **Conversation** | Base messaging container (`dm`, `group`, or `channel`) owning `next_seq` and `change_seq`. | [`SYNC_PROTOCOL.md`](SYNC_PROTOCOL.md) |
| **DM / Group chat** | Two-person and multi-person conversations with independent membership in unified Personal Chat (default UI) or scoped within an Organization. | [P0-D01](decisions/2026-11-23-p0-d01-direct-dms.md), [P0-D14](decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) |
| **Official** | A designation granted by a verified `CommsAdmin` to a community. The standard creation action is "Create community." | [`SECURITY_AND_AUTHZ.md`](SECURITY_AND_AUTHZ.md) |
| **Active / Disabled** | Account access states. A `Disabled` account is immediately denied across HTTP, SignalR, downloads, and sync. The optional `Former` state is pending [P0-D06](decisions/2026-11-23-p0-d06-d07-offboarding-and-break-glass.md). | [`SECURITY_AND_AUTHZ.md`](SECURITY_AND_AUTHZ.md) |
| **Verified / Unverified** | Organization membership trust levels, independent of account access state. An Organization's creator starts `Verified` as Owner; invited members start `Unverified` until verified within that Organization. | [P0-D14](decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) |
| **SystemAdmin / HRAdmin / CommsAdmin** | System or organization administration roles. Access to private conversation content still depends on resource membership (never implicit). | [`SECURITY_AND_AUTHZ.md`](SECURITY_AND_AUTHZ.md) |
