---
title: "M0.1 toolchain baseline is .NET SDK 10.0.401 and Node.js 24"
summary: "The M0.1 solution builds and tests cleanly on .NET SDK 10.0.401 (pinned in global.json) and Node.js 24; Windows PowerShell commands use npm.cmd."
area: tooling
recorded: 2026-11-23
status: verified
---

# M0.1 toolchain baseline is .NET SDK 10.0.401 and Node.js 24

The preparation environment verified `Nexus.slnx` restore/build/test and `src/Nexus.Client` build/publish with .NET SDK `10.0.401` (pinned in `global.json`) and Node.js `24`.

**Why:** Keeps local development and CI aligned on exact SDK and runtime major versions from M0.1 onward.

**How to apply:** Record the CI SDK version when connecting the Git host pipeline (M0.1/M0.7). On Windows, invoke `npm.cmd` instead of `npm` to avoid PowerShell script execution-policy issues.
