# Session Handoff — Milestone M5 Complete (`M5.0`–`M5.6`)

- **Date:** 2026-11-23
- **Branch:** `develop`
- **Status:** **M5.0–M5.6 Done** and verified against real PostgreSQL 17 (Testcontainers) + UI Design System audit + Security Guardrails audit + Release builds (`0 Warning(s), 0 Error(s)`).

## What was implemented

1. **Schema & EF Core Migration (`M5CommunitiesAndAnnouncements`):**
   - Extended `conversations` with `community_id`, `is_community_main_feed`, and `posting_policy` (`Members` | `AdminsOnly`) plus `ix_conversations_community_feed`.
   - Added `communities`, `community_members`, `channel_posts`, `post_comments`, `post_reactions`, `announcement_acknowledgments`, `polls`, and `poll_votes` with relational uniqueness constraints (`UNIQUE(post_id, user_id)` on acknowledgments, `UNIQUE(post_id, user_id, emoji)` on reactions, `UNIQUE(poll_id, user_id, option_id)` on poll votes).
2. **Teams Communities Lifecycle & Governance (`M5.0`–`M5.2`, `M5.4`, `P0-D04`):**
   - `CommunityEndpoints.cs`: `POST /api/communities` (`Idempotency-Key` enforced) atomically creates the Community, Owner membership, and primary main feed directly with **zero default `# General` sub-channel**.
   - Owners/Admins add optional sub-channels (`POST /api/communities/{id}/channels`) with `Members` or `AdminsOnly` posting policy; all sub-channels inherit Community membership and roles.
   - `CommunityAccessHelper.cs` unifies authorization across Chat, Community feeds, SignalR fanout (`MessagingFanoutService.cs`), and file uploads/downloads (`FilesEndpoints.cs`) so removing a member immediately revokes access everywhere.
   - Verified `CommsAdmin` with MFA can designate Official status (`COMMUNITY_OFFICIAL_CHANGED`), hiding/blocking `Unverified` users (`SEC-20b`). Verified `SystemAdmin` (or `CommsAdmin` on Official) with MFA can perform audited emergency ownership transfers (`COMMUNITY_EMERGENCY_OWNERSHIP_TRANSFER`).
   - Idempotent HR department sync (`PUT /department-mapping` + `POST /sync-department`, `CM-02`).
3. **Channel Feeds, Announcements, Acknowledgments & Polls (`M5.3`, `M5.5`, `M5.6`):**
   - `CommunityFeedEndpoints.cs`: pinned-first posts (`<= 50`), `AdminsOnly` enforcement, announcement read acknowledgments (`PUT /acknowledge`), JSON & formula-injection-safe CSV export (`GET /acknowledgments?format=csv` with `attachment` + `nosniff`), audited reminders (`ANNOUNCEMENT_REMINDER_SENT`), comments, idempotent emoji reactions (`PUT`/`DELETE`), pinning, and transactional single/multi-choice poll voting (`PUT /api/polls/{id}/vote`).
4. **Teams-Style Communities Client UI (`src/Nexus.Client/src/communities.tsx`):**
   - Added **Teams** tab to the App Rail in `src/Nexus.Client/src/main.tsx`.
   - Built strictly with Nexus Design System primitives (`src/ui/`), `lucide-react` icons, token-only CSS in `src/style.css` (0 raw `#hex`), and `renderSafeMarkdown` (`SEC-10`).

## Verification commands run

```powershell
pwsh -File .\scripts\Run-Tests.ps1
```

Results:
- `Test-SecurityGuardrails.ps1`: **0 violations** (124 files scanned)
- `Test-UiDesignCompliance.ps1`: **0 violations** (20 files scanned)
- `Build-KnowledgeIndex.ps1 -Check`: **OK** (4 facts, 8 decisions)
- `dotnet build Nexus.slnx -c Release`: **0 Warning(s), 0 Error(s)**
- `dotnet test tests/Nexus.IntegrationTests` (PostgreSQL 17 Testcontainers): **120 / 120 passed** (including all 3 new `CommunitiesM5Tests` suites)
- `npm --prefix src/Nexus.Client run test`: **3 / 3 passed** (`SEC-10` Markdown XSS)
- `npm --prefix src/Nexus.Client run build`: **TypeScript + Vite green**
- `dotnet build src/Nexus.Desktop -c Release`: **0 Warning(s), 0 Error(s)**

## Deployment reminder (Supabase + Render)

Before deploying M5 to Render (`main`), apply the new `M5CommunitiesAndAnnouncements` migration to Supabase PostgreSQL using the migration helper:

```powershell
pwsh -File .\scripts\deploy\Invoke-SupabaseMigrate.ps1 -MigrateOnly
```
