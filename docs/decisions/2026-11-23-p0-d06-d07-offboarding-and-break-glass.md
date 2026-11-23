---
id: P0-D06/D07
title: "Offboarding beyond Disabled and break-glass emergency access"
summary: "Open before M1.6 / M7.5: decide whether to add a Former account state (D06) and define two-approval break-glass access (D07)."
date: 2026-11-23
decided-by: pending
status: open
---

# Offboarding beyond Disabled and break-glass emergency access

Decision required with IT/HR before implementing `M1.6` (account offboarding states) and `M7.5` (audited break-glass exports). See [`docs/archive/project-nexus-audit-v0.4.md`](../archive/project-nexus-audit-v0.4.md) §7 for initial recommendations.

**Why:** Determines whether offboarded employees use `Disabled` only or a distinct `Former` state, and governs emergency legal/compliance access without giving `SystemAdmin` routine visibility into private chats.

**How to apply:** Until decided, implement `Active` and `Disabled` only (`M1.6`), and keep `SystemAdmin` denied from private conversations.
