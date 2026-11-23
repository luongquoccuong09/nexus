# Project Nexus — Agent Implementation Guide

Use English for product copy, accessibility labels, sample instructions, code identifiers, comments, tests, and project documentation. Preserve people's names and user-authored content in their original language.

## Where to look first

| Need to know | Read |
|---|---|
| **Start here for any code change:** module graph, entry points, task → file map, known pitfalls | [`docs/CODEMAP.md`](docs/CODEMAP.md) |
| All active canonical docs & doc governance rules | [`docs/README.md`](docs/README.md) |
| Accepted & open decisions (**supersedes historical proposals, audits, and prototypes**) | [`docs/decisions/INDEX.md`](docs/decisions/INDEX.md) |
| Measured runtime, DB, SignalR, auth, client, storage & tooling facts (**check before re-measuring**) | [`docs/facts/INDEX.md`](docs/facts/INDEX.md) |
| Current roadmap, milestones & session handoffs (start with M0.1 handoff) | [`docs/implementation-plan/README.md`](docs/implementation-plan/README.md) & [`ROADMAP.md`](docs/implementation-plan/ROADMAP.md) |
| Modular monolith boundaries & runtime flows | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |
| Resource authorization matrix & `SEC-01`–`SEC-24` traceability | [`docs/SECURITY_AND_AUTHZ.md`](docs/SECURITY_AND_AUTHZ.md) |
| Messaging sync, `client_msg_id`, `seq`/`change_seq`, idempotency | [`docs/SYNC_PROTOCOL.md`](docs/SYNC_PROTOCOL.md) |
| PostgreSQL schema ownership & migration governance | [`docs/DATA_MODEL.md`](docs/DATA_MODEL.md) |
| HTTP API pagination, SignalR & `/api/version` contracts | [`docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md) |
| Local dev, testing, CI security checks, publish & deploy | [`docs/MAINTAINER_GUIDE.md`](docs/MAINTAINER_GUIDE.md) |
| Canonical domain terms (`Organization`, `Community`, `Channel`, `Unverified`...) | [`docs/GLOSSARY.md`](docs/GLOSSARY.md) |

`docs/archive/` and `docs/mockups/` are **not** source of truth. Prototypes illustrate interaction only; the backend always enforces access.

## Workflow rules

- Work on one roadmap item at a time, verify it through its normal entry point, and record the exact commands run in the milestone's `HANDOFF-*.md`.
- Mark a roadmap item complete only after it meets its acceptance criteria.
- After adding or editing cards in `docs/decisions/` or `docs/facts/`, run `pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1`.

## Non-negotiable implementation constraints (proposal §9.1)

- Keep the modular monolith. Redis, microservices, CRM, AI, and unapproved dependencies are outside the implementation scope.
- Preserve the intent of security tests (`SEC-01`–`SEC-24`). Fix the implementation when it violates a test.
- Enforce CORS (no `AllowAnyOrigin`) and TLS certificate validation. Keep privileged storage credentials on the server. Use **Session Mode on port 5432** for the Supabase pooler.
- Check account status (`Active` vs `Disabled`) and resource access (`IAuthorizationService.Can`) across HTTP, SignalR, downloads, and sync.
- Paginate lists with at most **50 entries per page**.
- Exclude message bodies, passwords, cookies, and tokens from logs.

## Domain terminology quick reminder

- [P0-D14](docs/decisions/2026-11-23-p0-d14-personal-chat-and-organizations.md) defines Personal Chat with optional Organization membership and the boundaries for multiple Organizations on one server.
- Community navigation starts with a **Community → Channel**, and each MVP channel inherits its community's membership and roles ([P0-D04](docs/decisions/2026-11-23-p0-d04-community-channel-hierarchy.md)).
