# Plan: M2 — Core Messaging & Synchronization (`M2.1`–`M2.12`)

- Status: active (`develop`)
- Date: 2026-11-23
- Scope: `M2.1`–`M2.12` delivering **Release 1: usable realtime DM and group chat**.
- Security tests covered: `SEC-03`, `SEC-05`, `SEC-07`, `SEC-10`, `SEC-15`, `SEC-17`, `SEC-18`, `SEC-19`

## Key Design Decisions

1. **PostgreSQL authoritative state (`M2.1`, `M2.3`):**
   - `conversations` (`kind`, `dm_key`, `next_seq`, `change_seq`, `min_available_change_seq`), `conversation_members` (`role`, `last_read_seq`, `visible_from_seq`, `muted`, `left_at`), `messages` (`seq`, `client_msg_id`, `payload_sha256`, `body`), `conversation_events` (`change_seq`, `event_type`, `payload_json`), `user_blocks`, `user_reports`.
   - Single-transaction message send allocates `next_seq` + `change_seq` via atomic `UPDATE ... RETURNING`, enforces `UNIQUE(sender_id, client_msg_id)` (`409` on conflict — `SEC-19`), and persists message + event together.
2. **User-scoped SignalR fanout (`M2.4`, `M2.10`):**
   - `MessagingHub` (`/hubs/messaging`) adds connections strictly to `user:{id}` (never client-chosen conversation groups — `SEC-07`).
   - Recipients are resolved from PostgreSQL (`left_at IS NULL`) after commit and sent lightweight `{ conversationId, changeSeq }`.
   - Presence & typing indicators are throttled in memory only (no Redis).
3. **Sync, offline queue & sanitized Markdown (`M2.5`–`M2.9`):**
   - History cursor-paged by `seq` (max 50); deltas paged by `change_seq` (`GET /changes?after=N`, `410 Gone` on stale cursor); reconnect reconciliation (`GET /api/sync/reconcile`).
   - Monotonic reads via `GREATEST(last_read_seq, ...)`.
   - Client IndexedDB offline queue + SignalR reconnect jitter + `/api/version` reload detection + strict HTML-escaping Markdown renderer (`SEC-10`).
