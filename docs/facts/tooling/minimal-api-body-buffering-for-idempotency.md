---
title: "Minimal API model binding reads Request.Body before IEndpointFilter runs"
summary: "EnableBuffering() must run in middleware before Minimal API model binding so IdempotencyEndpointFilter can SHA-256 hash the real JSON body instead of an exhausted stream."
area: tooling
recorded: 2026-11-23
status: verified
---

# Minimal API model binding reads Request.Body before IEndpointFilter runs

Observed during `M0.5` integration testing (`PostgresFoundationTests`): calling `HttpRequest.EnableBuffering()` inside `IEndpointFilter.InvokeAsync` happens **after** ASP.NET Core Minimal API parameter binding has already consumed `Request.Body`, causing `CopyToAsync` to read 0 bytes and falsely treat mismatched payloads as identical replays.

**Why:** Minimal API endpoints bind JSON body parameters prior to invoking endpoint filters.

**How to apply:** `UseNexusSecurityDefaults` enables request buffering in middleware whenever the `Idempotency-Key` header is present, and `IdempotencyEndpointFilter.ComputeRequestBodyHashAsync` fails closed if `rawBytes.Length == 0` when `ContentLength > 0`.
