---
id: P0-D09
title: "Optional DM read receipts enabled by default with reciprocal privacy toggle"
summary: "Direct message read receipts are enabled by default (dm_read_receipts_enabled = true) and toggleable in Profile & security with strict two-way reciprocity."
date: 2026-11-24
decided-by: owner
status: accepted
---

# Optional DM read receipts enabled by default with reciprocal privacy toggle

Optional direct message read receipts (`M6.8`) follow a reciprocal privacy model:

- Every user defaults to `users.dm_read_receipts_enabled = true` and can toggle the setting in **Profile & security**.
- **Two-way reciprocity:** Disabling DM read receipts immediately stops both sending read receipts to DM peers and viewing any peer's DM read receipt, across both HTTP responses and live SignalR `DmReadReceiptUpdated` events.
- **Scope boundary:** Read receipts apply only to unblocked Direct Messages (`ConversationKind.Dm`) and are never exposed for Group chats, Community channels, Private Self chat, or blocked DMs.

**Why:** Gives DM participants clear delivery/seen feedback by default while ensuring opting out for privacy removes asymmetric visibility.

**How to apply:** Evaluate `actor.DmReadReceiptsEnabled && peer.DmReadReceiptsEnabled && !isBlocked` on both read-cursor projections and SignalR receipt fanout, and omit `peerLastReadSeq` / `peerLastReadAt` whenever either party has opted out.
