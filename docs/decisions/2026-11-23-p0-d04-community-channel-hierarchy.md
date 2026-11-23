---
id: P0-D04
title: "Community owns membership and main feed; sub-channels are optional"
summary: "Following Microsoft Teams Communities, each Community has its own main feed directly with zero default sub-channels; Owners/Admins add optional sub-channels that inherit membership and roles."
date: 2026-11-23
decided-by: owner
status: accepted
---

# Community owns membership and main feed; sub-channels are optional

Communities follow the **Microsoft Teams Communities** model:

- A **Community** owns membership and `Owner`/`Admin`/`Member` roles, and has its **own main feed** directly when selected — do **not** auto-create a `# General` / `# Chung` sub-channel or pre-baked generic rooms (such as "Meetups" / "Góc gặp gỡ").
- Community `Owner`/`Admin` users can add **optional sub-channels** directly beneath the Community on demand (`+ Add channel`).
- In MVP, sub-channels inherit membership and roles from their parent Community. DMs and group chats remain independent conversations under Chat.

This model supersedes the earlier D04 recommendation in [`docs/archive/project-nexus-audit-v0.4.md`](../archive/project-nexus-audit-v0.4.md) and refines the earlier "auto-created General channel" draft.

**Why:** Matches Microsoft Teams Communities directly, keeps the sidebar clean and uncluttered for small/project communities that do not need sub-channels yet, and avoids artificial default channels.

**How to apply:** Store `community_id` on community-scoped conversations (`M5.0`/`M5.1`): each Community has a primary community feed conversation (`is_default_feed` or `channel_id = null`) and zero or more named sub-channels. Authorize all reads, posts, searches, and file downloads via parent Community membership. Removing a user from a Community immediately revokes access to the Community feed and all sub-channels.
