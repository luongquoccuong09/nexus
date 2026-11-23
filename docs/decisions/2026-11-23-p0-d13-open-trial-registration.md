---
id: P0-D13
title: "Open registration during trial with Unverified accounts"
summary: "Registration is open during the trial period; newly registered accounts enter the Organization as Unverified until matched by an admin."
date: 2026-11-23
decided-by: owner
status: accepted
---

# Open registration during trial with Unverified accounts

During the trial phase, `RegistrationMode` defaults to `Open` (configurable to `OpenWithJoinCode`, and later `InviteOnly`/`Approval` in `M1d`). New accounts enter as `Unverified` with visible badges and restricted capabilities until verified against HR employee records.

**Why:** Enables rapid pilot onboarding without waiting for full HR integration, while clearly distinguishing unverified users to prevent impersonation.

**How to apply:** Display the `Unverified` badge wherever an unverified user's name appears, block official/announcement channel privileges and `@all`, cap daily new DMs, and test `SEC-20` and `SEC-20b`.
