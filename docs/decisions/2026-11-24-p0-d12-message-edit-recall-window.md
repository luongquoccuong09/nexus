---
id: P0-D12
title: "15-minute message edit and recall window with If-Match concurrency and recalled marker"
summary: "Authors may edit or recall their own messages within 15 minutes of created_at using version and If-Match optimistic concurrency, with recalled messages wiping prior content and displaying a consistent recalled marker."
date: 2026-11-24
decided-by: owner
status: accepted
---

# 15-minute message edit and recall window with If-Match concurrency and recalled marker

Message editing and recall (`M6.3`, `UX-05`) are governed by strict server-side rules:

- **15-minute author window:** Only the message author with active send permission (`MessageSend`) may edit or recall a message within **15 minutes** of `created_at` (enforced on the server; returns `409 Conflict` once expired).
- **Optimistic concurrency:** Mutations require the `If-Match` header matching `messages.version` (`412 Precondition Failed` on version mismatch, `409 Conflict` if already recalled), returning `ETag` with the incremented version.
- **Zero-leak recall marker:** Recalling wipes `messages.body`, removes `shared_links`, reactions, pins, and saved-message references, blocks attachment downloads (`403`), redacts earlier `conversation_events` payloads for that message, returns `404 link_unavailable` on `M6.25` content links, excludes the recalled message from search, and renders a consistent recalled marker (`"This message was recalled"`) in feeds, threads, and sidebar previews.

**Why:** Limits post-hoc tampering while supporting quick typo corrections and mistake recovery without exposing recalled content through caches, sync deltas, or previews.

**How to apply:** Never trust client clocks or cached versions; execute conditional versioned updates inside a PostgreSQL transaction, redact prior event payloads on recall, and project empty body/attachments when `deleted_at` is set.
