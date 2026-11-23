# Implementation Plans & Handoffs

This directory holds the master [`ROADMAP.md`](ROADMAP.md) plus milestone-scoped technical plans (`PLAN-*.md`), operational runbooks (`RUNBOOK-*.md`), and session handoffs (`HANDOFF-*.md`).

## Milestone directory index

| Milestone | Directory | Key documents | Status |
|---|---|---|---|
| **Master Roadmap** | [`ROADMAP.md`](ROADMAP.md) | P0 through M9 checklist and dependencies | Active |
| **M0 — Foundation** | [`m0-foundation/`](m0-foundation/) | [`PLAN-m0-foundation.md`](m0-foundation/PLAN-m0-foundation.md)<br>[`HANDOFF-m0-slice-ab.md`](m0-foundation/HANDOFF-m0-slice-ab.md)<br>[`HANDOFF-m0.1.md`](m0-foundation/HANDOFF-m0.1.md) | In progress on `develop` (`P0.2`, `P0.3`, `M0.1`, `M0.3`, `M0.4`, `M0.6`, `M0.7`, `M0.9` done; Docker/PG needed for `M0.2`, `M0.5`, `M0.8`) |
| **M1 — Identity & Directory** | [`m1-identity-directory/`](m1-identity-directory/) | *Create `PLAN-*.md` when starting M1* | Planned |
| **M2 — Core Messaging & Sync** | [`m2-messaging-sync/`](m2-messaging-sync/) | *Create `PLAN-*.md` when starting M2* | Planned |
| **M3 — Files** | [`m3-files/`](m3-files/) | *Create `PLAN-*.md` when starting M3* | Planned |
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
