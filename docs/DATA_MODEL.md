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

| Module | Planned tables | Critical invariants | Milestone |
|---|---|---|---|
| **Foundation** | `data_protection_keys`, `app_sessions`, `idempotency_keys` (**migrated** in `InitialFoundation`) | Separate `nexus_migrator` (DDL) vs `nexus_runtime` (DML only); `UNIQUE(user_id, http_method, route_template, idempotency_key)` with 24h TTL; server-side revocable BFF sessions; PostgreSQL Data Protection keys | M0.2, M0.5, M0.8 (**Done**) |
| **Org** | `organizations`, `departments`, `branches`, `employees` (**migrated** in `M1aIdentityAndOrg`) | Single Organization boundary ([P0-D02](decisions/2026-11-23-p0-d02-single-organization.md)); unique `(organization_id, employee_code)` for idempotent HR CSV upsert | M1.1, M1.9 (**Done**) |
| **Identity / People** | `users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `role_claims`, `user_tokens`, `admin_reset_links`, `user_blocks`, `user_reports` (**migrated** in `M1aIdentityAndOrg`, `M1bDirectoryAndProfiles`, `M2CoreMessaging`) | Unique normalized username; partial unique `employee_id WHERE employee_id IS NOT NULL`; security stamp revocation; `custom_status`, `avatar_object_key`; `public.immutable_unaccent(text)` + GIN `pg_trgm` indexes on `display_name` & `"UserName"`; composite PK `(blocker_user_id, blocked_user_id)` on `user_blocks` | M1.2–M1.11, M2.11 (**Done**), M1.12 |
| **Conversations** | `conversations`, `conversation_members` (**migrated** in `M2CoreMessaging`); `communities`, `community_members` (planned M5) | Unique partial `(organization_id, dm_key) WHERE dm_key IS NOT NULL` for 1:1 DMs; `conversations.kind` in (`Dm`, `Group`, `Channel`); `visible_from_seq`, `last_read_seq`, `left_at` on `conversation_members`; community-scoped channels add `community_id` in M5 | M2.1–M2.8 (**Done**), M5.0, M5.1 |
| **Messaging** | `messages`, `conversation_events` (**migrated** in `M2CoreMessaging`); `announcement_acknowledgments`, `polls`, `poll_votes` (planned M5) | `UNIQUE(conversation_id, seq)`; `UNIQUE(sender_id, client_msg_id)` with `payload_sha256` conflict check (`SEC-19`); `UNIQUE(conversation_id, change_seq)`; atomic `UPDATE ... RETURNING next_seq, change_seq`; `GREATEST(last_read_seq)` read state | M2.1–M2.7 (**Done**), M5.3, M5.6 |
| **Files** | `attachments` | Unique `client_upload_id`; random object keys; checksum; quarantine/scan state (`pending`/`clean`/`rejected`) | M3.1, M3.6 |
| **Notifications** | `outbox_messages`, `notifications`, `push_subscriptions` | `SELECT ... FOR UPDATE SKIP LOCKED` outbox worker; idempotent mark-as-read; deduplicated push tags | M4.1, M4.3, M4.5 |
| **Admin** | `audit_logs` (**migrated** in `M1aIdentityAndOrg`) | Append-only administrative audit trail (registration, login, verification, renames, disablements, password changes/resets, MFA) | M1.6 (**Done**), M7.1, M7.5 |

## 3. Search and indexing notes

- **Vietnamese directory search (M1.10):** Requires PostgreSQL `unaccent` and `pg_trgm` extensions; filter out `Disabled` (and `Former` if approved) accounts at query time.
- **Message search (M6.6):** Must enforce current conversation/community membership **inside the SQL query**, never by post-filtering in memory.
