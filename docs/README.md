# Documentation Index

`../AGENTS.md` at the repository root is the entry point for an assistant session: it routes to the canonical files below and holds non-negotiable project invariants. Keep `AGENTS.md` **short**; detailed contracts belong in the canonical documents listed here.

## Active canonical documentation

- [`CODEMAP.md`](CODEMAP.md): **map for a new AI or engineer session** — module graph, request/realtime flows by file, task-to-starting-file table, area pitfalls, and search scope. Paired with `codemap/` (machine-generated symbol/edge index).
- [`ARCHITECTURE.md`](ARCHITECTURE.md): system context, modular monolith boundaries, runtime flows (HTTP, SignalR, PostgreSQL, storage, outbox), and dependency rules.
- [`MAINTAINER_GUIDE.md`](MAINTAINER_GUIDE.md): local development, EF Core migrations, testing (xUnit, Testcontainers, Playwright), CI security gates, publishing, deployment, and rollback.
- [`GLOSSARY.md`](GLOSSARY.md): canonical domain terminology (Organization, Community, Channel, Conversation, Verified/Unverified, roles).
- [`SECURITY_AND_AUTHZ.md`](SECURITY_AND_AUTHZ.md): resource authorization matrix, `SEC-01`–`SEC-24` security test traceability, session/cookie/CORS/TLS invariants, and forbidden patterns.
- [`SYNC_PROTOCOL.md`](SYNC_PROTOCOL.md): messaging and synchronization contract (`client_msg_id`, `seq`, `change_seq`, `conversation_events`, idempotency keys, offline queue, and reconnect reconciliation).
- [`DATA_MODEL.md`](DATA_MODEL.md): PostgreSQL schema ownership by module, uniqueness/index invariants, migration role separation, and retention/offboarding rules.
- [`API_CONTRACTS.md`](API_CONTRACTS.md): HTTP API conventions, pagination limits (`<= 50`), SignalR hub contracts, error semantics, and `/api/version` reload behavior.
- [`decisions/`](decisions/README.md): **decision log** — one file per accepted or open product/architecture decision with date, owner, and rationale. Machine-generated index at [`decisions/INDEX.md`](decisions/INDEX.md). Check here before re-proposing a rejected option.
- [`facts/`](facts/README.md): **verified facts ledger** — one file per measured runtime, infrastructure, or library fact (PostgreSQL, SignalR, Auth, Client, Storage, Tooling). Machine-generated index at [`facts/INDEX.md`](facts/INDEX.md). Check here **before re-measuring**.
- [`implementation-plan/`](implementation-plan/README.md): master [`ROADMAP.md`](implementation-plan/ROADMAP.md) plus milestone-scoped `PLAN-*.md`, `HANDOFF-*.md`, and `RUNBOOK-*.md` files.
- [`design-system/`](design-system/README.md): UI/UX standards, accessibility rules, copy rules, and component patterns.
- [`mockups/`](mockups/README.md): interactive HTML prototypes illustrating interaction flows (backend always enforces access).

## Governance rules

1. **One contract, one canonical document.** Do not create a new standalone guide at the root of `docs/` when a section belongs in an existing canonical document.
2. **No loose handoffs or notes in `docs/` root.** All milestone plans, runbooks, and session handoffs live under `docs/implementation-plan/<milestone>/`.
3. **Decisions override history.** When a decision record in `docs/decisions/` conflicts with `docs/archive/` or `docs/mockups/`, the decision record wins.
4. **English only for project documentation.** Preserve people's names and user-authored content in their original language.
5. **Keep generated indexes in sync.** After adding or editing cards in `docs/decisions/` or `docs/facts/`, run `pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1`.

## Where to start

| Question | Read |
|---|---|
| Which file should I edit for task X? | [`CODEMAP.md`](CODEMAP.md) §3, then [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| What are the module boundaries and forbidden dependencies? | [`ARCHITECTURE.md`](ARCHITECTURE.md) §1–§2 |
| Can actor X perform action Y on resource Z? | [`SECURITY_AND_AUTHZ.md`](SECURITY_AND_AUTHZ.md) |
| How do `client_msg_id`, `seq`, `change_seq`, and retries work? | [`SYNC_PROTOCOL.md`](SYNC_PROTOCOL.md) |
| Which module owns table X, and what constraints protect it? | [`DATA_MODEL.md`](DATA_MODEL.md) |
| What are the API pagination, SignalR, and error conventions? | [`API_CONTRACTS.md`](API_CONTRACTS.md) |
| How do I build, test, migrate, publish, or deploy locally? | [`MAINTAINER_GUIDE.md`](MAINTAINER_GUIDE.md) |
| What does Organization / Community / Channel / Unverified mean? | [`GLOSSARY.md`](GLOSSARY.md) |
| Why did the project choose X instead of Y? | [`decisions/INDEX.md`](decisions/INDEX.md) |
| Has anyone already measured/diagnosed runtime quirk X? | [`facts/INDEX.md`](facts/INDEX.md) |
| What milestone is active and what is next? | [`implementation-plan/README.md`](implementation-plan/README.md) & [`ROADMAP.md`](implementation-plan/ROADMAP.md) |

## Archive

[`docs/archive/`](archive/README.md) holds historical proposals, audits, and feature idea lists. They are **not** source of truth and must not be cited over canonical docs or `docs/decisions/`.
