# PROJECT NEXUS — Plan v0.4 (todo list)

> Replaces §11 of [`docs/archive/project-nexus-proposal-v0.3.md`](../archive/project-nexus-proposal-v0.3.md). See [`docs/archive/project-nexus-audit-v0.4.md`](../archive/project-nexus-audit-v0.4.md) for the review that informed the plan.
> Target: about 1,000 accounts and 200 active users. One C# developer with a coding agent.
> Each checkbox is intended as a single implementation session covering the necessary migration, endpoint or hub, tests and minimal UI.
> Implementation details belong in the corresponding session. Milestones should end with a usable release.

A dependency is marked `→ X`. DoD means definition of done. Resolve decisions marked ⚑ before coding the associated item.

---

Start with the [repository guide](../../AGENTS.md), [decisions index](../decisions/INDEX.md) and [M0.1 handoff](m0-foundation/HANDOFF-m0.1.md).

## Product decisions · November 23, 2026

**Community → Channel.** People create and name communities and invite members. Each community starts with a **General** channel. Expand a community in the Communities tab and select a channel to read and discuss posts. DMs and group chats live in Chat.

D02 establishes one Organization for accounts. Under D04, communities manage membership and channels hold content. MVP channels inherit community members and roles. Posts, comments and announcements belong to a channel.

## P0 — Decisions and documentation

- [ ] **P0.1** Resolve P0-D05 through D12 with IT/HR (audit §7) and record outcomes in [`docs/decisions/`](../decisions/INDEX.md). Accepted on November 23, 2026: D01 direct DMs, D02 one Organization, D04 Community → Channel, D03 password first and Google linking later, and D13 open trial registration with Unverified accounts.
- [x] **P0.2** Complete the [authorization matrix](../SECURITY_AND_AUTHZ.md) by role, action and scope (audit §3.3). Cover administrative access and content access, community creation and joining, Owner/Admin channel creation, member reads and posts, and Unverified restrictions. Derive tests from the matrix.
- [x] **P0.3** Complete the [sync protocol](../SYNC_PROTOCOL.md) for `client_msg_id`, `seq`, `change_seq`, `conversation_events`, reconnects, gaps, reconciliation and idempotency keys (audit §4).
- [x] **P0.4** [Glossary](../GLOSSARY.md) covering Organization, Community, Channel, Conversation (dm/group/channel), official designation, Owner/Admin/Member and Former/Disabled.
- [ ] **P0.5** Update the proposal to v0.4 with 1,000 accounts, revised D02 and D04, consistent terminology, SEC-17 through SEC-24 and a pilot budget.
- [x] **P0.6** Prototypes in [`docs/mockups/`](../mockups/README.md) cover open DMs (D01), password registration and sign-in (D03/D13), the Unverified badge and simulated Google linking. The end-user prototype has a community/channel tree, General channel creation, additional channels and per-channel posts. Backend work belongs to M5.
- [x] **P0.7** [Agent instructions](../../AGENTS.md) link to the decision log, authorization matrix, sync protocol and constraints from proposal §9.1.
- [ ] **P0.8** Ask IT about permitted data regions, the security reviewer, internal SMTP for self-service recovery and future M365/Google Workspace identity linking.

## M0 — Foundation

- [ ] **M0.1** Same-origin ASP.NET Core (.NET LTS) and React/Vite/TypeScript solution. Modules: Identity, Org, People, Conversations, Messaging, Files, Notifications and Admin. Solution, client, HTTP smoke test and integrated publish verified locally. CI build and test remain pending Git host connection.
- [x] **M0.2** PostgreSQL, EF Core and a migration pipeline with separate runtime and migration roles, verified TLS and Testcontainers tests.
- [x] **M0.3** Secure defaults: authenticated `FallbackPolicy`, `ActiveUserMiddleware`, `ActiveUserHubFilter`, antiforgery, WebSocket Origin checks, CSP, body/page limits and rate limiting.
- [x] **M0.4** Implement `IAuthorizationService.Can(actor, action, resource)` and matrix-derived tests, initially denying all cases. → P0.2
- [x] **M0.5** Add the `idempotency_keys` table, an `Idempotency-Key` filter and a conditional state-transition helper. → P0.3
- [x] **M0.6** Serilog JSON logging without bodies or tokens, OpenTelemetry, `/healthz`, `/readyz` and `/api/version`.
- [x] **M0.7** CI checks: Gitleaks, `dotnet list package --vulnerable`, `npm audit`, Trivy and Roslyn security analyzers. Reject AllowAnyOrigin, disabled certificate validation and privileged storage credentials in the frontend.
- [x] **M0.8** Containerize and deploy the demo. Sleeping PaaS instances are acceptable for demos. Persist Data Protection keys and sessions in PostgreSQL. Use development-only seeded accounts for M0.
- [x] **M0.9** Provide `IFileStorage` for private Supabase storage/local volumes and an `IFileScanner` stub returning `skipped_prototype`.
- **M0 acceptance:** Simulate login, write a record, receive a SignalR event and upload/download a private PDF. CI passes the security checks.

## M1 — Identity and directory (D03, audit §8)

**M1a — Username and password**

- [x] **M1.1** Model one Organization, Department/Branch and `employees` records for verification. Bootstrap the first SystemAdmin via CLI or seed.
- [x] **M1.2** Use ASP.NET Core Identity, EF Core, PostgreSQL and a BFF session cookie. Store UUID, unique normalized username, display name, status, Unverified/Verified trust level, nullable unique employee_id, terms_version and accepted_at. Define username and reserved-name rules.
- [x] **M1.3** Open registration (D13) accepts username, password, display name and terms. New accounts enter the Organization as Unverified. Configure RegistrationMode (Open by default, OpenWithJoinCode, later InviteOnly/Approval). Add per-IP limits, honeypot, switchable CAPTCHA, noindex and trial confidentiality notice. A unique username prevents duplicate accounts. Test SEC-20.
- [x] **M1.4** Sign in and out. Require at least 12 characters, block common passwords, lock out repeated account failures, rate-limit by IP and return generic errors that conceal username existence. Test SEC-01 and SEC-21.
- [x] **M1.4b** Display Unverified wherever a name appears. Enforce official/announcement channel restrictions, @all and admin-role restrictions through the matrix. Limit new DMs per day. Admins verify employee_id and can rename impersonating accounts. Test SEC-20b.
- [x] **M1.5** Password changes update the security stamp, revoke other sessions and disconnect SignalR. Audit one-time admin reset links. Test SEC-24.
- [x] **M1.6** Support Active/Disabled and Former if D06 approves it. Revoke sessions, update the security stamp, abort SignalR connections and audit the change. Test SEC-08 and SEC-16.
- [x] **M1.7** List signed-in devices and support remote sign-out.
- [x] **M1.8** Use Identity authenticator TOTP. Require MFA for SystemAdmin, HRAdmin and CommsAdmin.

**M1b — Directory**

- [x] **M1.9** Import HR CSV records by employee_code with upsert, dry run and a diff report. Suggest matches between Unverified accounts and employees for administrator review.
- [x] **M1.10** Search Vietnamese names using unaccent and pg_trgm. Filter by department, show minimal profiles and hide Disabled/Former accounts.
- [x] **M1.11** Add custom status (UX-06) and re-encoded avatar images.

**M1c — Google linking after password sign-in stabilizes**

- [ ] **M1.12** Google OIDC with openid/email/profile, PKCE, nonce and state. To link Google in Security, require an active login and password reauthentication. Store unique (issuer, sub), audit and notify. Never link automatically by matching email. Test SEC-22 and SEC-23.
- [ ] **M1.13** Sign in with a linked Google account. Prevent removal of the last sign-in method.
- [ ] **M1.14** Google registration still selects a username and respects RegistrationMode. Reuse external login records for a future organizational IdP. Suggest optional Google linking after password registration for recovery.

- **M1 acceptance:** Register with a password and use the account immediately. Bulk registration is limited (SEC-20), Unverified restrictions hold (SEC-20b), guessing is blocked and disabling an account revokes access with a tab open. In M1c, an existing user can link Google and sign in using either method.

**M1d — Conversion to official use when approved**

- [ ] **M1.15** Bulk-match trial accounts against employees. Report outstanding verifications and set a cutoff date.
- [ ] **M1.16** Enable Approval/InviteOnly with hashed single-use invitation tokens expiring in seven days. Restrict or disable remaining Unverified accounts after the cutoff and retain chat history.

## M2 — Core messaging and synchronization

- [ ] **M2.1** Create conversations (kind, dm_key, next_seq, change_seq), conversation_members (role, last_read_seq, muted, left_at), messages and conversation_events.
- [ ] **M2.2** Open a DM with an active employee (D01). Upsert by dm_key so concurrent tabs open one conversation.
- [ ] **M2.3** In one transaction check membership, allocate seq, enforce unique(sender_id, client_msg_id) and write the event. Return the existing message on retry or 409 for different payloads.
- [ ] **M2.4** Fan out to SignalR groups named `user:{id}`. Read recipients from the database and send (conversation_id, change_seq).
- [ ] **M2.5** Cursor-page history by seq, capped at 50. Expose `GET /changes?after=N`.
- [ ] **M2.6** Use IndexedDB for queued offline sends, optimistic UI, deduplication, gap detection and sync. Reconnect with jitter, show connection status and reload when /api/version changes.
- [ ] **M2.7** Advance reads with GREATEST(last_read_seq), update unread counts across devices and show a New messages divider (UX-09).
- [ ] **M2.8** Create groups using Idempotency-Key. Add/remove members, leave, transfer ownership and appoint admins/deputies. Removal revokes history access.
- [ ] **M2.9** Render a sanitized Markdown subset with automatic links and emoji. Test SEC-10.
- [ ] **M2.10** Keep throttled presence and typing indicators in memory.
- [ ] **M2.11** Implement blocking and reporting for direct messaging.
- [ ] **M2.12** Test SEC-03, 05, 07, 15, 17 (complete sync), 18 (removal) and 19 (409 on conflicting retries). Use Playwright with A/B/C and two devices.
- **M2 acceptance:** Realtime DM and group chat survive disconnects, retries and deploys without duplicates or loss. C receives none of A/B's private data. This milestone delivers usable chat.

## M3 — Files

- [ ] **M3.1** Two-stage upload: create a pending attachment with a unique client_upload_id, then attach it to a message. Remove orphaned objects with a cleanup job.
- [ ] **M3.2** Check magic bytes, allowlisted types and D08 size limits. Generate random object keys and checksums. ⚑ D08
- [ ] **M3.3** Authorize downloads before streaming or issuing signed URLs valid for at most 60 seconds. Use attachment and nosniff headers. Test SEC-04 and SEC-11.
- [ ] **M3.4** Support image paste (Ctrl+V) and drag/drop, server-side re-encoding and thumbnails (UX-08).
- [ ] **M3.5** Add a Files tab per conversation, per-user and per-conversation quotas, and storage warnings.
- [ ] **M3.6** Quarantine, scan with ClamAV, then release clean files or reject. Fail closed before accepting real data.

## M4 — Notifications and outbox

- [ ] **M4.1** Run the transactional outbox with BackgroundService, SKIP LOCKED, retry/backoff, idempotent handlers and dead-letter handling.
- [ ] **M4.2** Add @user and authorized @channel/@all mentions with separate unread mention counts.
- [ ] **M4.3** Provide an in-app notification center backed by the notifications table. Mark-as-read is idempotent.
- [ ] **M4.4** Set per-conversation all/mentions/mute preferences (UX-07) and scheduled quiet hours.
- [ ] **M4.5** Web Push using VAPID. Omit message content, recheck read/mute state before delivery and deduplicate by tag. Document iOS PWA installation.
- [ ] **M4.6** Optional email digest for messages unread after N hours when push fails. Omit private DM content.

## M5 — Communities, channels and announcements

- [ ] **M5.0** Create a community and its General channel atomically. The creator becomes Owner. Store membership and roles on the community. Support joining public communities and invitations to private ones.
- [ ] **M5.1** Owners/Admins add channels. A channel is a conversation of kind channel with a community_id foreign key. Authorize reads, posts, searches and downloads using community membership. DMs and group chats remain independent. → M5.0
- [ ] **M5.2** Use one Create community flow. A verified CommsAdmin can designate a community as official. Transfer responsibility when its owner becomes inactive. → M5.1
- [ ] **M5.3** Channels can restrict announcements to admins (posting = admins_only). Store acknowledgments with unique(post,user), offer an authorized report/CSV and remind outstanding recipients (CM-03).
- [ ] **M5.4** Optional HR integration (CM-02) maps departments to chosen communities. Reimporting the same data yields the same result.
- [ ] **M5.5** Channel feeds support posts, comments, reactions and pinned posts (CM-04).
- [ ] **M5.6** Polls (CM-01) use PUT and uniqueness constraints for votes.
- **M5 acceptance:** Create a community with General and a second channel. Post, switch and reload, verifying channel isolation. Removed members lose API and realtime access across the community. Measure acknowledgments for an announcement sent to 1,000 recipients.

## M6 — Rich chat features selected from pilot feedback

- [ ] **M6.1** Quick reactions and hover actions (UX-01) with PUT/DELETE and change events.
- [ ] **M6.2** Threads (UX-02) with parent_message_id, reply counts, following and unread state. ⚑ D05
- [ ] **M6.3** Edit/recall (UX-05) with version, If-Match, the D12 time limit and a recalled-message marker. ⚑ D12
- [ ] **M6.4** Pin and save messages (UX-03/04) with PUT/DELETE.
- [ ] **M6.5** Mark unread using manual_unread_from_seq.
- [ ] **M6.6** Search messages with membership enforced inside the query. Filter by sender, channel, date and attachment.
- [ ] **M6.7** Ctrl+K quick switcher and keyboard navigation (UX-10).
- [ ] **M6.8** Optional DM read receipts. ⚑ D09
- [ ] **M6.9** Forward messages after checking file access at the destination.
- [ ] **M6.10** Create meeting links with Meet/Jitsi.

## M7 — Admin & Governance

- [ ] **M7.1** Manage accounts, imports, disablement, department changes and role grants. Provide a filterable audit log.
- [ ] **M7.2** Manage official channels and audited emergency ownership transfers by SystemAdmin.
- [ ] **M7.3** Add passkeys alongside M1.8 TOTP. Offer self-service password reset once SMTP is available.
- [ ] **M7.4** Implement D11 retention jobs, user anonymization and data subject exports. ⚑ D11
- [ ] **M7.5** Break-glass process with two approvals and audited exports. ⚑ D07
- [ ] **M7.6** Report DAU/WAU, messages per day, storage and push adoption without reading message content.

## M8 — Hardening & IT gate

- [ ] **M8.1** Automate SEC-01 through SEC-24, run OWASP ZAP on staging and review the ASVS L2 checklist.
- [ ] **M8.2** Use k6/NBomber for 300 sockets, reconnect bursts and a channel with 1,000 members. Meet p95 targets.
- [ ] **M8.3** Back up the database and files consistently, encrypt offsite backups and rehearse restoration (SEC-14) with someone other than the developer.
- [ ] **M8.4** Always-on pilot infrastructure per D10: Docker Compose with app, Postgres, Caddy and ClamAV, firewall, non-root processes and VPN/Zero Trust if IT requires it.
- [ ] **M8.5** Monitor and alert on uptime, errors, p95, sockets, disk/quota, backup status and scan failures.
- [ ] **M8.6** Document deployment, rollback, restore, key rotation, disabling accounts and incidents. Include the privacy notice and data inventory.
- [ ] **M8.7** Submit the IT review package (proposal §12) for a go/no-go decision.

## M9 — Pilot → Rollout

- [ ] **M9.1** Pilot with 20–30 people across 2–3 departments for four weeks. Measure adoption, iOS/Android push delivery and errors.
- [ ] **M9.2** Use Nexus for official announcements, let pilot users name communities and channels, provide PWA installation instructions and a support channel.
- [ ] **M9.3** Decide whether to add a Capacitor wrapper using pilot push-delivery measurements.
- [ ] **M9.4** Roll out to 100, 300 and then 1,000 accounts. Monitor quota and support load.
- [ ] **M9.5** Optional cloud-to-on-premises migration per proposal §11.

## Post-rollout backlog (uncommitted)

Remind me (UX-11), scheduled send (UX-12), events and RSVP, knowledge base, Former status if D06 defers it, and HRM/CRM/AI integrations from proposal §14.

---

## Dependencies and release points

```
P0 → M0 → M1 → M2 ──► [Release 1: usable chat]
                 ├→ M3 (files)
                 └→ M4 (notifications) → M5 ──► [Release 2: communities, channels and announcements]
M1a (open password registration) → M1b (directory) → M1c (Google linking, can overlap M2). Start M1d only after the conversion decision.
Select M6 features using feedback. M1.8 (admin MFA), M3.6 and M8 are required before the pilot or use of real data.
```

Rough estimate for one full-time developer with AI: P0 one week, M0 one to two, M1 two, M2 three to four, M3 one to two, M4 two, M5 two to three under the earlier scope (re-estimate after P0.2), M7 and M8 three to four. Allow about **four to five months** to pilot, with M6 driven by demand.
