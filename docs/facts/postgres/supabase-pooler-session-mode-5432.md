---
title: "Supabase pooler requires Session Mode on port 5432"
summary: "When connecting Nexus to PostgreSQL via Supabase's pooler, always use Session Mode on port 5432 rather than Transaction Mode on port 6543."
area: postgres
recorded: 2026-11-23
status: verified
---

# Supabase pooler requires Session Mode on port 5432

Project Nexus requires Session Mode on port `5432` for Supabase connection pooling (codified in `AGENTS.md` and proposal §9.1).

**Why:** EF Core / Npgsql prepared statements and session-scoped PostgreSQL features break or behave unpredictably under Transaction Mode (`6543`).

**How to apply:** Check connection strings in local dev, staging, and CI whenever configuring Supabase PostgreSQL; reject port `6543` pooler URLs.
