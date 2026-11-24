# M4 — Notifications and Outbox

Follow the [M4 roadmap](../ROADMAP.md#m4--notifications-and-outbox) and the [authorization matrix](../../SECURITY_AND_AUTHZ.md). The module uses the existing PostgreSQL role separation and the Personal and Organization resource boundaries.

| Item | Delivery path | Acceptance |
|---|---|---|
| **M4.1 Outbox** | Save the event in the producer's transaction. Claim due rows with `FOR UPDATE SKIP LOCKED` and commit handler effects with the completion state. Trigger real work from **Remind outstanding** in Community announcements. | Concurrent workers skip locked rows, transient failures roll back before a delayed retry, exhausted or unknown events are dead-lettered, and replay creates one effect per recipient. |
| **M4.2 Mentions** | Support targeted user mentions in chat and Community feeds. Apply `MentionChannelOrAll` to group or Channel broadcasts. Resolve the audience in the source scope and enqueue without copying message text into the outbox. | Eligible recipients see unread mention counts independently of unread message counts. Deduplicate repeated mentions and verify Unverified, removed, and out-of-scope users with the current authorization rules. |
| **M4.3 Notification center** | Expose recipient-owned notifications in a paged Activity view. Resolve access to the source resource before showing it. Provide an idempotent read operation and show links into the existing authorized chat and feed entry points. | Verify paging, parallel read requests, cross-device read state, and access changes between generation and display. |
| **M4.4 Preferences** | Store per-account, per-conversation All, Mentions, or Muted settings and quiet hours with a time zone. Use them when selecting notification delivery. | Verify preference updates across devices, channels with inherited access, quiet intervals crossing midnight, and daylight-saving changes. |
| **M4.5 Web Push** | Evaluate a compatible VAPID/Web Push library under the dependency rules. Keep private keys server-side. Use a same-origin service worker and a per-resource notification tag. Document iOS Home Screen installation and permission flow. | Verify subscribe, revoke, dismiss, expired subscription handling, and deduplication. Recheck resource access, read status, and preferences before dispatch. Push payloads contain no private content. |
| **M4.6 Email digest** | Enable optional SMTP digest delivery after the configured unread age when push is unavailable. | Verify expiry, user preference, and read/access rechecks at send time. Deduplicate retries and keep private DM content out of email. |

Use the normal browser or Desktop entry point for each UI flow and record the exact environment and verification commands in the M4 handoff.
