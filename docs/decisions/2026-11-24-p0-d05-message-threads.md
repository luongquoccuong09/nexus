---
id: P0-D05
title: "Message threads use parent_message_id with reply pane, following, and thread unread state"
summary: "Chat threads use nullable self-referencing parent_message_id (single-level replies), per-user thread_subscriptions for following and unread state, and a right-hand Thread pane capped at 50 replies per page."
date: 2026-11-24
decided-by: owner
status: accepted
---

# Message threads use parent_message_id with reply pane, following, and thread unread state

Chat threads (`M6.2`, `UX-02`) use a single-level thread model anchored to `messages.parent_message_id`:

- Root messages keep `parent_message_id IS NULL` and track denormalized `reply_count`, `last_reply_at`, and `last_reply_seq`. Nested reply-to-reply messages are rejected (`400 nested_thread_not_allowed`).
- Replies share the conversation's `seq`/`change_seq` space while main conversation unread counts follow `conversations.latest_main_seq` so thread traffic never inflates main timeline unread badges.
- Per-user follow state and `last_read_reply_seq` live in `thread_subscriptions`, indexed alongside `(conversation_id, parent_message_id, seq)` on `messages`.
- Users inspect and compose thread replies in a dedicated right-hand Thread pane paginated at up to 50 replies per page.

**Why:** Keeps side discussions organized without cluttering the main conversation stream while preserving `seq`-based history ordering and `VisibleFromSeq` (`SEC-18`) access boundaries.

**How to apply:** Enforce `parent_message_id` single-level validation, `VisibleFromSeq`, and `IAuthorizationService.Can` checks across sends, thread reply queries, follow/unfollow, thread read updates, SignalR deltas, and `M6.25` deep links.
