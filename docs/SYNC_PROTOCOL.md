# Message Synchronization & Idempotency Protocol

- Status: active (canonical — completes `P0.3`)
- Last updated: 2026-11-23
- Scope: `M0.5` idempotency keys & conditional state transitions, and `M2` messaging ordering, delta sync, offline queue, and reconnect reconciliation.

PostgreSQL stores the authoritative state. SignalR is a **notification channel** that tells clients when to fetch changes — never the sole source of message delivery.

## 1. Message Send & Ordering (`client_msg_id`, `seq`, `change_seq`)

1. **Client-generated idempotency key (`client_msg_id`):**
   - The client generates a **UUIDv7** `client_msg_id` once per intentional send, persists it in IndexedDB (offline queue), and reuses the exact same `client_msg_id` across every retry and device reconnect.
2. **Single-transaction commit:**
   In one PostgreSQL transaction:
   - Verify account is `Active` and actor is authorized (`IAuthorizationService.Can`) as a current member (and DM not blocked).
   - Check `UNIQUE(sender_id, client_msg_id)`:
     - If a row already exists:
       - Re-verify that the actor still has access to that conversation (deny if removed since original send).
       - Compare `(conversation_id, payload_sha256)`. If identical, return the original HTTP `200`/`201` message response.
       - If conversation or payload hash differs, return **HTTP `409 Conflict`** (`SEC-19`).
   - Atomically increment `conversations.next_seq` and `conversations.change_seq` (`UPDATE conversations SET next_seq = next_seq + 1, change_seq = change_seq + 1 WHERE id = @id RETURNING next_seq, change_seq`).
   - Insert the `messages` row with `seq = next_seq` and `payload_sha256`.
   - Insert a `conversation_events` row with `change_seq` and `event_type = 'message.created'`.
   - Insert transactional outbox rows (M4) in the same transaction.
3. **Dual sequence numbers:**
   - **`seq`** (monotonic per conversation): chronological message ordering, history cursor pagination (`GET /api/conversations/{id}/messages?beforeSeq=N&limit=50`, max **50**), and monotonic read cursor (`last_read_seq = GREATEST(last_read_seq, @seq)`).
   - **`change_seq`** (monotonic per conversation): captures **every** mutation — new messages, edits, recalls, reactions, pins, thread reply counts, membership changes, and read-cursor updates.
   - Fetch per-conversation deltas via `GET /api/conversations/{id}/changes?after=N&limit=50` (max **50** events per page).

## 2. SignalR Fanout & Crash Recovery

1. **User-scoped SignalR groups (`user:{id}`):**
   - Clients never join arbitrary conversation groups. On connect, the server authenticates the session (`ActiveUserHubFilter`) and binds the connection to `user:{currentUserId}`.
   - After PostgreSQL commit, the server reads current authorized recipients from PostgreSQL and broadcasts a lightweight nudge: `{ conversationId, changeSeq }`.
2. **Crash between DB commit and SignalR broadcast:**
   - Because SignalR is best-effort, a server crash or network drop after commit means clients may miss the nudge.
   - Recovery relies on two mechanisms:
     1. **Reconnect reconciliation:** Whenever SignalR reconnects (with randomized exponential jitter), the client calls `GET /api/sync/reconcile` (returning accessible conversations with their latest `change_seq`, capped/paginated at 50) to discover missed changes and newly created DMs/groups/communities.
     2. **Active-view staleness check:** While a tab is open and connected, a low-frequency heartbeat/visibility-change reconcile (`visibilitychange` to `visible`) catches any silent missed nudge without polling storms.
3. **Client delta application:**
   - Apply events strictly in ascending `change_seq` order.
   - Discard any event with `change_seq <= localChangeSeq`.
   - If the first returned event has `change_seq > localChangeSeq + 1` (gap detected), fetch the missing range or fall back to full conversation reload.
   - Advance `localChangeSeq` in IndexedDB **only after** applying the batch cleanly.
   - Always recheck membership on delta reads and offline queue replay (`SEC-18`).

## 3. Delta Retention, Full-Resync Triggers & Rejoin History

| Rule | Specification |
|---|---|
| **`conversation_events` retention** | Retain durable delta events for **30 days** (pruned by background cleanup job; configurable). Each conversation tracks `min_available_change_seq`. |
| **Full-resync trigger** | If a client requests `GET /changes?after=N` where `N < conversation.min_available_change_seq - 1` (stale offline device), the server returns **HTTP `410 Gone`** with `{ "code": "resync_required", "latestChangeSeq": M }`. The client clears cached messages for that conversation, fetches the latest page (`limit <= 50`), and sets `localChangeSeq = M`. |
| **History after removal** | When a member is removed (`left_at` set), delta reads and history queries immediately return `403 Forbidden` (`SEC-18`), and the client evicts that conversation from local IndexedDB cache. |
| **History after rejoining** | For **Group chats** and **Private communities**, rejoining sets `visible_from_seq = conversation.next_seq` at rejoin time so prior private history stays hidden. For **Public communities**, rejoining grants access to current community/channel history (`visible_from_seq = 0`). |
| **Version reload** | On reconnect or periodic check against `GET /api/version`, if the returned version differs from the loaded SPA version, prompt/reload the client so schema/protocol contracts stay aligned across deploys. |

## 4. Non-Message `Idempotency-Key` Contract (`M0.5`)

Used on mutating creation endpoints that lack a natural unique key (e.g., `POST /api/groups`, `POST /api/communities`, `POST /api/communities/{id}/channels`).

| Property | Specification |
|---|---|
| **Header** | `Idempotency-Key: <string>` (1–128 ASCII chars, recommended UUIDv7). |
| **Uniqueness scope** | `UNIQUE(user_id, http_method, route_template, idempotency_key)` in `idempotency_keys`. |
| **TTL** | **24 hours** from creation (`expires_at`). Expired rows are pruned periodically and treated as non-existent on lookup. |
| **Request fingerprint** | `request_hash = SHA-256(canonical JSON request body)`. |
| **Replay behavior** | - **Exact match (`request_hash` equals stored hash) & completed:** Re-check authorization; return the stored `status_code` and `response_json` with header `Idempotency-Replayed: true`.<br>- **Hash mismatch:** Return **HTTP `409 Conflict`** (`{ "code": "idempotency_key_conflict" }`) (`SEC-19`).<br>- **Concurrent in-flight request (stored row has `completed_at IS NULL`):** Return **HTTP `409 Conflict`** (`{ "code": "idempotency_in_progress" }`) with `Retry-After: 1`. |
| **Failure handling** | If the underlying handler throws an unhandled `5xx` error, delete/release the in-flight key row so the client can safely retry. Deterministic `4xx` responses (e.g., `400`/`422`) are not cached as successful creations. |

## 5. Conditional State Transitions & Idempotent Mutations (`M0.5`)

- **State transitions** (e.g., ownership transfer, join request approval, quarantine release) execute a single conditional update:
  `UPDATE ... SET status = @target WHERE id = @id AND status = @expected`
  - If `rowsAffected == 1`: transition succeeded.
  - If `rowsAffected == 0`: inspect current row — if already at `@target`, return idempotent success; otherwise return **HTTP `409 Conflict`**.
- **Set-membership / toggle actions** (reactions, pins, saves, mute, poll votes, announcement acknowledgments) use idempotent `PUT` / `DELETE` backed by `UNIQUE` constraints (`ON CONFLICT DO NOTHING`) rather than non-idempotent toggle endpoints.

## 6. Required Verification Scenarios (`M0.5` & `M2.12`)

- **M0.5:** Exact `Idempotency-Key` replay returns identical payload; mismatched body under same key returns `409 Conflict`; expired key allows fresh execution; conditional state transition returns `409` on concurrent conflict.
- **M2.12:** Missed final SignalR signal recovered via reconnect reconciliation (`SEC-17`); two devices retrying the same `client_msg_id` concurrently produce 1 message (`409` on payload mismatch, `SEC-19`); member removed while offline is rejected on delta sync and offline queue replay (`SEC-18`); stale cursor beyond retention triggers `410 Gone` full resync.
