---
title: "ResetAuthenticatorKeyAsync rotates SecurityStamp and ITicketStore renews existing session key"
summary: "UserManager.ResetAuthenticatorKeyAsync rotates SecurityStamp, and calling SignInAsync on an already-authenticated cookie request calls ITicketStore.RenewAsync on the current session key rather than creating a new row."
area: auth
recorded: 2026-11-23
status: verified
---

# ResetAuthenticatorKeyAsync rotates SecurityStamp and ITicketStore renews existing session key

Observed during `M1a` integration testing (`IdentityM1aTests`):

1. `UserManager.ResetAuthenticatorKeyAsync(user)` internally rotates `user.SecurityStamp`. When `ActiveUserMiddleware` validates the cookie's `nexus.security_stamp` claim against PostgreSQL, subsequent requests fail with `401 session_revoked` unless the current session is renewed after resetting the key.
2. When `CookieAuthenticationOptions.SessionStore` (`ITicketStore`) is configured and `HttpContext.SignInAsync` is called during an already-authenticated request, `CookieAuthenticationHandler` reuses the existing cookie's session key and invokes `ITicketStore.RenewAsync(existingKey, ticket)` (not `StoreAsync`). If the handler marks `currentSessionId` revoked before calling `SignInAsync`, `RenewAsync` updates 0 rows and the user is immediately signed out.

**Why:** ASP.NET Core Identity treats authenticator key rotation as a security-stamp change, and `CookieAuthenticationHandler` renews the active `ITicketStore` key in-place during re-sign-in.

**How to apply:** On password change or MFA setup/enable, revoke only `Id != currentSessionId` in `app_sessions`, and call `SignInAsync` without `SignOutAsync` so `PostgreSqlSessionTicketStore.RenewAsync` updates the current session's `TicketBytes` and `SecurityStamp` cleanly in-place.
