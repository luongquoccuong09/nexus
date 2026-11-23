# Message Synchronization Protocol

- Status: active (canonical — supersedes `docs/sync-protocol.md`)
- Last updated: 2026-11-23
- Scope: P0.3, M0.5, and M2 messaging ordering, idempotency, delta sync, and reconnect reconciliation.

PostgreSQL stores the authoritative state. SignalR is a **notification channel** that tells clients when to fetch changes — never the sole source of message delivery.

## 1. Core invariants

1. **Client-generated idempotency key (`client_msg_id`):**
   Generate a `client_msg_id` for each intentional send and reuse it on every retry across offline queues and network hiccups.
2. **Single-transaction commit:**
   In one PostgreSQL transaction:
   - Authorize the send (account `Active` + current membership).
   - Enforce uniqueness on `(sender_id, client_msg_id)`.
   - Allocate the conversation's next message `seq`.
   - Increment the conversation's `change_seq`.
   - Persist both the message and its `conversation_events` record.
   - On retry with the same `(sender_id, client_msg_id)`: after checking access, return the original result if conversation and payload match; return **HTTP `409 Conflict`** if the conversation or payload differs (`SEC-19`).
3. **Dual sequence numbers (`seq` vs `change_seq`):**
   - Use `seq` for chronological message order, history cursor pagination (capped at 50), and read state (`GREATEST(last_read_seq)`).
   - Use `change_seq` for all conversation mutations (new messages, edits, recalls, reactions, pins).
   - Page history by `seq`; fetch per-conversation deltas by `change_seq` (`GET /changes?after=N`).
4. **Client delta application and reconciliation:**
   - Apply deltas strictly in `change_seq` order, discard duplicates, and advance the local cursor **only after** successful application.
   - On reconnect, reconcile accessible conversations and their cursors to discover new DMs, groups, or channels.
   - Always recheck membership on delta reads and offline queue replay (`SEC-18`).

## 2. SignalR fanout contract (M2.4)

- Clients join per-user SignalR groups named `user:{id}` (never trust client-supplied conversation group joins).
- After transaction commit, read authorized recipients from PostgreSQL and push a lightweight `(conversation_id, change_seq)` signal.
- Clients seeing a gap or new `change_seq` pull authoritative deltas over HTTP.

## 3. Open items to finalize before M0.5 / M2 (P0.3)

- **Crash between commit and broadcast:** replay / client catch-up window and polling fallback.
- **Delta retention and full-resync triggers:** how long `conversation_events` are kept and what response tells a stale client to perform a full resync.
- **History after rejoining:** visibility boundary when a removed member rejoins a group or community.
- **Non-message `Idempotency-Key` (M0.5):** key scope, TTL, and replay response format for operations such as group creation and community creation.

## 4. Required verification scenarios (M2.12)

- Missed final SignalR signal (client catches up via reconnect/delta sync — `SEC-17`).
- Two devices retrying the same `client_msg_id` concurrently (no duplicates; `409` on conflicting payload — `SEC-19`).
- Member removed while offline, then reconnecting and replaying queued sends (`SEC-18`).
