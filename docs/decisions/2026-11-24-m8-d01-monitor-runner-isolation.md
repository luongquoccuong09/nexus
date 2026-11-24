---
id: M8-D01
title: "Independent runner for the Nexus Monitor"
summary: "Nexus.Web coordinates deployment-scoped sandbox runs while a dedicated .NET runner owns provisioning and workload execution."
date: 2026-11-24
decided-by: owner
status: accepted
---

# Independent runner for the Nexus Monitor

Nexus Monitor owns the operator interface, authorization, run state, lifecycle audit, and aggregate reports. The independent .NET runner owns Docker provisioning, real HTTP and SignalR traffic, deadlines, local emergency stop, and cleanup. The first target is a disposable localhost Docker Sandbox built from an approved Nexus image.

This keeps the control plane available when the target is saturated and preserves the modular monolith. The runner initiates machine-authenticated HTTPS calls so the same control flow can work with a runner behind NAT. Nexus.Web has no Docker or host execution permissions. Operator access is deployment-scoped and independent of Organization roles.

Use OpenTelemetry for application instrumentation and a dedicated metrics store and dashboard for durable telemetry and independent alerting. The default 60-user collaboration preset is an initial local benchmark. Pilot topology, retention policy, and the capacity acceptance criteria remain governed by the outstanding roadmap decisions.
