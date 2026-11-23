# Design System & UX Conventions

- Status: active (scaffold — expand as React UI components are built from M1+)
- Last updated: 2026-11-23

## Core UI/UX rules

1. **Language:** All product copy, labels, buttons, empty states, error messages, and accessibility (`aria-*`) labels are in **English**. Preserve people's names and user-authored content in their original language.
2. **Teams-style Community → optional sub-channels ([P0-D04](../decisions/2026-11-23-p0-d04-community-channel-hierarchy.md)):** Communities appear in the Communities sidebar with a rounded-square avatar, name, and clean divider. Selecting a Community opens its main feed directly (no forced `# General` sub-channel). Optional sub-channels appear directly beneath the Community with a channel icon and a subtle `+ Add channel` action for Owners/Admins. DMs and group chats live in Chat.
3. **Trust visibility ([P0-D13](../decisions/2026-11-23-p0-d13-open-trial-registration.md)):** Display the `Unverified` badge wherever an unverified account's name appears (`M1.4b`).
4. **Standard community creation:** Use a single "Create community" action; `Official` is a separate designation granted by a verified `CommsAdmin` (`M5.2`).
5. **Prototypes are visual guides only:** See [`../mockups/README.md`](../mockups/README.md). Backend always enforces authorization.

Add tokens, typography, spacing, and component specs here as `src/Nexus.Client/src/` grows.
