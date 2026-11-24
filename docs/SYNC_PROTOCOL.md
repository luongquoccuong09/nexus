# Message Synchronization & Idempotency Protocol

- Status: active (canonical — completes `P0.3`)
- Last updated: 2026-11-24
- Scope: `M0.5` idempotency keys & conditional state transitions, and `M2` messaging ordering, delta sync, offline queue, and reconnect reconciliation.

PostgreSQL stores the authoritative state. SignalR delivers realtime updates and nudges — never the sole source of message persistence.

The single-window SPA routes unified account-level Chat (`dm` and `group`) through `/api/personal` (with IndexedDB drafts, queue, and cursors scoped by `${userId}:personal`), while Organization-scoped Communities, Channels, Directory, HR, and administration use `/api/organizations/{organizationId}` without page reloads. Scoped `/api/organizations/{organizationId}` conversation and sync routes remain enforced on the backend for explicit multi-scope isolation.

## 1. Message Send & Ordering (`client_msg_id`, `seq`, `change_seq`)

1. **Client-generated idempotency key (`client_msg_id`):**
   - The SPA generates a `client_msg_id` once per intentional send. It persists the key and original payload in IndexedDB and reuses them across retries and reconnects.
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

### Local drafts and send recovery (`M6.17`)

- Each chat has its own local draft text and staged attachment references. Drafts survive reloads in the same browser profile. Attachment bytes stay in the existing server upload pipeline. On recovery, the client checks the pending IDs and asks the sender to attach unavailable files again.
- An IndexedDB transaction moves the submitted draft into the queue with its original conversation, body, attachment IDs, and `client_msg_id`. A later composer edit keeps its own revision. Older text-only queue entries remain replayable. A network interruption queues an automatic retry on reconnect. For an HTTP failure, the sender can retry, move the content back to an empty composer, or dismiss it. A changed payload gets a new key.
- Sign out clears that account's local chat database. Other tabs of the same account return to sign in. Group removal or leave evicts the conversation's draft, queued work, cursor, and in-memory messages. HTTP history and attachment checks also revoke local access when the realtime notice was missed.

### Visible read state (`M6.18`)

- The SPA evaluates displayed message rows in the selected conversation when the Chat destination and browser document are visible. It advances the read cursor to the highest displayed sequence through the existing read endpoint. Other tabs receive the monotonic cursor through `ReadStateUpdated`.
- The active scroll position follows the end while the reader stays near it. Away from the end, the client restores the visible message and its offset across inserted history or newly delivered messages. **New messages** jumps to the latest displayed message and moves keyboard focus there.
- SPA sends opt out of implicit send-time read advancement. Offline replay can commit while Chat is out of view and leaves read advancement to the visible view. Older send clients retain their existing behavior.

## 2. SignalR Fanout & Crash Recovery

1. **SignalR groups per account and scope.**
   - On connect to `/hubs/personal/messaging` (single-window default), the server authenticates the session and binds the connection to `scope:{Guid.Empty}:user:{currentUserId}` plus `scope:{orgId}:user:{currentUserId}` for each active Organization membership (refreshable via `RefreshOrganizationSubscriptions()`). Scoped `/hubs/organizations/{orgId}/messaging` hubs remain supported and verify membership on connect.
   - After PostgreSQL commit, the server reads current authorized recipients from PostgreSQL and broadcasts via batched `Clients.Groups(...)`: `{ conversationId, changeSeq, message?, delta? }` for chat and `{ conversationId, delta? }` for community feed updates. Clients apply sequential `change_seq == localChangeSeq + 1` payloads directly into in-memory caches and fall back to `GET /changes?after=N` on a gap or reconnect. Revoking an Organization membership immediately removes live connections from `scope:{orgId}:user:{memberId}`, aborts scoped org hub connections, and pushes `OrganizationAccessRevoked`.
2. **Crash between DB commit and SignalR broadcast:**
   - Because SignalR is best-effort, a server crash or network drop after commit means clients may miss the nudge.
   - Recovery relies on two mechanisms:
     1. **Reconnect reconciliation:** Whenever SignalR reconnects (with randomized exponential jitter), the client calls `GET /api/sync/reconcile` (returning accessible conversations with their latest `change_seq`, capped/paginated at 50) to discover missed changes and newly created DMs/groups/communities.
     2. **Active-view staleness check:** While a tab is open and connected, a low-frequency heartbeat/visibility-change reconcile (`visibilitychange` to `visible`) catches any silent missed nudge without polling storms.
3. **Client delta application:**
   - Apply events strictly in ascending `change_seq` order.
   - Discard any event with `change_seq <= localChangeSeq`.
   - When a SignalR event arrives across a gap, fetch `/changes` from the local cursor. The server can omit events outside the member's history and still advance `nextAfter` across them. A `410 Gone` response triggers a fresh snapshot.
   - Advance `localChangeSeq` in IndexedDB **only after** applying the batch cleanly.
   - Always recheck membership on delta reads and offline queue replay (`SEC-18`).

### Chat reactions (`M6.1`)

- A reaction PUT or DELETE takes the conversation lock shared with group membership changes. A state change advances `change_seq` and writes `message.reaction.added` or `message.reaction.removed` in the same transaction as the reaction row. Both events carry `{ messageId, seq, emoji, userId, reacted }`. Repeated requests with the same desired state allocate no event. Reactions leave the chronological message sequence and conversation sort date intact.
- SignalR sends the same transition as a chat delta with `type: "reaction"`. Live fanout and `/changes` enforce `seq > visible_from_seq` for the target message. The server aggregates history summaries in one query per page and returns them with a cursor from the same snapshot.
- The SPA displays the pending reaction on the target message immediately and allows one request per message and emoji at a time. SignalR and replay update the confirmed state. A failed request clears the pending change. Replay skips deltas already included in the target message's history snapshot so a live update or delayed history response cannot double-count reactions.

### Chat threads (`M6.2`)

- A reply increments `next_seq` and `change_seq` and updates the root's reply summary in the same transaction. Only root messages advance `latest_main_seq` or move the main read cursor at send time. The idempotency fingerprint includes the root ID for a reply, and existing root-message fingerprints remain valid.
- The durable `message.thread_reply.created` event contains the root summary and reply DTO. Live `ConversationChanged` sends the same payload to members whose history includes the root. The root visibility floor also applies to later reply reaction events and reply links and attachments. Clients apply the summary to the root and add the reply to an open thread without inserting it in the main timeline.
- A thread page snapshots its root, replies, and conversation cursor together. The cursor supports catch-up through `/changes`. Follow and read state are per user and are delivered through `ThreadStateUpdated` with a monotonically increasing state version. The thread's read cursor does not advance the main chat cursor.

### Message edit and recall (`M6.3`)

- The server checks authorship, current send permission, the 15-minute window, and `If-Match` in the mutation transaction. It advances the message version and `change_seq` and writes `message.edited` or `message.recalled`. Message sequence and conversation ordering stay intact.
- The same delta is used for live delivery and replay. An edit replaces the visible body. A recall applies the marker and clears body, attachments, and reactions from the displayed message. The server removes prior content from that message's replay events as part of a recall. An authorized reply mutation uses the root sequence as its visibility floor.
- The client updates optimistically with at most one mutation for the message in flight. A stale version returns `412` so the client reconciles against the current version. The server never accepts an optimistic change beyond the author window.

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
| **Uniqueness scope** | `UNIQUE(user_id, http_method, route_template, idempotency_key)` in `idempotency_keys`. Scoped routes include the scope identifier in the stored route template. |
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
