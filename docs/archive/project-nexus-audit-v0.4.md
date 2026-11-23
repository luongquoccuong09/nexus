# Project Nexus — Review of proposal v0.3.1 and mockups v0.3.2

> Reviewed November 23, 2026: `project-nexus-proposal-v0.3.md`, `project-nexus-feature-ideas.md`,
> `project-nexus-m1-m2-mockup.html`, `project-nexus-enduser-mockup.html`.
> Goal: an internal communication app drawing on Teams, Slack and Zalo for approximately 1,000 accounts and 200 active users.
> Companion implementation plan: `project-nexus-plan-v0.4.md`, replacing proposal §11. This audit records the findings and recommendations at review time. Consult the [decision log](docs/decision-log.md) for accepted changes.

Severity: 🔴 resolve before implementing the affected feature, 🟠 before pilot, 🟡 before rollout.

---

## 0. Findings

**Keep these design choices**

- A C# modular monolith, PostgreSQL and SignalR fit one C# developer and the target population. The audit found no need for Redis, a broker or microservices.
- Retain deny-by-default access, BFF cookies, issuer + sub identity, resource authorization on each hub method, download-only files with nosniff, SEC-01–16 and the five development guardrails.
- Preserve unique(sender_id, client_msg_id), per-conversation sequences and the database as the source of truth for realtime signals.
- Administrative privileges grant no implicit DM access. The mockups distinguish management from content access.

**Priority issues**

1. 🔴 The mockup added Workspace, Workspace Owner, rescue transfers, Owner/Deputy and three access invariants absent from the proposal's authoritative decision log. Agents could implement incompatible models.
2. 🔴 Access rules are scattered. Define a capability matrix covering resource scope, public/private discovery, broad mentions and announcement posting.
3. 🔴 Requiring invitations before colleague DMs adds friction for 1,000 accounts. M01 was organized around this extra step.
4. 🔴 A new-message cursor misses edits, deletes, reactions, thread changes, membership and read state after reconnect.
5. 🟠 A zero-dollar pilot is unrealistic. Sleeping hosts interrupt realtime delivery, and estimated file growth of 4.4 GB/month exceeds the cited 1 GB free storage. Budget for recoverable backups.
6. 🟠 Personal Gmail adds recovery, MFA, email-change and offboarding issues. Ask whether managed M365 or Google Workspace accounts are available.
7. 🟠 M02 combines group chat, unread state, mentions, notifications and search with 8–10 extra UX features. Four to six weeks for one developer is optimistic.
8. 🟠 Adoption needs a concrete reason to open Nexus when staff already use Zalo daily. Measure iOS PWA notification delivery during pilot.

---

## 1. Document consistency

| ID | Issue | Severity | Recommendation |
|---|---|---|---|
| C1 | Mockup v0.3.2 added an independent workspace, ownership, removal, admin rescue transfers and three invariants absent in proposal v0.3.1. | 🔴 | Decide the single-organization/workspace question in the decision log and align both documents and prototypes. |
| C2 | The documents alternate among Work/Social, Work/Personal and independent Community with null workspace_id. | 🟡 | Use a glossary. The audit proposed Official for organization-owned spaces and Social for member-created spaces. |
| C3 | The proposal allows Admin to create Work communities. The mockup gives that right to Workspace Owner. | 🔴 | Name each role and scope in the matrix. |
| C4 | HR/Comms/Leadership bypass DM invitations, but Leadership and its assignment process are undefined. | 🟠 | Define dm.initiate_without_connection or remove this exception if D01 opens DMs. |
| C5 | Feature ideas assign UX-01–08 to M02 in addition to the proposal's group/unread/mention/notification/search work. | 🟠 | Split M02 into smaller milestones. |
| C6 | The proposal mentions fewer than 200 active users and omits the 1,000-account target. | 🟠 | Size directory imports, @all, storage and push fan-out accordingly. |
| C7 | Former retains access to Social despite the HR allowlist gate. | 🟠 | Seek HR/legal approval. Prefer Disabled at departure and consider Former separately. |
| C8 | The mockup has anonymized users but the proposal lacks deletion/anonymization procedures. | 🟡 | Include retention and data subject requests in the plan. |

---

## 2. System architecture

### 2.1 Existing design

- One ASP.NET Core process for API, SignalR and workers, backed by PostgreSQL and object storage. The audit estimated a 2–4 vCPU VM for 1,000 accounts and roughly 300 sockets. Validate sizing under load.
- A same-origin SPA and BFF cookie reduce token exposure in JavaScript and simplify CORS.
- Retain IFileStorage and IFileScanner as integration boundaries.

### 2.2 Gaps and recommendations

| ID | Issue | Severity | Recommendation |
|---|---|---|---|
| A1 | messages.sequence captures inserts but misses edits, deletes, reactions, pins, thread counts, membership and read pointers. | 🔴 | Maintain a conversation change_seq and durable conversation_events. Fetch changes after a cursor and apply them in order on reconnect. |
| A2 | SignalR groups per conversation require membership updates and can race with revocation (SEC-16). | 🟠 | Use user:{id} groups and resolve authorized recipients from the database. |
| A3 | Notifications, push, mentions, email digests, reminders and scheduled sends require durable jobs. | 🟠 | Write the outbox transactionally, process with BackgroundService and SKIP LOCKED, retry with backoff, use idempotent handlers and dead-letter failed events. |
| A4 | Sleeping free hosts interrupt chat and push delivery. Render's cited timeout was about 15 minutes. | 🟠 | Use free hosting for M00–M01 demos. Budget an always-on pilot VPS (estimated $10–30/month) or paid PaaS. |
| A5 | Free-tier assumptions at review time: ~500 MB database, ~1 GB storage, no downloadable backup and inactive project pauses. Files could grow 4.4 GB/month. | 🟠 | Consider VPS PostgreSQL or Supabase Pro, scheduled pg_dump to separate storage, file quotas and retention. |
| A6 | Deploys drop active sockets. | 🟡 | Use jitter, resume with change_seq, show connection state and expose /api/version for stale PWA clients after redeploy. |
| A7 | Presence and typing exist in the mockup only. | 🟡 | Keep them in memory on one instance. Throttle and notify only relevant viewers. |
| A8 | Vietnamese full-text search needs appropriate indexing. | 🟡 | Normalize with unaccent and use pg_trgm/GIN. Join membership inside the query. Reassess against the estimate of ~100k messages/month over two to three years. |
| A9 | DWG/RVT/IFC files can be hundreds of MB, beyond the sample 10 MB PDF limit. | 🟠 | Resolve D08. Share links to existing NAS/Drive/SharePoint storage for large engineering files. |
| A10 | Backend downloads use application bandwidth and memory. | 🟡 | Stream initially. Consider authorized signed URLs valid for ≤60 seconds with Content-Disposition: attachment. Record the trade-off. |
| A11 | Observability has no concrete implementation. | 🟡 | Use Serilog JSON, OpenTelemetry traces/metrics, /healthz and /readyz. Review privacy before sending stack traces to external services. |
| A12 | Migrations need a deployment protocol. | 🟡 | Expand, migrate, then contract. Run migrations during deployment with separate credentials. |
| A13 | The module list omits organization data and governance. | 🟡 | Add Org and Admin/Governance for departments, audit, retention, HR imports and reporting. |

---

## 3. Proposed authorization model

Combine scoped roles and resource checks. The following was the audit's draft for discussion.

### 3.1 Scopes

```
System
 └─ Organization (one organization or multiple workspaces, pending D02 at review time)
     ├─ Department / Branch          (HR data for automatic assignment)
     ├─ Official channel/community   (organization-owned)
     ├─ Social community             (member-created)
     ├─ Group chat                   (private, independent)
     └─ DM                           (two people)
```

The initial proposal used Conversation with kind dm/group/channel. Channels would have visibility public/private, ownership official/social and posting everyone/admins_only. Feed could be a presentation mode or a separate kind if its interactions differed materially. The subsequent D04 decision sets Community → Channel.

### 3.2 Roles

| Role | Scope | Assigned by | Responsibilities |
|---|---|---|---|
| SystemAdmin | System | Bootstrap/IT | Suspend accounts, rescue ownership and inspect audits. Require MFA. No automatic content access. |
| HRAdmin | Organization | SystemAdmin | Employee allowlist/import, department changes and offboarding. No implicit content access. |
| CommsAdmin | Organization | SystemAdmin | Official channels, organization-wide announcements, @all and acknowledgment reports. |
| Member | Organization | Default | Employee participation. |
| Owner / Admin (Deputy) / Member | Conversation | Owner | Membership, pins, metadata and moderation according to role. Owner can transfer ownership. Keep an organizational owner for official channels. |
| Former (optional) | Organization | HR | See P0-D06. |

### 3.3 Draft capability matrix

| Action | Member | Conversation admin | Conversation owner | CommsAdmin | HRAdmin | SystemAdmin |
|---|---|---|---|---|---|---|
| Direct-message an employee | ✅ (P0-D01) | | | ✅ | ✅ | ✅ |
| Create group chat | ✅ | | | ✅ | ✅ | ✅ |
| Create a public/private social channel | ✅ | | | ✅ | | |
| Create an official channel | ❌ | | | ✅ | | |
| Discover and join public channels | ✅ | | | ✅ | | |
| Add/remove members | ❌ | ✅ | ✅ | ✅ (official) | | |
| Post to admins_only channels | ❌ | ✅ | ✅ | ✅ | | |
| @channel / @all | ❌ (configurable) | ✅ | ✅ | ✅ | | |
| Edit/recall own message | ✅ (time window pending) | | | | | |
| Moderate another user's message | ❌ | ✅ | ✅ | ✅ (official) | | |
| Pin message | Configurable | ✅ | ✅ | | | |
| Transfer ownership | | | ✅ | ✅ (official) | | ✅ (audited rescue) |
| Read a conversation without membership | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ (break-glass only, P0-D07) |
| Import/disable user | | | | | ✅ | ✅ |
| View audit log | | | | | HR scope | ✅ |

**Implementation**

- A single IAuthorizationService.Can(actor, action, resource) handles controller, hub and file access. ConversationGuard is part of that service.
- Check account status, system role, membership/resource role and resource policy (posting mode, locks) in that order.
- Turn matrix cells into xUnit Theory cases so permission changes require corresponding tests.
- Keep management of metadata/members separate from content access. Managing a channel does not confer the right to read it.

### 3.4 Access policy gaps

- 🔴 Define public/private channel discovery and joining.
- 🟠 Define break-glass/eDiscovery for investigations: written request, two approvals, scoped export and audit. Avoid ad hoc database reads.
- 🟠 On removal, deny further history reads and retain messages already sent by that member.
- 🟠 On departure, propose CommsAdmin as successor for official channels and the longest-serving admin for social channels. Mark orphaned channels for SystemAdmin reassignment.
- 🟡 Add block/report controls alongside open DMs.

---

## 4. Idempotency, ordering and synchronization

Proposed rules:

1. Give client creation requests a client-generated UUIDv7. Enforce a unique natural key such as client_msg_id, or use idempotency_keys with user_id, key, request_hash, status, response_json and expiry. Return the original authorized response on retry and 409 for a conflicting payload.
2. Make transitions conditional, e.g. UPDATE … SET status='Accepted' WHERE id=@id AND status='Pending'. If no row changed, inspect current state. Return success if already at the target or 409 on a conflicting transition.
3. Use PUT/DELETE for set membership, reactions, saves, pins and mute settings to make retries safe.
4. Keep cursors monotonic: last_read_seq = GREATEST(last_read_seq, @seq).
5. Protect edits with version (or xmin) and If-Match.
6. Deliver push, email and realtime side effects via an outbox with at-least-once delivery and event_id deduplication.
7. Realtime signals carry (conversation_id, change_seq). Ignore already-applied changes and sync on gaps.

| Action | Risk | Handling |
|---|---|---|
| Send message | Duplicate sends or incorrect ordering | Unique(sender_id, client_msg_id). Increment next_seq with UPDATE … RETURNING in the same transaction. Authorize before returning an existing message. Return 409 if the payload differs. |
| Attach file | Orphan or duplicate upload when send fails | Return a pending attachment_id with unique client_upload_id. Reference it in the message and clean up stale pending objects. |
| Invite to connect, if retained | Concurrent invitations or spam | Partial unique index on normalized pair WHERE status='Pending'. Accept crossed invitations. |
| Accept and create DM | Two DMs in a race | Unique connection pair and dm_key. INSERT … ON CONFLICT DO NOTHING followed by SELECT in one transaction. |
| Open DM | Concurrent tabs create duplicates | Upsert unique dm_key = min(a,b):max(a,b). |
| Create group/channel | Repeated clicks | Use Idempotency-Key. |
| Add/remove member | Duplicate events or concurrent send | Upsert or soft-remove via left_at, write conversation_events and check membership in the sequence-allocation transaction. |
| Edit | Concurrent devices | version and If-Match, event per edit, optional edit history. |
| Recall | Retries | Soft-delete using deleted_at, clear body, return 204 on repeat. |
| Reaction | Repeated toggle removes reaction | PUT/DELETE /messages/{id}/reactions/{emoji} and unique(message_id,user_id,emoji). |
| Mark read | Stale device overwrites newer cursor | GREATEST plus a user:{id} event to sync devices. |
| Mark unread (UX-09) | Conflict with monotonic reads | Store manual_unread_from_seq separately. |
| Acknowledge announcement | Duplicate counts | Unique(post_id,user_id), ON CONFLICT DO NOTHING. |
| Poll vote | Duplicate vote | Use PUT and unique(poll_id,user_id), or include option for multiple choices. |
| Web Push | Duplicates or already-read alerts | Outbox event_id, recheck read/mute before sending, client deduplication using notification tag. |
| Disable account | Race with broadcast (SEC-16) | Change status, advance security_stamp, remove sessions, check status on delivery and abort the user's connections. |
| Transfer ownership | Two admins act concurrently | Conditional update based on current owner and one audit event. |
| HR import | Duplicate records | Upsert by employee_code, with dry run and diff. |
| Scheduled send/reminder | Repeated job | Unique job_key and conditional sent_at IS NULL check. |
| Future webhooks/integrations | Duplicate requests | Reuse idempotency_keys. |

Add SEC-17 for offline edit/delete/reaction sync, SEC-18 for no history or events after removal, and SEC-19 for 409 on conflicting idempotency key reuse.

---

## 5. Feature review against Teams, Slack and Zalo goals

| Area | Existing proposal | Gap or decision | Severity |
|---|---|---|---|
| Identity | Google OIDC and allowlist | Managed Entra/Google IdP, bulk HR import for 1,000, email changes and pilot admin MFA | 🟠 |
| Directory | Name search | Department/branch, role, photo, status, simple org chart and hiding former users | 🟡 |
| DMs | Invitation required | Direct messaging, blocking/reporting and optional Seen receipts | 🔴 |
| Groups | M02 | Member cap, leave and ownership transfer | 🟡 |
| Public/private channels | Missing | Discovery, joining and optional department mapping (CM-02) | 🔴 |
| Announcements and acknowledgments | M03 | Admin-only posting, acknowledgment reporting and reminders | 🟡 |
| Threads | Feature backlog | Model, unread/notification behavior and following | 🟡 |
| Mentions | M02 | @user, permission-controlled @channel/@all and separate unread counts | 🟡 |
| Notifications | M02 Web Push | In-app center, per-channel preferences, quiet hours and fallback digest for unreliable iOS push | 🟠 |
| Files | 10 MB PDFs | Re-encoded images, thumbnails, Office documents, Files tab, quotas and links to larger files | 🟠 |
| Formatting | Unspecified | Safe Markdown subset, emphasis, code, lists, automatic links and emoji | 🟡 |
| Edit/recall | UX-05 | Time limit and a recalled-message marker | 🟡 |
| Quotes and forwarding | Missing | Authorize forwarded files and select one main reply mechanism | 🟡 |
| Search | M02 | Sender, channel, date and attachment filters | 🟡 |
| Calls | Outside MVP | Create a Meet/Jitsi link | 🟡 |
| Mobile | PWA | iOS Home Screen installation for Web Push (16.4+ in review). Measure delivery and define a threshold for Capacitor | 🟠 |
| Administration | Scattered | Users, imports, offboarding, official channels, audits, quotas, retention and usage | 🟠 |
| Retention/DSR | Mentioned | Concrete retention policy, deletion jobs, exports and anonymization | 🟠 |
| Vietnam privacy requirements | General compliance statement | Have counsel verify Decree 13/2023/ND-CP, the Personal Data Protection Law (January 1, 2026 date cited by the review) and cross-border hosting implications | 🟠 |

---

## 6. Operations, budget and staffing

| ID | Issue | Severity | Recommendation |
|---|---|---|---|
| O1 | One developer | 🟠 | Keep architecture/decision records and require IT to rehearse deploy/restore using the runbook before pilot. |
| O2 | No post-prototype budget | 🟠 | Present estimated 4 vCPU/8 GB VPS ($20–40/month), storage/offsite backup ($5–10/month) and domain at the IT gate. |
| O3 | M02 UX included in a 4–6 month estimate | 🟠 | Split milestones into usable releases. |
| O4 | Adoption | 🟠 | Use Nexus for pilot announcements, prepopulate relevant channels, support PWA installation and track DAU/WAU with go/no-go criteria. |
| O5 | Help desk | 🟡 | Provide a Nexus support channel, FAQ and in-app issue reporting. |

---

## 7. Decision inputs for v0.4

| ID | Question | Audit recommendation at the time |
|---|---|---|
| P0-D01 | Require invitations for DMs? | Accepted Nov 23: direct DMs within the organization with block/report/mute. Remove connection_requests from M1/M2. |
| P0-D02 | One organization or multiple workspaces? | Accepted Nov 23: one Organization, with Department/Branch. Retain org_id for future extension and remove Workspace from navigation. |
| P0-D03 | Sign-in method? | Accepted Nov 23: username/password first, then link Google to an existing account and allow either method. Future corporate IdPs can use the same mechanism. See §8. |
| P0-D13 | Who can register? | Accepted Nov 23: anyone during trial, initially Unverified. Configure Open, OpenWithJoinCode, Approval or InviteOnly. Keep sensitive information out of the trial. |
| P0-D04 | Are community feeds and chat channels separate? | Initial suggestion: conversation kinds with a feed presentation, admins_only posting and acknowledgments. Superseded by Community → Channel in the current decision log. |
| P0-D05 | Thread or quote? | Threads in groups/channels, optional lightweight references without nested quoting. |
| P0-D06 | Default offboarding? | Disabled by default. Optional Former after pilot with HR/legal approval. |
| P0-D07 | Emergency access to content? | Two-person approval, audit and scoped export with no unrestricted reading UI. |
| P0-D08 | File types and limits? | For pilot: PDF, re-encoded images and Office files up to 25 MB. Link to existing storage for CAD/BIM. |
| P0-D09 | Read receipts? | Optional Seen in DMs. Show counts in groups/channels except named acknowledgments for announcements. |
| P0-D10 | Pilot hosting and budget? | Always-on VPS/VM, self-managed PostgreSQL and offsite backup. Free tier for demos. |
| P0-D11 | Retention? | Illustrative only: messages three years, files one year after last reference, audit one year, backups 30 days. Organization decides. |
| P0-D12 | Edit/recall window? | Proposed 24 hours for members, audited moderation at any time for channel admins. |

---

## 8. Password-first identity and later Google linking (D03)

Holding password credentials makes Nexus responsible for hashing, guessing protection, recovery and MFA. Use established platform components.

### 8.1 Open trial registration (D13)

Decision of November 23, 2026: anyone can try the service immediately without an invitation or HR approval. The team wants to build familiarity before proposing full adoption.

Make registration mode configurable so conversion does not require a rewrite.

| RegistrationMode | Stage | Behavior |
|---|---|---|
| Open | Trial default | Create a username, password and display name. Join the Organization as Unverified. |
| OpenWithJoinCode | Optional abuse control | Require a shared, rotatable internal join code, retaining self-registration. |
| Approval / InviteOnly | Official use | Require approval or a hashed single-use invite expiring in seven days. |

Store trust_level (Unverified/Verified) separately from status (Active/Disabled/other approved states).

- Show the Unverified badge wherever a self-registered account's name appears, including directory, messages and group membership.
- Admin/HR links one employee record through nullable UNIQUE(employee_id). The audit also discussed later self-verification using a linked organizational email on the HR list.
- Trial users can DM, create groups and create/join social channels. Restrict official creation, announcement posting, @all and admin roles.

**Open registration risks and controls**

| Risk | Control |
|---|---|
| Automated signups | Per-IP limit (example: five per hour), honeypot, form timing and configurable CAPTCHA such as Turnstile when abuse is observed. |
| Outsiders browse directory or contact staff | Use nonconfidential trial data, a persistent notice, terms and noindex. Switch to OpenWithJoinCode when needed. |
| Impersonation | Display Unverified badges, warn about verified-name collisions, offer reporting and audited admin rename/disable actions. |
| DM spam | Limit new DMs per day for Unverified users. Add block/report (M2.11). |
| Username squatting | Reserve sensitive names. Allow audited administrative renaming. |
| Forgotten password without email | Encourage optional Google linking for recovery. Otherwise verify identity directly before an admin reset. |
| Personal data obligations | Publish a privacy notice and record trial terms_version and accepted_at. |

At conversion, HR imports the roster. Suggest matches to trial accounts by name or linked email for admin review. After the cutoff, restrict or disable unverified accounts, enable Approval/InviteOnly and retain trial conversation history.

### 8.2 Usernames and passwords

- Use 3–32 username characters from [a-z0-9._-], a normalized case-insensitive unique index and reserved names such as admin, system and support. Store a separate Unicode display name, including Vietnamese diacritics.
- Use ASP.NET Core Identity UserManager, PasswordHasher (PBKDF2), lockout and security stamps. Avoid custom password hashing.
- Proposed policy: minimum 12 characters, block common passwords against an offline list, allow paste/password managers and avoid arbitrary character-class rules. Never send passwords to an external service.
- Use per-account lockout (example: ten failures, 15 minutes), per-IP login limits and generic credential errors to avoid username disclosure.
- Changing a password updates the security stamp, signs out other sessions and disconnects SignalR.

### 8.3 Recovery

Without SMTP, HR/admin issues an audited single-use reset link after verifying the employee and delivers it directly. Users with linked Google can sign in and set a new password. Add self-service email recovery once SMTP is available.

### 8.4 Linking Google to an existing account

- Require an authenticated password session. Under Security → Link Google, complete OIDC and store the globally unique (issuer, sub) in AspNetUserLogins with LoginProvider = issuer and ProviderKey = sub.
- Never automatically link by email. An unlinked Google identity matching an employee email cannot enter that account. Prompt the user to sign in with their username first.
- Require recent password reauthentication (within five minutes) to link or unlink. Audit the event and notify the account in-app.
- Retain at least one sign-in method.
- Later, Google sign-up can complete OIDC, choose a username, create the user and store the link. Apply the same RegistrationMode as password registration.

### 8.5 MFA

Require TOTP for SystemAdmin, HRAdmin and CommsAdmin from pilot onward. Offer it to employees. Treat linked Google sign-in as one factor, with additional MFA still required for admins.

### 8.6 Additional security tests

| ID | Scenario | Expected result |
|---|---|---|
| SEC-20 | Bulk signup from one IP, missing/used/expired invite or wrong join code | Rate limiting and mode-specific rejection work |
| SEC-20b | Unverified account creates official channel, posts announcement, uses @all or receives admin role | Denied |
| SEC-21 | Fifty password guesses | Lockout and rate limiting apply without username disclosure |
| SEC-22 | Unlinked Google identity matches employee email | No account access |
| SEC-23 | Link a Google identity already bound elsewhere | Denied |
| SEC-24 | Change password or unlink | Other sessions are revoked and SignalR disconnected |
