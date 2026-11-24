# Implementation Plans & Handoffs

This directory holds the master [`ROADMAP.md`](ROADMAP.md) plus milestone-scoped technical plans (`PLAN-*.md`), operational runbooks (`RUNBOOK-*.md`), and session handoffs (`HANDOFF-*.md`).

## Milestone directory index

| Milestone | Directory | Key documents | Status |
|---|---|---|---|
| **Master Roadmap** | [`ROADMAP.md`](ROADMAP.md) | P0 through M9 checklist and dependencies | Active |
| **M0 — Foundation** | [`m0-foundation/`](m0-foundation/) | [`PLAN-m0-foundation.md`](m0-foundation/PLAN-m0-foundation.md)<br>[`HANDOFF-m0-complete.md`](m0-foundation/HANDOFF-m0-complete.md)<br>[`HANDOFF-m0.1.md`](m0-foundation/HANDOFF-m0.1.md) | **Done** on `develop` (`M0.1`–`M0.9` + `P0.2`/`P0.3` verified with Testcontainers & Docker) |
| **M1 — Identity & Directory** | [`m1-identity-directory/`](m1-identity-directory/) | [`PLAN-m1a-identity.md`](m1-identity-directory/PLAN-m1a-identity.md)<br>[`HANDOFF-m1a.md`](m1-identity-directory/HANDOFF-m1a.md)<br>[`HANDOFF-m1b.md`](m1-identity-directory/HANDOFF-m1b.md)<br>[`HANDOFF-m1e.md`](m1-identity-directory/HANDOFF-m1e.md) | P0-D14 implemented locally. Legacy production review and cutover pending. |
| **M2 — Core Messaging & Sync** | [`m2-messaging-sync/`](m2-messaging-sync/) | [`PLAN-m2-messaging.md`](m2-messaging-sync/PLAN-m2-messaging.md)<br>[`HANDOFF-m2.md`](m2-messaging-sync/HANDOFF-m2.md)<br>[`RUNBOOK-supabase-render.md`](m2-messaging-sync/RUNBOOK-supabase-render.md) | **Done** on `develop` (`M2.1`–`M2.11` + multi-client HTTP/SignalR `M2.12` + Desktop `.msi` + Supabase/Render kit verified; Playwright browser E2E deferred) |
| **M3 — Files** | [`m3-files/`](m3-files/) | [`PLAN-m3-files.md`](m3-files/PLAN-m3-files.md)<br>[`HANDOFF-m3.md`](m3-files/HANDOFF-m3.md) | **Done** on `develop` (`M3.1`–`M3.6` verified with Testcontainers PostgreSQL 17, `SEC-04`, `SEC-11`, mock ClamAV `zINSTREAM`) |
| **M4 — Notifications & Outbox** | [`m4-notifications/`](m4-notifications/) | [`PLAN-m4-notifications.md`](m4-notifications/PLAN-m4-notifications.md)<br>[`HANDOFF-m4.md`](m4-notifications/HANDOFF-m4.md) | M4.1–M4.6 verified locally. Pilot provider checks remain open. |
| **M5 — Communities & Channels** | [`m5-communities/`](m5-communities/) | [`PLAN-m5-communities.md`](m5-communities/PLAN-m5-communities.md)<br>[`HANDOFF-m5.md`](m5-communities/HANDOFF-m5.md) | **Done** on `develop` (`M5.0`–`M5.6` Teams Communities hierarchy per P0-D04 verified with Testcontainers PostgreSQL 17) |
| **M6 — Collaboration UX** | [`m6-collaboration-ux/`](m6-collaboration-ux/) | [`PLAN-m6-collaboration-ux.md`](m6-collaboration-ux/PLAN-m6-collaboration-ux.md)<br>[`HANDOFF-m6-collaboration-ux.md`](m6-collaboration-ux/HANDOFF-m6-collaboration-ux.md) | M6.16, M6.17, and M6.20–M6.22 verified. M6.18 and M6.19 await native browser checks. M6.23–M6.34 open. |
| **M6 — Calendar & Meetings** | [`m6-calendar-meetings/`](m6-calendar-meetings/) | [`PLAN-m6-calendar-meetings.md`](m6-calendar-meetings/PLAN-m6-calendar-meetings.md) | Planned (`M6.10`–`M6.15`, `M6.35`–`M6.39`). Start with M6.10 component evaluation. |

## Management rules

1. **Separate plans from canonical contracts:**
   - Permanent architecture, security, sync, schema, and operational contracts live at the root of `docs/`.
   - Active milestone plans, checklists, runbooks, and session handoffs live under `docs/implementation-plan/<milestone>/`.
2. **When starting a milestone or major feature:**
   - Add `PLAN-<slug>.md` and/or `HANDOFF-<slug>.md` in the matching milestone directory (or create `m6-rich-chat/`, `m7-admin-governance/`, `m8-hardening/` as needed).
   - Update the table above.
3. **When completing a milestone item:**
   - Record verification commands in `HANDOFF-*.md`.
   - Fold permanent contracts into the canonical docs at `docs/`.
   - Check off the item in [`ROADMAP.md`](ROADMAP.md).
