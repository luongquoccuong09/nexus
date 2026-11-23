# Plan — Milestone M5: Communities, Channels & Announcements (`M5.0`–`M5.6`)

- Status: in progress
- Branch: `develop`
- Design baseline: [`P0-D04`](../../decisions/2026-11-23-p0-d04-community-channel-hierarchy.md) (**Microsoft Teams Communities** model: each Community has its own main feed directly with zero forced `# General` sub-channel; Owners/Admins add optional sub-channels on demand) and [`SECURITY_AND_AUTHZ.md`](../../SECURITY_AND_AUTHZ.md).

## Deliverables

1. **Schema & Migration (`M5CommunitiesAndAnnouncements`):**
   - `communities` (`name`, `description`, `visibility`, `is_official`, `mapped_department_id`), `community_members` (`role`, `joined_at`, `left_at`).
   - Extend `conversations` with nullable `community_id`, `is_community_main_feed`, and `posting_policy` (`Members` | `AdminsOnly`).
   - `channel_posts`, `post_comments`, `post_reactions`, `announcement_acknowledgments` (`UNIQUE(post_id, user_id)`), `polls`, and `poll_votes` (`UNIQUE(poll_id, user_id, option_id)`).
2. **Community & Channel Lifecycle (`M5.0`, `M5.1`, `M5.2`, `M5.4`):**
   - Single `POST /api/communities` (`Idempotency-Key`) atomically creates the Community, Owner membership, and primary main feed conversation (zero default sub-channels).
   - `POST /api/communities/{id}/channels` adds optional sub-channels inheriting Community membership & roles.
   - Public discovery/join, Private member invites, removal revoking access across the main feed, all sub-channels, posts, SignalR fanout, and files immediately.
   - `CommsAdmin` (Verified + MFA) official designation (`POST /official`) and audited emergency ownership transfer (`POST /emergency-transfer-ownership`).
   - Optional HR department mapping & idempotent sync (`PUT /department-mapping`, `POST /sync-department`, `CM-02`).
3. **Channel Feeds, Announcements & Polls (`M5.3`, `M5.5`, `M5.6`):**
   - Posts, comments, idempotent emoji reactions (`PUT`/`DELETE`), pinned posts (`CM-04`).
   - `AdminsOnly` announcement channels, idempotent acknowledgments (`PUT /acknowledge`), JSON/CSV acknowledgment report (`GET /acknowledgments?format=csv`), and outstanding recipient reminders (`POST /remind`, `CM-03`).
   - Polls (`CM-01`) with `PUT /api/polls/{pollId}/vote` and uniqueness constraints.
4. **Teams-Style Communities UI (`src/Nexus.Client/src/communities.tsx`):**
   - App Rail **Communities** tab, Community tree with main feed + optional sub-channels + `+ Add channel`, Create Community / Discover & Join modals, Post/Announcement/Poll composer, Reactions, Comments, Acknowledgment button + CSV export, and Members drawer — 100% Nexus Design System compliant.
5. **Integration Tests (`tests/Nexus.IntegrationTests/CommunitiesM5Tests.cs`):**
   - Testcontainers PostgreSQL 17 suite verifying Community main feed + sub-channel isolation, member removal revoking access across all channels & files, `Official` designation & `Unverified` restriction (`SEC-20b`), `AdminsOnly` posting policy, announcement acknowledgments & CSV report, idempotent HR department sync, reactions, pinning, and poll vote uniqueness.
