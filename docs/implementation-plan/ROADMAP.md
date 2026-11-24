# PROJECT NEXUS — Plan v0.4 (todo list)

> Replaces §11 of [`docs/archive/project-nexus-proposal-v0.3.md`](../archive/project-nexus-proposal-v0.3.md). See [`docs/archive/project-nexus-audit-v0.4.md`](../archive/project-nexus-audit-v0.4.md) for the review that informed the plan.
> Target: about 1,000 accounts and 200 active users. One C# developer with a coding agent.
> Each checkbox is intended as a single implementation session covering the necessary migration, endpoint or hub, tests and minimal UI.
> Implementation details belong in the corresponding session. Milestones should end with a usable release.

A dependency is marked `→ X`. DoD means definition of done. Resolve decisions marked ⚑ before coding the associated item.

---

Start with the [repository guide](../../AGENTS.md), [decisions index](../decisions/INDEX.md) and [M0.1 handoff](m0-foundation/HANDOFF-m0.1.md).

## Product decisions · November 23, 2026

**Personal Chat and Organizations.** [P0-D14](../decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) establishes one account, consent-based Personal Chat without an Organization, and separate memberships in multiple Organizations. An Organization can host its own Chat, Directory and Communities.

**Community → Channel.** Under D04, each Community has a main feed and can add optional Channels. Channels inherit Community membership and roles.

## P0 — Decisions and documentation

- [ ] **P0.1** Resolve P0-D05 through D12 with IT/HR (audit §7) and record outcomes in the [decisions index](../decisions/INDEX.md). D14 replaces the single Organization boundary and automatic Organization enrollment. D01 governs Organization DMs, D03 governs sign-in, and D04 governs Community → Channel.
- [x] **P0.2** Complete the [authorization matrix](../SECURITY_AND_AUTHZ.md) by role, action and scope (audit §3.3). Cover administrative access and content access, community creation and joining, Owner/Admin channel creation, member reads and posts, and Unverified restrictions. Derive tests from the matrix.
- [x] **P0.3** Complete the [sync protocol](../SYNC_PROTOCOL.md) for `client_msg_id`, `seq`, `change_seq`, `conversation_events`, reconnects, gaps, reconciliation and idempotency keys (audit §4).
- [x] **P0.4** [Glossary](../GLOSSARY.md) covering Organization, Community, Channel, Conversation (dm/group/channel), official designation, Owner/Admin/Member and Former/Disabled.
- [ ] **P0.5** Update the proposal with 1,000 accounts, D04 and D14, consistent terminology, SEC-17 through SEC-24 and a pilot budget.
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
- [x] **M1.16** Implement Organization invitations under D14. Store hashed single-use tokens that expire after seven days. Require authenticated confirmation, handle concurrent redemption, and allow Owner or Admin to revoke pending invitations. → M1.17

**M1e — Account and Organization membership (D14)**

- [x] **M1.17** Split global identity from Organization membership and directory profiles. Add Organization creation, role administration with TOTP, and membership revocation. Account registration grants zero memberships. → P0-D14
- [x] **M1.18** Scope Directory, Community, and administrative entry points to the selected Organization. Add the context picker and invitation and membership settings. → M1.16, M1.17

## M2 — Core messaging and synchronization

**D14 follow-up.** Add a separate Personal scope for chat. Existing Organization conversations keep their data and remain in their Organization.

- [x] **M2.13** Add consent-based contacts via exact username or contact link, Personal DMs and group chats using accepted contacts. Keep Personal Chat usable with zero Organization memberships. → M1.17
- [x] **M2.14** Scope chat, downloads, SignalR and reconciliation across Personal and Organization contexts. Partition offline queues and cursors by account and scope. Verify data isolation across two browser tabs and Organization revocation. → M1.18, M2.13
- [x] **M2.15** Consolidate the schema into a clean greenfield baseline migration (`InitialSchema`) with no legacy cutover state, enable Organization creation by default, and verify against a fresh PostgreSQL 17 database. → M2.14

- [x] **M2.1** Create conversations (kind, dm_key, next_seq, change_seq), conversation_members (role, last_read_seq, muted, left_at), messages and conversation_events.
- [x] **M2.2** Open a DM with an active employee (D01). Upsert by dm_key so concurrent tabs open one conversation.
- [x] **M2.3** In one transaction check membership, allocate seq, enforce unique(sender_id, client_msg_id) and write the event. Return the existing message on retry or 409 for different payloads.
- [x] **M2.4** Fan out to SignalR groups named `scope:{scopeId}:user:{userId}`. Read authorized recipients from the database and send (conversation_id, change_seq).
- [x] **M2.5** Cursor-page history by seq, capped at 50. Expose `GET /changes?after=N`.
- [x] **M2.6** Use IndexedDB for queued offline sends, optimistic UI, deduplication, gap detection and sync. Reconnect with jitter, show connection status and reload when /api/version changes.
- [x] **M2.7** Advance reads with GREATEST(last_read_seq), update unread counts across devices and show a New messages divider (UX-09).
- [x] **M2.8** Create groups using Idempotency-Key. Add/remove members, leave, transfer ownership and appoint admins/deputies. Removal revokes history access.
- [x] **M2.9** Render a sanitized Markdown subset with automatic links and emoji. Test SEC-10.
- [x] **M2.10** Keep throttled presence and typing indicators in memory.
- [x] **M2.11** Implement blocking and reporting for direct messaging.
- [ ] **M2.12** Test SEC-03, 05, 07, 15, 17 (complete sync), 18 (removal) and 19 (409 on conflicting retries). Use Playwright with A/B/C and two devices. *(Backend HTTP + multi-client SignalR Testcontainers suite verified in `MessagingM2Tests.cs` + `SEC-10` in `markdown.test.mjs`; headless Playwright browser suite under `tests/e2e/` deferred.)*
- **M2 acceptance:** Realtime DM and group chat survive disconnects, retries and deploys without duplicates or loss. C receives none of A/B's private data. This milestone delivers usable chat.

## M3 — Files

- [x] **M3.1** Two-stage upload: create a pending attachment with a unique client_upload_id, then attach it to a message. Remove orphaned objects with a cleanup job.
- [x] **M3.2** Check magic bytes, allowlisted types and D08 size limits. Generate random object keys and checksums. ⚑ D08
- [x] **M3.3** Authorize downloads before streaming or issuing signed URLs valid for at most 60 seconds. Use attachment and nosniff headers. Test SEC-04 and SEC-11.
- [x] **M3.4** Support image paste (Ctrl+V) and drag/drop, server-side re-encoding and thumbnails (UX-08).
- [x] **M3.5** Add a Files tab per conversation, per-user and per-conversation quotas, and storage warnings.
- [x] **M3.6** Quarantine, scan with ClamAV, then release clean files or reject. Fail closed before accepting real data.

## M4 — Notifications and outbox

- [x] **M4.1** Run the transactional outbox with BackgroundService, SKIP LOCKED, retry/backoff, idempotent handlers and dead-letter handling.
- [x] **M4.2** Add @user and authorized @channel/@all mentions with separate unread mention counts.
- [x] **M4.3** Provide an in-app notification center backed by the notifications table. Mark-as-read is idempotent.
- [x] **M4.4** Set per-conversation all/mentions/mute preferences (UX-07) and scheduled quiet hours.
- [x] **M4.5** Web Push using VAPID. Omit message content, recheck read/mute state before delivery and deduplicate by tag. Document iOS PWA installation.
- [x] **M4.6** Optional email digest for messages unread after N hours when push fails. Omit private DM content.

## M5 — Communities, channels and announcements

- [x] **M5.0** Create a community and its primary main feed atomically (Teams Communities hierarchy per P0-D04 — no default `# General` sub-channel). The creator becomes Owner. Store membership and roles on the community. Support joining public communities and invitations to private ones.
- [x] **M5.1** Owners/Admins add optional sub-channels on demand. A channel is a conversation of kind channel with a community_id foreign key. Authorize reads, posts, searches and downloads using community membership. DMs and group chats remain independent. → M5.0
- [x] **M5.2** Use one Create community flow. A verified CommsAdmin can designate a community as official. Transfer responsibility when its owner becomes inactive (including audited emergency transfer by SystemAdmin/CommsAdmin with MFA). → M5.1
- [x] **M5.3** Channels can restrict announcements to admins (posting = admins_only). Store acknowledgments with unique(post,user), offer an authorized report/CSV and remind outstanding recipients (CM-03).
- [x] **M5.4** Optional HR integration (CM-02) maps departments to chosen communities. Reimporting/syncing the same data yields the same result.
- [x] **M5.5** Channel feeds support posts, comments, reactions and pinned posts (CM-04).
- [x] **M5.6** Polls (CM-01) use PUT and uniqueness constraints for votes.
- **M5 acceptance:** Create a community with its main feed and optional sub-channels. Post, switch and reload, verifying channel isolation. Removed members lose API, file, and realtime access across the community. *(Verified end-to-end in `CommunitiesM5Tests.cs` on PostgreSQL 17; 1,000-recipient load benchmark runs in `M8.2`.)*

## M6 — Rich chat, calendar and meetings

Select rich chat items `M6.1`–`M6.9` using pilot feedback. The [Calendar and Meetings plan](m6-calendar-meetings/PLAN-m6-calendar-meetings.md) covers `M6.10`–`M6.15` and `M6.35`–`M6.39`. The [Collaboration UX plan](m6-collaboration-ux/PLAN-m6-collaboration-ux.md) covers `M6.16`–`M6.34` and gives the delivery order and acceptance criteria.

### Rich chat

- [ ] **M6.1** Quick reactions and hover actions (UX-01) with PUT/DELETE and change events.
- [ ] **M6.2** Threads (UX-02) with parent_message_id, reply counts, following and unread state. ⚑ D05
- [ ] **M6.3** Edit/recall (UX-05) with version, If-Match, the D12 time limit and a recalled-message marker. ⚑ D12
- [ ] **M6.4** Pin and save messages (UX-03/04) with PUT/DELETE.
- [ ] **M6.5** Mark unread using manual_unread_from_seq.
- [ ] **M6.6** Search messages with membership enforced inside the query. Filter by sender, channel, date and attachment.
- [ ] **M6.7** Ctrl+K quick switcher and keyboard navigation (UX-10).
- [ ] **M6.8** Optional DM read receipts. ⚑ D09
- [ ] **M6.9** Forward messages after checking file access at the destination.

### Calendar and meeting essentials

- [ ] **M6.10** Evaluate and select approved open-source components. Verify authenticated rooms, participant removal and rejoin, audio/video, screen sharing, WebView2, and corporate network connectivity using a self-hosted media provider. Record the selection and expected operating cost.
- [ ] **M6.11** Add a Calendar with week, month, and agenda views, event create/edit/cancel, attendee invitations, RSVP, time zones, and reminders. Verify visibility and access after membership revocation. → M4.1, M4.3, M5, M6.10
- [ ] **M6.12** Add scheduled meetings and Meet now. Reuse the provider for group audio/video and screen sharing. Verify joining from an event or conversation and leaving or ending the meeting. → M6.10, M6.11
- [ ] **M6.13** Add direct voice and video calls from eligible Personal and Organization chats. Handle ringing, accept, decline, cancel, missed, and end states across signed-in devices. Reuse meeting media and screen sharing. → M2.14, M6.12
- [ ] **M6.14** Add recurring events, single-occurrence exceptions and cancellations, and authorized `.ics` export. Verify local meeting times across daylight-saving transitions. → M6.10, M6.11
- [ ] **M6.15** Verify the complete calendar and calling flows in supported browsers and the Windows Desktop client. Exercise permission denial, reconnect, concurrent devices, and authorization changes. Measure media capacity in the target deployment. → M6.12–M6.14, M8.2

### Existing workflows and UI gaps

- [x] **M6.16** Finish cursor pagination in the Chat sidebar and Files drawer. Verify older entries remain reachable and background refresh preserves the user's selection and loaded results. → M2.5, M3.5
- [x] **M6.17** Keep unsent text and recoverable attachment state per conversation. Show failed sends with retry and dismiss actions and preserve the original destination across a chat switch. → M2.6, M3.1
- [ ] **M6.18** Advance read state when messages are displayed in the active, visible Chat view. Preserve scroll position while reading history and provide a New messages action. → M2.7
- [ ] **M6.19** Handle Enter during IME composition without sending. Verify Shift+Enter, the on-screen Send button, and composition on supported clients.
- [x] **M6.20** Add Blocked users and Unblock to Profile & security using the existing account-level safety endpoints. → M2.11
- [x] **M6.21** Add authorized ownership transfer to group chat and Community member management using the existing transfer endpoints. → M2.8, M5.2
- [x] **M6.22** Add the account reset form for operator-issued tokens, including expired, invalid, and used-token states. → M1.5
- [x] **M6.23** Finish pagination in People, Community lists and feeds, and member pickers with capped lists. Preserve filters, pinned post order, and current membership checks. → M6.16, M5
- [x] **M6.24** Retest the outstanding UX audit findings and populated role-specific screens. Verify keyboard, screen reader, narrow window, loading, empty, and failure states. → M6.16–M6.23

### Everyday collaboration

- [x] **M6.25** Add authorized links to messages, posts, and channels. Load the target in context and highlight it. Reuse the link route for search hits, activity items, and saved results. → M2.5, M5.5
- [x] **M6.26** Add Favorites, user-defined sections, hide/show, unread filtering, and message previews for the Chat and Communities sidebars. → M6.16, M6.18, M6.23
- [x] **M6.27** Add formatting controls, link insertion, and a searchable Unicode emoji picker with native iPhone rendering. Evaluate a licensed cross-platform style if needed. → M2.9, M6.17, M6.19
- [x] **M6.28** Add accessible profile cards and timed availability states such as Busy, Away, and Do not disturb. Keep availability and notification preferences coordinated. → M1.11, M2.10, M4.4
- [x] **M6.29** Add a private self chat for notes and files with one conversation per account. → M2.13, M3.3
- [x] **M6.30** Add Community and Channel metadata editing and Channel archive/restore. Archived channels remain readable and enforce read-only behavior on the server. → M5.1, M5.2
- [x] **M6.31** Attach files to feed posts using the existing upload, scan, and download controls. Add a Channel Files view and links to the source posts. → M3.3, M3.6, M5.5, M6.25
- [x] **M6.32** Add a Shared view with Files, Links, and Media filters for chats and channels. Support opening the source message or post. → M6.16, M6.25, M6.31
- [ ] **M6.33** Add scoped People tags for targeted mentions and authorized tag management. → M4.2, M5.2
- [ ] **M6.34** Add short voice notes with explicit microphone permission, preview before sending, and accessible playback. Review approved audio formats and limits under D08. → M3.2, M3.6, M6.27

### Calendar and calling enhancements

- [ ] **M6.35** Add Calls history with incoming, outgoing, and missed filters and Call back. → M6.13, M6.16
- [ ] **M6.36** Add pre-join device selection, camera preview, microphone meter, and speaker test using approved provider or browser capabilities. → M6.10, M6.12
- [ ] **M6.37** Keep a meeting or call available through a compact view while the user navigates Nexus. Preserve call controls and verify the supported WebView2 behavior. → M6.12, M6.13
- [ ] **M6.38** Add a Scheduling Assistant with free/busy and tentative time only for authorized invitees. → M6.11, M6.14
- [ ] **M6.39** Add meeting lobby, organizer and presenter roles, and controls for who can present or share. Enforce permissions through the provider and record role changes. → M6.10, M6.12

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
- [ ] **M8.7** Submit the IT review package (proposal §12) for a go/no-go decision. If meetings or calls are included in the pilot, include M6.15 results and the media deployment, access controls, and data handling.

## M9 — Pilot → Rollout

- [ ] **M9.1** Pilot with 20–30 people across 2–3 departments for four weeks. Measure adoption, iOS/Android push delivery and errors.
- [ ] **M9.2** Use Nexus for official announcements, let pilot users name communities and channels, provide PWA installation instructions and a support channel.
- [ ] **M9.3** Decide whether to add a Capacitor wrapper using pilot push-delivery measurements.
- [ ] **M9.4** Roll out to 100, 300 and then 1,000 accounts. Monitor quota and support load.
- [ ] **M9.5** Optional cloud-to-on-premises migration per proposal §11.

## Post-rollout backlog (uncommitted)

- Remind me (UX-11), scheduled send (UX-12), knowledge base, Former status if D06 defers it, and HRM/CRM/AI integrations from proposal §14.
- Evaluate private or cross-Organization shared Channels and external meeting guests as separate product and authorization decisions. D04 Community membership inheritance and D14 Organization boundaries govern current delivery.
- Consider meeting chat and recap, live captions, recording and transcription, breakout rooms, and whiteboard or task integrations after validating pilot demand and reviewing data handling. See the [meeting follow-ups](m6-calendar-meetings/PLAN-m6-calendar-meetings.md#deferred-capabilities).

---

## Dependencies and release points

```
P0 → M0 → M1 → M2 ──► [Release 1: usable chat]
                 ├→ M3 (files)
                 └→ M4 (notifications) → M5 ──► [Release 2: communities, channels and announcements]
M1a (open password registration) → M1b (directory) → M1c (Google linking, can overlap M2). Start M1d only after the conversion decision.
M6.16 starts the collaboration UX work. Its core fixes and UI gaps can be delivered before Calendar and Meetings.
Select M6.1–M6.9 and optional collaboration enhancements using feedback. Calendar and meetings can follow Release 2. M1.8 (admin MFA), M3.6 and M8 are required before the pilot or use of real data.
```

Earlier estimates for one full-time developer with AI were one week for P0, one to two for M0, two for M1, three to four for M2, one to two for M3, two for M4, two to three for M5 under its earlier scope, and three to four for M7 and M8. The four-to-five-month pilot estimate covers the earlier core scope. Estimate the added collaboration sessions separately and estimate calendar and meetings after M6.10.
