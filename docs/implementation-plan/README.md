# Implementation Plans & Handoffs

This directory holds the master [`ROADMAP.md`](ROADMAP.md) plus milestone-scoped technical plans (`PLAN-*.md`), operational runbooks (`RUNBOOK-*.md`), and session handoffs (`HANDOFF-*.md`).

## Milestone directory index

| Milestone | Directory | Key documents | Status |
|---|---|---|---|
| **Master Roadmap** | [`ROADMAP.md`](ROADMAP.md) | P0 through M9 checklist and dependencies | Active |
| **M0 — Foundation** | [`m0-foundation/`](m0-foundation/) | [`PLAN-m0-foundation.md`](m0-foundation/PLAN-m0-foundation.md)<br>[`HANDOFF-m0-complete.md`](m0-foundation/HANDOFF-m0-complete.md)<br>[`HANDOFF-m0.1.md`](m0-foundation/HANDOFF-m0.1.md) | **Done** on `develop` (`M0.1`–`M0.9` + `P0.2`/`P0.3` verified with Testcontainers & Docker) |
| **M1 — Identity & Directory** | [`m1-identity-directory/`](m1-identity-directory/) | [`PLAN-m1a-identity.md`](m1-identity-directory/PLAN-m1a-identity.md)<br>[`HANDOFF-m1a.md`](m1-identity-directory/HANDOFF-m1a.md)<br>[`HANDOFF-m1b.md`](m1-identity-directory/HANDOFF-m1b.md) | In progress on `develop` (**M1a + M1b `M1.1`–`M1.11` done**; M1c Google OIDC or M2 Messaging next) |
| **M2 — Core Messaging & Sync** | [`m2-messaging-sync/`](m2-messaging-sync/) | [`PLAN-m2-messaging.md`](m2-messaging-sync/PLAN-m2-messaging.md)<br>[`HANDOFF-m2.md`](m2-messaging-sync/HANDOFF-m2.md)<br>[`RUNBOOK-supabase-render.md`](m2-messaging-sync/RUNBOOK-supabase-render.md) | **Done** on `develop` (`M2.1`–`M2.11` + multi-client HTTP/SignalR `M2.12` + Desktop `.msi` + Supabase/Render kit verified; Playwright browser E2E deferred) |
| **M3 — Files** | [`m3-files/`](m3-files/) | [`PLAN-m3-files.md`](m3-files/PLAN-m3-files.md)<br>[`HANDOFF-m3.md`](m3-files/HANDOFF-m3.md) | **Done** on `develop` (`M3.1`–`M3.6` verified with Testcontainers PostgreSQL 17, `SEC-04`, `SEC-11`, mock ClamAV `zINSTREAM`) |
| **M4 — Notifications & Outbox** | [`m4-notifications/`](m4-notifications/) | *Create `PLAN-*.md` when starting M4* | Planned |
| **M5 — Communities & Channels** | [`m5-communities/`](m5-communities/) | *Create `PLAN-*.md` when starting M5* | Planned |

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
