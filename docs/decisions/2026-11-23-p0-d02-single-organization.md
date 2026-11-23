---
id: P0-D02
title: "Single Organization per deployment"
summary: "Each Nexus deployment uses one Organization as its account boundary; department and branch are optional directory metadata."
date: 2026-11-23
decided-by: owner
status: superseded
---

Superseded by [P0-D14](2026-11-23-p0-d14-personal-chat-and-organizations.md).

# Single Organization per deployment

Each deployment operates within a single `Organization` account boundary rather than multi-tenant SaaS organizations. Department and branch are stored as optional directory metadata for search and HR mapping.

**Why:** Target scale is ~1,000 accounts in one company. Avoiding multi-tenant complexity keeps authorization, schema design, and operations simple and auditable.

**How to apply:** Model one `Organization` record (`M1.1`). Do not introduce tenant-switching UI or cross-organization routing.
