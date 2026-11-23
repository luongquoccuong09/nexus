# Decisions — Decision Log

Each file in this directory is **one** product or architecture decision: what was chosen, who decided it, when, why, and what constraints it imposes. Machine-generated index: [`INDEX.md`](INDEX.md).

This directory supersedes the legacy flat `docs/decision-log.md` and takes precedence over historical proposals, audits (`docs/archive/`), and prototypes (`docs/mockups/`).

## What belongs here

| Is a decision | Is not a decision |
|---|---|
| Product or domain rule (e.g., Community → Channel hierarchy, open trial registration) | A measured runtime/infra behavior → [`../facts/`](../facts/README.md) |
| Architectural boundary or scope exclusion (e.g., modular monolith, single Organization) | An in-progress milestone checklist → [`../implementation-plan/`](../implementation-plan/README.md) |
| Security or governance policy (e.g., password-first auth, no auto-linking Google by email) | Day-to-day dev/deploy instructions → [`../MAINTAINER_GUIDE.md`](../MAINTAINER_GUIDE.md) |

## Card contract

File name: `yyyy-MM-dd-<slug>.md` (date of decision or when the open decision was logged).

```markdown
---
id: P0-D04
title: "Short decision title"
summary: "One sentence stating the decision clearly on its own."
date: 2026-11-23
decided-by: owner         # owner | team | it-hr | pending
status: accepted          # accepted | open | relaxed | superseded
---

# Short decision title

Context, options considered, and the agreed outcome.

**Why:** Rationale behind the choice.

**How to apply:** Concrete rules engineers and agents must follow.
```

| `status` | Meaning |
|---|---|
| `accepted` | Currently binding. |
| `open` | Needs stakeholder/owner sign-off before the dependent milestone starts. |
| `relaxed` | Still active with a documented relaxation (state date and scope in the body). |
| `superseded` | Replaced by a newer decision. Keep the file and link to the replacement on line 1 of the body. |

## Adding or updating a decision

1. If superseding an older decision, change its `status` to `superseded` in the same commit.
2. If the decision adds a hard invariant for coding agents, add a one-line pointer in `AGENTS.md` (do not duplicate the rationale there).
3. Regenerate the index:

```powershell
pwsh -File ./scripts/audit/Build-KnowledgeIndex.ps1
```
