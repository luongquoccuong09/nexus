# Data Model

- Status: active (living contract — populated as migrations land from M0.2+)
- Last updated: 2026-11-23
- Scope: PostgreSQL schema ownership by module, key relational invariants, and migration governance.

## 1. Database governance (M0.2+)

- **Engine:** PostgreSQL with EF Core.
- **Role separation:**
  - `migration` role: owns schema DDL and runs EF Core migrations.
  - `runtime` role: least-privilege DML only; used by `Nexus.Web` at runtime.
- **Pooler rule:** When using Supabase's pooler, connect via **Session Mode on port 5432** (see [`facts/postgres/supabase-pooler-session-mode-5432.md`](facts/postgres/supabase-pooler-session-mode-5432.md)).
- **Persistence of framework state:** ASP.NET Core Data Protection keys and BFF sessions are persisted in PostgreSQL (M0.8).

## 2. Planned table ownership by module

Update this table whenever a migration adds or changes a table.

| Module | Tables | Critical invariants | Milestone |
|---|---|---|---|
| **Foundation** | `data_protection_keys`, `app_sessions`, `idempotency_keys` | Separate `nexus_migrator` (DDL) vs `nexus_runtime` (DML only); `UNIQUE(user_id, http_method, route_template, idempotency_key)` with 24h TTL; server-side revocable BFF sessions; PostgreSQL Data Protection keys | M0.2, M0.5, M0.8 (**Done**) |
| **Org** | `organizations`, `organization_memberships`, `organization_invitations`, `departments`, `branches`, `employees` | Membership is keyed by `(organization_id, user_id)` and holds role, status, verification and Directory profile. Invitations store unique token hashes and redemption, revocation and expiration timestamps. HR rows have unique `(organization_id, employee_code)`. | M1.1, M1.9, M1.16–M1.18 (**Done**) |
| **Identity / People** | `users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `role_claims`, `user_tokens`, `admin_reset_links`, `personal_connections`, `personal_contact_links`, `user_blocks`, `user_reports` | Account-level identity only (`users` stores no Organization columns); unique normalized username and security stamp revocation. Personal connections use a unique ordered account pair. Contact links store token hashes. Blocks are keyed by scope and account pair. | M1.2–M1.11, M2.11, M2.13 (**Done**) |
| **Conversations** | `conversations`, `conversation_members`, `communities`, `community_members` | The filtered unique `(organization_id, dm_key)` index treats nulls as equal so a Personal DM has one row per account pair. Conversation members own `visible_from_seq`, `last_read_seq`, and `left_at`. Community roles are inherited by the main feed and channels. | M2.1–M2.8, M2.14, M5.0–M5.4 (**Done**) |
| **Messaging** | `messages`, `conversation_events`, `channel_posts`, `post_comments`, `post_reactions`, `announcement_acknowledgments`, `polls`, `poll_votes` | `UNIQUE(conversation_id, seq)`; `UNIQUE(sender_id, client_msg_id)` with `payload_sha256` conflict check (`SEC-19`); `UNIQUE(conversation_id, change_seq)`; atomic `UPDATE ... RETURNING next_seq, change_seq`; `GREATEST(last_read_seq)` read state; `UNIQUE(post_id, user_id)` on `announcement_acknowledgments`; `UNIQUE(post_id, user_id, emoji)` on `post_reactions`; `UNIQUE(poll_id, user_id, option_id)` on `poll_votes` | M2.1–M2.7, M5.3, M5.5–M5.6 (**Done**) |
| **Files** | `attachments` | `UNIQUE(uploader_user_id, client_upload_id)`; random `object_key` & optional `thumbnail_object_key`; SHA-256 checksum; `state` (`Pending`/`Attached`/`Quarantined`/`Rejected`); `scan_status`; orphan cleanup job | M3.1–M3.6 (**Done**) |
| **Notifications** | `outbox_messages`, `notifications`, `push_subscriptions` | `SELECT ... FOR UPDATE SKIP LOCKED` outbox worker; idempotent mark-as-read; deduplicated push tags | M4.1, M4.3, M4.5 |
| **Admin** | `audit_logs` | Append-only administrative audit trail (registration, login, verification, renames, disablements, password changes/resets, MFA) | M1.6 (**Done**), M7.1, M7.5 |

## 3. Search and indexing notes

- **Vietnamese directory search (M1.10).** The Organization membership Directory uses PostgreSQL `public.immutable_unaccent` and GIN `pg_trgm` indexes on `organization_memberships(display_name)` and `organization_memberships(username)` and requires active account and membership rows.
- **Message search (M6.6):** Must enforce current conversation/community membership **inside the SQL query**, never by post-filtering in memory.

## 4. Greenfield baseline migration

Because Project Nexus has not yet been released to production, all schema definitions up through explicit Personal/Organization scopes are consolidated into a single clean baseline migration (`20261123132240_InitialSchema`). A null `organization_id` on a conversation, attachment, or user report represents Personal Chat; Organization roles, verification, and Directory profile fields live strictly on `organization_memberships`. Refer to the [M1e handoff](implementation-plan/m1-identity-directory/HANDOFF-m1e.md).
