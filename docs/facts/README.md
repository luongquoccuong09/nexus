# Facts — Verified Facts Ledger

Each file in this directory records **one** measured or verified fact about Nexus's runtime, database, SignalR, authentication, browser client, file storage, or local/CI tooling, along with why it matters and how to apply it. Machine-generated index: [`INDEX.md`](INDEX.md).

Check [`INDEX.md`](INDEX.md) **before re-measuring or re-debugging** a known symptom.

## How facts differ from canonical docs

| Kind of knowledge | Where it lives | Format |
|---|---|---|
| Full contract or architecture for a domain | `ARCHITECTURE.md`, `SECURITY_AND_AUTHZ.md`, `SYNC_PROTOCOL.md`, `DATA_MODEL.md`, `MAINTAINER_GUIDE.md` | Long-form canonical chapter |
| One discrete measured behavior or quirk | `facts/<area>/` | One file, one fact |
| A product or architectural choice | [`../decisions/`](../decisions/README.md) | One file, one decision |
| Canonical domain terms | [`../GLOSSARY.md`](../GLOSSARY.md) | Table |

Facts never replace canonical contracts. Once a fact is folded into a canonical doc, keep the card concise and link to the canonical section.

## Six areas

| Folder | Scope |
|---|---|
| `postgres/` | PostgreSQL, EF Core, Supabase pooler, `unaccent`/`pg_trgm`, `SKIP LOCKED`, migration roles |
| `signalr/` | WebSocket Origin checks, `user:{id}` fanout, hub filters, disconnect/reconnect behavior |
| `auth/` | ASP.NET Core Identity, BFF cookies, security stamp revocation, TOTP MFA, Google OIDC |
| `client/` | React/Vite/TypeScript, IndexedDB offline queue, Service Worker, Web Push / iOS PWA |
| `storage/` | Supabase Storage, local volumes, signed URLs, magic-byte validation, ClamAV scanning |
| `tooling/` | .NET SDK, Node/npm on Windows/CI, Testcontainers, Playwright, security scanners |

## Card contract

File name: kebab-case stating the fact (`supabase-pooler-session-mode-5432.md`).

```markdown
---
title: "Short assertion title"
summary: "One standalone sentence stating the measured fact."
area: postgres            # must match folder name
recorded: 2026-11-23      # date measured or last verified
status: verified          # verified | imported | superseded
---

# Short assertion title

Where and how it was observed, with exact numbers/versions/error messages.

**Why:** Root cause or why it matters.

**How to apply:** What to do or check when encountering this area/symptom.
```

| `status` | Meaning |
|---|---|
| `verified` | Confirmed against code, tests, or live environment on `recorded`. |
| `imported` | Captured from notes/session memory and not yet re-verified against live code. |
| `superseded` | No longer true. Keep the file and link to the replacement on line 1 of the body so nobody re-tests it. |

## Adding or updating a fact

1. Search [`INDEX.md`](INDEX.md) first; update an existing card rather than creating a near-duplicate.
2. Place the file in the matching `docs/facts/<area>/` folder.
3. Regenerate the index:

```powershell
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1
```
